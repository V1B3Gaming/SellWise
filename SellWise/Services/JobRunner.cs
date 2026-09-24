using System;
using System.Collections.Generic;
using System.Linq;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>
/// Starts a gather-and-craft job the whole way through: works out what's still missing after what's in your bags, hunts
/// any materials that only drop from monsters, then hands over to GatherBuddy and Artisan for the rest. Without this,
/// a recipe needing a hide would gather everything else and stall.
/// </summary>
public sealed class JobRunner
{
    private static readonly TimeSpan LookupWait = TimeSpan.FromSeconds(20);

    private enum Step
    {
        Idle,
        Lookup,
        Hunting,
    }

    private sealed record Hunt(uint ItemId, string Name, int Missing, MobSpot Spot);

    private readonly CraftCoordinator crafter;
    private readonly MobHunter hunter;
    private readonly MobDropService drops;
    private readonly InventoryTracker tracker;
    private readonly Queue<Hunt> hunts = new();
    private List<(uint ItemId, string Name, int Missing)> candidates = [];
    private Func<string?>? launch;
    private Step step = Step.Idle;
    private DateTime started;
    private int huntCount;

    public JobRunner(CraftCoordinator crafter, MobHunter hunter, MobDropService drops, InventoryTracker tracker)
    {
        this.crafter = crafter;
        this.hunter = hunter;
        this.drops = drops;
        this.tracker = tracker;
    }

    public bool IsBusy => step != Step.Idle;
    public string Status { get; private set; } = "";
    public bool Failed { get; private set; }

    /// <summary>What's being made, for the progress window.</summary>
    public string What { get; private set; } = "";

    /// <summary>Hunts left after the current one.</summary>
    public int HuntsLeft => hunts.Count;

    /// <summary>
    /// Prepares and starts a job. <paramref name="plans"/> are costed the way GatherBuddy gathers (what has to be in
    /// your bags before crafting); <paramref name="launch"/> starts the gathering and crafting once any hunting is done.
    /// Returns an error, or null when it's under way. Must be called on the framework thread.
    /// </summary>
    public string? Start(string what, IReadOnlyList<(CraftOpportunity Plan, int Crafts)> plans, Func<string?> launch)
    {
        if (IsBusy) return "SellWise is already getting a job ready.";
        if (crafter.IsRunning) return "A craft job is already running.";
        if (hunter.IsBusy) return "Finish or stop the hunt first.";

        // Raw materials across every job, less what's already in your bags. Parts you hold already
        // take their own materials off the list (see NeedsAfterWhatYouHave).
        candidates = plans
            .SelectMany(p => crafter.LeafNeeds(p.Plan, p.Crafts))
            .Where(x => x.Line.Source is MaterialSource.Buy or MaterialSource.Unknown)
            .GroupBy(x => x.Line.ItemId)
            .Select(g => (g.Key, g.First().Line.Name, g.Sum(x => x.Need) - tracker.CountInBags(g.Key)))
            .Where(x => x.Item3 > 0)
            .ToList();

        What = what;
        Failed = false;
        huntCount = 0;
        hunts.Clear();
        if (candidates.Count == 0) return launch();

        this.launch = launch;
        started = DateTime.UtcNow;
        Status = "Checking which materials only drop from monsters...";
        step = Step.Lookup;
        return null;
    }

    public void Stop()
    {
        if (!IsBusy) return;
        hunter.Stop();
        hunts.Clear();
        step = Step.Idle;
        Status = "Stopped before crafting.";
    }

    /// <summary>Called from Framework.Update.</summary>
    public void Update()
    {
        switch (step)
        {
            case Step.Lookup:
            {
                var lookups = candidates.Select(c => (c, Spots: drops.Spots(c.ItemId))).ToList();
                if (lookups.Any(l => l.Spots == null) && DateTime.UtcNow - started < LookupWait) return;

                var notes = new List<string>();
                foreach (var (c, spots) in lookups)
                {
                    if (spots is not { Count: > 0 }) continue; // not a monster drop (a market item): "Buy first" covers it
                    var (spot, problem) = MobHunter.Choose(spots);
                    if (spot != null) hunts.Enqueue(new Hunt(c.ItemId, c.Name, c.Missing, spot));
                    else notes.Add($"{c.Name}: {problem}");
                }
                if (notes.Count > 0)
                {
                    Finish($"Can't hunt everything this needs, so nothing was started. {string.Join(" ", notes)}", failed: true);
                    return;
                }
                huntCount = hunts.Count;
                if (hunts.Count == 0)
                {
                    Launch();
                    return;
                }
                step = Step.Hunting;
                NextHunt();
                break;
            }

            case Step.Hunting:
                if (hunter.IsBusy)
                {
                    Status = $"Hunt {huntCount - hunts.Count} of {huntCount}: {hunter.Status}";
                    return;
                }
                if (hunter.Failed)
                {
                    Finish($"{hunter.Status} Nothing was crafted.", failed: true);
                    return;
                }
                if (hunts.Count > 0) NextHunt();
                else Launch();
                break;
        }
    }

    private void NextHunt()
    {
        var h = hunts.Dequeue();
        if (hunter.Start(h.ItemId, h.Name, h.Missing, h.Spot) is { } error)
            Finish($"Couldn't start hunting {h.Name}: {error}", failed: true);
    }

    private void Launch()
    {
        hunter.Dismiss(); // the craft job's progress takes over the window
        var error = launch?.Invoke();
        Finish(error ?? (huntCount > 0 ? "Hunting done; gathering and crafting the rest." : ""), failed: error != null);
    }

    private void Finish(string status, bool failed)
    {
        Status = status;
        Failed = failed;
        step = Step.Idle;
        launch = null;
    }
}
