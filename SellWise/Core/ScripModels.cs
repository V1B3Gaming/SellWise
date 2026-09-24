using System;
using System.Collections.Generic;

namespace SellWise.Core;

public enum ScripKind
{
    PurpleCrafter,
    OrangeCrafter,
    PurpleGatherer,
    OrangeGatherer,
}

public static class Scrips
{
    public static readonly ScripKind[] Crafter = [ScripKind.PurpleCrafter, ScripKind.OrangeCrafter];

    /// <summary>The scrip's item ID (what the currency manager counts).</summary>
    public static uint ItemId(ScripKind kind) => kind switch
    {
        ScripKind.PurpleCrafter => 33913,
        ScripKind.OrangeCrafter => 41784,
        ScripKind.PurpleGatherer => 33914,
        _ => 41785,
    };

    /// <summary>
    /// The game's short code for a scrip, as used both by collectable rewards (CollectablesShopRewardScrip.Currency)
    /// and by scrip exchange prices (SpecialShop costs with UseCurrencyType 16).
    /// </summary>
    public static ScripKind? FromCode(int code) => code switch
    {
        2 => ScripKind.PurpleCrafter,
        6 => ScripKind.OrangeCrafter,
        4 => ScripKind.PurpleGatherer,
        7 => ScripKind.OrangeGatherer,
        _ => null,
    };

    public static string Name(ScripKind kind) => kind switch
    {
        ScripKind.PurpleCrafter => "Purple Crafters' Scrip",
        ScripKind.OrangeCrafter => "Orange Crafters' Scrip",
        ScripKind.PurpleGatherer => "Purple Gatherers' Scrip",
        _ => "Orange Gatherers' Scrip",
    };

    public static string Short(ScripKind kind) => kind is ScripKind.PurpleCrafter or ScripKind.PurpleGatherer ? "purple" : "orange";

    public static bool IsCrafter(ScripKind kind) => kind is ScripKind.PurpleCrafter or ScripKind.OrangeCrafter;
}

/// <summary>A collectable the appraiser takes, the collectability each tier needs, and the scrips each tier pays.</summary>
public sealed record CollectableInfo(
    uint ItemId,
    int JobIndex,
    int LevelMin,
    int LevelMax,
    ScripKind Scrip,
    int LowCollectability,
    int MidCollectability,
    int HighCollectability,
    int LowReward,
    int MidReward,
    int HighReward)
{
    /// <summary>Scrips paid for a piece with this collectability (0 below the lowest tier).</summary>
    public int RewardAt(int collectability)
        => collectability >= HighCollectability && HighCollectability > 0 ? HighReward
            : collectability >= MidCollectability && MidCollectability > 0 ? MidReward
            : collectability >= LowCollectability ? LowReward
            : 0;
}

/// <summary>Something the scrip exchange sells.</summary>
public sealed record ScripShopItem(uint ItemId, int Count, ScripKind Scrip, int Cost, string ShopName);

/// <summary>How a collectable recipe's top tier looks with your saved stats.</summary>
public enum TopTier
{
    /// <summary>Stats not saved yet, or still simulating.</summary>
    Unknown,
    Reaches,
    NeedsBuffs,
    Short,
}

/// <summary>A crafter collectable worth farming: what it pays, what it costs, and how fast it goes.</summary>
public sealed class ScripOption
{
    public required CollectableInfo Collectable { get; init; }
    public required CraftOpportunity Opportunity { get; init; }
    public TopTier TopTier { get; set; }

    /// <summary>Seconds per craft, from the planned rotation when there is one.</summary>
    public double SecondsPerCraft { get; set; }

    public int Reward => Collectable.HighReward * Math.Max(1, Opportunity.Recipe.Yield);
    public double ScripsPerHour => ScripMath.PerHour(Reward, SecondsPerCraft);

    /// <summary>What the materials for one craft are worth on the market, per scrip earned.</summary>
    public double? GilPerScrip => ScripMath.GilPerScrip(Opportunity.MaterialValue, Reward);
}

public static class ScripMath
{
    public static double PerHour(int reward, double secondsPerCraft)
        => secondsPerCraft > 0 ? reward * 3600 / secondsPerCraft : 0;

    public static double? GilPerScrip(double costPerCraft, int reward)
        => reward > 0 ? costPerCraft / reward : null;

    /// <summary>Crafts needed to earn <paramref name="scrips"/> at <paramref name="reward"/> each.</summary>
    public static int CraftsFor(int scrips, int reward)
        => scrips <= 0 ? 0 : reward <= 0 ? int.MaxValue : (int)Math.Ceiling(scrips / (double)reward);

    /// <summary>Most crafts you can turn in before the scrip cap stops you.</summary>
    public static int CraftsBeforeCap(int balance, int cap, int reward)
        => reward <= 0 || cap <= 0 ? int.MaxValue : Math.Max(0, (cap - balance) / reward);

    /// <summary>Scrips still needed for a set of goals (cost × how many), after what you already have.</summary>
    public static int StillNeeded(IEnumerable<(int Cost, int Count)> goals, int balance)
    {
        var total = 0;
        foreach (var (cost, count) in goals) total += cost * Math.Max(1, count);
        return Math.Max(0, total - balance);
    }
}
