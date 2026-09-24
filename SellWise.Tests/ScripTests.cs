using SellWise.Core;
using Xunit;

namespace SellWise.Tests;

public class ScripTests
{
    // Rarefied Ginseng Earrings (CRP 90): 451/615/779 collectability for 107/117/128 purple scrips.
    private static readonly CollectableInfo Earrings = new(1, 0, 90, 91, ScripKind.PurpleCrafter, 451, 615, 779, 107, 117, 128);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(450, 0)]
    [InlineData(451, 107)]
    [InlineData(700, 117)]
    [InlineData(779, 128)]
    [InlineData(1200, 128)]
    public void PaysByTier(int collectability, int reward)
        => Assert.Equal(reward, Earrings.RewardAt(collectability));

    [Fact]
    public void TwoTierCollectablesPayTheMidRewardAtTheTop()
    {
        // Some collectables (the old custom deliveries style) have no high tier.
        var twoTier = Earrings with { HighCollectability = 0, HighReward = 0 };
        Assert.Equal(117, twoTier.RewardAt(2000));
    }

    [Theory]
    [InlineData(2, ScripKind.PurpleCrafter)]
    [InlineData(6, ScripKind.OrangeCrafter)]
    [InlineData(4, ScripKind.PurpleGatherer)]
    [InlineData(7, ScripKind.OrangeGatherer)]
    public void MapsGameCodes(int code, ScripKind kind) => Assert.Equal(kind, Scrips.FromCode(code));

    [Fact]
    public void UnknownCodesAreNotScrips() => Assert.Null(Scrips.FromCode(1));

    [Fact]
    public void ScripsPerHour() => Assert.Equal(128 * 60, ScripMath.PerHour(128, 60), 3);

    [Fact]
    public void NoTimeMeansNoRate() => Assert.Equal(0, ScripMath.PerHour(128, 0));

    [Fact]
    public void GilPerScrip() => Assert.Equal(10, ScripMath.GilPerScrip(1280, 128)!.Value, 3);

    [Fact]
    public void CraftsForRoundsUp()
    {
        Assert.Equal(4, ScripMath.CraftsFor(500, 128));
        Assert.Equal(0, ScripMath.CraftsFor(0, 128));
        Assert.Equal(int.MaxValue, ScripMath.CraftsFor(10, 0));
    }

    [Fact]
    public void CraftsBeforeCap()
    {
        Assert.Equal(7, ScripMath.CraftsBeforeCap(3000, 4000, 128)); // 7 × 128 = 896; an 8th would overflow
        Assert.Equal(0, ScripMath.CraftsBeforeCap(4000, 4000, 128));
    }

    [Fact]
    public void GoalsSubtractTheBalance()
    {
        Assert.Equal(1500, ScripMath.StillNeeded([(500, 1), (250, 4)], 0));
        Assert.Equal(0, ScripMath.StillNeeded([(500, 1)], 900));
    }
}
