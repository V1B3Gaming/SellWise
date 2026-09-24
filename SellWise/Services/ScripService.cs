using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>
/// Works out which crafter collectables to farm for scrips: the ones your job levels unlock, with material costs
/// priced on your world. Whether each reaches the top collectability tier comes from <see cref="QualityService"/>.
/// </summary>
public sealed class ScripService : IDisposable
{
    private static readonly TimeSpan PriceMaxAge = TimeSpan.FromMinutes(30);

    private readonly Configuration config;
    private readonly ProfitScanner scanner;
    private readonly MarketService market;
    private readonly ItemCatalog catalog;
    private readonly CancellationTokenSource cts = new();
    private Task<ScripDb>? dbTask;
    private volatile bool refreshing;

    public ScripService(Configuration config, ProfitScanner scanner, MarketService market, ItemCatalog catalog)
    {
        this.config = config;
        this.scanner = scanner;
        this.market = market;
        this.catalog = catalog;
    }

    public ScripDb? Db => dbTask is { IsCompletedSuccessfully: true } t ? t.Result : null;
    public IReadOnlyList<ScripOption> Options { get; private set; } = [];
    public string Status { get; private set; } = "";
    public bool IsRefreshing => refreshing;
    public DateTime? RefreshedUtc { get; private set; }

    /// <summary>Job levels (index = CraftType) as of the last refresh.</summary>
    public int[] JobLevels { get; private set; } = new int[8];

    public void EnsureLoaded() => dbTask ??= Task.Run(() => ScripDb.Load(CityTeleporter.Cities.Select(c => c.TerritoryId)));

    /// <summary>Called on the framework thread.</summary>
    public void Refresh(string world)
    {
        if (refreshing || string.IsNullOrEmpty(world)) return;
        refreshing = true;
        Status = "Loading collectables...";
        EnsureLoaded();
        _ = Task.Run(() => DoRefresh(world, cts.Token));
    }

    private async Task DoRefresh(string world, CancellationToken token)
    {
        try
        {
            var recipes = await scanner.LoadDb();
            var scrips = await dbTask!;
            var unlocks = await Plugin.Framework.RunOnFrameworkThread(() => ProfitScanner.ReadUnlocks(recipes));
            JobLevels = unlocks.JobLevels;

            var candidates = scrips.Collectables.Values
                .Where(c => recipes.ByResult.TryGetValue(c.ItemId, out var r) && r.CollectableQuality != null)
                .Select(c => (Info: c, Recipe: recipes.ByResult[c.ItemId]))
                .ToList();

            var materialIds = new HashSet<uint>();
            foreach (var (_, recipe) in candidates) ProfitScanner.CollectMaterials(recipe, recipes, Math.Max(config.Craft.MaxIntermediateDepth, 5), materialIds);
            await market.FetchAggregatedAsync(materialIds.ToList(), world, PriceMaxAge,
                new Progress<(int Done, int Total)>(p => Status = $"Pricing materials for {candidates.Count} collectables... {p.Done}/{p.Total}"), token);

            var calc = scanner.NewCalculator();
            var options = new List<ScripOption>();
            foreach (var (info, recipe) in candidates)
            {
                if (calc?.Cost(recipe) is not { } o) continue;
                o.LockedReason = unlocks.Describe(recipe);
                options.Add(new ScripOption { Collectable = info, Opportunity = o });
            }

            Options = options.OrderByDescending(o => o.Collectable.LevelMin).ThenBy(o => o.Collectable.JobIndex).ToList();
            RefreshedUtc = DateTime.UtcNow;
            var ready = Options.Count(o => o.Opportunity.Unlocked);
            Status = $"{ready} crafter collectables you can make right now.";

            // Price what the exchanges sell too, for "gil per scrip".
            var shopIds = scrips.ShopItems.Select(i => i.ItemId).Where(id => catalog.Get(id) is { Marketable: true }).Distinct().ToList();
            await market.FetchAggregatedAsync(shopIds, world, TimeSpan.FromHours(2), null, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Scrip refresh failed");
            Status = $"Couldn't load collectables: {e.Message}";
        }
        finally
        {
            refreshing = false;
        }
    }

    /// <summary>Market value of one exchange item per scrip it costs, when it sells on the market board.</summary>
    public double? GilPerScrip(ScripShopItem item)
    {
        if (catalog.Get(item.ItemId) is not { Marketable: true }) return null;
        return market.GetAggregated(item.ItemId)?.CheapestListing is { } price ? price * (double)item.Count / item.Cost : null;
    }

    public void Dispose()
    {
        cts.Cancel();
        cts.Dispose();
    }
}
