using System;
using System.Collections.Generic;

namespace SellWise.Core;

/// <summary>Where an owned stack currently sits.</summary>
public enum StackSource
{
    Bag,
    Crystals,
    Saddlebag,
    Armory,
    Retainer,
}

/// <summary>A stack of items the character owns (not currently listed on the market board).</summary>
public sealed record OwnedStack(uint ItemId, bool Hq, int Quantity, StackSource Source, string? RetainerName = null);

/// <summary>An item one of the character's retainers currently has listed on the market board.</summary>
public sealed record OwnListing(uint ItemId, bool Hq, int Quantity, uint UnitPrice, ulong RetainerId, string RetainerName, int Slot);

/// <summary>Static item data pulled from the game sheets.</summary>
public sealed record ItemInfo(
    uint Id,
    string Name,
    uint VendorPrice,
    bool Marketable,
    bool Tradable,
    bool CanBeHq,
    bool Collectable,
    ushort Icon,
    uint VendorBuyPrice = 0);

public sealed record MarketListing(uint UnitPrice, int Quantity, bool Hq, string RetainerName, string? WorldName);

public sealed record MarketSale(uint UnitPrice, int Quantity, bool Hq, DateTimeOffset Time);

/// <summary>Cheapest listings across the whole data center (buyers can world-visit to buy).</summary>
public sealed record DcSummary(uint? MinNq, uint? MinHq, string? CheapestWorldNq, string? CheapestWorldHq);

/// <summary>Home-world market snapshot for one item, as returned by Universalis.</summary>
public sealed class ItemMarketData
{
    public required uint ItemId { get; init; }
    public bool HasData { get; init; }

    /// <summary>Current listings, cheapest first.</summary>
    public IReadOnlyList<MarketListing> Listings { get; init; } = [];

    /// <summary>Recent sales, newest first.</summary>
    public IReadOnlyList<MarketSale> History { get; init; } = [];

    /// <summary>How many history entries were requested; if we got that many the history is truncated.</summary>
    public int HistoryLimit { get; init; }

    public DateTimeOffset LastUpload { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public DcSummary? DataCenter { get; set; }
}
