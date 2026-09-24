using System;
using System.Linq;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>Recomputes the sell plan whenever inventory, market data or settings change.</summary>
public sealed class AdviceService
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly Configuration config;
    private readonly InventoryTracker tracker;
    private readonly MarketService market;
    private readonly ItemCatalog catalog;

    private (int Inventory, int Market, int Config, int Minute) lastInputs = (-1, -1, -1, -1);
    private DateTime nextAutoRefresh = DateTime.MinValue;

    public AdvicePlan Plan { get; private set; } = AdvicePlan.Empty;
    public string HomeWorld { get; private set; } = "";
    public string DataCenter { get; private set; } = "";

    public AdviceService(Configuration config, InventoryTracker tracker, MarketService market, ItemCatalog catalog)
    {
        this.config = config;
        this.tracker = tracker;
        this.market = market;
        this.catalog = catalog;
    }

    public string PricingWorld => string.IsNullOrWhiteSpace(config.WorldOverride) ? HomeWorld : config.WorldOverride.Trim();

    /// <summary>Called from Framework.Update.</summary>
    public void Update(bool windowOpen)
    {
        if (Plugin.PlayerState.IsLoaded && HomeWorld.Length == 0)
        {
            var world = Plugin.PlayerState.HomeWorld.ValueNullable;
            HomeWorld = world?.Name.ExtractText() ?? "";
            DataCenter = world?.DataCenter.ValueNullable?.Name.ExtractText() ?? "";
        }

        // Include the minute so time-based numbers (sales in window) age even without new data.
        var inputs = (tracker.Version, market.Version, config.Revision, DateTime.UtcNow.Minute);
        if (inputs != lastInputs)
        {
            lastInputs = inputs;
            Recompute();
        }

        if (windowOpen && config.AutoRefresh && DateTime.UtcNow >= nextAutoRefresh)
        {
            nextAutoRefresh = DateTime.UtcNow + AutoRefreshInterval;
            RefreshPrices(force: false);
        }
    }

    public void RefreshPrices(bool force)
    {
        var ids = tracker.GetStacks(config).Select(s => s.ItemId)
            .Concat(tracker.GetListings().Select(l => l.ItemId))
            .Where(id => !config.IgnoredItems.Contains(id))
            .Distinct()
            .Where(id => catalog.Get(id) is { Marketable: true, Tradable: true })
            .ToList();

        market.Refresh(ids, PricingWorld, config.FetchDataCenter ? DataCenter : null, TimeSpan.FromMinutes(Math.Max(1, config.CacheMinutes)), force);
    }

    public void RefreshItem(uint itemId)
        => market.Refresh([itemId], PricingWorld, config.FetchDataCenter ? DataCenter : null, TimeSpan.Zero, force: true);

    public void OnHomeWorldChanged() => HomeWorld = "";

    private void Recompute()
    {
        Plan = SellAdvisor.BuildPlan(
            tracker.GetStacks(config),
            tracker.GetListings(),
            catalog.Get,
            market.Get,
            tracker.RetainerNames,
            config.IgnoredItems,
            tracker.FreeListingSlots,
            config.Advisor,
            DateTimeOffset.UtcNow);
    }
}
