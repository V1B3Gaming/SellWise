namespace SellWise.Core;

public sealed class AdvisorSettings
{
    /// <summary>Gil to undercut the cheapest competing listing by.</summary>
    public int UndercutGil { get; set; } = 1;

    /// <summary>If &gt; 0, undercut by this percentage instead of a flat gil amount.</summary>
    public float UndercutPercent { get; set; } = 0f;

    /// <summary>
    /// Never suggest a price below this fraction of the recent median sale price.
    /// Protects against chasing a single dumped listing to the bottom.
    /// </summary>
    public float FloorFraction { get; set; } = 0.65f;

    /// <summary>Markup over the recent median when nobody else is selling the item.</summary>
    public float NoCompetitionMarkup { get; set; } = 1.10f;

    /// <summary>Market board sales tax taken from the seller.</summary>
    public float TaxRate { get; set; } = 0.05f;

    /// <summary>The market must pay at least this multiple of the vendor price to be worth a listing slot.</summary>
    public float VendorMargin { get; set; } = 1.25f;

    /// <summary>How far back sales are considered when judging the "fair" price and sale velocity.</summary>
    public int HistoryWindowDays { get; set; } = 14;

    /// <summary>Listings expected to take longer than this are marked slow.</summary>
    public float SlowDays { get; set; } = 14f;

    /// <summary>An existing listing priced below this fraction of what it could fetch is flagged "raise".</summary>
    public float RaiseThreshold { get; set; } = 0.85f;
}
