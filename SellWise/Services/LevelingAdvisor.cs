using System;
using System.Collections.Generic;
using System.Linq;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>Something to do to level a job up to its next quest.</summary>
/// <param name="Craft">A recipe to make (crafters), costed with the current material choices.</param>
/// <param name="GatherItemId">An item to gather (Miner, Botanist).</param>
public sealed record LevelingTip(int JobIndex, string Headline, string Detail, CraftOpportunity? Craft = null, uint GatherItemId = 0, string GatherName = "");

/// <summary>
/// What to craft or gather to reach the level the next job quest needs. Crafters: recipes at (or just under) your
/// level, cheapest materials first, since recipes at your level give the most experience. Gatherers: items from
/// always-up nodes at your level.
/// </summary>
public sealed class LevelingAdvisor
{
    private const int CraftTips = 3;
    private const int GatherTips = 3;

    private readonly ProfitScanner scanner;
    private readonly ItemCatalog catalog;

    public LevelingAdvisor(ProfitScanner scanner, ItemCatalog catalog)
    {
        this.scanner = scanner;
        this.catalog = catalog;
    }

    /// <summary>Tips for one job. Call on the framework thread (reads unlocks).</summary>
    public List<LevelingTip> For(int job, int level, JobQuest? next, string? reason)
    {
        var name = JobQuestDb.JobNames[job];
        var tips = new List<LevelingTip>();
        var goal = next != null ? $"{name} {level}: \"{next.Name}\" needs level {next.Level}." : $"{name} {level}: no more job quests with routes.";
        if (next != null && reason != null && !reason.StartsWith("Reach", StringComparison.Ordinal))
            goal = $"{name} {level}: \"{next.Name}\" is locked ({reason}).";

        if (scanner.Db is not { } db) return [new LevelingTip(job, goal, "Recipes are still loading.")];

        if (job < 8)
        {
            var unlocks = ProfitScanner.ReadUnlocks(db);
            var calc = scanner.NewCalculator();
            var picks = db.Recipes
                .Where(r => r.CraftType == job && r.Level <= level && r.Level >= Math.Max(1, level - 2) && !r.IsExpert && !r.IsSpecialist)
                .Where(r => unlocks.IsUnlocked(r) && r.CollectableQuality == null)
                .Select(r => calc?.Cost(r))
                .OfType<CraftOpportunity>()
                .Where(o => o.Warning == null)
                .OrderByDescending(o => o.Recipe.Level) // your own level gives the most experience
                .ThenBy(o => o.MaterialValue / Math.Max(1, o.Recipe.Yield))
                .GroupBy(o => o.Recipe.Level)
                .SelectMany(g => g.Take(2))
                .Take(CraftTips)
                .ToList();
            foreach (var o in picks)
                tips.Add(new LevelingTip(job, goal, $"Craft {o.Item.Name} (lv {o.Recipe.Level}): about {o.MaterialValue:N0} gil of materials each.", Craft: o));
        }
        else if (job < 10)
        {
            var picks = db.GatherInfo.Values
                .Where(g => g.JobIndex == job && g.Kind == NodeKind.Regular && g.Level <= level && g.Level >= Math.Max(1, level - 4))
                .Select(g => (Info: g, Item: catalog.Get(g.ItemId)))
                .Where(x => x.Item != null && x.Info.ItemId > 19) // shards, crystals and clusters come off every node
                .OrderByDescending(x => x.Info.Level)
                .Take(GatherTips)
                .ToList();
            foreach (var (info, item) in picks)
                tips.Add(new LevelingTip(job, goal, $"Gather {item!.Name} (lv {info.Level} nodes) with GatherBuddy.", GatherItemId: info.ItemId, GatherName: item.Name));
        }
        else
        {
            tips.Add(new LevelingTip(job, goal, "Fish at your level: AutoHook or GatherBuddy's fishing timers help. Ocean fishing gives a lot of experience."));
        }

        if (tips.Count == 0) tips.Add(new LevelingTip(job, goal, "Nothing suitable found at your level."));
        return tips;
    }
}
