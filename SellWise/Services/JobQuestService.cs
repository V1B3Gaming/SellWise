using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>How to get one quest item, and how many are still missing.</summary>
public sealed record QuestItemPlan(QuestItem Item, int Need, int Have, CraftOpportunity? Craft, bool Gatherable, bool Fish)
{
    public int Missing => Math.Max(0, Need - Have);
}

/// <summary>
/// Crafter and gatherer job quests: which ones you can do now (from your levels and finished quests), and a plan for
/// the items each one wants: craft it, gather it, or it's handed to you by the quest.
/// </summary>
public sealed class JobQuestService
{
    private readonly ProfitScanner scanner;
    private readonly MarketService market;
    private readonly InventoryTracker tracker;
    private Task<JobQuestDb>? dbTask;
    private Task<HashSet<uint>>? fishTask;
    private readonly Dictionary<uint, (QuestStatus Status, string? Reason)> statuses = [];
    private readonly HashSet<uint> priced = [];
    private readonly Dictionary<uint, (int Version, DateTime At, List<QuestItemPlan> Plans)> plans = [];
    private DateTime nextStatus;
    private DateTime nextUnlocks;
    private RecipeUnlocks? unlocks;

    public JobQuestService(ProfitScanner scanner, MarketService market, InventoryTracker tracker)
    {
        this.scanner = scanner;
        this.market = market;
        this.tracker = tracker;
    }

    public JobQuestDb? Db => dbTask is { IsCompletedSuccessfully: true } t ? t.Result : null;
    public int[] Levels { get; private set; } = new int[JobQuestDb.JobNames.Length];

    public void EnsureLoaded()
    {
        dbTask ??= Task.Run(JobQuestDb.Load);
        fishTask ??= Task.Run(() =>
        {
            var data = Plugin.DataManager;
            var fish = data.GetExcelSheet<FishParameter>().Select(f => f.Item.RowId).ToHashSet();
            fish.UnionWith(data.GetExcelSheet<SpearfishingItem>().Select(s => s.Item.RowId));
            return fish;
        });
        _ = scanner.LoadDb();
    }

    public (QuestStatus Status, string? Reason) Status(JobQuest q)
        => statuses.TryGetValue(q.QuestId, out var s) ? s : (QuestStatus.Locked, "Checking...");

    /// <summary>Called from Framework.Update while the tab is open: re-reads quest progress and levels.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextStatus || Db is not { } db || !Plugin.PlayerState.IsLoaded) return;
        nextStatus = now.AddSeconds(2);
        Levels = JobQuestDb.ReadLevels();
        foreach (var q in db.Quests) statuses[q.QuestId] = db.Status(q, Levels);
        if (scanner.Db is { } recipes && now >= nextUnlocks)
        {
            nextUnlocks = now.AddSeconds(30);
            unlocks = ProfitScanner.ReadUnlocks(recipes);
            plans.Clear();
        }
    }

    /// <summary>What each item in the quest needs. Starts pricing the materials the first time a quest is looked at.</summary>
    public List<QuestItemPlan> Plan(JobQuest q, string world)
    {
        // Drawn every frame for every row, so reuse it until your bags change (or prices may have arrived).
        var now = DateTime.UtcNow;
        if (plans.TryGetValue(q.QuestId, out var cached) && cached.Version == tracker.Version && now - cached.At < TimeSpan.FromSeconds(5))
            return cached.Plans;
        var result = BuildPlan(q, world);
        plans[q.QuestId] = (tracker.Version, now, result);
        return result;
    }

    private List<QuestItemPlan> BuildPlan(JobQuest q, string world)
    {
        var recipes = scanner.Db;
        var fish = fishTask is { IsCompletedSuccessfully: true } f ? f.Result : [];
        var calc = scanner.NewCalculator();

        if (recipes != null && priced.Add(q.QuestId) && world.Length > 0)
        {
            var ids = new HashSet<uint>();
            foreach (var item in q.Items)
                if (recipes.ByResult.TryGetValue(item.ItemId, out var r)) ProfitScanner.CollectMaterials(r, recipes, 5, ids);
            if (ids.Count > 0) _ = market.FetchAggregatedAsync(ids.ToList(), world, TimeSpan.FromMinutes(30), null, default);
        }

        return q.Items.Select(item =>
        {
            var need = item.Count ?? 1;
            var have = tracker.CountInBags(item.ItemId, item.Hq);
            CraftOpportunity? craft = null;
            if (!item.FromQuest && recipes != null && recipes.ByResult.TryGetValue(item.ItemId, out var recipe) && calc?.Cost(recipe) is { } cost)
            {
                cost.LockedReason = unlocks?.Describe(recipe);
                craft = cost;
            }
            var gatherable = recipes?.Gatherable.Contains(item.ItemId) == true;
            return new QuestItemPlan(item, need, have, craft, gatherable, fish.Contains(item.ItemId));
        }).ToList();
    }
}
