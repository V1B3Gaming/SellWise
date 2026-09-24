using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace SellWise.Services;

/// <summary>
/// Gets you next to a quest giver: teleports to the zone's aetheryte, or for zones without one (Old Gridania, the
/// Steps of Thal, the Upper Decks...) teleports to the city's aetheryte and takes the aethernet with Lifestream, then
/// walks the rest with vnavmesh.
/// </summary>
public sealed unsafe class QuestTravel
{
    private const float ArriveRange = 5f;
    private const uint DismountAction = 23;
    private const uint MountRouletteAction = 9;
    private const float MountAbove = 60f;
    private const byte AethernetMarker = 4;
    private const byte AetheryteMarker = 3;

    private enum Step
    {
        Idle,
        LeaveCrafting,
        Teleport,
        WaitForCity,
        Aethernet,
        WaitForZone,
        Walk,
        Dismount,
    }

    /// <summary>An aethernet stop: its Aetheryte row, the PlaceName Lifestream takes, where it stands, and its city's aetheryte.</summary>
    private sealed record Shard(uint AetheryteId, uint PlaceNameId, uint TerritoryId, Vector2 Position, uint MainAetheryteId);

    private readonly ICallGateSubscriber<bool> navIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<uint, bool> lifestreamAethernet;
    private readonly ICallGateSubscriber<bool> lifestreamBusy;

    private Step step = Step.Idle;
    private DateTime stepStarted;
    private DateTime nextTick;
    private TravelTarget? target;
    private Shard? shard;
    private bool walkRequested;
    private Dictionary<uint, List<Shard>>? shardsByTerritory;
    private Dictionary<uint, Vector2> aetherytePositions = [];
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;
    private string fallback = "Crafting here instead.";
    private DateTime mountTried;

    public QuestTravel()
    {
        var pi = Plugin.PluginInterface;
        navIsReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        lifestreamAethernet = pi.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportByPlaceNameId");
        lifestreamBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        nearestPoint = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
    }

    public static bool LifestreamAvailable => Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == "Lifestream" && p.IsLoaded);

    public bool IsBusy => step != Step.Idle;
    public string Status { get; private set; } = "";

    /// <summary>The last trip ended short of the target (the caller carries on where you are).</summary>
    public bool Failed { get; private set; }

    public static bool IsNear(TravelTarget t, float range = ArriveRange + 3)
        => Plugin.ClientState.TerritoryType == t.TerritoryId && Plugin.ObjectTable.LocalPlayer is { } p && Distance(p.Position, t.Position) <= range;

    /// <summary>Distance that ignores height when the target's height isn't known yet (NaN).</summary>
    private static float Distance(Vector3 a, Vector3 b)
        => float.IsNaN(b.Y) ? Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z)) : Vector3.Distance(a, b);

    /// <summary>
    /// Must be called on the framework thread. <paramref name="whenStuck"/> finishes the message when the trip can't be
    /// made (what the caller does instead). A target with an unknown height (NaN Y) gets it from the navmesh on arrival.
    /// </summary>
    public void Start(TravelTarget t, string whenStuck = "Crafting here instead.")
    {
        fallback = whenStuck;
        target = t;
        shard = null;
        Failed = false;
        walkRequested = false;
        mountTried = default;
        if (IsNear(t))
        {
            Status = $"Next to {t.Name}.";
            step = Step.Idle;
            return;
        }
        Status = $"Heading to {t.Name}...";
        Go(Step.LeaveCrafting);
    }

    public void Stop()
    {
        if (!IsBusy) return;
        StopWalking();
        step = Step.Idle;
        Status = "Travel cancelled.";
    }

    /// <summary>Called from Framework.Update.</summary>
    public void Update()
    {
        if (step == Step.Idle) return;
        var now = DateTime.UtcNow;
        if (now < nextTick) return;
        nextTick = now.AddMilliseconds(250);

        if (now - stepStarted > Timeout(step))
        {
            Fail($"Couldn't get to {target?.Name} ({step} took too long).");
            return;
        }

        try
        {
            Tick();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Quest travel step failed");
            Fail($"Couldn't get to {target?.Name}: {e.Message}");
        }
    }

    private void Tick()
    {
        var t = target!;
        switch (step)
        {
            case Step.LeaveCrafting:
                // The crafting log blocks teleports and movement.
                if (Plugin.Condition[ConditionFlag.Crafting] || Plugin.Condition[ConditionFlag.PreparingToCraft] || Plugin.Condition[ConditionFlag.ExecutingCraftingAction])
                {
                    if (TryAddon("RecipeNote", out var log) && Since() > TimeSpan.FromSeconds(1)) log->Close(true);
                    break;
                }
                Go(Plugin.ClientState.TerritoryType == t.TerritoryId ? Step.Walk : Step.Teleport);
                break;

            case Step.Teleport:
                TickTeleport(t);
                break;

            case Step.WaitForCity:
            {
                var city = shard == null ? t.TerritoryId : AetheryteTerritory(shard.MainAetheryteId);
                if (Plugin.ClientState.TerritoryType != city || !Ready()) break;
                Go(shard == null ? Step.Walk : Step.Aethernet);
                break;
            }

            case Step.Aethernet:
                if (Since() < TimeSpan.FromSeconds(1.5) || SafeLifestreamBusy()) break;
                if (!lifestreamAethernet.InvokeFunc(shard!.PlaceNameId))
                {
                    Fail("Lifestream couldn't take the aethernet from here.");
                    break;
                }
                Status = $"Taking the aethernet towards {t.Name}...";
                Go(Step.WaitForZone);
                break;

            case Step.WaitForZone:
                if (Plugin.ClientState.TerritoryType == t.TerritoryId && Ready() && !SafeLifestreamBusy())
                {
                    walkRequested = false;
                    Go(Step.Walk);
                }
                break;

            case Step.Walk:
                TickWalk(t);
                break;

            case Step.Dismount:
                if (!Plugin.Condition[ConditionFlag.Mounted])
                {
                    step = Step.Idle;
                    Status = $"Next to {t.Name}.";
                }
                else if (Since() > TimeSpan.FromSeconds(1))
                {
                    ActionManager.Instance()->UseAction(ActionType.GeneralAction, DismountAction);
                }
                break;
        }
    }

    private void TickTeleport(TravelTarget t)
    {
        var ui = UIState.Instance();
        var aetherytes = Plugin.DataManager.GetExcelSheet<Aetheryte>();

        // The zone has its own aetheryte: go straight to the one nearest the target (field zones have several).
        Shards();
        var target2d = new Vector2(t.Position.X, t.Position.Z);
        var direct = aetherytes
            .Where(a => a.IsAetheryte && a.Territory.RowId == t.TerritoryId && ui->IsAetheryteUnlocked(a.RowId))
            .OrderBy(a => aetherytePositions.TryGetValue(a.RowId, out var p) ? Vector2.Distance(p, target2d) : float.MaxValue)
            .FirstOrDefault();
        if (direct.RowId != 0)
        {
            Teleport(direct.RowId, t);
            return;
        }

        // Otherwise: the city's aetheryte, then the aethernet stop nearest the quest giver.
        var stops = Shards().GetValueOrDefault(t.TerritoryId) ?? [];
        var nearest = stops.OrderBy(s => Vector2.Distance(s.Position, new Vector2(t.Position.X, t.Position.Z))).FirstOrDefault();
        if (nearest == null)
        {
            Fail($"SellWise doesn't know how to get to {t.Name}'s area. {fallback}");
            return;
        }
        if (!LifestreamAvailable)
        {
            Fail($"{t.Name} is off the aethernet; install Lifestream so SellWise can take it. {fallback}");
            return;
        }
        if (!ui->IsAetheryteUnlocked(nearest.MainAetheryteId))
        {
            Fail($"You haven't attuned to the aetheryte near {t.Name}. {fallback}");
            return;
        }
        shard = nearest;
        Teleport(nearest.MainAetheryteId, t);
    }

    private void Teleport(uint aetheryteId, TravelTarget t)
    {
        var telepo = Telepo.Instance();
        if (telepo == null || !telepo->Teleport(aetheryteId, 0))
        {
            Fail("The game refused the teleport.");
            return;
        }
        Status = $"Teleporting towards {t.Name}...";
        Go(Step.WaitForCity);
    }

    private void TickWalk(TravelTarget t)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        // Hunting spots come without a height: ask the navmesh for the ground there.
        if (float.IsNaN(t.Position.Y))
        {
            if (!SafeNavReady()) return;
            Vector3? ground = null;
            try { ground = nearestPoint.InvokeFunc(new Vector3(t.Position.X, player.Position.Y, t.Position.Z), 15, 1000); } catch { /* vnavmesh not loaded */ }
            if (ground is not { } g)
            {
                Fail($"vnavmesh couldn't find ground near {t.Name}. {fallback}");
                return;
            }
            target = t = t with { Position = g };
        }

        var distance = Vector3.Distance(player.Position, t.Position);
        if (distance <= ArriveRange)
        {
            StopWalking();
            Go(Step.Dismount);
            return;
        }
        if (!walkRequested)
        {
            if (!BellNavigator.VnavmeshLoaded)
            {
                Fail($"vnavmesh isn't loaded, so SellWise can't walk the last bit. {fallback}");
                return;
            }
            if (!SafeNavReady()) return;
            if (distance > MountAbove && !Plugin.Condition[ConditionFlag.Mounted] && CanMountHere())
            {
                // A long way: mount first (vnavmesh rides just the same). One try; walk if it doesn't work.
                if (mountTried == default)
                {
                    mountTried = DateTime.UtcNow;
                    ActionManager.Instance()->UseAction(ActionType.GeneralAction, MountRouletteAction);
                    Status = $"Mounting up for the ride to {t.Name}...";
                }
                if (DateTime.UtcNow - mountTried < TimeSpan.FromSeconds(4)) return;
            }
            Status = $"{(Plugin.Condition[ConditionFlag.Mounted] ? "Riding" : "Walking")} to {t.Name}...";
            walkRequested = moveCloseTo.InvokeFunc(t.Position, false, ArriveRange - 1);
            if (!walkRequested) Fail($"vnavmesh couldn't find a path to {t.Name}. {fallback}");
            return;
        }
        if (!pathIsRunning.InvokeFunc() && !pathfindInProgress.InvokeFunc() && Since() > TimeSpan.FromSeconds(2))
            walkRequested = false; // stopped short; ask again
    }

    /// <summary>Aethernet stops by territory, with positions from the map markers.</summary>
    private Dictionary<uint, List<Shard>> Shards()
    {
        if (shardsByTerritory != null) return shardsByTerritory;
        var data = Plugin.DataManager;
        var aetherytes = data.GetExcelSheet<Aetheryte>();
        var byPlace = aetherytes.Where(a => !a.IsAetheryte && a.AethernetName.RowId != 0).GroupBy(a => a.AethernetName.RowId).ToDictionary(g => g.Key, g => g.First());
        var mainByGroup = aetherytes.Where(a => a.IsAetheryte && a.AethernetGroup != 0).GroupBy(a => a.AethernetGroup).ToDictionary(g => g.Key, g => g.First().RowId);
        var markers = data.GetSubrowExcelSheet<MapMarker>();
        var result = new Dictionary<uint, List<Shard>>();

        var positions = new Dictionary<uint, Vector2>();
        foreach (var territory in data.GetExcelSheet<TerritoryType>())
        {
            if (territory.Map.ValueNullable is not { } map || map.SizeFactor == 0) continue;
            if (markers.GetRowOrDefault(map.MapMarkerRange) is not { } rows) continue;
            var scale = map.SizeFactor / 100f;
            foreach (var m in rows)
            {
                if (m.DataType == AetheryteMarker)
                    positions.TryAdd(m.DataKey.RowId, new Vector2((m.X - 1024f) / scale - map.OffsetX, (m.Y - 1024f) / scale - map.OffsetY));
                if (m.DataType != AethernetMarker || !byPlace.TryGetValue(m.DataKey.RowId, out var a) || a.Territory.RowId != territory.RowId) continue;
                if (!mainByGroup.TryGetValue(a.AethernetGroup, out var main)) continue;
                var pos = new Vector2((m.X - 1024f) / scale - map.OffsetX, (m.Y - 1024f) / scale - map.OffsetY);
                if (!result.TryGetValue(territory.RowId, out var list)) result[territory.RowId] = list = [];
                list.Add(new Shard(a.RowId, m.DataKey.RowId, territory.RowId, pos, main));
            }
        }
        aetherytePositions = positions;
        return shardsByTerritory = result;
    }

    private static bool CanMountHere()
        => Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Plugin.ClientState.TerritoryType)?.Mount == true
           && !Plugin.Condition[ConditionFlag.InCombat];

    private static uint AetheryteTerritory(uint aetheryteId)
        => Plugin.DataManager.GetExcelSheet<Aetheryte>().GetRowOrDefault(aetheryteId)?.Territory.RowId ?? 0;

    private bool Ready() => !Plugin.Condition[ConditionFlag.BetweenAreas] && !Plugin.Condition[ConditionFlag.BetweenAreas51] && Plugin.ObjectTable.LocalPlayer != null;

    private static bool TryAddon(string name, out AtkUnitBase* addon)
        => GenericHelpers.TryGetAddonByName(name, out addon) && GenericHelpers.IsAddonReady(addon);

    private bool SafeNavReady()
    {
        try { return navIsReady.InvokeFunc(); } catch { return false; }
    }

    private bool SafeLifestreamBusy()
    {
        try { return lifestreamBusy.InvokeFunc(); } catch { return false; }
    }

    private void StopWalking()
    {
        try { pathStop.InvokeAction(); } catch { /* vnavmesh not loaded */ }
        walkRequested = false;
    }

    private void Fail(string message)
    {
        StopWalking();
        Failed = true;
        Status = message;
        step = Step.Idle;
    }

    private void Go(Step next)
    {
        step = next;
        stepStarted = DateTime.UtcNow;
    }

    private TimeSpan Since() => DateTime.UtcNow - stepStarted;

    private static TimeSpan Timeout(Step s) => s switch
    {
        Step.LeaveCrafting => TimeSpan.FromSeconds(30),
        Step.WaitForCity or Step.WaitForZone => TimeSpan.FromSeconds(60),
        Step.Walk => TimeSpan.FromMinutes(3),
        _ => TimeSpan.FromSeconds(20),
    };
}
