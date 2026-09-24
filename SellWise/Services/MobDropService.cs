using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>Where to hunt a monster: its zone and roughly where it roams (height unknown until you're there).</summary>
public sealed record MobSpot(GarlandMob Mob, uint TerritoryId, string ZoneName, Vector2 Position);

/// <summary>
/// Which monsters drop an item and where they are, from Garland Tools (the game data has no drop tables). Looked up
/// once per item and kept on disk for a month. Only open-world spots are kept: no dungeons, trials or FATE-only mobs.
/// </summary>
public sealed class MobDropService : IDisposable
{
    private const string Endpoint = "https://www.garlandtools.org/db/doc";
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(30);
    private const uint OpenWorld = 1; // TerritoryIntendedUse

    private sealed class CacheEntry
    {
        public DateTime FetchedUtc { get; set; }
        public List<GarlandMob> Mobs { get; set; } = [];
    }

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim gate = new(2);
    private readonly string cachePath;
    private readonly object saveLock = new();
    private readonly ConcurrentDictionary<uint, CacheEntry> cache;
    private readonly ConcurrentDictionary<uint, Task> pending = new();
    private readonly Dictionary<uint, (DateTime Fetched, List<MobSpot> Spots)> resolved = [];
    private Dictionary<uint, (uint Territory, string Name, ushort Size, short OffsetX, short OffsetY)>? zones;

    public MobDropService(DirectoryInfo configDir)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SellWise/1.0 (FFXIV Dalamud plugin)");
        cachePath = Path.Combine(configDir.FullName, "mobdrops.json");
        cache = Load(cachePath);
    }

    /// <summary>
    /// Open-world spots for monsters that drop the item, easiest first. Null while it's being looked up (the first
    /// call starts the lookup); empty when nothing is known to drop it.
    /// </summary>
    public IReadOnlyList<MobSpot>? Spots(uint itemId)
    {
        if (cache.TryGetValue(itemId, out var entry) && DateTime.UtcNow - entry.FetchedUtc < CacheAge)
        {
            // Asked every frame by the materials list, so keep the resolved spots.
            if (resolved.TryGetValue(itemId, out var r) && r.Fetched == entry.FetchedUtc) return r.Spots;
            var spots = Resolve(entry.Mobs);
            resolved[itemId] = (entry.FetchedUtc, spots);
            return spots;
        }
        pending.GetOrAdd(itemId, id => Task.Run(() => Fetch(id)));
        return null;
    }

    private async Task Fetch(uint itemId)
    {
        await gate.WaitAsync();
        try
        {
            var itemJson = await http.GetStringAsync($"{Endpoint}/item/en/3/{itemId}.json");
            var mobs = new List<GarlandMob>();
            foreach (var mobId in GarlandData.ItemDrops(itemJson).Take(20))
            {
                try
                {
                    if (GarlandData.Mob(await http.GetStringAsync($"{Endpoint}/mob/en/2/{mobId}.json")) is { } mob) mobs.Add(mob);
                }
                catch (HttpRequestException)
                {
                    // One missing mob page shouldn't lose the rest.
                }
            }
            cache[itemId] = new CacheEntry { FetchedUtc = DateTime.UtcNow, Mobs = mobs };
            Save();
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            cache[itemId] = new CacheEntry { FetchedUtc = DateTime.UtcNow };
            Save();
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, $"Couldn't look up mob drops for item {itemId}");
        }
        finally
        {
            gate.Release();
            pending.TryRemove(itemId, out _);
        }
    }

    private List<MobSpot> Resolve(List<GarlandMob> mobs)
    {
        zones ??= BuildZones();
        var spots = new List<MobSpot>();
        foreach (var mob in mobs)
        {
            if (!zones.TryGetValue(mob.PlaceNameId, out var z)) continue;
            var pos = new Vector2(MapMath.ToWorld(mob.MapX, z.Size, z.OffsetX), MapMath.ToWorld(mob.MapY, z.Size, z.OffsetY));
            spots.Add(new MobSpot(mob, z.Territory, z.Name, pos));
        }
        return spots.OrderBy(s => s.Mob.Level).ToList();
    }

    /// <summary>Garland's zone numbers are map place names; keep the open-world ones.</summary>
    private static Dictionary<uint, (uint, string, ushort, short, short)> BuildZones()
    {
        var result = new Dictionary<uint, (uint, string, ushort, short, short)>();
        foreach (var map in Plugin.DataManager.GetExcelSheet<Map>())
        {
            if (map.TerritoryType.ValueNullable is not { } territory || territory.TerritoryIntendedUse.RowId != OpenWorld || map.SizeFactor == 0) continue;
            var entry = (territory.RowId, map.PlaceName.ValueNullable?.Name.ExtractText() ?? "", map.SizeFactor, map.OffsetX, map.OffsetY);
            result.TryAdd(map.PlaceName.RowId, entry);
            if (map.PlaceNameSub.RowId != 0) result.TryAdd(map.PlaceNameSub.RowId, entry);
        }
        return result;
    }

    private static ConcurrentDictionary<uint, CacheEntry> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<uint, CacheEntry>>(File.ReadAllText(path)) is { } saved)
                return new ConcurrentDictionary<uint, CacheEntry>(saved);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Couldn't read the mob drop cache; starting fresh");
        }
        return new ConcurrentDictionary<uint, CacheEntry>();
    }

    private void Save()
    {
        try
        {
            lock (saveLock) File.WriteAllText(cachePath, JsonSerializer.Serialize(cache.ToDictionary(k => k.Key, v => v.Value)));
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Couldn't save the mob drop cache");
        }
    }

    public void Dispose()
    {
        http.Dispose();
        gate.Dispose();
    }
}
