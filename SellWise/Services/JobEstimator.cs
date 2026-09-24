using System;
using System.Collections.Generic;
using System.Linq;
using SellWise.Core;

namespace SellWise.Services;

public enum JobPhase
{
    Repairing,
    Gathering,
    CraftingParts,
    Crafting,
    Done,
    Stopped,
    Failed,
}

/// <summary>One material's progress towards a job, with a gathering time estimate when it's gathered.</summary>
public sealed record MaterialProgress(MaterialLine Line, ItemInfo? Info, int Need, int Have, int OnRetainers, GatherEstimate? Gather)
{
    public int Missing => Math.Max(0, Need - Have);
    public bool Done => Have >= Need;
}

public sealed record JobTimeEstimate(
    TimeSpan Gather,
    TimeSpan Craft,
    IReadOnlyList<MaterialProgress> Materials,
    IReadOnlyList<MaterialProgress> Parts,
    int ActionsPerCraft)
{
    public TimeSpan Total => Gather + Craft;
}

/// <summary>Works out what's left to gather and craft for a job, and roughly how long it will take.</summary>
public sealed class JobEstimator
{
    private readonly Plugin plugin;

    public JobEstimator(Plugin plugin) => this.plugin = plugin;

    /// <summary>
    /// Estimate for making <paramref name="crafts"/> crafts, <paramref name="made"/> of which are already done.
    /// Retainer stock counts towards gathering (Vulcan pulls it) but not towards what's in your bags.
    /// </summary>
    public JobTimeEstimate Estimate(CraftOpportunity o, int crafts, int made = 0)
    {
        var tracker = plugin.Tracker;
        var db = plugin.Scanner.Db;
        var now = DateTimeOffset.UtcNow;
        var left = Math.Max(0, crafts - made);

        MaterialProgress Progress(MaterialLine line, double needed)
        {
            var need = (int)Math.Ceiling(needed);
            var have = tracker.CountInBags(line.ItemId);
            var retainers = tracker.CountOnRetainers(line.ItemId);
            GatherEstimate? gather = null;
            if (line.Source == MaterialSource.Gather && db?.GatherInfo.TryGetValue(line.ItemId, out var info) == true)
                gather = TimeEstimator.Gather(info, Math.Max(0, need - have - retainers), now);
            return new MaterialProgress(line, plugin.Catalog.Get(line.ItemId), need, have, retainers, gather);
        }

        var materials = o.Materials.Where(m => m.Source != MaterialSource.Craft)
            .GroupBy(m => m.ItemId)
            .Select(g => Progress(g.First(), g.Sum(m => m.AmountPerCraft) * left))
            .OrderBy(p => p.Done)
            .ThenBy(p => p.Line.Source)
            .ToList();

        var parts = o.Materials.Where(m => m.Source == MaterialSource.Craft)
            .Select(m => Progress(m, m.AmountPerCraft * left))
            .ToList();

        // Timed nodes mostly mean waiting, and regular gathering can happen during the wait.
        var gathers = materials.Where(m => m.Gather is { Visits: > 0 }).Select(m => m.Gather!).ToList();
        var busy = gathers.Sum(g => g.Timed
            ? TimeEstimator.SecondsPerMaterial + g.Visits * TimeEstimator.SecondsPerVisit
            : g.Total.TotalSeconds);
        var longestTimed = gathers.Where(g => g.Timed).Select(g => g.Total.TotalSeconds).DefaultIfEmpty(0).Max();
        var gatherTime = TimeSpan.FromSeconds(Math.Max(busy, longestTimed));

        var report = plugin.Quality.Get(o, plugin.Advice.PricingWorld);
        var plan = report?.ReachesUnbuffed == true ? report.Unbuffed : report?.Buffs?.Plan ?? report?.Unbuffed;
        var actions = plan is { Rotation.Count: > 0 } p ? p.Rotation.Count : TimeEstimator.DefaultCraftActions;

        var partCrafts = parts.Sum(p => (int)Math.Ceiling(p.Missing / (double)Math.Max(1, p.Line.RecipeYield)));
        var craftTime = TimeEstimator.Craft(partCrafts) + TimeEstimator.Craft(left, actions);

        return new JobTimeEstimate(gatherTime, craftTime, materials, parts, actions);
    }

    /// <summary>Which phase a running job is in, judged from what's in the bags.</summary>
    public static JobPhase Phase(CraftJob job, JobTimeEstimate estimate) => job.State switch
    {
        CraftJobState.Finished => JobPhase.Done,
        CraftJobState.Stopped => JobPhase.Stopped,
        CraftJobState.Failed => JobPhase.Failed,
        _ when job.WaitingForRepair => JobPhase.Repairing,
        _ when job.Made == 0 && estimate.Materials.Any(m => !m.Done) => JobPhase.Gathering,
        _ when job.Made == 0 && estimate.Parts.Any(p => !p.Done) => JobPhase.CraftingParts,
        _ => JobPhase.Crafting,
    };

    /// <summary>A short "when" for one material: "3m", "spawns in 12m", "up now", "buy", "done".</summary>
    public static string When(MaterialProgress m)
    {
        if (m.Done) return "done";
        if (m.Have + m.OnRetainers >= m.Need) return "on retainers";
        return m.Line.Source switch
        {
            MaterialSource.Buy => "buy",
            MaterialSource.Vendor => "NPC",
            MaterialSource.Unknown => "?",
            _ when m.Gather is { Timed: true, UpNow: true } g => $"up now · {TimeEstimator.Format(g.Total)}",
            _ when m.Gather is { Timed: true } g => $"spawns in {TimeEstimator.Format(g.NextSpawn ?? TimeSpan.Zero)}",
            _ when m.Gather is { } g => TimeEstimator.Format(g.Total),
            _ => "",
        };
    }
}
