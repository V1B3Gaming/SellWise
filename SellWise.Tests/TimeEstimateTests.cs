using SellWise.Core;
using Xunit;

namespace SellWise.Tests;

public class TimeEstimateTests
{
    // Unix time 0 is Eorzea midnight: a whole number of 4200-second days.
    private static DateTimeOffset AtEorzea(int hour, int minute = 0)
        => DateTimeOffset.FromUnixTimeMilliseconds((long)((hour * 60 + minute) * EorzeaTime.RealSecondsPerEorzeaMinute * 1000) + 4200L * 1000 * 1000);

    [Fact]
    public void EorzeaClockRoundTrips()
    {
        Assert.Equal(9 * 60, EorzeaTime.MinuteOfDay(AtEorzea(9)));
        // From 09:00 to 12:00 ET is 3 ET hours = 3 × 175 real seconds.
        Assert.Equal(525, EorzeaTime.Until(12 * 60, AtEorzea(9)).TotalSeconds, 0);
        // Wraps round midnight: 23:00 -> 01:00 is 2 ET hours.
        Assert.Equal(350, EorzeaTime.Until(60, AtEorzea(23)).TotalSeconds, 0);
    }

    [Fact]
    public void RegularNodesAreVisitsTimesVisitTime()
    {
        var ore = new GatherInfo(5116, NodeKind.Regular, 50, []);
        var e = TimeEstimator.Gather(ore, 44, AtEorzea(0)); // 8 per visit -> 6 visits
        Assert.Equal(6, e.Visits);
        Assert.Equal(TimeEstimator.SecondsPerMaterial + 6 * TimeEstimator.SecondsPerVisit, e.Total.TotalSeconds, 0);
        Assert.False(e.Timed);
    }

    [Fact]
    public void NothingMissingTakesNoTime()
        => Assert.Equal(TimeSpan.Zero, TimeEstimator.Gather(new GatherInfo(1, NodeKind.Regular, 90, []), 0, AtEorzea(0)).Total);

    [Fact]
    public void CrystalsGatherFasterThanOre()
    {
        var shard = new GatherInfo(2, NodeKind.Regular, 20, []);
        Assert.Equal(2, TimeEstimator.Gather(shard, 60, AtEorzea(0)).Visits);
    }

    [Fact]
    public void TimedNodeWaitsForTheNextSpawn()
    {
        var sap = new GatherInfo(7590, NodeKind.Timed, 50, [new SpawnWindow(3 * 60, 180)]);
        var e = TimeEstimator.Gather(sap, 10, AtEorzea(1)); // spawns at 03:00, two ET hours away
        Assert.True(e.Timed);
        Assert.False(e.UpNow);
        Assert.Equal(350, e.NextSpawn!.Value.TotalSeconds, 0);
        Assert.True(e.Total.TotalSeconds >= 350 + TimeEstimator.SecondsPerVisit);
    }

    [Fact]
    public void TimedNodeUpNowHasNoWait()
    {
        var sap = new GatherInfo(7590, NodeKind.Timed, 50, [new SpawnWindow(3 * 60, 180)]);
        var e = TimeEstimator.Gather(sap, 10, AtEorzea(4));
        Assert.True(e.UpNow);
        Assert.Equal(TimeSpan.Zero, e.NextSpawn);
    }

    [Fact]
    public void ExtraTimedVisitsWaitForLaterWindows()
    {
        var node = new GatherInfo(1, NodeKind.Timed, 90, [new SpawnWindow(0, 120), new SpawnWindow(12 * 60, 120)]);
        var one = TimeEstimator.Gather(node, 10, AtEorzea(0, 30));
        var three = TimeEstimator.Gather(node, 30, AtEorzea(0, 30));
        // Two windows a day: each extra visit waits half an Eorzea day (35 real minutes).
        Assert.Equal(2 * 2100 + 2 * TimeEstimator.SecondsPerVisit, (three.Total - one.Total).TotalSeconds, 0);
    }

    [Fact]
    public void SpawnWindowWrapsPastMidnight()
    {
        var w = new SpawnWindow(22 * 60, 180); // 22:00 - 01:00
        Assert.True(w.Contains(23 * 60));
        Assert.True(w.Contains(30));
        Assert.False(w.Contains(2 * 60));
    }

    [Fact]
    public void CraftTimeScalesWithActions()
    {
        Assert.Equal(11 * (15 * 3 + 5), TimeEstimator.Craft(11).TotalSeconds);
        Assert.Equal(11 * (1 * 3 + 5), TimeEstimator.Craft(11, 1).TotalSeconds);
    }

    [Fact]
    public void FormatsDurations()
    {
        Assert.Equal("40s", TimeEstimator.Format(TimeSpan.FromSeconds(40)));
        Assert.Equal("12m", TimeEstimator.Format(TimeSpan.FromMinutes(12.2)));
        Assert.Equal("1h 5m", TimeEstimator.Format(TimeSpan.FromMinutes(65)));
    }
}
