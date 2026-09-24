using SellWise.Core;
using Xunit;

namespace SellWise.Tests;

public class UniversalisParserTests
{
    private const string Multi = """
    {
      "itemIDs": [5057, 4],
      "items": {
        "5057": {
          "itemID": 5057, "lastUploadTime": 1790180353949,
          "listings": [
            { "pricePerUnit": 250, "quantity": 2, "hq": true, "retainerName": "Alpha", "worldName": "Gilgamesh" },
            { "pricePerUnit": 200, "quantity": 5, "hq": false, "retainerName": "Beta" }
          ],
          "recentHistory": [
            { "pricePerUnit": 199, "quantity": 1, "hq": false, "timestamp": 1790170000 },
            { "pricePerUnit": 225, "quantity": 3, "hq": false, "timestamp": 1790178581 }
          ],
          "minPriceNQ": 200, "minPriceHQ": 250
        },
        "4": { "itemID": 4, "lastUploadTime": 0, "listings": [], "recentHistory": [] }
      },
      "unresolvedItems": []
    }
    """;

    [Fact]
    public void ParsesMultiItemResponse_SortingListingsAndHistory()
    {
        var result = UniversalisParser.ParseWorld(Multi, 40, DateTimeOffset.UnixEpoch).ToDictionary(d => d.ItemId);

        var ingot = result[5057];
        Assert.True(ingot.HasData);
        Assert.Equal([200u, 250u], ingot.Listings.Select(l => l.UnitPrice));
        Assert.Equal("Beta", ingot.Listings[0].RetainerName);
        Assert.True(ingot.Listings[1].Hq);
        Assert.Equal(225u, ingot.History[0].UnitPrice); // newest first
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790180353949), ingot.LastUpload);

        Assert.False(result[4].HasData);
    }

    [Fact]
    public void ParsesSingleItemResponse()
    {
        const string single = """{ "itemID": 5057, "listings": [ { "pricePerUnit": 10, "quantity": 1, "hq": false } ], "recentHistory": [] }""";

        var item = Assert.Single(UniversalisParser.ParseWorld(single, 40, DateTimeOffset.UnixEpoch));
        Assert.Equal(5057u, item.ItemId);
        Assert.Equal("", item.Listings[0].RetainerName);
    }

    [Fact]
    public void ParsesDataCenterSummary()
    {
        const string dc = """
        { "items": { "5057": { "itemID": 5057, "minPriceNQ": 180, "minPriceHQ": 0,
          "listings": [ { "pricePerUnit": 180, "hq": false, "worldName": "Jenova" } ] } } }
        """;

        var summary = UniversalisParser.ParseDataCenter(dc)[5057];
        Assert.Equal(180u, summary.MinNq);
        Assert.Null(summary.MinHq);
        Assert.Equal("Jenova", summary.CheapestWorldNq);
    }

    /// <summary>Hits the real API. Run with SELLWISE_LIVE=1.</summary>
    [Fact]
    public async Task LiveUniversalisSmokeTest()
    {
        if (Environment.GetEnvironmentVariable("SELLWISE_LIVE") != "1") return;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SellWise-Tests/0.1");
        var world = await http.GetStringAsync("https://universalis.app/api/v2/Gilgamesh/5057,5058,4?listings=20&entries=40");
        var parsed = UniversalisParser.ParseWorld(world, 40, DateTimeOffset.UtcNow);
        Assert.Equal(3, parsed.Count);
        Assert.Contains(parsed, d => d.HasData && d.Listings.Count > 0 && d.History.Count > 0);

        var dc = await http.GetStringAsync("https://universalis.app/api/v2/Aether/5057,5058?listings=5&entries=0");
        Assert.Equal(2, UniversalisParser.ParseDataCenter(dc).Count);

        // Run the whole advisor on real data to make sure nothing throws and prices are sane.
        var item = new ItemInfo(5057, "Iron Ingot", 7, true, true, true, false, 0);
        var data = parsed.Single(d => d.ItemId == 5057);
        var rec = SellAdvisor.AdviseStack(item, false, 10, "Bag 10", data, new HashSet<string>(), new AdvisorSettings(), DateTimeOffset.UtcNow);
        Assert.NotEqual(Verdict.Untradable, rec.Verdict);
        Console.WriteLine($"Live: {rec.Verdict} at {rec.SuggestedPrice} (lowest {rec.LowestCompetitor}, median {rec.FairPrice}, {rec.UnitsPerDay:0.##}/day) - {rec.Reason}");
    }
}
