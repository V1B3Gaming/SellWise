using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using SellWise.Core;

namespace SellWise.Services;

public enum CraftBackend
{
    /// <summary>GatherBuddy Reborn's Vulcan: gathers missing materials, pulls from retainers, then crafts.</summary>
    Vulcan,
    /// <summary>Artisan: crafts only; materials must already be in your bags.</summary>
    Artisan,
}

public enum CraftJobState
{
    Running,
    Finished,
    Stopped,
    Failed,
}

public sealed class CraftJob
{
    public required CraftOpportunity Opportunity { get; init; }
    public required int Crafts { get; init; }
    public required CraftBackend Backend { get; init; }
    public int StartCount { get; init; }
    public int TargetCount { get; init; }
    public int CurrentCount { get; set; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public CraftJobState State { get; set; } = CraftJobState.Running;
    public string Status { get; set; } = "";

    /// <summary>Artisan runs one recipe at a time: intermediates first, then the final item.</summary>
    public List<(uint RecipeId, int Crafts, string Name)> Steps { get; init; } = [];
    public int StepIndex { get; set; }
    public bool StepSeenBusy { get; set; }
    public DateTime StepStartedUtc { get; set; }

    /// <summary>The backend hasn't been started (or the next Artisan step is held) until a repair finishes.</summary>
    public bool WaitingForRepair { get; set; }

    public int Made => Math.Max(0, CurrentCount - StartCount);
    public int Wanted => TargetCount - StartCount;
}

/// <summary>Hands a craft off to Vulcan or Artisan and watches your inventory until the items arrive.</summary>
public sealed class CraftCoordinator
{
    private static readonly TimeSpan ArtisanStartTimeout = TimeSpan.FromSeconds(15);

    private readonly InventoryTracker tracker;
    private readonly RepairService repair;
    private readonly ICallGateSubscriber<ushort, int, object> artisanCraft;
    private readonly ICallGateSubscriber<bool> artisanBusy;
    private readonly ICallGateSubscriber<bool, object> artisanStop;
    private readonly ICallGateSubscriber<bool, object> gbrSetAutoGather;
    private readonly ICallGateSubscriber<string> gbrStatus;
    private DateTime nextPoll = DateTime.MinValue;

    public CraftJob? Job { get; private set; }

    /// <summary>Fired once when a job's items have all been made.</summary>
    public event Action<CraftJob>? Finished;

    public CraftCoordinator(InventoryTracker tracker, RepairService repair)
    {
        this.tracker = tracker;
        this.repair = repair;
        var pi = Plugin.PluginInterface;
        artisanCraft = pi.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem");
        artisanBusy = pi.GetIpcSubscriber<bool>("Artisan.IsBusy");
        artisanStop = pi.GetIpcSubscriber<bool, object>("Artisan.SetStopRequest");
        gbrSetAutoGather = pi.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled");
        gbrStatus = pi.GetIpcSubscriber<string>("GatherBuddyReborn.GetAutoGatherStatusText");
    }

    public static bool VulcanAvailable => IsLoaded("GatherBuddyReborn");
    public static bool ArtisanAvailable => IsLoaded("Artisan");

    private static bool IsLoaded(string internalName)
        => Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == internalName && p.IsLoaded);

    public bool IsRunning => Job is { State: CraftJobState.Running };

    /// <summary>Materials Artisan would need in your bags that you don't have (item, missing amount).</summary>
    public List<(MaterialLine Line, int Missing)> MissingForArtisan(CraftOpportunity opp, int crafts)
    {
        var missing = new List<(MaterialLine, int)>();
        foreach (var line in LeafMaterials(opp, crafts))
        {
            var need = (int)Math.Ceiling(line.Need);
            var have = tracker.CountInBags(line.Line.ItemId);
            if (have < need) missing.Add((line.Line, need - have));
        }
        return missing;
    }

    /// <summary>Must be called on the framework thread.</summary>
    public string? Start(CraftOpportunity opp, int crafts, CraftBackend backend)
    {
        if (IsRunning) return "A craft job is already running.";
        if (crafts <= 0) return "Pick a quantity.";
        if (opp.LockedReason is { } locked) return $"You can't craft this yet: {locked}.";

        var itemId = opp.Item.Id;
        var start = CountResult(itemId);
        var job = new CraftJob
        {
            Opportunity = opp,
            Crafts = crafts,
            Backend = backend,
            StartCount = start,
            CurrentCount = start,
            TargetCount = start + crafts * Math.Max(1, opp.Recipe.Yield),
            Steps = backend == CraftBackend.Artisan ? ArtisanSteps(opp, crafts) : [],
        };

        if (backend == CraftBackend.Vulcan && !VulcanAvailable) return "GatherBuddy Reborn isn't loaded.";
        if (backend == CraftBackend.Artisan && !ArtisanAvailable) return "Artisan isn't loaded.";

        if (repair.NeedsRepair)
        {
            // Repair first; Update() launches the backend once gear is fixed.
            job.WaitingForRepair = true;
            job.Status = "Repairing gear before starting...";
            repair.Start();
            Job = job;
            return null;
        }

        var error = Launch(job);
        if (error == null) Job = job;
        return error;
    }

    private string? Launch(CraftJob job)
    {
        try
        {
            switch (job.Backend)
            {
                case CraftBackend.Vulcan:
                    if (!Plugin.CommandManager.ProcessCommand($"/vulcan craft {job.Opportunity.Recipe.RecipeId} {job.Crafts}"))
                        return "GatherBuddy Reborn didn't accept /vulcan craft. Is it up to date?";
                    job.Status = "Vulcan is gathering and crafting. Watch its window for progress.";
                    break;

                case CraftBackend.Artisan:
                    StartArtisanStep(job);
                    break;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Failed to start craft job");
            return $"Couldn't start: {e.Message}";
        }

        return null;
    }

    public void Stop()
    {
        if (Job is not { State: CraftJobState.Running } job) return;
        repair.Stop();
        try
        {
            if (job.Backend == CraftBackend.Artisan) artisanStop.InvokeAction(true);
            else gbrSetAutoGather.InvokeAction(false);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Stop request failed");
        }

        job.State = CraftJobState.Stopped;
        job.Status = job.Backend == CraftBackend.Vulcan
            ? "Asked GatherBuddy to stop gathering. If Vulcan is mid-craft, stop it from its window."
            : "Asked Artisan to stop.";
    }

    public void MarkFinished()
    {
        if (Job is not { } job) return;
        job.State = CraftJobState.Finished;
        job.Status = "Marked as finished.";
        Finished?.Invoke(job);
    }

    public void Dismiss()
    {
        if (!IsRunning) Job = null;
    }

    /// <summary>Called from Framework.Update.</summary>
    public void Update()
    {
        if (Job is not { State: CraftJobState.Running } job) return;
        var now = DateTime.UtcNow;
        if (now < nextPoll) return;
        nextPoll = now.AddSeconds(1);

        if (job.WaitingForRepair)
        {
            if (repair.IsBusy)
            {
                job.Status = repair.Status;
                return;
            }
            if (repair.Failed)
            {
                job.State = CraftJobState.Failed;
                job.Status = repair.Status;
                return;
            }

            job.WaitingForRepair = false;
            if (job.Backend == CraftBackend.Artisan && job.StepIndex > 0)
            {
                StartArtisanStep(job); // resume the step that was held for the repair
                return;
            }
            if (Launch(job) is { } error)
            {
                job.State = CraftJobState.Failed;
                job.Status = error;
            }
            return;
        }

        job.CurrentCount = CountResult(job.Opportunity.Item.Id);
        if (job.CurrentCount >= job.TargetCount)
        {
            job.State = CraftJobState.Finished;
            job.Status = $"Done: made {job.Made}.";
            Finished?.Invoke(job);
            return;
        }

        if (job.Backend == CraftBackend.Vulcan)
        {
            try
            {
                var s = gbrStatus.InvokeFunc();
                if (!string.IsNullOrWhiteSpace(s)) job.Status = $"GatherBuddy: {s}";
            }
            catch
            {
                // Status is cosmetic; the item count is what tells us we're done.
            }
            return;
        }

        UpdateArtisan(job, now);
    }

    private void UpdateArtisan(CraftJob job, DateTime now)
    {
        bool busy;
        try
        {
            busy = artisanBusy.InvokeFunc();
        }
        catch
        {
            job.State = CraftJobState.Failed;
            job.Status = "Lost contact with Artisan.";
            return;
        }

        if (busy)
        {
            job.StepSeenBusy = true;
            return;
        }

        if (!job.StepSeenBusy)
        {
            if (now - job.StepStartedUtc > ArtisanStartTimeout)
            {
                job.State = CraftJobState.Failed;
                job.Status = $"Artisan didn't start crafting {job.Steps[job.StepIndex].Name}. Missing materials, or the wrong job/gear?";
            }
            return;
        }

        // Step finished; move to the next one.
        job.StepIndex++;
        if (job.StepIndex >= job.Steps.Count)
        {
            job.State = CraftJobState.Finished;
            job.Status = job.Made >= job.Wanted ? $"Done: made {job.Made}." : $"Artisan stopped after making {job.Made} of {job.Wanted}.";
            Finished?.Invoke(job);
            return;
        }

        if (repair.NeedsRepair)
        {
            // Between Artisan steps is a safe point to repair; the step starts once gear is fixed.
            job.WaitingForRepair = true;
            job.Status = "Repairing gear before the next step...";
            repair.Start();
            return;
        }

        StartArtisanStep(job);
    }

    private void StartArtisanStep(CraftJob job)
    {
        var (recipeId, crafts, name) = job.Steps[job.StepIndex];
        artisanCraft.InvokeAction((ushort)recipeId, crafts);
        job.StepSeenBusy = false;
        job.StepStartedUtc = DateTime.UtcNow;
        job.Status = job.Steps.Count > 1
            ? $"Artisan step {job.StepIndex + 1}/{job.Steps.Count}: {name} x{crafts}"
            : $"Artisan is crafting {name} x{crafts}";
    }

    /// <summary>Intermediates you don't already have, deepest first, then the final recipe.</summary>
    private List<(uint, int, string)> ArtisanSteps(CraftOpportunity opp, int crafts)
    {
        var steps = new List<(uint, int, string)>();
        foreach (var (line, need) in Requirements(opp, crafts)
                     .Where(x => x.Line.Source == MaterialSource.Craft && x.Line.RecipeId != null)
                     .OrderByDescending(x => x.Line.Depth))
        {
            var missing = (int)Math.Ceiling(need) - tracker.CountInBags(line.ItemId);
            if (missing <= 0) continue;
            steps.Add((line.RecipeId!.Value, (int)Math.Ceiling(missing / (double)Math.Max(1, line.RecipeYield)), line.Name));
        }

        steps.Add((opp.Recipe.RecipeId, crafts, opp.Item.Name));
        return steps;
    }

    /// <summary>
    /// How much of each material line is needed for <paramref name="crafts"/> crafts. Intermediates you already hold
    /// reduce (or remove) the need for their own sub-materials.
    /// </summary>
    private IEnumerable<(MaterialLine Line, double Need)> Requirements(CraftOpportunity opp, int crafts)
    {
        // Lines are ordered parent-first, so a factor per depth tracks how much of the current parent still has to be made.
        var factors = new double[32];
        factors[0] = 1;
        foreach (var m in opp.Materials)
        {
            if (m.Depth >= factors.Length - 1) continue;
            var need = m.AmountPerCraft * crafts * factors[m.Depth];
            if (m.Source == MaterialSource.Craft)
            {
                var missing = Math.Max(0, need - tracker.CountInBags(m.ItemId));
                factors[m.Depth + 1] = need > 0 ? factors[m.Depth] * missing / need : 0;
            }
            if (need > 0) yield return (m, need);
        }
    }

    /// <summary>Materials that must be in your bags for Artisan: everything that isn't itself crafted along the way.</summary>
    private IEnumerable<(MaterialLine Line, double Need)> LeafMaterials(CraftOpportunity opp, int crafts)
        => Requirements(opp, crafts).Where(x => x.Line.Source != MaterialSource.Craft)
            .GroupBy(x => x.Line.ItemId)
            .Select(g => (g.First().Line, g.Sum(x => x.Need)));

    private static unsafe int CountResult(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        return im->GetInventoryItemCount(itemId, false, false, false, 0) + im->GetInventoryItemCount(itemId, true, false, false, 0);
    }
}
