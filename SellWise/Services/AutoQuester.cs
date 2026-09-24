using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>
/// Job quests on autopilot, for the jobs you pick: takes the next quest you can do, gets its items ready (hunting,
/// gathering, crafting with Artisan), hands the quest to Questionable to accept, walk, talk and turn in, then moves
/// on to the next. When nothing is left that you can take, it says what to craft or gather to level up.
/// </summary>
public sealed class AutoQuester
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QuestionableGrace = TimeSpan.FromSeconds(20);

    private enum Step
    {
        Idle,
        Next,
        Preparing,
        Questing,
    }

    private readonly JobQuestService quests;
    private readonly JobRunner runner;
    private readonly CraftCoordinator crafter;
    private readonly MobHunter hunter;
    private readonly QuestionableBridge questionable;
    private readonly LevelingAdvisor advisor;
    private readonly ProfitScanner scanner;
    private readonly Configuration config;
    private readonly Func<string> world;

    private Step step = Step.Idle;
    private DateTime stepStarted;
    private DateTime nextTick;
    private DateTime? notRunningSince;
    private HashSet<int> jobs = [];

    public AutoQuester(JobQuestService quests, JobRunner runner, CraftCoordinator crafter, MobHunter hunter, QuestionableBridge questionable,
        LevelingAdvisor advisor, ProfitScanner scanner, Configuration config, Func<string> world)
    {
        this.quests = quests;
        this.runner = runner;
        this.crafter = crafter;
        this.hunter = hunter;
        this.questionable = questionable;
        this.advisor = advisor;
        this.scanner = scanner;
        this.config = config;
        this.world = world;
    }

    public bool IsBusy => step != Step.Idle;
    public string Status { get; private set; } = "";
    public bool Failed { get; private set; }
    public JobQuest? Current { get; private set; }
    public List<string> Completed { get; } = [];

    /// <summary>How to level up, once there's no quest left to take.</summary>
    public IReadOnlyList<LevelingTip> Tips { get; private set; } = [];

    public void DismissTips() => Tips = [];

    /// <summary>Must be called on the framework thread.</summary>
    public string? Start(IReadOnlyCollection<int> forJobs)
    {
        if (IsBusy) return "Auto questing is already running.";
        if (forJobs.Count == 0) return "Pick at least one job.";
        if (!QuestionableBridge.Available) return "Auto questing needs Questionable to walk, talk and turn quests in.";
        if (crafter.IsRunning || runner.IsBusy || hunter.IsBusy) return "Finish or stop what's running first.";
        if (quests.Db == null) return "Job quests are still loading.";

        jobs = forJobs.ToHashSet();
        Completed.Clear();
        Tips = [];
        Failed = false;
        Current = null;
        Status = "Picking the first quest...";
        Go(Step.Next);
        stepStarted = DateTime.MinValue; // no need to settle before the first one
        return null;
    }

    public void Stop()
    {
        if (!IsBusy) return;
        if (step == Step.Questing) questionable.Stop();
        runner.Stop();
        hunter.Stop();
        crafter.Stop();
        Finish("Auto questing stopped.", failed: false);
    }

    /// <summary>Called from Framework.Update.</summary>
    public void Update()
    {
        if (step == Step.Idle) return;
        var now = DateTime.UtcNow;
        if (now < nextTick) return;
        nextTick = now.AddSeconds(1);

        try
        {
            Tick(now);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Auto questing step failed");
            Finish($"Auto questing stopped: {e.Message}", failed: true);
        }
    }

    private void Tick(DateTime now)
    {
        switch (step)
        {
            case Step.Next:
                if (now - stepStarted < Settle) return; // let quest progress and levels update after a turn-in
                PickNext();
                break;

            case Step.Preparing:
                if (runner.IsBusy || hunter.IsBusy || crafter.IsRunning || crafter.Queued > 0)
                {
                    var detail = runner.IsBusy ? runner.Status : hunter.IsBusy ? hunter.Status : crafter.Job?.Status ?? "";
                    Status = $"Getting the items for \"{Current!.Name}\": {detail}";
                    return;
                }
                if (crafter.Job is { State: CraftJobState.Failed or CraftJobState.Stopped } job)
                {
                    Finish($"Couldn't make the items for \"{Current!.Name}\": {job.Status}", failed: true);
                    return;
                }
                if (runner.Failed && !runner.Status.StartsWith("Buy these first", StringComparison.Ordinal))
                {
                    Finish($"Couldn't get the items for \"{Current!.Name}\": {runner.Status}", failed: true);
                    return;
                }
                // Things only an NPC sells: Questionable's routes buy those themselves, so carry on.
                crafter.Dismiss();
                StartQuest();
                break;

            case Step.Questing:
                if (QuestManager.IsQuestComplete(Current!.QuestId))
                {
                    Completed.Add(Current.Name);
                    Status = $"Done: \"{Current.Name}\". Picking the next quest...";
                    notRunningSince = null;
                    Go(Step.Next);
                    return;
                }
                if (questionable.IsRunning)
                {
                    notRunningSince = null;
                    Status = $"Questionable is doing \"{Current.Name}\" ({JobQuestDb.JobNames[Current.JobIndex]} {Current.Level}).";
                    return;
                }
                notRunningSince ??= now;
                if (now - notRunningSince > QuestionableGrace)
                    Finish($"Questionable stopped partway through \"{Current.Name}\". Finish that step by hand (or check Questionable's window), then start auto questing again.", failed: true);
                break;
        }
    }

    private void PickNext()
    {
        var db = quests.Db!;
        var next = db.Quests
            .Where(q => jobs.Contains(q.JobIndex))
            .Select(q => (Quest: q, Status: quests.Status(q).Status))
            .Where(x => x.Status is QuestStatus.Accepted or QuestStatus.Ready)
            .OrderBy(x => x.Status == QuestStatus.Accepted ? 0 : 1)
            .ThenBy(x => x.Quest.Level)
            .Select(x => x.Quest)
            .FirstOrDefault();

        if (next == null)
        {
            Recommend();
            return;
        }

        Current = next;
        Prepare(next);
    }

    /// <summary>Gets the quest's hand-in items ready before Questionable runs it (its craft steps then skip ahead).</summary>
    private void Prepare(JobQuest q)
    {
        var plans = quests.Plan(q, world());
        var vendorSold = scanner.Db?.VendorSold;
        var finishWithArtisan = config.FinishWithArtisan && CraftCoordinator.ArtisanAvailable;

        var crafts = plans
            .Where(p => !p.Item.FromQuest && p.Missing > 0 && p.Craft is { LockedReason: null })
            .Select(p =>
            {
                var o = p.Craft!;
                var count = (int)Math.Ceiling(p.Missing / (double)Math.Max(1, o.Recipe.Yield));
                return CraftCoordinator.VulcanAvailable
                    ? new QueuedJob(o, count, CraftBackend.Vulcan, finishWithArtisan ? scanner.VulcanPlan(o) : null)
                    : new QueuedJob(o, count, CraftBackend.Artisan);
            })
            .ToList();

        // Hand-ins that are themselves monster drops. Gathered ones (Miner, Botanist, Fisher quests) and NPC-sold ones
        // are left to Questionable, whose routes gather and buy them.
        var drops = plans
            .Where(p => !p.Item.FromQuest && p.Missing > 0 && p.Craft == null && !p.Gatherable && !p.Fish && vendorSold?.Contains(p.Item.ItemId) != true)
            .Select(p => (p.Item.ItemId, p.Item.Name, p.Missing))
            .ToList();

        if (crafts.Count == 0 && drops.Count == 0)
        {
            StartQuest();
            return;
        }

        var error = runner.Start(q.Name, crafts.Select(j => (j.Plan ?? j.Opp, j.Crafts)).ToList(),
            () => crafts.Count switch
            {
                0 => null,
                1 => crafter.Start(crafts[0].Opp, crafts[0].Crafts, crafts[0].Backend, crafts[0].Plan),
                _ => crafter.StartAll(crafts),
            },
            gatherAllFirst: crafts, alsoGet: drops);
        if (error != null)
        {
            Finish($"Couldn't start getting the items for \"{q.Name}\": {error}", failed: true);
            return;
        }
        Status = $"Getting the items for \"{q.Name}\"...";
        Go(Step.Preparing);
    }

    private void StartQuest()
    {
        var q = Current!;
        if (!questionable.Start(q.QuestId))
        {
            Finish($"Questionable has no route for \"{q.Name}\" ({JobQuestDb.JobNames[q.JobIndex]} {q.Level}). Its job quest routes stop at level 70.", failed: true);
            return;
        }
        notRunningSince = null;
        Status = $"Questionable is doing \"{q.Name}\".";
        Go(Step.Questing);
    }

    private void Recommend()
    {
        var db = quests.Db!;
        var tips = new List<LevelingTip>();
        foreach (var job in jobs.OrderBy(j => j))
        {
            var (next, reason) = db.Quests
                .Where(q => q.JobIndex == job)
                .Select(q => (Quest: q, S: quests.Status(q)))
                .Where(x => x.S.Status == QuestStatus.Locked)
                .OrderBy(x => x.Quest.Level)
                .Select(x => (x.Quest, x.S.Reason))
                .FirstOrDefault();
            tips.AddRange(advisor.For(job, quests.Levels[job], next, reason));
        }
        Tips = tips;
        var done = Completed.Count > 0 ? $"Finished {Completed.Count} quest{(Completed.Count == 1 ? "" : "s")}. " : "";
        Finish(done + "No job quest you can take right now; here's how to level up for the next one.", failed: false);
    }

    private void Finish(string status, bool failed)
    {
        Status = status;
        Failed = failed;
        step = Step.Idle;
    }

    private void Go(Step next)
    {
        step = next;
        stepStarted = DateTime.UtcNow;
    }
}
