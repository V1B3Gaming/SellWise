using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc;
using Lumina.Excel.Sheets;

namespace SellWise.Services;

/// <summary>Walks to the nearest summoning bell using vnavmesh's IPC.</summary>
public sealed class BellNavigator
{
    /// <summary>EObjName row for "Summoning Bell"; used to get the localized name.</summary>
    private const uint SummoningBellEObj = 2000401;

    private const float ArriveRange = 2.5f;
    private const float TargetRange = 5f;

    private readonly ICallGateSubscriber<bool> navIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly string bellName;

    private ulong? travellingTo;
    private DateTime travelStarted;

    public string Status { get; private set; } = "";

    public BellNavigator()
    {
        var pi = Plugin.PluginInterface;
        navIsReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");

        bellName = Plugin.DataManager.GetExcelSheet<EObjName>().GetRowOrDefault(SummoningBellEObj)?.Singular.ExtractText()
                   ?? "summoning bell";
    }

    public static bool VnavmeshLoaded
        => Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == "vnavmesh" && p.IsLoaded);

    public bool IsTravelling => travellingTo != null;

    public IGameObject? FindNearestBell()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;

        return Plugin.ObjectTable
            .Where(o => o != null && o.IsTargetable && string.Equals(o.Name.TextValue, bellName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => Vector3.DistanceSquared(o.Position, player.Position))
            .FirstOrDefault();
    }

    public void GoToNearestBell()
    {
        if (!VnavmeshLoaded)
        {
            Status = "vnavmesh isn't loaded.";
            return;
        }

        var bell = FindNearestBell();
        if (bell == null)
        {
            Status = "No summoning bell nearby. Teleport to a city, your house, or an inn first.";
            return;
        }

        try
        {
            if (!navIsReady.InvokeFunc())
            {
                Status = "vnavmesh is still building the navmesh for this zone. Try again in a moment.";
                return;
            }

            if (!moveCloseTo.InvokeFunc(bell.Position, false, ArriveRange))
            {
                Status = "vnavmesh refused the move request.";
                return;
            }

            travellingTo = bell.GameObjectId;
            travelStarted = DateTime.UtcNow;
            var dist = Vector3.Distance(Plugin.ObjectTable.LocalPlayer!.Position, bell.Position);
            Status = $"Walking to summoning bell ({dist:0} yalms)...";
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "vnavmesh IPC call failed");
            Status = "Couldn't talk to vnavmesh. Is it up to date?";
        }
    }

    public void Stop()
    {
        travellingTo = null;
        try { pathStop.InvokeAction(); } catch { /* vnavmesh not loaded */ }
        Status = "Stopped.";
    }

    /// <summary>Called from Framework.Update; targets the bell on arrival so you only need to press confirm.</summary>
    public void Update(bool targetOnArrival)
    {
        if (travellingTo is not { } id) return;
        if (DateTime.UtcNow - travelStarted < TimeSpan.FromSeconds(1)) return;

        bool moving;
        try
        {
            moving = pathIsRunning.InvokeFunc() || pathfindInProgress.InvokeFunc();
        }
        catch
        {
            travellingTo = null;
            return;
        }

        if (moving) return;
        travellingTo = null;

        var bell = Plugin.ObjectTable.SearchById(id);
        var player = Plugin.ObjectTable.LocalPlayer;
        if (bell == null || player == null) return;

        if (Vector3.Distance(bell.Position, player.Position) <= TargetRange)
        {
            if (targetOnArrival) Plugin.TargetManager.Target = bell;
            Status = "Arrived. Interact with the bell and open each retainer to update its snapshot.";
        }
        else
        {
            Status = "Stopped short of the bell. Try again or walk the rest of the way.";
        }
    }
}
