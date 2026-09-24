using System;
using System.Collections.Generic;
using System.Linq;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>
/// Starts a gather-and-craft job the whole way through: works out what's still missing after what's in your bags, hunts
/// any materials that only drop from monsters, then hands over to GatherBuddy and Artisan for the rest.
/// <para>
/// With several items to make (a quest wanting more than one craft), everything is gathered first and nothing is
/// crafted until every item's materials are in your bags; then Artisan crafts them one after another.
/// </para>
/// </summary>
public sealed class JobRunner
{
    private static readonly TimeSpan LookupWait = TimeSpan.FromSeconds(20);
    private const int MaxTopUps = 3;

    private enum Step
    {
        Idle,
        Lookup,
        Hunting,
        Gathering,
    }

    private sealed record Hunt(uint ItemId, string Name, int Missing, MobSpot Spot);

    private readonly CraftCoordinator crafter;
    private readonly MobHunter hunter;
    private readonly MobDropService drops;
    private readonly InventoryTracker tracker;
    private readonly Queue<Hunt> hunts = new();
    private readonly Queue<(CraftOpportunity Opp, int Crafts, CraftOpportunity Plan)> gathers = new();
    private List<(uint ItemId, string Name, int Missing)> candidates = [];
    private IReadOnlyList<(CraftOpportunity Plan, int Crafts)> plans = [];
    private IReadOnlyList<QueuedJob>? together;
    private Func<string?>? launch;
    private Step step = Step.Idle;
    private DateTime started;
    private int huntCount;
    private int gatherCount;
    private int topUps;

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
    /// Pass <paramref name="gatherAllFirst"/> (several items) to gather for all of them before crafting any.
    /// Returns an error, or null when it's under way. Must be called on the framework thread.
    /// </summary>
    public string? Start(string what, IReadOnlyList<(CraftOpportunity Plan, int Crafts)> plans, Func<string?> launch,
        IReadOnlyList<QueuedJob>? gatherAllFirst = null)
    {
        if (IsBusy) return "SellWise is already getting a job ready.";
        if (crafter.IsRunning) return "A craft job is already running.";
        if (hunter.IsBusy) return "Finish or stop the hunt first.";

        this.plans = plans;
        together = gatherAllFirst is { Count: > 1 } && CraftCoordinator.VulcanAvailable && CraftCoordinator.ArtisanAvailable ? gatherAllFirst : null;
        What = what;
        Failed = false;
        huntCount = 0;
        topUps = 0;
        hunts.Clear();
        gathers.Clear();
        this.launch = launch;

        // Raw materials GatherBuddy can't gather, across every item, less what's already in your bags. Parts you
        // hold already take their own materials off the list (see NeedsAfterWhatYouHave).
        // NPC-sold items count too: GatherBuddy doesn't buy them in this pipeline.
        candidates = Shortfall().Where(x => x.Line.Source is MaterialSource.Buy or MaterialSource.Unknown or MaterialSource.Vendor)
            .Select(x => (x.Line.ItemId, x.Line.Name, x.Missing)).ToList();
        if (candidates.Count == 0)
        {
            AfterHunting();
            return Failed ? Status : null;
        }

        started = DateTime.UtcNow;
        Status = "Checking which materials only drop from monsters...";
        step = Step.Lookup;
        return null;
    }

    public void Stop()
    {
        if (!IsBusy) return;
        hunter.Stop();
        if (step == Step.Gathering) crafter.Stop();
        hunts.Clear();
        gathers.Clear();
        Finish("Stopped before crafting.", failed: false);
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

                var problems = new List<string>();
                var toBuy = new List<string>();
                var planned = new List<Hunt>();
                var here = Plugin.ClientState.TerritoryType;
                foreach (var (c, spots) in lookups)
                {
                    if (spots is not { Count: > 0 })
                    {
                        toBuy.Add($"{c.Name} x{c.Missing}" + (VendorName(c.ItemId) is { } npc ? $" (from {npc})" : " (market board)"));
                        continue;
                    }
                    // Fewer teleports: the zone you're in, or one another hunt already goes to, wins if it's suitable.
                    var (spot, problem) = MobHunter.Choose(spots, [here, .. planned.Select(p => p.Spot.TerritoryId)]);
                    if (spot != null) planned.Add(new Hunt(c.ItemId, c.Name, c.Missing, spot));
                    else if (VendorName(c.ItemId) is { } seller) toBuy.Add($"{c.Name} x{c.Missing} (from {seller}; can't hunt it: {problem})");
                    else problems.Add($"{c.Name}: {problem}");
                }
                if (toBuy.Count > 0)
                {
                    // GatherBuddy can't get these either; starting would gather the rest and stall.
                    Finish($"Buy these first (SellWise never buys for you): {string.Join(", ", toBuy)}. Nothing was started.", failed: true);
                    return;
                }
                if (problems.Count > 0)
                {
                    Finish($"Can't hunt everything this needs, so nothing was started. {string.Join(" ", problems)}", failed: true);
                    return;
                }

                // One trip per zone, starting with the one you're in.
                foreach (var h in planned.OrderBy(h => h.Spot.TerritoryId == here ? 0 : 1)
                             .ThenBy(h => planned.FindIndex(p => p.Spot.TerritoryId == h.Spot.TerritoryId)))
                    hunts.Enqueue(h);
                huntCount = hunts.Count;
                if (hunts.Count == 0)
                {
                    AfterHunting();
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
                else AfterHunting();
                break;

            case Step.Gathering:
                if (crafter.IsRunning)
                {
                    Status = $"Gathering for everything first ({gatherCount - gathers.Count} of {gatherCount}): {crafter.Job?.Status}";
                    return;
                }
                if (crafter.Job is { State: CraftJobState.Failed or CraftJobState.Stopped } job)
                {
                    Finish($"Gathering stopped: {job.Status} Nothing was crafted.", failed: true);
                    return;
                }
                if (gathers.Count > 0) NextGather();
                else CheckEverythingGathered();
                break;
        }
    }

    private readonly Dictionary<uint, string?> vendorNames = [];

    /// <summary>An NPC that sells the item for gil, or null.</summary>
    private string? VendorName(uint itemId)
    {
        if (vendorNames.TryGetValue(itemId, out var cached)) return cached;
        string? name = null;
        var data = Plugin.DataManager;
        var shops = new HashSet<uint>();
        foreach (var shop in data.GetSubrowExcelSheet<Lumina.Excel.Sheets.GilShopItem>())
            foreach (var entry in shop)
                if (entry.Item.RowId == itemId) shops.Add(shop.RowId);
        if (shops.Count > 0)
        {
            var residents = data.GetExcelSheet<Lumina.Excel.Sheets.ENpcResident>();
            foreach (var npc in data.GetExcelSheet<Lumina.Excel.Sheets.ENpcBase>())
            {
                if (!npc.ENpcData.Any(d => shops.Contains(d.RowId))) continue;
                name = residents.GetRowOrDefault(npc.RowId)?.Singular.ExtractText();
                if (!string.IsNullOrEmpty(name)) break;
            }
            name ??= "an NPC vendor";
        }
        vendorNames[itemId] = name;
        return name;
    }

    /// <summary>Raw materials still missing across every item, after what's in your bags.</summary>
    private List<(MaterialLine Line, int Missing)> Shortfall()
        => plans
            .SelectMany(p => crafter.LeafNeeds(p.Plan, p.Crafts))
            .GroupBy(x => x.Line.ItemId)
            .Select(g => (g.First().Line, g.Sum(x => x.Need) - tracker.CountInBags(g.Key)))
            .Where(x => x.Item2 > 0)
            .ToList();

    private void NextHunt()
    {
        var h = hunts.Dequeue();
        if (hunter.Start(h.ItemId, h.Name, h.Missing, h.Spot) is { } error)
            Finish($"Couldn't start hunting {h.Name}: {error}", failed: true);
    }

    private void AfterHunting()
    {
        hunter.Dismiss(); // the craft job's progress takes over the window
        if (together == null)
        {
            var error = launch?.Invoke();
            Finish(error ?? (huntCount > 0 ? "Hunting done; gathering and crafting the rest." : ""), failed: error != null);
            return;
        }

        // Several items: gather for all of them now, craft later.
        foreach (var job in together)
            gathers.Enqueue((job.Opp, job.Crafts, job.Plan ?? job.Opp));
        gatherCount = gathers.Count;
        step = Step.Gathering;
        NextGather();
    }

    private void NextGather()
    {
        while (gathers.Count > 0)
        {
            var (opp, crafts, plan) = gathers.Dequeue();
            var error = crafter.StartGatherOnly(opp, crafts, plan);
            if (error == null)
            {
                step = Step.Gathering;
                return;
            }
            if (error.StartsWith("You already have everything", StringComparison.Ordinal)) continue; // nothing to gather for this one
            Finish($"Couldn't gather for {opp.Item.Name}: {error}", failed: true);
            return;
        }
        CheckEverythingGathered();
    }

    /// <summary>
    /// GatherBuddy plans each item against what's already in your bags, so two items sharing a material (shards, an
    /// ore) come up short. Top that up before crafting anything.
    /// </summary>
    private void CheckEverythingGathered()
    {
        var shortfall = Shortfall();
        if (shortfall.Count == 0)
        {
            StartCrafting();
            return;
        }
        if (topUps >= MaxTopUps)
        {
            Finish("Still short of " + string.Join(", ", shortfall.Select(s => $"{s.Line.Name} x{s.Missing}")) +
                   " after gathering, so nothing was crafted.", failed: true);
            return;
        }
        topUps++;

        // For each missing material, ask GatherBuddy to gather for the item that uses the most of it per craft,
        // with enough crafts that its plan covers the shortfall on top of what's already in your bags.
        foreach (var (line, missing) in shortfall)
        {
            var best = plans
                .Select(p => (p.Plan, PerCraft: crafter.LeafNeeds(p.Plan, 1).Where(x => x.Line.ItemId == line.ItemId).Sum(x => x.Need)))
                .Where(x => x.PerCraft > 0)
                .OrderByDescending(x => x.PerCraft)
                .FirstOrDefault();
            if (best.Plan == null) continue;
            var crafts = (int)Math.Min(999, Math.Ceiling((tracker.CountInBags(line.ItemId) + missing) / (double)best.PerCraft));
            gathers.Enqueue((best.Plan, crafts, best.Plan));
        }
        gatherCount = gathers.Count;
        Status = $"Topping up materials shared between items ({string.Join(", ", shortfall.Select(s => s.Line.Name))})...";
        NextGather();
    }

    private void StartCrafting()
    {
        // Artisan crafts from what's now in your bags, following the same plan GatherBuddy gathered for.
        var jobs = together!.Select(j => new QueuedJob(j.Plan ?? j.Opp, j.Crafts, CraftBackend.Artisan, null, j.Destination)).ToList();
        crafter.Dismiss();
        var error = crafter.StartAll(jobs);
        Finish(error ?? "Everything's gathered; crafting now.", failed: error != null);
    }

    private void Finish(string status, bool failed)
    {
        Status = status;
        Failed = failed;
        step = Step.Idle;
        launch = null;
    }
}
