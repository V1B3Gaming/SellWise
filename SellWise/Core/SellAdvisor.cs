using System;
using System.Collections.Generic;
using System.Linq;

namespace SellWise.Core;

public enum Verdict
{
    /// <summary>List it now at the suggested price.</summary>
    List,
    /// <summary>Worth listing, but it will take a while to sell; use spare slots.</summary>
    ListSlow,
    /// <summary>The market has been dumped below recent sale prices; wait or list at the floor.</summary>
    Hold,
    /// <summary>An NPC vendor pays about as much as the market would.</summary>
    Vendor,
    /// <summary>No listings or sales history to price against.</summary>
    NoData,
    Untradable,
    NotMarketable,
    /// <summary>Your listing has been undercut.</summary>
    Relist,
    /// <summary>Your listing is priced well below what it could get.</summary>
    Raise,
    /// <summary>Your listing is competitive.</summary>
    ListingOk,
}

/// <summary>What the market looks like from the point of view of one seller of one item/quality.</summary>
public sealed record MarketEstimate(
    uint? FairPrice,
    int FairSampleSize,
    double UnitsPerDay,
    uint? LowestCompetitor,
    IReadOnlyList<MarketListing> Competitors);

public sealed class Recommendation
{
    public required ItemInfo Item { get; init; }
    public bool Hq { get; init; }
    public int Quantity { get; init; }
    public string Where { get; init; } = "";
    public Verdict Verdict { get; init; }
    public uint? SuggestedPrice { get; init; }

    /// <summary>Gil you receive after tax if everything sells at the suggested price.</summary>
    public long NetTotal { get; init; }

    public long VendorTotal { get; init; }
    public double EstDays { get; init; } = double.PositiveInfinity;
    public double UnitsPerDay { get; init; }
    public uint? FairPrice { get; init; }
    public int FairSampleSize { get; init; }
    public uint? LowestCompetitor { get; init; }
    public string Reason { get; init; } = "";

    /// <summary>Sort key: expected gil per day a listing slot is occupied.</summary>
    public double Priority { get; init; }

    /// <summary>Set for recommendations about an existing market listing.</summary>
    public OwnListing? Listing { get; init; }

    /// <summary>True if this is among the best items to fill your currently free market slots.</summary>
    public bool FitsFreeSlot { get; set; }

    public ItemMarketData? Market { get; init; }
}

public sealed class AdvicePlan
{
    public IReadOnlyList<Recommendation> Stacks { get; init; } = [];
    public IReadOnlyList<Recommendation> Listings { get; init; } = [];
    public static readonly AdvicePlan Empty = new();
}

public static class SellAdvisor
{
    private const int MinFallbackSales = 10;

    public static AdvicePlan BuildPlan(
        IEnumerable<OwnedStack> stacks,
        IEnumerable<OwnListing> listings,
        Func<uint, ItemInfo?> itemLookup,
        Func<uint, ItemMarketData?> marketLookup,
        IReadOnlySet<string> ownRetainers,
        ISet<uint> ignoredItems,
        int freeSlots,
        AdvisorSettings s,
        DateTimeOffset now)
    {
        var stackRecs = new List<Recommendation>();
        foreach (var group in stacks.Where(x => !ignoredItems.Contains(x.ItemId)).GroupBy(x => (x.ItemId, x.Hq)))
        {
            var item = itemLookup(group.Key.ItemId);
            if (item == null) continue;
            var qty = group.Sum(x => x.Quantity);
            stackRecs.Add(AdviseStack(item, group.Key.Hq, qty, DescribeWhere(group), marketLookup(item.Id), ownRetainers, s, now));
        }

        // Fill free market slots with the listings that earn the most gil per slot-day.
        foreach (var rec in stackRecs.Where(r => r.Verdict is Verdict.List or Verdict.ListSlow)
                                     .OrderByDescending(r => r.Priority)
                                     .Take(Math.Max(0, freeSlots)))
            rec.FitsFreeSlot = true;

        var listingRecs = new List<Recommendation>();
        foreach (var l in listings)
        {
            var item = itemLookup(l.ItemId);
            if (item == null) continue;
            listingRecs.Add(AdviseListing(item, l, marketLookup(item.Id), ownRetainers, s, now));
        }

        return new AdvicePlan { Stacks = stackRecs, Listings = listingRecs };
    }

    public static Recommendation AdviseStack(
        ItemInfo item, bool hq, int qty, string where, ItemMarketData? data,
        IReadOnlySet<string> ownRetainers, AdvisorSettings s, DateTimeOffset now)
    {
        var vendorUnit = VendorUnitPrice(item, hq);
        var vendorTotal = (long)vendorUnit * qty;

        Recommendation Make(Verdict v, string reason, uint? price = null, MarketEstimate? est = null, double days = double.PositiveInfinity)
        {
            var net = price is { } p ? (long)NetUnit(p, s) * qty : 0;
            return new Recommendation
            {
                Item = item, Hq = hq, Quantity = qty, Where = where, Verdict = v, Reason = reason,
                SuggestedPrice = price, NetTotal = net, VendorTotal = vendorTotal, EstDays = days,
                UnitsPerDay = est?.UnitsPerDay ?? 0, FairPrice = est?.FairPrice, FairSampleSize = est?.FairSampleSize ?? 0,
                LowestCompetitor = est?.LowestCompetitor, Market = data,
                Priority = v is Verdict.List or Verdict.ListSlow or Verdict.Hold ? net / Math.Max(1.0, days) : 0,
            };
        }

        if (!item.Tradable || item.Collectable)
            return Make(Verdict.Untradable, item.Collectable ? "Collectables can't go on the market board." : "Untradable item.");

        if (!item.Marketable)
            return vendorUnit > 0
                ? Make(Verdict.Vendor, $"Can't be sold on the market board; vendors pay {vendorUnit:N0} each.")
                : Make(Verdict.NotMarketable, "Can't be sold on the market board.");

        if (data == null || !data.HasData)
            return Make(Verdict.NoData, data == null
                ? "No market data yet. Press Refresh prices."
                : "Universalis has no listings or sales for this item on your world.");

        var est = Estimate(item, hq, data, ownRetainers, s, now);

        uint price;
        string reason;
        var verdict = Verdict.List;
        var floor = Floor(est, s);
        var legit = LegitCompetitors(est, floor, s);

        if (est.LowestCompetitor is { } lowest)
        {
            if (legit.Count == est.Competitors.Count)
            {
                price = Undercut(lowest, s);
                reason = $"Undercut the cheapest listing ({lowest:N0}). Recent sales: {Fair(est)}.";
            }
            else
            {
                // Listings dumped below the floor usually get bought out quickly; line up behind them at a sane price.
                verdict = Verdict.Hold;
                price = legit.Count > 0 ? Undercut(legit[0].UnitPrice, s) : est.FairPrice!.Value;
                reason = $"Someone dumped at {lowest:N0}, far below recent sales ({Fair(est)}). " +
                         $"Wait for it to clear, or list at {price:N0} to be next in line once it's gone.";
            }
        }
        else if (est.FairPrice is { } fair)
        {
            price = (uint)Math.Max(1, Math.Round(fair * s.NoCompetitionMarkup));
            reason = $"Nobody else is selling. Priced slightly above recent sales ({Fair(est)}).";
        }
        else
        {
            return Make(Verdict.NoData, "Not enough matching sales or listings to price this.", est: est);
        }

        var unitsAhead = est.Competitors.Where(c => c.UnitPrice <= price).Sum(c => c.Quantity);
        var days = est.UnitsPerDay > 0 ? (unitsAhead + qty) / est.UnitsPerDay : double.PositiveInfinity;
        var netTotal = (long)NetUnit(price, s) * qty;

        if (vendorTotal > 0 && netTotal <= vendorTotal * s.VendorMargin)
            return Make(Verdict.Vendor,
                $"Vendor pays {vendorTotal:N0}; the market would net about {netTotal:N0} after tax. Not worth a slot.",
                price, est, days);

        if (verdict == Verdict.List && days > s.SlowDays)
        {
            verdict = Verdict.ListSlow;
            reason += est.UnitsPerDay > 0
                ? $" Slow market: about {est.UnitsPerDay:0.##} sold per day, so this could take {days:0} days."
                : " No sales in the recent window, so it may take a long time.";
        }

        return Make(verdict, reason, price, est, days);
    }

    public static Recommendation AdviseListing(
        ItemInfo item, OwnListing listing, ItemMarketData? data,
        IReadOnlySet<string> ownRetainers, AdvisorSettings s, DateTimeOffset now)
    {
        Recommendation Make(Verdict v, string reason, uint? suggested, MarketEstimate? est)
            => new()
            {
                Item = item, Hq = listing.Hq, Quantity = listing.Quantity, Where = listing.RetainerName,
                Verdict = v, Reason = reason, SuggestedPrice = suggested, Listing = listing, Market = data,
                NetTotal = (long)NetUnit(suggested ?? listing.UnitPrice, s) * listing.Quantity,
                VendorTotal = (long)VendorUnitPrice(item, listing.Hq) * listing.Quantity,
                UnitsPerDay = est?.UnitsPerDay ?? 0, FairPrice = est?.FairPrice, FairSampleSize = est?.FairSampleSize ?? 0,
                LowestCompetitor = est?.LowestCompetitor,
                Priority = v == Verdict.Relist ? 2 : v == Verdict.Raise ? 1 : 0,
            };

        if (data == null || !data.HasData)
            return Make(Verdict.NoData, "No market data yet. Press Refresh prices.", null, null);

        var est = Estimate(item, listing.Hq, data, ownRetainers, s, now);
        var mine = listing.UnitPrice;
        var floor = Floor(est, s);
        var legit = LegitCompetitors(est, floor, s);
        var dumpNote = legit.Count < est.Competitors.Count
            ? $" Ignoring {est.Competitors.Count - legit.Count} dumped listing(s) from {est.LowestCompetitor:N0}, far below recent sales."
            : "";

        // Best price to ask: just under the cheapest sane competitor, or a bit over recent sales if there is none.
        uint? target = legit.Count > 0 ? Undercut(legit[0].UnitPrice, s)
            : est.FairPrice is { } fair ? (uint)Math.Round(fair * s.NoCompetitionMarkup)
            : null;

        if (legit.Count > 0 && legit[0].UnitPrice < mine)
            return Make(Verdict.Relist, $"Undercut: someone is selling at {legit[0].UnitPrice:N0}.{dumpNote}", target, est);

        if (target is { } t && mine < t * s.RaiseThreshold)
            return Make(Verdict.Raise, legit.Count > 0
                ? $"You're the cheapest by a wide margin. The next listing is {legit[0].UnitPrice:N0}.{dumpNote}"
                : $"No competition, and recent sales ({Fair(est)}) are well above your price.{dumpNote}", t, est);

        if (est.LowestCompetitor is { } lowest && lowest < mine)
            return Make(Verdict.ListingOk, $"Undercut at {lowest:N0}, but that's a dump far below recent sales ({Fair(est)}). Keeping your price.", null, est);

        return Make(Verdict.ListingOk, est.LowestCompetitor is { } next ? $"You're the cheapest. The next listing is {next:N0}." : "No competing listings.", null, est);
    }

    /// <summary>Lowest sane asking price, derived from what the item actually sells for.</summary>
    private static uint Floor(MarketEstimate est, AdvisorSettings s)
        => est.FairPrice is { } f ? (uint)Math.Ceiling(f * s.FloorFraction) : 0u;

    /// <summary>Competitors you'd undercut without dropping below the floor (cheapest first).</summary>
    private static List<MarketListing> LegitCompetitors(MarketEstimate est, uint floor, AdvisorSettings s)
        => est.Competitors.Where(c => floor == 0 || Undercut(c.UnitPrice, s) >= floor).ToList();

    /// <summary>Reads the market from the perspective of someone selling <paramref name="item"/> at the given quality.</summary>
    public static MarketEstimate Estimate(
        ItemInfo item, bool hq, ItemMarketData data, IReadOnlySet<string> ownRetainers, AdvisorSettings s, DateTimeOffset now)
    {
        // HQ sellers compete with HQ listings. NQ sellers compete with everything, since a cheaper HQ listing steals NQ buyers.
        var competitors = data.Listings
            .Where(l => !ownRetainers.Contains(l.RetainerName))
            .Where(l => !item.CanBeHq || !hq || l.Hq)
            .OrderBy(l => l.UnitPrice)
            .ToList();

        var matching = data.History.Where(h => !item.CanBeHq || h.Hq == hq).ToList();
        var windowStart = now - TimeSpan.FromDays(s.HistoryWindowDays);
        var inWindow = matching.Where(h => h.Time >= windowStart).ToList();

        // Fair price: median unit price of matching sales in the window (or the latest few if the window is sparse).
        var sample = inWindow.Count >= 3 ? inWindow : matching.Take(MinFallbackSales).ToList();
        uint? fair = sample.Count > 0 ? Median(sample.Select(h => h.UnitPrice)) : null;

        // Units sold per day. If the fetched history was truncated, measure only over the span it covers.
        double unitsPerDay = 0;
        if (inWindow.Count > 0)
        {
            var truncated = data.HistoryLimit > 0 && data.History.Count >= data.HistoryLimit;
            var span = truncated && data.History.Count > 0
                ? Math.Min(s.HistoryWindowDays, (now - data.History[^1].Time).TotalDays)
                : s.HistoryWindowDays;
            unitsPerDay = inWindow.Sum(h => h.Quantity) / Math.Max(0.5, span);
        }

        return new MarketEstimate(fair, sample.Count, unitsPerDay, competitors.Count > 0 ? competitors[0].UnitPrice : null, competitors);
    }

    public static uint Undercut(uint price, AdvisorSettings s)
    {
        var target = s.UndercutPercent > 0
            ? (long)Math.Floor(price * (1 - s.UndercutPercent / 100.0))
            : (long)price - s.UndercutGil;
        return (uint)Math.Clamp(target, 1, price);
    }

    public static uint NetUnit(uint price, AdvisorSettings s) => (uint)Math.Floor(price * (1 - s.TaxRate));

    /// <summary>NPC vendors pay about 10% more for HQ items.</summary>
    public static uint VendorUnitPrice(ItemInfo item, bool hq)
        => hq ? (uint)(((ulong)item.VendorPrice * 11 + 9) / 10) : item.VendorPrice;

    private static string Fair(MarketEstimate est)
        => est.FairPrice is { } f ? $"median {f:N0} over {est.FairSampleSize} sale{(est.FairSampleSize == 1 ? "" : "s")}" : "none";

    private static uint Median(IEnumerable<uint> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (uint)(((ulong)sorted[mid - 1] + sorted[mid]) / 2);
    }

    private static string DescribeWhere(IEnumerable<OwnedStack> stacks)
        => string.Join(", ", stacks
            .GroupBy(x => x.Source switch
            {
                StackSource.Retainer => x.RetainerName ?? "Retainer",
                StackSource.Saddlebag => "Chocobo bag",
                StackSource.Armory => "Armoury",
                _ => x.Source.ToString(),
            })
            .Select(g => $"{g.Key} {g.Sum(x => x.Quantity)}"));
}
