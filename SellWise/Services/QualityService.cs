using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Player;
using SellWise.Core;

namespace SellWise.Services;

public sealed class SavedCrafterStats
{
    public int Craftsmanship { get; set; }
    public int Control { get; set; }
    public int CP { get; set; }
    public int Level { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public CrafterStats ToStats() => new(Craftsmanship, Control, CP, Level);
}

/// <summary>What SellWise worked out about hitting HQ (or top collectability) on a recipe.</summary>
public sealed record QualityReport(
    string TargetLabel,
    int Target,
    CrafterStats? Stats,
    DateTime? StatsSavedUtc,
    QualityPlan? Unbuffed,
    BuffSuggestion? Buffs,
    string? Note)
{
    public bool ReachesUnbuffed => Unbuffed?.Reaches(Target) == true;
    public bool ReachesWithBuffs => Buffs?.Plan.Reaches(Target) == true;
}

/// <summary>
/// Remembers each crafting job's stats (the game only shows the current job's), and estimates whether a recipe
/// reaches HQ, plus the cheapest food and medicine that would get it there.
/// </summary>
public sealed class QualityService
{
    private const uint WellFed = 48, Medicated = 49;

    private readonly Configuration config;
    private readonly MarketService market;
    private readonly InventoryTracker tracker;
    private readonly ProfitScanner scanner;
    private readonly ConcurrentDictionary<string, Task<QualityReport>> cache = new();
    private DateTime nextSnapshot;
    private bool pricesRequested;

    public QualityService(Configuration config, MarketService market, InventoryTracker tracker, ProfitScanner scanner)
    {
        this.config = config;
        this.market = market;
        this.tracker = tracker;
        this.scanner = scanner;
    }

    public SavedCrafterStats? StatsFor(int craftType)
        => tracker.ContentId != 0 && config.CrafterStats.TryGetValue(tracker.ContentId, out var jobs) && jobs.TryGetValue(craftType, out var s) ? s : null;

    /// <summary>Called from Framework.Update: saves the current crafter job's stats when no food/medicine is active.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextSnapshot) return;
        nextSnapshot = now.AddSeconds(3);

        var ps = Plugin.PlayerState;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (!ps.IsLoaded || player == null || tracker.ContentId == 0) return;

        var job = (int)ps.ClassJob.RowId;
        if (job < 8 || job > 15) return;
        if (player.StatusList.Any(s => s.StatusId is WellFed or Medicated)) return; // would inflate the saved stats

        var stats = new SavedCrafterStats
        {
            Craftsmanship = ps.GetAttribute(PlayerAttribute.Craftsmanship),
            Control = ps.GetAttribute(PlayerAttribute.Control),
            CP = ps.GetAttribute(PlayerAttribute.CraftingPoints),
            Level = ps.Level,
            UpdatedUtc = now,
        };
        if (stats.Craftsmanship <= 0) return;

        if (!config.CrafterStats.TryGetValue(tracker.ContentId, out var jobs))
            config.CrafterStats[tracker.ContentId] = jobs = [];

        var craftType = job - 8;
        if (jobs.TryGetValue(craftType, out var old) && old.Craftsmanship == stats.Craftsmanship && old.Control == stats.Control
            && old.CP == stats.CP && old.Level == stats.Level)
        {
            old.UpdatedUtc = now; // same gear; just note it's current
            return;
        }

        jobs[craftType] = stats;
        config.Save();
    }

    /// <summary>The report for a recipe, or null while it's being worked out in the background. Call on the framework thread.</summary>
    public QualityReport? Get(CraftOpportunity o, string world)
    {
        var recipe = o.Recipe;
        var saved = StatsFor(recipe.CraftType);
        var key = $"{recipe.RecipeId}:{saved?.Craftsmanship}/{saved?.Control}/{saved?.CP}/{saved?.Level}";
        var task = cache.GetOrAdd(key, _ => Task.Run(() => Compute(o, saved, world)));
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    private async Task<QualityReport> Compute(CraftOpportunity o, SavedCrafterStats? saved, string world)
    {
        var recipe = o.Recipe;
        if (recipe.Craft is not { } craft)
            return new QualityReport("", 0, null, null, null, null, "No crafting data for this recipe.");

        string label;
        int target;
        if (recipe.CollectableQuality is { Count: 3 } tiers)
        {
            target = tiers[2];
            label = $"top-tier scrips (collectability {tiers[2] / 10})";
        }
        else if (recipe.CanHq && o.Item.CanBeHq)
        {
            target = craft.MaxQuality;
            label = "HQ";
        }
        else
        {
            return new QualityReport("", 0, null, null, null, null, "This item has no HQ version.");
        }

        if (saved == null)
            return new QualityReport(label, target, null, null, null, null,
                $"Switch to {recipe.Job} once so SellWise can read its stats (without food or medicine active).");

        var stats = saved.ToStats();
        var unbuffed = CraftPlanner.Plan(stats, craft, target);
        if (unbuffed.Reaches(target))
            return new QualityReport(label, target, stats, saved.UpdatedUtc, unbuffed, null, null);

        // Short: find the cheapest food + medicine that closes the gap.
        var consumables = scanner.Db?.Consumables ?? [];
        if (consumables.Count == 0)
            return new QualityReport(label, target, stats, saved.UpdatedUtc, unbuffed, null, "Food data is still loading.");

        if (!pricesRequested)
        {
            pricesRequested = true;
            await market.FetchAggregatedAsync(consumables.Select(c => c.ItemId).Distinct().ToList(), world, TimeSpan.FromHours(1), null, default);
        }

        long? Cost(Consumable c)
        {
            if (tracker.CountInBags(c.ItemId) > 0) return 0;
            var p = market.GetAggregated(c.ItemId);
            return (c.Hq ? p?.Hq.MinListing : p?.Nq.MinListing ?? p?.Hq.MinListing) is { } price ? price : null;
        }

        var buffs = BuffPlanner.FindCheapest(stats, craft, target, consumables, Cost);
        return new QualityReport(label, target, stats, saved.UpdatedUtc, unbuffed, buffs, null);
    }

    public void Invalidate() => cache.Clear();
}
