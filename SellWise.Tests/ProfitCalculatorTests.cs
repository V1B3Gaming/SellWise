using SellWise.Core;
using Xunit;

namespace SellWise.Tests;

public class ProfitCalculatorTests
{
    // Items: 1 = Sword (result), 2 = Ingot (craftable intermediate), 3 = Ore (gatherable), 4 = Flux (vendor), 5 = Crystal (gatherable)
    private readonly Dictionary<uint, ItemInfo> items = new()
    {
        [1] = new(1, "Sword", 50, true, true, true, false, 0),
        [2] = new(2, "Ingot", 5, true, true, true, false, 0),
        [3] = new(3, "Ore", 1, true, true, false, false, 0),
        [4] = new(4, "Flux", 1, true, true, false, false, 0, VendorBuyPrice: 20),
        [5] = new(5, "Crystal", 1, true, true, false, false, 0),
    };

    private readonly Dictionary<uint, AggregatedPrice> prices = new()
    {
        [1] = Price(1, hqMin: 10_000, hqAvg: 9_000, hqVel: 4),
        [2] = Price(2, nqMin: 900),
        [3] = Price(3, nqMin: 100),
        [4] = Price(4, nqMin: 50),
        [5] = Price(5, nqMin: 10),
    };

    private static readonly RecipeInfo Sword = new(100, 1, 1, 1, 90, true, false, false, 0, [new(2, 2), new(5, 5)]);
    private static readonly RecipeInfo IngotRecipe = new(101, 2, 1, 1, 80, true, false, false, 0, [new(3, 3), new(4, 1)]);

    private static AggregatedPrice Price(uint id, uint? nqMin = null, double? nqAvg = null, double nqVel = 0, uint? hqMin = null, double? hqAvg = null, double hqVel = 0)
        => new(id, new QualityPrices(nqMin, nqAvg, nqVel, null), new QualityPrices(hqMin, hqAvg, hqVel, null));

    private ProfitCalculator Calc(CraftSettings? cs = null, Dictionary<uint, MaterialSource>? overrides = null)
        => new(id => items.GetValueOrDefault(id), id => prices.GetValueOrDefault(id),
            id => id == 2 ? IngotRecipe : null,
            new HashSet<uint> { 3, 5 }, new HashSet<uint> { 4 },
            cs ?? new CraftSettings(), new AdvisorSettings(), overrides);

    [Fact]
    public void GatherAndCraftMakesPartsEvenWhenBuyingIsCheaper()
    {
        prices[2] = Price(2, nqMin: 100); // ingots on the market for less than they cost to make
        Assert.Equal(MaterialSource.Craft, Calc().Evaluate(Sword)!.Materials.Single(m => m.ItemId == 2).Source);
        Assert.Equal(MaterialSource.Buy, Calc(new CraftSettings { MaterialMode = MaterialMode.Cheapest }).Evaluate(Sword)!.Materials.Single(m => m.ItemId == 2).Source);
    }

    [Fact]
    public void BuyEverythingBuysWhatsListed()
    {
        var o = Calc(new CraftSettings { MaterialMode = MaterialMode.BuyAll }).Evaluate(Sword)!;
        Assert.Equal(MaterialSource.Buy, o.Materials.Single(m => m.ItemId == 2).Source);  // ingot bought, not crafted
        Assert.Equal(MaterialSource.Buy, o.Materials.Single(m => m.ItemId == 5).Source);  // crystals bought, not gathered
        Assert.DoesNotContain(o.Materials, m => m.Depth > 0);                              // nothing crafted, so no sub-materials
        Assert.Equal(2 * 900 + 5 * 10, o.CashCost);
    }

    [Fact]
    public void PerMaterialOverrideWins()
    {
        var o = Calc(overrides: new() { [2] = MaterialSource.Buy }).Evaluate(Sword)!;
        Assert.Equal(MaterialSource.Buy, o.Materials.Single(m => m.ItemId == 2).Source);
        Assert.Equal(MaterialSource.Gather, o.Materials.Single(m => m.ItemId == 5).Source); // others follow the mode
    }

    [Fact]
    public void OverrideToUnavailableSourceFallsBackToMode()
    {
        var o = Calc(overrides: new() { [5] = MaterialSource.Craft }).Evaluate(Sword)!; // crystals have no recipe
        Assert.Equal(MaterialSource.Gather, o.Materials.Single(m => m.ItemId == 5).Source);
    }

    [Fact]
    public void ChoicesListEveryWayToGetAMaterial()
    {
        var ingot = Calc().Choices(2).Select(c => c.Source).ToHashSet();
        Assert.Equal(new HashSet<MaterialSource> { MaterialSource.Buy, MaterialSource.Craft }, ingot);
        var ore = Calc().Choices(3);
        Assert.Contains(ore, c => c.Source == MaterialSource.Gather && c.Cash == 0 && c.Value == 100);
    }

    [Fact]
    public void CostsIntermediatesByCheapestRoute()
    {
        // Crafting an ingot: 3 ore (gathered, worth 100 each) + 1 flux (vendor 20) = 320, far below buying at 900.
        var o = Calc().Evaluate(Sword)!;

        var ingot = o.Materials.Single(m => m.ItemId == 2);
        Assert.Equal(MaterialSource.Craft, ingot.Source);
        Assert.Equal(101u, ingot.RecipeId);
        Assert.Equal(20, ingot.UnitCash); // only the flux costs gil
        Assert.Equal(320, ingot.UnitValue);

        Assert.Contains(o.Materials, m => m.ItemId == 3 && m.Depth == 1 && m.AmountPerCraft == 6 && m.Source == MaterialSource.Gather);
        Assert.Contains(o.Materials, m => m.ItemId == 4 && m.Depth == 1 && m.Source == MaterialSource.Vendor);
    }

    [Fact]
    public void ComputesProfitAndDailyProfit()
    {
        var o = Calc().Evaluate(Sword)!;

        // Sells HQ at min(undercut 9,999, avg 9,000) = 9,000, nets 8,550 after 5% tax.
        Assert.True(o.SellHq);
        Assert.Equal(9_000u, o.SalePrice);
        // Materials at market value: 2 ingots x 320 + 5 crystals x 10 = 690. Cash: 2 x 20 = 40.
        Assert.Equal(690, o.MaterialValue);
        Assert.Equal(40, o.CashCost);
        Assert.Equal(8_550 - 690, o.ProfitPerCraft);
        Assert.Equal(8_550 - 40, o.CashProfitPerCraft);
        Assert.Equal((8_550 - 690) * 4, o.DailyProfit);
        Assert.Equal(8, o.SuggestedCrafts); // 4/day x 2 days
        Assert.Equal((8_550 - 690) * 8, o.BatchProfit);
    }

    [Fact]
    public void SuggestedCraftsCapAt99AndAccountForYield()
    {
        prices[1] = Price(1, hqMin: 10_000, hqAvg: 9_000, hqVel: 600);
        Assert.Equal(99, Calc().Evaluate(Sword)!.SuggestedCrafts);

        prices[1] = Price(1, hqMin: 10_000, hqAvg: 9_000, hqVel: 30);
        Assert.Equal(20, Calc().Evaluate(Sword with { Yield = 3 })!.SuggestedCrafts); // 60 units / 3 per craft
    }

    [Fact]
    public void GatheredMatsCanBeTreatedAsFree()
    {
        var o = Calc(new CraftSettings { ValueGatheredAtMarket = false }).Evaluate(Sword)!;
        Assert.Equal(o.CashProfitPerCraft, o.ProfitPerCraft);
    }

    [Fact]
    public void BuysWhenNotGathering()
    {
        var o = Calc(new CraftSettings { MaterialMode = MaterialMode.Cheapest, GatherWhenPossible = false }).Evaluate(Sword)!;

        Assert.Equal(MaterialSource.Buy, o.Materials.Single(m => m.ItemId == 5).Source);
        Assert.Equal(MaterialSource.Craft, o.Materials.Single(m => m.ItemId == 2).Source);
        Assert.Equal(690, o.CashCost); // everything costs gil now
    }

    [Fact]
    public void SkipsSlowSellers()
    {
        prices[1] = Price(1, hqMin: 10_000, hqAvg: 9_000, hqVel: 0.2);
        Assert.Null(Calc().Evaluate(Sword));
    }

    [Fact]
    public void UsesNqPricesWhenNotAssumingHq()
    {
        prices[1] = Price(1, nqMin: 5_000, nqAvg: 6_000, nqVel: 10, hqMin: 10_000, hqAvg: 9_000, hqVel: 4);
        var o = Calc(new CraftSettings { AssumeHq = false }).Evaluate(Sword)!;

        Assert.False(o.SellHq);
        Assert.Equal(4_999u, o.SalePrice);
        Assert.Equal(10, o.UnitsPerDay);
    }

    [Fact]
    public void FlagsMaterialsWithNoKnownSource()
    {
        items[6] = new(6, "Mystery Hide", 1, true, true, false, false, 0);
        var recipe = Sword with { Ingredients = [new(6, 1)] };

        var o = Calc().Evaluate(recipe)!;

        Assert.Equal(MaterialSource.Unknown, o.Materials.Single().Source);
        Assert.Contains("Mystery Hide", o.Warning);
    }

    [Fact]
    public void RespectsIntermediateDepthLimit()
    {
        var o = Calc(new CraftSettings { MaterialMode = MaterialMode.Cheapest, MaxIntermediateDepth = 0 }).Evaluate(Sword)!;
        Assert.Equal(MaterialSource.Buy, o.Materials.Single(m => m.ItemId == 2).Source);
    }

    [Fact]
    public void ParsesAggregatedResponse()
    {
        const string json = """
        { "results": [ { "itemId": 5057,
            "nq": { "minListing": { "world": { "price": 200 }, "dc": { "price": 50, "worldId": 73 } },
                    "averageSalePrice": { "world": { "price": 212.6 } },
                    "dailySaleVelocity": { "world": { "quantity": 7.29 } } },
            "hq": { "averageSalePrice": { "world": { "price": 208.7 } } } } ],
          "failedItems": [] }
        """;

        var p = UniversalisParser.ParseAggregated(json)[5057];
        Assert.Equal(200u, p.Nq.MinListing);
        Assert.Equal(50u, p.Nq.DcMinListing);
        Assert.Equal(7.29, p.Nq.UnitsPerDay, 2);
        Assert.Null(p.Hq.MinListing);
        Assert.Equal(0, p.Hq.UnitsPerDay);
        Assert.Equal(200u, p.CheapestListing);
    }

    [Fact]
    public async Task LiveAggregatedSmokeTest()
    {
        if (Environment.GetEnvironmentVariable("SELLWISE_LIVE") != "1") return;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SellWise-Tests/0.1");
        var json = await http.GetStringAsync("https://universalis.app/api/v2/aggregated/Gilgamesh/5057,5058,4");
        var parsed = UniversalisParser.ParseAggregated(json);
        Assert.Equal(3, parsed.Count);
        Assert.Contains(parsed.Values, p => p.Nq.UnitsPerDay > 0 && p.Nq.MinListing > 0);
        Console.WriteLine($"Live aggregated: iron ingot NQ min {parsed[5057].Nq.MinListing}, {parsed[5057].Nq.UnitsPerDay:0.#}/day");
    }
}
