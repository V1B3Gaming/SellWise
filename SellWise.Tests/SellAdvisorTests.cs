using SellWise.Core;
using Xunit;

namespace SellWise.Tests;

public class SellAdvisorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly AdvisorSettings Settings = new();
    private static readonly IReadOnlySet<string> Mine = new HashSet<string>(["Bob"], StringComparer.OrdinalIgnoreCase);

    private static ItemInfo Item(uint vendor = 10, bool canBeHq = true, bool marketable = true, bool tradable = true, bool collectable = false)
        => new(5057, "Test Ingot", vendor, marketable, tradable, canBeHq, collectable, 0);

    private static MarketListing L(uint price, int qty = 1, bool hq = false, string retainer = "Stranger")
        => new(price, qty, hq, retainer, "Gilgamesh");

    private static MarketSale S(uint price, double daysAgo, int qty = 1, bool hq = false)
        => new(price, qty, hq, Now - TimeSpan.FromDays(daysAgo));

    private static ItemMarketData Data(IEnumerable<MarketListing> listings, IEnumerable<MarketSale> history, int historyLimit = 40)
        => new()
        {
            ItemId = 5057,
            HasData = true,
            Listings = listings.OrderBy(l => l.UnitPrice).ToList(),
            History = history.OrderByDescending(h => h.Time).ToList(),
            HistoryLimit = historyLimit,
            FetchedAt = Now,
        };

    private static IEnumerable<MarketSale> SteadySales(uint price, int count = 10, bool hq = false)
        => Enumerable.Range(0, count).Select(i => S(price, i * 0.5, 1, hq));

    [Fact]
    public void UndercutsCheapestCompetitor_IgnoringOwnRetainers()
    {
        var data = Data([L(900, retainer: "bob"), L(1000), L(1200)], SteadySales(1000));

        var rec = SellAdvisor.AdviseStack(Item(), false, 5, "Bags 5", data, Mine, Settings, Now);

        Assert.Equal(Verdict.List, rec.Verdict);
        Assert.Equal(999u, rec.SuggestedPrice);
        Assert.Equal(1000u, rec.LowestCompetitor);
        Assert.Equal((long)Math.Floor(999 * 0.95) * 5, rec.NetTotal);
    }

    [Fact]
    public void PercentUndercut()
    {
        var s = new AdvisorSettings { UndercutPercent = 5 };
        Assert.Equal(950u, SellAdvisor.Undercut(1000, s));
        Assert.Equal(1u, SellAdvisor.Undercut(1, s));
    }

    [Fact]
    public void HoldsWhenMarketDumpedBelowFloor()
    {
        var data = Data([L(100), L(1000)], SteadySales(1000));

        var rec = SellAdvisor.AdviseStack(Item(), false, 1, "", data, Mine, Settings, Now);

        Assert.Equal(Verdict.Hold, rec.Verdict);
        Assert.Equal(999u, rec.SuggestedPrice); // line up behind the dump, under the next sane listing
    }

    [Fact]
    public void HoldAtMedianWhenOnlyDumpedListingsExist()
    {
        var data = Data([L(100)], SteadySales(1000));

        var rec = SellAdvisor.AdviseStack(Item(), false, 1, "", data, Mine, Settings, Now);

        Assert.Equal(Verdict.Hold, rec.Verdict);
        Assert.Equal(1000u, rec.SuggestedPrice);
    }

    [Fact]
    public void ListingRelistIgnoresDumpedListing()
    {
        var listing = new OwnListing(5057, false, 1, 1000, 1, "Bob", 0);
        var data = Data([L(100), L(900)], SteadySales(1000));

        var rec = SellAdvisor.AdviseListing(Item(), listing, data, Mine, Settings, Now);

        Assert.Equal(Verdict.Relist, rec.Verdict);
        Assert.Equal(899u, rec.SuggestedPrice);
        Assert.Contains("dumped", rec.Reason);
    }

    [Fact]
    public void VendorsWhenMarketBarelyBeatsNpc()
    {
        var data = Data([L(110)], SteadySales(110));

        var rec = SellAdvisor.AdviseStack(Item(vendor: 100), false, 10, "", data, Mine, Settings, Now);

        Assert.Equal(Verdict.Vendor, rec.Verdict);
        Assert.Equal(1000, rec.VendorTotal);
    }

    [Fact]
    public void HqVendorPriceIsTenPercentHigher()
        => Assert.Equal(110u, SellAdvisor.VendorUnitPrice(Item(vendor: 100), hq: true));

    [Fact]
    public void MarksSlowMarkets()
    {
        // One sale in two weeks and 20 units to sell.
        var data = Data([L(5000)], [S(5000, 3)]);

        var rec = SellAdvisor.AdviseStack(Item(), false, 20, "", data, Mine, Settings, Now);

        Assert.Equal(Verdict.ListSlow, rec.Verdict);
        Assert.True(rec.EstDays > Settings.SlowDays);
    }

    [Fact]
    public void HqSellerIgnoresNqListings()
    {
        var data = Data([L(500, hq: false), L(2000, hq: true)], SteadySales(2000, hq: true));

        var rec = SellAdvisor.AdviseStack(Item(), true, 1, "", data, Mine, Settings, Now);

        Assert.Equal(2000u, rec.LowestCompetitor);
        Assert.Equal(1999u, rec.SuggestedPrice);
    }

    [Fact]
    public void NqSellerCompetesWithCheaperHq()
    {
        var data = Data([L(900, hq: true), L(1000, hq: false)], SteadySales(1000));

        var rec = SellAdvisor.AdviseStack(Item(), false, 1, "", data, Mine, Settings, Now);

        Assert.Equal(899u, rec.SuggestedPrice);
    }

    [Fact]
    public void PricesAboveMedianWhenNoCompetition()
    {
        var data = Data([], SteadySales(1000));

        var rec = SellAdvisor.AdviseStack(Item(), false, 1, "", data, Mine, Settings, Now);

        Assert.Equal(Verdict.List, rec.Verdict);
        Assert.Equal(1100u, rec.SuggestedPrice);
    }

    [Fact]
    public void MedianIgnoresOutlierSales()
    {
        var sales = SteadySales(1000, 9).Append(S(99_999, 0.1));
        var est = SellAdvisor.Estimate(Item(), false, Data([], sales), Mine, Settings, Now);

        Assert.Equal(1000u, est.FairPrice);
    }

    [Fact]
    public void VelocityUsesCoveredSpanWhenHistoryTruncated()
    {
        // 40 sales (the fetch limit) spread over 2 days = 20/day, not 40/14.
        var sales = Enumerable.Range(0, 40).Select(i => S(100, i * 2.0 / 39));
        var est = SellAdvisor.Estimate(Item(), false, Data([], sales, historyLimit: 40), Mine, Settings, Now);

        Assert.InRange(est.UnitsPerDay, 19.5, 20.5);
    }

    [Fact]
    public void UntradableAndCollectableAreFlagged()
    {
        Assert.Equal(Verdict.Untradable, SellAdvisor.AdviseStack(Item(tradable: false), false, 1, "", null, Mine, Settings, Now).Verdict);
        Assert.Equal(Verdict.Untradable, SellAdvisor.AdviseStack(Item(collectable: true), false, 1, "", null, Mine, Settings, Now).Verdict);
    }

    [Fact]
    public void UnmarketableWithVendorPriceSaysVendor()
        => Assert.Equal(Verdict.Vendor, SellAdvisor.AdviseStack(Item(vendor: 50, marketable: false), false, 1, "", null, Mine, Settings, Now).Verdict);

    [Fact]
    public void NoMarketDataSaysNoData()
        => Assert.Equal(Verdict.NoData, SellAdvisor.AdviseStack(Item(), false, 1, "", null, Mine, Settings, Now).Verdict);

    [Fact]
    public void ListingUndercut_SuggestsRelist()
    {
        var listing = new OwnListing(5057, false, 1, 1000, 1, "Bob", 0);
        var data = Data([L(950), L(1000, retainer: "Bob")], SteadySales(1000));

        var rec = SellAdvisor.AdviseListing(Item(), listing, data, Mine, Settings, Now);

        Assert.Equal(Verdict.Relist, rec.Verdict);
        Assert.Equal(949u, rec.SuggestedPrice);
    }

    [Fact]
    public void ListingUndercutBelowFloor_KeepsPrice()
    {
        var listing = new OwnListing(5057, false, 1, 1000, 1, "Bob", 0);
        var data = Data([L(100)], SteadySales(1000));

        var rec = SellAdvisor.AdviseListing(Item(), listing, data, Mine, Settings, Now);

        Assert.Equal(Verdict.ListingOk, rec.Verdict);
    }

    [Fact]
    public void ListingCheapest_IsOk()
    {
        var listing = new OwnListing(5057, false, 1, 990, 1, "Bob", 0);
        var data = Data([L(1000)], SteadySales(1000));

        Assert.Equal(Verdict.ListingOk, SellAdvisor.AdviseListing(Item(), listing, data, Mine, Settings, Now).Verdict);
    }

    [Fact]
    public void ListingFarBelowNextCompetitor_SuggestsRaise()
    {
        var listing = new OwnListing(5057, false, 1, 500, 1, "Bob", 0);
        var data = Data([L(1000)], SteadySales(1000));

        var rec = SellAdvisor.AdviseListing(Item(), listing, data, Mine, Settings, Now);

        Assert.Equal(Verdict.Raise, rec.Verdict);
        Assert.Equal(999u, rec.SuggestedPrice);
    }

    [Fact]
    public void BuildPlan_GroupsStacksAndMarksBestForFreeSlots()
    {
        var items = new Dictionary<uint, ItemInfo>
        {
            [1] = Item() with { Id = 1, Name = "Cheap" },
            [2] = Item() with { Id = 2, Name = "Pricey" },
            [3] = Item() with { Id = 3, Name = "Ignored" },
        };
        var market = new Dictionary<uint, ItemMarketData>
        {
            [1] = Data([L(1000)], SteadySales(1000)),
            [2] = Data([L(50_000)], SteadySales(50_000)),
            [3] = Data([L(50_000)], SteadySales(50_000)),
        };
        var stacks = new[]
        {
            new OwnedStack(1, false, 3, StackSource.Bag),
            new OwnedStack(1, false, 2, StackSource.Retainer, "Bob"),
            new OwnedStack(2, false, 1, StackSource.Bag),
            new OwnedStack(3, false, 1, StackSource.Bag),
        };

        var plan = SellAdvisor.BuildPlan(stacks, [], id => items.GetValueOrDefault(id), id => market.GetValueOrDefault(id),
            Mine, new HashSet<uint> { 3 }, freeSlots: 1, Settings, Now);

        Assert.Equal(2, plan.Stacks.Count);
        var cheap = plan.Stacks.Single(r => r.Item.Id == 1);
        Assert.Equal(5, cheap.Quantity);
        Assert.Equal("Bag 3, Bob 2", cheap.Where);
        Assert.False(cheap.FitsFreeSlot);
        Assert.True(plan.Stacks.Single(r => r.Item.Id == 2).FitsFreeSlot);
    }
}
