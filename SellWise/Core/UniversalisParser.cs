using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SellWise.Core;

/// <summary>
/// Parses Universalis v2 market responses. Requests for a single item return the item object at the root,
/// requests for several items return <c>{ "items": { "id": {...} } }</c>; both shapes are handled.
/// </summary>
public static class UniversalisParser
{
    public static List<ItemMarketData> ParseWorld(string json, int historyLimit, DateTimeOffset fetchedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<ItemMarketData>();
        foreach (var item in Items(doc.RootElement))
        {
            var listings = new List<MarketListing>();
            if (item.TryGetProperty("listings", out var ls) && ls.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in ls.EnumerateArray())
                {
                    listings.Add(new MarketListing(
                        GetUInt(l, "pricePerUnit"),
                        (int)GetUInt(l, "quantity"),
                        GetBool(l, "hq"),
                        GetString(l, "retainerName") ?? "",
                        GetString(l, "worldName")));
                }
            }

            var history = new List<MarketSale>();
            if (item.TryGetProperty("recentHistory", out var hs) && hs.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hs.EnumerateArray())
                {
                    history.Add(new MarketSale(
                        GetUInt(h, "pricePerUnit"),
                        (int)GetUInt(h, "quantity"),
                        GetBool(h, "hq"),
                        DateTimeOffset.FromUnixTimeSeconds(GetLong(h, "timestamp"))));
                }
            }

            var lastUploadMs = GetLong(item, "lastUploadTime");
            result.Add(new ItemMarketData
            {
                ItemId = GetUInt(item, "itemID"),
                HasData = listings.Count > 0 || history.Count > 0,
                Listings = listings.OrderBy(l => l.UnitPrice).ToList(),
                History = history.OrderByDescending(h => h.Time).ToList(),
                HistoryLimit = historyLimit,
                LastUpload = lastUploadMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastUploadMs) : DateTimeOffset.MinValue,
                FetchedAt = fetchedAt,
            });
        }

        return result;
    }

    public static Dictionary<uint, DcSummary> ParseDataCenter(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<uint, DcSummary>();
        foreach (var item in Items(doc.RootElement))
        {
            string? worldNq = null, worldHq = null;
            if (item.TryGetProperty("listings", out var ls) && ls.ValueKind == JsonValueKind.Array)
            {
                // Listings are cheapest-first, so the first of each quality is the cheapest.
                foreach (var l in ls.EnumerateArray())
                {
                    var hq = GetBool(l, "hq");
                    if (hq && worldHq == null) worldHq = GetString(l, "worldName");
                    if (!hq && worldNq == null) worldNq = GetString(l, "worldName");
                }
            }

            result[GetUInt(item, "itemID")] = new DcSummary(
                NullIfZero(GetUInt(item, "minPriceNQ")),
                NullIfZero(GetUInt(item, "minPriceHQ")),
                worldNq,
                worldHq);
        }

        return result;
    }

    /// <summary>Parses <c>/api/v2/aggregated/{world}/{ids}</c>, which is cheap enough for scanning thousands of items.</summary>
    public static Dictionary<uint, AggregatedPrice> ParseAggregated(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<uint, AggregatedPrice>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var r in results.EnumerateArray())
        {
            var id = GetUInt(r, "itemId");
            if (id == 0) continue;
            result[id] = new AggregatedPrice(id, Quality(r, "nq"), Quality(r, "hq"));
        }

        return result;

        static QualityPrices Quality(JsonElement r, string name)
        {
            if (!r.TryGetProperty(name, out var q) || q.ValueKind != JsonValueKind.Object) return QualityPrices.None;
            var min = Path(q, "minListing", "world", "price");
            var avg = Path(q, "averageSalePrice", "world", "price");
            var vel = Path(q, "dailySaleVelocity", "world", "quantity");
            var dcMin = Path(q, "minListing", "dc", "price");
            return new QualityPrices(
                min is { } m ? (uint)Math.Round(m) : null,
                avg,
                vel ?? 0,
                dcMin is { } d ? (uint)Math.Round(d) : null);
        }

        static double? Path(JsonElement e, params string[] path)
        {
            foreach (var p in path)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
            }
            return e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in items.EnumerateObject())
                yield return p.Value;
        }
        else if (root.TryGetProperty("itemID", out _))
        {
            yield return root;
        }
    }

    private static uint? NullIfZero(uint v) => v == 0 ? null : v;

    private static uint GetUInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        return v.TryGetUInt32(out var u) ? u : (uint)Math.Clamp(v.GetDouble(), 0, uint.MaxValue);
    }

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    private static bool GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
