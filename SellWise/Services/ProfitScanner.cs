using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>Finds the recipes that earn the most on your world, and marks which ones you can't craft yet.</summary>
public sealed class ProfitScanner : IDisposable
{
    private const int MaxResults = 400;
    private static readonly TimeSpan PriceMaxAge = TimeSpan.FromMinutes(30);

    private readonly Configuration config;
    private readonly MarketService market;
    private readonly ItemCatalog catalog;
    private readonly CancellationTokenSource cts = new();
    private Task<RecipeDb>? recipeDbTask;
    private volatile bool scanning;

    public ProfitScanner(Configuration config, MarketService market, ItemCatalog catalog)
    {
        this.config = config;
        this.market = market;
        this.catalog = catalog;
    }

    public bool IsScanning => scanning;
    public string Status { get; private set; } = "Press Scan to find profitable crafts.";
    public IReadOnlyList<CraftOpportunity> Results { get; private set; } = [];
    public DateTime? LastScanUtc { get; private set; }

    /// <summary>Job levels (index = CraftType) captured at scan time.</summary>
    public int[] JobLevels { get; private set; } = new int[8];

    public RecipeDb? Db => recipeDbTask is { IsCompletedSuccessfully: true } t ? t.Result : null;

    /// <summary>Called on the framework thread.</summary>
    public void Start(string world)
    {
        if (scanning || string.IsNullOrEmpty(world)) return;
        scanning = true;
        Status = "Loading recipes...";

        recipeDbTask ??= Task.Run(RecipeDb.Load);

        var craft = config.Craft;
        var adv = config.Advisor;
        _ = Task.Run(() => Scan(world, craft, adv, cts.Token));
    }

    private async Task Scan(string world, CraftSettings craft, AdvisorSettings adv, CancellationToken token)
    {
        try
        {
            var db = await recipeDbTask!;
            var unlocks = await Plugin.Framework.RunOnFrameworkThread(() => ReadUnlocks(db));
            JobLevels = unlocks.JobLevels;

            // Every marketable recipe, locked or not, so you can see what's worth unlocking.
            // Where several recipes make the same item, prefer one you can craft, then the lowest level.
            var candidates = db.Recipes
                .Where(r => r.CraftType is >= 0 and < 8 && r.Level > 0)
                .Where(r => craft.IncludeExpert || !r.IsExpert)
                .Where(r => craft.IncludeSpecialist || !r.IsSpecialist)
                .Where(r => catalog.Get(r.ResultItemId) is { Marketable: true, Tradable: true })
                .GroupBy(r => r.ResultItemId)
                .Select(g => g.OrderBy(r => unlocks.IsUnlocked(r) ? 0 : 1).ThenBy(r => r.Level).First())
                .ToList();

            // Pass 1: what do the results sell for, and how fast?
            await market.FetchAggregatedAsync(candidates.Select(r => r.ResultItemId).ToList(), world, PriceMaxAge,
                new Progress<(int Done, int Total)>(p => Status = $"Checking prices of {candidates.Count:N0} craftable items... {p.Done}/{p.Total}"), token);

            var sellers = candidates.Where(r =>
            {
                if (market.GetAggregated(r.ResultItemId) is not { } p) return false;
                var q = craft.AssumeHq && r.CanHq ? p.Hq : p.Nq;
                return q.UnitsPerDay >= craft.MinUnitsPerDay;
            }).ToList();

            // Pass 2: price the materials (and intermediate materials) of the ones that sell.
            var materialIds = new HashSet<uint>();
            foreach (var r in sellers) CollectMaterials(r, db, craft.MaxIntermediateDepth, materialIds);
            await market.FetchAggregatedAsync(materialIds.ToList(), world, PriceMaxAge,
                new Progress<(int Done, int Total)>(p => Status = $"Pricing {materialIds.Count:N0} materials for {sellers.Count:N0} items that sell... {p.Done}/{p.Total}"), token);

            var calc = new ProfitCalculator(catalog.Get, market.GetAggregated,
                id => db.ByResult.TryGetValue(id, out var sub) ? sub : null,
                db.Gatherable, db.VendorSold, craft, adv);

            var results = new List<CraftOpportunity>();
            foreach (var r in sellers)
            {
                if (calc.Evaluate(r) is not { } o) continue;
                o.LockedReason = unlocks.Describe(r);
                results.Add(o);
            }

            Results = results.OrderByDescending(o => o.BatchProfit).Take(MaxResults).ToList();
            LastScanUtc = DateTime.UtcNow;
            var unlockedCount = Results.Count(o => o.Unlocked);
            Status = $"Scanned {candidates.Count:N0} recipes; {sellers.Count:N0} sell at least {craft.MinUnitsPerDay:0.#}/day on {world}. " +
                     $"{unlockedCount} of the top {Results.Count} are unlocked for you.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Profit scan failed");
            Status = $"Scan failed: {e.Message}";
        }
        finally
        {
            scanning = false;
        }
    }

    /// <summary>Reads job levels, master recipe books and quest completion. Must run on the framework thread.</summary>
    private static unsafe RecipeUnlocks ReadUnlocks(RecipeDb db)
    {
        var levels = new int[8];
        var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
        for (var i = 0; i < 8; i++)
        {
            if (jobs.GetRowOrDefault((uint)(8 + i)) is { } job)
                levels[i] = Plugin.PlayerState.GetClassJobLevel(job);
        }

        var books = new HashSet<uint>();
        var ps = PlayerState.Instance();
        foreach (var bookId in db.Recipes.Select(r => r.SecretBookId).Where(id => id != 0).Distinct())
        {
            if (ps != null && ps->IsSecretRecipeBookUnlocked(bookId))
                books.Add(bookId);
        }

        var quests = db.Recipes.Select(r => r.QuestId).Where(id => id != 0).Distinct()
            .Where(QuestManager.IsQuestComplete)
            .ToHashSet();

        var bookSheet = Plugin.DataManager.GetExcelSheet<SecretRecipeBook>();
        var questSheet = Plugin.DataManager.GetExcelSheet<Quest>();
        return new RecipeUnlocks
        {
            JobLevels = levels,
            UnlockedBooks = books,
            CompletedQuests = quests,
            BookName = id => bookSheet.GetRowOrDefault(id)?.Name.ExtractText() is { Length: > 0 } n ? n : $"master recipe book #{id}",
            QuestName = id => questSheet.GetRowOrDefault(id)?.Name.ExtractText() is { Length: > 0 } n ? n : $"quest #{id}",
        };
    }

    private static void CollectMaterials(RecipeInfo recipe, RecipeDb db, int depth, HashSet<uint> into)
    {
        foreach (var ing in recipe.Ingredients)
        {
            if (!into.Add(ing.ItemId)) continue;
            if (depth > 0 && db.ByResult.TryGetValue(ing.ItemId, out var sub))
                CollectMaterials(sub, db, depth - 1, into);
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        cts.Dispose();
    }
}
