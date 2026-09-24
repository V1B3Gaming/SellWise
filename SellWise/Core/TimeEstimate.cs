using System;
using System.Collections.Generic;
using System.Linq;

namespace SellWise.Core;

public enum NodeKind
{
    /// <summary>Always up.</summary>
    Regular,
    /// <summary>Only up during certain Eorzea hours (unspoiled, legendary, ephemeral).</summary>
    Timed,
}

/// <summary>A spawn window in Eorzea minutes of the day.</summary>
public readonly record struct SpawnWindow(int StartMinute, int DurationMinutes)
{
    public bool Contains(int minuteOfDay)
    {
        var end = StartMinute + DurationMinutes;
        return minuteOfDay >= StartMinute && minuteOfDay < end
               || end > 1440 && minuteOfDay < end - 1440;
    }
}

/// <summary>How an item is gathered, for time estimates.</summary>
public sealed record GatherInfo(uint ItemId, NodeKind Kind, int Level, IReadOnlyList<SpawnWindow> Windows);

public static class EorzeaTime
{
    /// <summary>One Eorzea day is 70 real minutes.</summary>
    public const double RealSecondsPerEorzeaDay = 4200;
    public const double RealSecondsPerEorzeaMinute = RealSecondsPerEorzeaDay / 1440;

    public static int MinuteOfDay(DateTimeOffset now)
        => (int)(now.ToUnixTimeMilliseconds() / 1000.0 / RealSecondsPerEorzeaMinute % 1440);

    /// <summary>Real time until an Eorzea minute of the day comes round next (zero if it's now).</summary>
    public static TimeSpan Until(int minuteOfDay, DateTimeOffset now)
    {
        var current = now.ToUnixTimeMilliseconds() / 1000.0 / RealSecondsPerEorzeaMinute % 1440;
        var delta = (minuteOfDay - current + 1440) % 1440;
        return TimeSpan.FromSeconds(delta * RealSecondsPerEorzeaMinute);
    }
}

/// <summary>What a gathering estimate is made of, so the UI can explain it.</summary>
public sealed record GatherEstimate(TimeSpan Total, int Visits, bool Timed, TimeSpan? NextSpawn, bool UpNow);

/// <summary>
/// Rough time estimates for gathering and crafting with GatherBuddy Reborn / Artisan. Deliberately simple:
/// node visits × a typical visit time, plus waiting for timed nodes to spawn.
/// </summary>
public static class TimeEstimator
{
    /// <summary>Flying to the next node and gathering it out.</summary>
    public const double SecondsPerVisit = 40;

    /// <summary>Teleport, mount up and fly to the first node of a new material.</summary>
    public const double SecondsPerMaterial = 60;

    /// <summary>One crafting action (the game's animation plus a little plugin delay).</summary>
    public const double SecondsPerCraftAction = 3;

    /// <summary>Opening the recipe, starting the synth and finishing it.</summary>
    public const double SecondsPerCraftOverhead = 5;

    public const int DefaultCraftActions = 15;

    /// <summary>Units per node visit with a gathering rotation (GP skills) — conservative.</summary>
    public static int YieldPerVisit(GatherInfo g) => g.ItemId switch
    {
        >= 2 and <= 7 => 30,   // shards
        >= 8 and <= 13 => 15,  // crystals
        >= 14 and <= 19 => 6,  // clusters
        _ => g.Kind == NodeKind.Timed ? 10 : g.Level < 50 ? 5 : 8,
    };

    public static GatherEstimate Gather(GatherInfo g, int missing, DateTimeOffset now)
    {
        if (missing <= 0) return new GatherEstimate(TimeSpan.Zero, 0, g.Kind == NodeKind.Timed, null, false);

        var visits = (int)Math.Ceiling(missing / (double)YieldPerVisit(g));
        if (g.Kind == NodeKind.Regular || g.Windows.Count == 0)
            return new GatherEstimate(TimeSpan.FromSeconds(SecondsPerMaterial + visits * SecondsPerVisit), visits, false, null, false);

        // Timed: one visit per spawn window; wait for the next window, then one window per extra visit.
        var minute = EorzeaTime.MinuteOfDay(now);
        var upNow = g.Windows.Any(w => w.Contains(minute));
        var nextSpawn = upNow ? TimeSpan.Zero : g.Windows.Select(w => EorzeaTime.Until(w.StartMinute, now)).Min();
        var spacing = EorzeaTime.RealSecondsPerEorzeaDay / g.Windows.Count;
        var total = nextSpawn.TotalSeconds + SecondsPerMaterial + visits * SecondsPerVisit + (visits - 1) * spacing;
        return new GatherEstimate(TimeSpan.FromSeconds(total), visits, true, nextSpawn, upNow);
    }

    public static TimeSpan Craft(int crafts, int actionsPerCraft = DefaultCraftActions)
        => TimeSpan.FromSeconds(Math.Max(0, crafts) * (actionsPerCraft * SecondsPerCraftAction + SecondsPerCraftOverhead));

    /// <summary>"1h 5m", "12m", "40s".</summary>
    public static string Format(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))}s";
        if (t.TotalHours < 1) return $"{(int)Math.Round(t.TotalMinutes)}m";
        return $"{(int)t.TotalHours}h {t.Minutes}m";
    }
}
