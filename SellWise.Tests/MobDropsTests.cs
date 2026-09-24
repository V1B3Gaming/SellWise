using SellWise.Core;
using Xunit;

namespace SellWise.Tests;

public class MobDropsTests
{
    [Fact]
    public void ReadsDropsFromAnItemPage()
    {
        const string json = """{"item":{"name":"Bat Wing","id":5551,"drops":[10370000000038,380000000364]},"partials":[]}""";
        Assert.Equal([10370000000038UL, 380000000364UL], GarlandData.ItemDrops(json));
    }

    [Fact]
    public void NoDropsIsEmpty()
        => Assert.Empty(GarlandData.ItemDrops("""{"item":{"name":"Ash Lumber","id":5364}}"""));

    [Fact]
    public void ReadsAMob()
    {
        const string json = """{"mob":{"name":"Basilisk","id":1730000000304,"coords":[22.46,24.6,21.95],"zoneid":46,"lvl":"49","drops":[5263,5306]}}""";
        var mob = GarlandData.Mob(json)!;
        Assert.Equal(304u, mob.NameId);
        Assert.Equal("Basilisk", mob.Name);
        Assert.Equal(49, mob.Level);
        Assert.Equal(46u, mob.PlaceNameId);
        Assert.Equal(22.46, mob.MapX, 3);
        Assert.Equal(24.6, mob.MapY, 3);
    }

    [Fact]
    public void LevelRangesUseTheLowEnd()
    {
        var mob = GarlandData.Mob("""{"mob":{"name":"x","id":10,"coords":[1,2],"zoneid":1,"lvl":"12 - 14"}}""")!;
        Assert.Equal(12, mob.Level);
    }

    [Fact]
    public void MobsWithoutALocationAreSkipped()
        => Assert.Null(GarlandData.Mob("""{"mob":{"name":"x","id":10,"lvl":"5"}}"""));

    [Theory]
    [InlineData(100, 0, 0f)]
    [InlineData(100, 0, 350f)]
    [InlineData(200, -448, -120f)]
    public void MapCoordinatesRoundTrip(ushort size, short offset, float world)
        => Assert.Equal(world, MapMath.ToWorld(MapMath.ToMap(world, size, offset), size, offset), 2);

    [Fact]
    public void CentreOfAFieldMap()
        => Assert.Equal(21.48, MapMath.ToMap(0, 100, 0), 2); // the middle of a 41×41 map
}
