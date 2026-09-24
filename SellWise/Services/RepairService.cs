using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc;
using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace SellWise.Services;

/// <summary>
/// Keeps gear above the durability threshold: self-repairs with Dark Matter when the character can,
/// otherwise teleports to a city with a mender, walks there with vnavmesh and repairs everything.
/// </summary>
public sealed unsafe class RepairService
{
    private const uint RepairAction = 6;
    private const uint DismountAction = 23;
    private const float InteractRange = 4f;

    private enum Step
    {
        Idle,
        Dismount,
        SelfOpen,
        Teleport,
        WaitForZone,
        Walk,
        Interact,
        Menu,
        RepairWindow,
        Confirm,
        WaitForRepair,
        Close,
    }

    private readonly Configuration config;
    private readonly CityTeleporter teleporter;
    private readonly ICallGateSubscriber<bool> navIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<object> pathStop;
    private Task<MenderDb>? menders;

    private Step step = Step.Idle;
    private Step afterDismount;
    private DateTime stepStarted;
    private DateTime nextTick;
    private Mender? target;
    private bool walkRequested;

    public RepairService(Configuration config, CityTeleporter teleporter)
    {
        this.config = config;
        this.teleporter = teleporter;
        var pi = Plugin.PluginInterface;
        navIsReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public bool IsBusy => step != Step.Idle;
    public string Status { get; private set; } = "";

    /// <summary>True once the last repair attempt ended in failure (cleared when a new one starts).</summary>
    public bool Failed { get; private set; }

    /// <summary>Lowest durability of any equipped item, 0–100.</summary>
    public static int MinCondition()
    {
        var equipped = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        if (equipped == null || !equipped->IsLoaded) return 100;

        var min = int.MaxValue;
        for (var i = 0; i < equipped->Size; i++)
        {
            var item = equipped->GetInventorySlot(i);
            if (item != null && item->ItemId != 0)
                min = Math.Min(min, item->Condition);
        }

        return min == int.MaxValue ? 100 : (int)Math.Ceiling(min / 300.0);
    }

    public bool NeedsRepair => config.AutoRepair && MinCondition() < config.RepairThreshold;

    /// <summary>
    /// Whether every equipped item under the threshold can be self-repaired: the right crafter is high enough
    /// (item level − 10) and Dark Matter of the required grade or better is in the bags.
    /// </summary>
    public bool CanSelfRepairAll()
    {
        var equipped = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        if (equipped == null) return false;

        var items = Plugin.DataManager.GetExcelSheet<Item>();
        var darkMatters = Plugin.DataManager.GetExcelSheet<ItemRepairResource>().Select(r => r.Item.RowId).Where(id => id != 0).ToList();
        for (var i = 0; i < equipped->Size; i++)
        {
            var slot = equipped->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0 || slot->Condition / 300 >= config.RepairThreshold) continue;
            if (items.GetRowOrDefault(slot->GetBaseItemId()) is not { } item || item.ClassJobRepair.RowId == 0) return false;

            var needed = item.ItemRepair.ValueNullable?.Item.RowId ?? uint.MaxValue;
            if (!darkMatters.Any(dm => dm >= needed && InventoryManager.Instance()->GetInventoryItemCount(dm) > 0)) return false;

            if (item.ClassJobRepair.ValueNullable is not { } job) return false;
            if (Plugin.PlayerState.GetClassJobLevel(job) < Math.Max(item.LevelEquip - 10, 1)) return false;
        }

        return true;
    }

    /// <summary>Starts a repair. Must be called on the framework thread.</summary>
    public void Start()
    {
        if (IsBusy) return;
        Failed = false;
        menders ??= Task.Run(() => MenderDb.Load(CityTeleporter.Cities.Select(c => c.TerritoryId)));

        if (config.AllowSelfRepair && CanSelfRepairAll())
        {
            Status = "Repairing with Dark Matter...";
            Go(Plugin.Condition[ConditionFlag.Mounted] ? Step.Dismount : Step.SelfOpen, Step.SelfOpen);
            return;
        }

        if (!config.AllowNpcRepair)
        {
            Fail("Gear needs repair, but self-repair isn't possible and mender trips are turned off.");
            return;
        }

        Status = "Looking for a mender...";
        target = null;
        Go(Step.Teleport);
    }

    public void Stop()
    {
        if (!IsBusy) return;
        StopWalking();
        step = Step.Idle;
        Status = "Repair cancelled.";
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
            Fail($"Repair timed out ({step}).");
            return;
        }

        try
        {
            Tick();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Repair step failed");
            Fail($"Repair failed: {e.Message}");
        }
    }

    private void Tick()
    {
        switch (step)
        {
            case Step.Dismount:
                if (!Plugin.Condition[ConditionFlag.Mounted])
                    Go(afterDismount);
                else if (Since() > TimeSpan.FromSeconds(1))
                    ActionManager.Instance()->UseAction(ActionType.GeneralAction, DismountAction);
                break;

            case Step.SelfOpen:
                if (AddonReady("Repair"))
                    Go(Step.RepairWindow);
                else if (Since() > TimeSpan.FromMilliseconds(500) && !Plugin.Condition[ConditionFlag.Occupied39])
                    ActionManager.Instance()->UseAction(ActionType.GeneralAction, RepairAction);
                break;

            case Step.Teleport:
                TickTeleport();
                break;

            case Step.WaitForZone:
                if (target != null && Plugin.ClientState.TerritoryType == target.TerritoryId && !Plugin.Condition[ConditionFlag.BetweenAreas]
                    && Plugin.ObjectTable.LocalPlayer != null && SafeNavReady())
                {
                    Status = $"Walking to {target.Name}...";
                    walkRequested = false;
                    Go(Step.Walk);
                }
                break;

            case Step.Walk:
                TickWalk();
                break;

            case Step.Interact:
                if (NearbyMender() is not { } npc)
                {
                    Fail("Couldn't find the mender once there.");
                    break;
                }
                if (Plugin.Condition[ConditionFlag.Mounted])
                {
                    Go(Step.Dismount, Step.Interact);
                    break;
                }
                TargetSystem.Instance()->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address);
                Go(Step.Menu);
                break;

            case Step.Menu:
                if (AddonReady("Repair"))
                {
                    Go(Step.RepairWindow);
                }
                else if (TryAddon("SelectIconString", out var menu) && NearbyMender() is { } talking
                         && menders!.Result.MenuIndexByNpc.TryGetValue(talking.BaseId, out var index))
                {
                    new AddonMaster.SelectIconString(menu).Entries[index].Select();
                }
                break;

            case Step.RepairWindow:
                if (!TryAddon("Repair", out var repair)) break;
                var master = new AddonMaster.Repair(repair);
                if (!master.Addon->RepairAllButton->IsEnabled)
                {
                    Status = "Nothing needs repairing.";
                    Go(Step.Close);
                    break;
                }
                master.RepairAll();
                Go(Step.Confirm);
                break;

            case Step.Confirm:
                if (TryAddon("SelectYesno", out var yesno))
                {
                    new AddonMaster.SelectYesno(yesno).Yes();
                    Go(Step.WaitForRepair);
                }
                break;

            case Step.WaitForRepair:
                if (!Plugin.Condition[ConditionFlag.Occupied39] && Since() > TimeSpan.FromSeconds(1.5))
                    Go(Step.Close);
                break;

            case Step.Close:
                if (TryAddon("Repair", out var window))
                {
                    window->Close(true);
                    break;
                }
                step = Step.Idle;
                Status = $"Repaired. Lowest durability now {MinCondition()}%.";
                break;
        }
    }

    private void TickTeleport()
    {
        if (menders is not { IsCompleted: true } load)
            return; // still reading city layouts

        // A mender already in this zone and in range of the object table? Just walk there.
        if (NearbyMender(maxDistance: 80f) is { } here)
        {
            target = new Mender(here.BaseId, here.Name.TextValue, Plugin.ClientState.TerritoryType, here.Position, 0);
            Status = $"Walking to {target.Name}...";
            walkRequested = false;
            Go(Step.Walk);
            return;
        }

        var db = load.Result;
        var options = teleporter.Available(config.DisabledTeleportCities)
            .Where(c => db.ByTerritory.ContainsKey(c.TerritoryId))
            .ToList();
        if (options.Count == 0)
        {
            Fail("No attuned city with a mender is enabled in settings.");
            return;
        }

        var city = options[Random.Shared.Next(options.Count)];
        var cityMenders = db.ByTerritory[city.TerritoryId];
        target = cityMenders[Random.Shared.Next(cityMenders.Count)];

        if (Plugin.ClientState.TerritoryType == city.TerritoryId)
        {
            Go(Step.WaitForZone);
            return;
        }

        var telepo = Telepo.Instance();
        if (telepo == null || !telepo->Teleport(city.AetheryteId, 0))
        {
            Fail($"The game refused the teleport to {city.Name}.");
            return;
        }

        Status = $"Teleporting to {city.Name} to repair...";
        Go(Step.WaitForZone);
    }

    private void TickWalk()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || target == null) return;

        if (Vector3.Distance(player.Position, target.Position) <= InteractRange && NearbyMender() != null)
        {
            StopWalking();
            Go(Step.Interact);
            return;
        }

        if (!walkRequested)
        {
            if (!BellNavigator.VnavmeshLoaded)
            {
                Fail("vnavmesh isn't loaded, so SellWise can't walk to the mender.");
                return;
            }
            walkRequested = moveCloseTo.InvokeFunc(target.Position, false, InteractRange - 1);
            if (!walkRequested) Fail("vnavmesh refused the path to the mender.");
            return;
        }

        if (!pathIsRunning.InvokeFunc() && !pathfindInProgress.InvokeFunc() && Since() > TimeSpan.FromSeconds(2))
            walkRequested = false; // stopped short; ask again
    }

    private IGameObject? NearbyMender(float maxDistance = InteractRange + 2)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || menders is not { IsCompletedSuccessfully: true } load) return null;
        return Plugin.ObjectTable
            .Where(o => o.ObjectKind == ObjectKind.EventNpc && load.Result.MenuIndexByNpc.ContainsKey(o.BaseId))
            .Where(o => target == null || target.NpcId == o.BaseId || maxDistance > InteractRange + 2)
            .Where(o => Vector3.Distance(o.Position, player.Position) <= maxDistance)
            .OrderBy(o => Vector3.Distance(o.Position, player.Position))
            .FirstOrDefault();
    }

    private static bool AddonReady(string name) => TryAddon(name, out _);

    private static bool TryAddon(string name, out AtkUnitBase* addon)
        => GenericHelpers.TryGetAddonByName(name, out addon) && GenericHelpers.IsAddonReady(addon);

    private bool SafeNavReady()
    {
        try { return navIsReady.InvokeFunc(); } catch { return false; }
    }

    private void StopWalking()
    {
        try { pathStop.InvokeAction(); } catch { /* vnavmesh not loaded */ }
        walkRequested = false;
    }

    private void Go(Step next, Step afterDismountStep = Step.Idle)
    {
        step = next;
        if (next == Step.Dismount) afterDismount = afterDismountStep;
        stepStarted = DateTime.UtcNow;
    }

    private TimeSpan Since() => DateTime.UtcNow - stepStarted;

    private static TimeSpan Timeout(Step s) => s switch
    {
        Step.Teleport => TimeSpan.FromSeconds(30),
        Step.WaitForZone => TimeSpan.FromSeconds(60),
        Step.Walk => TimeSpan.FromMinutes(3),
        _ => TimeSpan.FromSeconds(20),
    };

    private void Fail(string message)
    {
        StopWalking();
        step = Step.Idle;
        Failed = true;
        Status = message;
        Plugin.Log.Warning($"[Repair] {message}");
    }
}
