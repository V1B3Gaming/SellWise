using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SellWise.Core;

/// <summary>
/// A monster that drops an item, as listed by Garland Tools (community-gathered; the game data has no drop tables).
/// <paramref name="NameId"/> is the BNpcName row, which is what the game reports for the monster in front of you.
/// <paramref name="MapX"/>/<paramref name="MapY"/> are the in-game map coordinates of where it's found.
/// </summary>
public sealed record GarlandMob(ulong MobId, uint NameId, string Name, int Level, uint PlaceNameId, double MapX, double MapY);

public static class GarlandData
{
    /// <summary>Garland mob IDs are BNpcBase × 10¹⁰ + BNpcName.</summary>
    public static uint NameIdOf(ulong mobId) => (uint)(mobId % 10_000_000_000UL);

    /// <summary>The mob IDs an item's page lists under "drops" (empty when nothing drops it).</summary>
    public static List<ulong> ItemDrops(string itemJson)
    {
        using var doc = JsonDocument.Parse(itemJson);
        if (!doc.RootElement.TryGetProperty("item", out var item) || !item.TryGetProperty("drops", out var drops) || drops.ValueKind != JsonValueKind.Array)
            return [];
        return drops.EnumerateArray().Select(d => d.ValueKind == JsonValueKind.Number ? d.GetUInt64() : ulong.Parse(d.GetString()!, CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>A mob's page: name, level (the low end of a range), zone and map coordinates. Null when it has no location.</summary>
    public static GarlandMob? Mob(string mobJson)
    {
        using var doc = JsonDocument.Parse(mobJson);
        if (!doc.RootElement.TryGetProperty("mob", out var mob)) return null;
        if (!mob.TryGetProperty("zoneid", out var zone) || !mob.TryGetProperty("coords", out var coords) || coords.ValueKind != JsonValueKind.Array || coords.GetArrayLength() < 2)
            return null;

        var id = mob.GetProperty("id").ValueKind == JsonValueKind.Number ? mob.GetProperty("id").GetUInt64() : ulong.Parse(mob.GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
        var name = mob.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var level = 0;
        if (mob.TryGetProperty("lvl", out var lvl))
        {
            var text = lvl.ValueKind == JsonValueKind.Number ? lvl.GetInt32().ToString(CultureInfo.InvariantCulture) : lvl.GetString() ?? "";
            int.TryParse(text.Split('-')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out level);
        }
        var zoneId = zone.ValueKind == JsonValueKind.Number ? zone.GetUInt32() : uint.Parse(zone.GetString()!, CultureInfo.InvariantCulture);
        return new GarlandMob(id, NameIdOf(id), name, level, zoneId, coords[0].GetDouble(), coords[1].GetDouble());
    }
}

public static class MapMath
{
    /// <summary>
    /// Map coordinate (as shown on the in-game map) to world X or Z. Inverse of the game's
    /// map = 0.02·offset + 2048/sizeFactor + 0.02·world + 1.
    /// </summary>
    public static float ToWorld(double mapCoord, ushort sizeFactor, short offset)
        => (float)((mapCoord - 1.0 - 2048.0 / sizeFactor - 0.02 * offset) / 0.02);

    public static double ToMap(float world, ushort sizeFactor, short offset)
        => 0.02 * offset + 2048.0 / sizeFactor + 0.02 * world + 1.0;
}
