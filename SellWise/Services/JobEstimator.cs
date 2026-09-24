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
    /// <para>
    /// For GatherBuddy's Vulcan (the default route) the materials follow what Vulcan actually does, which isn't
    /// SellWise's cheapest-cost plan: Vulcan crafts every ingredient that has a recipe, gathers what's gatherable,
    /// and only buys the rest. Artisan jobs follow SellWise's plan, since you supply those materials yourself.
    /// </para>
    /// </summary>
    public JobTimeEstimate Estimate(CraftOpportunity o, int crafts, int made = 0, CraftBackend backend = CraftBackend.Vulcan)
    {
        var tracker = plugin.Tracker;
        var db = plugin.Scanner.Db;
        var now = DateTimeOffset.UtcNow;
        var left = Math.Max(0, crafts - made);
        var lines = backend == CraftBackend.Vulcan && db != null ? VulcanRoute(o.Recipe, db) : o.Materials;
        var needs = ScaledNeeds(lines, left);

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

        var materials = needs.Where(x => x.Line.Source != MaterialSource.Craft && x.Need > 0)
            .GroupBy(x => x.Line.ItemId)
            .Select(g => Progress(g.First().Line, g.Sum(x => x.Need)))
            .OrderBy(p => p.Done)
            .ThenBy(p => p.Line.Source)
            .ToList();

        var parts = needs.Where(x => x.Line.Source == MaterialSource.Craft && x.Need > 0)
            .GroupBy(x => x.Line.ItemId)
            .Select(g => Progress(g.First().Line, g.Sum(x => x.Need)))
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

    /// <summary>
    /// Which phase a running job is in, judged from what's in the bags. Only gatherable materials hold the job in
    /// the gathering phase; things that have to be bought are listed but don't block it.
    /// </summary>
    public static JobPhase Phase(CraftJob job, JobTimeEstimate estimate) => job.State switch
    {
        CraftJobState.Finished => JobPhase.Done,
        CraftJobState.Stopped => JobPhase.Stopped,
        CraftJobState.Failed => JobPhase.Failed,
        _ when job.WaitingForRepair => JobPhase.Repairing,
        _ when job.Made == 0 && estimate.Materials.Any(m => m.Line.Source == MaterialSource.Gather && m.Have + m.OnRetainers < m.Need) => JobPhase.Gathering,
        _ when job.Made == 0 && estimate.Parts.Any(p => !p.Done) => JobPhase.CraftingParts,
        _ => JobPhase.Crafting,
    };

    /// <summary>
    /// The material tree the way GatherBuddy's Vulcan works it out: gatherable → gather, else has a recipe → craft
    /// it (and recurse into its ingredients), else NPC vendor, else buy. Amounts are per craft of the final recipe.
    /// </summary>
    private List<MaterialLine> VulcanRoute(RecipeInfo recipe, RecipeDb db)
    {
        var lines = new List<MaterialLine>();
        void Walk(RecipeInfo r, double craftsPerFinal, int depth, HashSet<uint> path)
        {
            foreach (var ing in r.Ingredients)
            {
                var amount = craftsPerFinal * ing.Amount;
                var name = plugin.Catalog.Get(ing.ItemId)?.Name ?? $"Item {ing.ItemId}";
                if (db.Gatherable.Contains(ing.ItemId))
                {
                    lines.Add(new MaterialLine(ing.ItemId, name, amount, MaterialSource.Gather, 0, 0, depth));
                }
                else if (depth < 6 && !path.Contains(ing.ItemId) && db.ByResult.TryGetValue(ing.ItemId, out var sub))
                {
                    lines.Add(new MaterialLine(ing.ItemId, name, amount, MaterialSource.Craft, 0, 0, depth, sub.RecipeId, sub.Yield));
                    Walk(sub, amount / Math.Max(1, sub.Yield), depth + 1, [.. path, ing.ItemId]);
                }
                else
                {
                    lines.Add(new MaterialLine(ing.ItemId, name, amount,
                        db.VendorSold.Contains(ing.ItemId) ? MaterialSource.Vendor : MaterialSource.Buy, 0, 0, depth));
                }
            }
        }

        Walk(recipe, 1, 0, [recipe.ResultItemId]);
        return lines;
    }

    /// <summary>
    /// How much of each line is still needed. Lines are parent-first, so intermediates you already hold reduce
    /// (or remove) the need for their own ingredients.
    /// </summary>
    private List<(MaterialLine Line, int Need)> ScaledNeeds(IReadOnlyList<MaterialLine> lines, int crafts)
    {
        var tracker = plugin.Tracker;
        var factors = new double[16];
        factors[0] = 1;
        var result = new List<(MaterialLine, int)>();
        foreach (var m in lines)
        {
            if (m.Depth >= factors.Length - 1) continue;
            var need = m.AmountPerCraft * crafts * factors[m.Depth];
            if (m.Source == MaterialSource.Craft)
            {
                var held = tracker.CountInBags(m.ItemId) + tracker.CountOnRetainers(m.ItemId);
                factors[m.Depth + 1] = need > 0 ? factors[m.Depth] * Math.Max(0, need - held) / need : 0;
            }
            result.Add((m, (int)Math.Ceiling(need - 1e-9)));
        }
        return result;
    }

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
