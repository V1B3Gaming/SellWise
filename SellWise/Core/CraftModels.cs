using System;
using System.Collections.Generic;

namespace SellWise.Core;

public sealed record Ingredient(uint ItemId, int Amount);

public sealed record RecipeInfo(
    uint RecipeId,
    uint ResultItemId,
    int Yield,
    int CraftType,
    int Level,
    bool CanHq,
    bool IsExpert,
    bool IsSpecialist,
    uint SecretBookId,
    IReadOnlyList<Ingredient> Ingredients,
    uint QuestId = 0)
{
    public static readonly string[] JobAbbreviations = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"];

    public string Job => CraftType >= 0 && CraftType < JobAbbreviations.Length ? JobAbbreviations[CraftType] : "?";
}

/// <summary>Home-world price summary for one quality, from Universalis' aggregated endpoint.</summary>
public sealed record QualityPrices(uint? MinListing, double? AverageSale, double UnitsPerDay, uint? DcMinListing)
{
    public static readonly QualityPrices None = new(null, null, 0, null);
}

public sealed record AggregatedPrice(uint ItemId, QualityPrices Nq, QualityPrices Hq)
{
    /// <summary>Cheapest listing of either quality, i.e. what buying one would cost.</summary>
    public uint? CheapestListing
        => Nq.MinListing is { } n ? (Hq.MinListing is { } h ? System.Math.Min(n, h) : n) : Hq.MinListing;
}

public enum MaterialSource
{
    Gather,
    Vendor,
    Buy,
    Craft,
    Unknown,
}

public sealed class CraftSettings
{
    /// <summary>Price crafted results as HQ when the recipe can be HQ (the solvers in Artisan/Vulcan usually hit HQ).</summary>
    public bool AssumeHq { get; set; } = true;

    /// <summary>Gather materials yourself when they are gatherable, instead of buying them.</summary>
    public bool GatherWhenPossible { get; set; } = true;

    /// <summary>
    /// Count gathered materials at their market value when ranking (their opportunity cost: you could sell them instead).
    /// Off = gathered materials are free.
    /// </summary>
    public bool ValueGatheredAtMarket { get; set; } = true;

    public double MinUnitsPerDay { get; set; } = 1;
    public uint MinSalePrice { get; set; } = 500;

    /// <summary>Suggest crafting this many days of sales at a time, so you don't flood your own market.</summary>
    public float DaysOfSupply { get; set; } = 2;

    public bool IncludeExpert { get; set; }
    public bool IncludeSpecialist { get; set; }
    public int MaxIntermediateDepth { get; set; } = 3;
}

/// <summary>One material needed for a single craft of a recipe. Intermediates are followed by their own materials at Depth + 1.</summary>
public sealed record MaterialLine(
    uint ItemId,
    string Name,
    double AmountPerCraft,
    MaterialSource Source,
    double UnitCash,
    double UnitValue,
    int Depth,
    uint? RecipeId = null,
    int RecipeYield = 1);

public sealed class CraftOpportunity
{
    public required RecipeInfo Recipe { get; init; }
    public required ItemInfo Item { get; init; }
    public bool SellHq { get; init; }
    public uint SalePrice { get; init; }
    public double UnitsPerDay { get; init; }

    /// <summary>Material cost per craft with gathered items at market value.</summary>
    public double MaterialValue { get; init; }

    /// <summary>Gil you actually spend per craft (gathered items are free).</summary>
    public double CashCost { get; init; }

    public double ProfitPerCraft { get; init; }
    public double CashProfitPerCraft { get; init; }

    /// <summary>Profit per unit × units sold per day on your whole world (an upper bound: you won't capture every sale).</summary>
    public double DailyProfit { get; init; }

    /// <summary>Crafts the market can absorb in <see cref="CraftSettings.DaysOfSupply"/> days (1–99).</summary>
    public int SuggestedCrafts { get; init; }

    /// <summary>Profit from one batch of <see cref="SuggestedCrafts"/>: what making it actually earns you. The default ranking.</summary>
    public double BatchProfit => ProfitPerCraft * SuggestedCrafts;
    public IReadOnlyList<MaterialLine> Materials { get; init; } = [];
    public string? Warning { get; init; }

    /// <summary>Why you can't craft this yet (level, master book, quest), or null if you can.</summary>
    public string? LockedReason { get; set; }

    public bool Unlocked => LockedReason == null;
}

/// <summary>What stands between the character and a recipe.</summary>
public sealed class RecipeUnlocks
{
    public required int[] JobLevels { get; init; }
    public required IReadOnlySet<uint> UnlockedBooks { get; init; }
    public required IReadOnlySet<uint> CompletedQuests { get; init; }
    public Func<uint, string> BookName { get; init; } = id => $"master recipe book {id}";
    public Func<uint, string> QuestName { get; init; } = id => $"quest {id}";

    public bool IsUnlocked(RecipeInfo r) => Describe(r) == null;

    /// <summary>Null if the recipe can be crafted now; otherwise every reason it can't, joined.</summary>
    public string? Describe(RecipeInfo r)
    {
        var reasons = new List<string>();
        var level = r.CraftType >= 0 && r.CraftType < JobLevels.Length ? JobLevels[r.CraftType] : 0;
        if (level <= 0)
            reasons.Add($"{r.Job} isn't unlocked");
        else if (level < r.Level)
            reasons.Add($"Needs {r.Job} {r.Level} (you're {level})");

        if (r.SecretBookId != 0 && !UnlockedBooks.Contains(r.SecretBookId))
            reasons.Add($"Needs {BookName(r.SecretBookId)}");

        if (r.QuestId != 0 && !CompletedQuests.Contains(r.QuestId))
            reasons.Add($"Needs quest \"{QuestName(r.QuestId)}\"");

        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }
}
