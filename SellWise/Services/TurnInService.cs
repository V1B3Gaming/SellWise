using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Ipc;
using ECommons;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>
/// Takes the crafter collectables in your bags to a collectable appraiser: teleports to Solution Nine or
/// Radz-at-Han (unless one's already nearby), walks over with vnavmesh, and turns each one in, job tab by job tab.
/// Stops before going over the scrip cap. Never buys anything.
/// </summary>
public sealed unsafe class TurnInService
{
    private const uint DismountAction = 23;
    private const float InteractRange = 4f;
    private const string ShopAddon = "CollectablesShop";

    // CollectablesShop callbacks (the same ones the game's buttons send).
    private const int SelectJobCallback = 14;
    private const int SelectItemCallback = 12;
    private const int SubmitCallback = 15;

    private enum Step
    {
        Idle,
        Teleport,
        WaitForZone,
        Walk,
        Interact,
        Open,
        SelectJob,
        SelectItem,
        Submit,
        AwaitSubmit,
        Close,
    }

    private readonly CityTeleporter teleporter;
    private readonly Func<ScripDb?> scripDb;
    private readonly ICallGateSubscriber<bool> navIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<object> pathStop;

    private Step step = Step.Idle;
    private DateTime stepStarted;
    private DateTime nextTick;
    private NpcSpot? target;
    private bool walkRequested;
    private int currentJob = -1;
    private (uint ItemId, int Before)? submitting;

    public TurnInService(CityTeleporter teleporter, Func<ScripDb?> scripDb)
    {
        this.teleporter = teleporter;
        this.scripDb = scripDb;
        var pi = Plugin.PluginInterface;
        navIsReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public bool IsBusy => step != Step.Idle;
    public string Status { get; private set; } = "";
    public bool Failed { get; private set; }

    /// <summary>Scrips earned on the current (or last) trip, by kind.</summary>
    public Dictionary<ScripKind, int> Earned { get; } = [];

    /// <summary>Fired when a trip ends, successfully or not.</summary>
    public event Action? Ended;

    /// <summary>Crafter collectables in your bags that an appraiser takes (item, job tab, count).</summary>
    public List<(CollectableInfo Info, int Count)> InBags()
    {
        var list = new List<(CollectableInfo, int)>();
        if (scripDb() is not { } db) return list;
        var counts = new Dictionary<uint, int>();
        var im = InventoryManager.Instance();
        foreach (var type in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var container = im->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || !slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable)) continue;
                if (db.Collectables.ContainsKey(slot->ItemId)) counts[slot->ItemId] = counts.GetValueOrDefault(slot->ItemId) + slot->Quantity;
            }
        }
        foreach (var (id, count) in counts) list.Add((db.Collectables[id], count));
        return list.OrderBy(x => x.Item1.JobIndex).ToList();
    }

    /// <summary>Starts a turn-in trip. Must be called on the framework thread.</summary>
    public void Start()
    {
        if (IsBusy) return;
        Failed = false;
        Earned.Clear();
        currentJob = -1;
        submitting = null;
        if (scripDb() == null)
        {
            Fail("Scrip data is still loading. Try again in a moment.");
            return;
        }
        if (InBags().Count == 0)
        {
            Status = "No crafter collectables in your bags to turn in.";
            Ended?.Invoke();
            return;
        }
        Status = "Heading to a collectable appraiser...";
        Go(ShopReady() ? Step.SelectJob : Step.Teleport);
    }

    public void Stop()
    {
        if (!IsBusy) return;
        StopWalking();
        step = Step.Idle;
        Status = "Turn-in cancelled.";
        Ended?.Invoke();
    }

    /// <summary>Called from Framework.Update.</summary>
    public void Update()
    {
        if (step == Step.Idle) return;
        var now = DateTime.UtcNow;
        if (now < nextTick) return;
        nextTick = now.AddMilliseconds(300);

        if (now - stepStarted > Timeout(step))
        {
            Fail($"Turn-in timed out ({step}).");
            return;
        }

        try
        {
            Tick();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Turn-in step failed");
            Fail($"Turn-in failed: {e.Message}");
        }
    }

    private void Tick()
    {
        switch (step)
        {
            case Step.Teleport:
                TickTeleport();
                break;

            case Step.WaitForZone:
                if (target != null && Plugin.ClientState.TerritoryType == target.TerritoryId && !Plugin.Condition[ConditionFlag.BetweenAreas]
                    && Plugin.ObjectTable.LocalPlayer != null && SafeNavReady())
                {
                    Status = "Walking to the collectable appraiser...";
                    walkRequested = false;
                    Go(Step.Walk);
                }
                break;

            case Step.Walk:
                TickWalk();
                break;

            case Step.Interact:
                if (Plugin.Condition[ConditionFlag.Mounted])
                {
                    if (Since() > TimeSpan.FromSeconds(1)) ActionManager.Instance()->UseAction(ActionType.GeneralAction, DismountAction);
                    break;
                }
                if (NearbyAppraiser() is not { } npc)
                {
                    Fail("Couldn't find the appraiser once there.");
                    break;
                }
                TargetSystem.Instance()->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address);
                Go(Step.Open);
                break;

            case Step.Open:
                if (ShopReady())
                {
                    Status = "Turning in...";
                    Go(Step.SelectJob);
                }
                else if (TryAddon("Talk", out var talk))
                    new AddonMaster.Talk(talk).Click();
                else if (TryAddon("SelectIconString", out var icons))
                    new AddonMaster.SelectIconString(icons).Entries[0].Select();
                else if (TryAddon("SelectString", out var strings))
                    new AddonMaster.SelectString(strings).Entries[0].Select();
                break;

            case Step.SelectJob:
            {
                if (!TryAddon(ShopAddon, out var shop))
                {
                    Fail("The collectables window closed.");
                    break;
                }
                var next = InBags().FirstOrDefault();
                if (next.Info == null)
                {
                    Go(Step.Close);
                    break;
                }
                if (currentJob != next.Info.JobIndex)
                {
                    Callback.Fire(shop, false, SelectJobCallback, (uint)next.Info.JobIndex);
                    currentJob = next.Info.JobIndex;
                    break; // give the list a moment to change
                }
                Go(Step.SelectItem);
                break;
            }

            case Step.SelectItem:
            {
                if (!TryAddon(ShopAddon, out var shop))
                {
                    Fail("The collectables window closed.");
                    break;
                }
                var next = InBags().FirstOrDefault(x => x.Info.JobIndex == currentJob);
                if (next.Info == null)
                {
                    Go(Step.SelectJob);
                    break;
                }
                var name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(next.Info.ItemId)?.Name.ExtractText() ?? "";
                var index = ItemIndex(shop, name);
                if (index < 0)
                {
                    if (Since() > TimeSpan.FromSeconds(3)) Fail($"{name} isn't in the appraiser's list.");
                    break;
                }
                Callback.Fire(shop, false, SelectItemCallback, (uint)index);
                submitting = (next.Info.ItemId, next.Count);
                Status = $"Turning in {name}...";
                Go(Step.Submit);
                break;
            }

            case Step.Submit:
                if (Since() < TimeSpan.FromMilliseconds(300)) break;
                if (!TryAddon(ShopAddon, out var submitShop))
                {
                    Fail("The collectables window closed.");
                    break;
                }
                Callback.Fire(submitShop, true, SubmitCallback, 0u);
                Go(Step.AwaitSubmit);
                break;

            case Step.AwaitSubmit:
                if (TryAddon("SelectYesno", out var yesno))
                {
                    // The game asks whether to go over the scrip cap. Say no and stop here.
                    new AddonMaster.SelectYesno(yesno).No();
                    Status = "Stopped: turning in more would go over the scrip cap. Spend some at the scrip exchange, then turn in the rest.";
                    Go(Step.Close);
                    break;
                }
                if (submitting is { } s)
                {
                    var now = InBags().FirstOrDefault(x => x.Info.ItemId == s.ItemId).Count;
                    if (now < s.Before)
                    {
                        if (scripDb()?.Collectables.GetValueOrDefault(s.ItemId) is { } info)
                            Earned[info.Scrip] = Earned.GetValueOrDefault(info.Scrip) + info.HighReward; // shown as an estimate
                        submitting = null;
                        Go(Step.SelectItem);
                        break;
                    }
                }
                if (Since() > TimeSpan.FromSeconds(4))
                    Fail("The appraiser didn't take the collectable. Is its collectability too low?");
                break;

            case Step.Close:
                if (TryAddon(ShopAddon, out var closing))
                {
                    Callback.Fire(closing, true, -1);
                    closing->Close(true);
                    break;
                }
                step = Step.Idle;
                if (!Status.StartsWith("Stopped"))
                    Status = Earned.Count > 0 ? "All turned in." : "Nothing left to turn in.";
                Ended?.Invoke();
                break;
        }
    }

    private void TickTeleport()
    {
        if (scripDb() is not { } db) return;

        // Straight after a craft job the crafting log can still be open, which blocks teleports.
        if (Plugin.Condition[ConditionFlag.Crafting] || Plugin.Condition[ConditionFlag.PreparingToCraft])
        {
            if (TryAddon("RecipeNote", out var log)) log->Close(true);
            Status = "Waiting for crafting to finish...";
            return;
        }

        if (NearbyAppraiser(maxDistance: 80f) is { } here)
        {
            target = new NpcSpot(here.BaseId, Plugin.ClientState.TerritoryType, here.Position);
            walkRequested = false;
            Status = "Walking to the collectable appraiser...";
            Go(Step.Walk);
            return;
        }

        var available = teleporter.Available(new HashSet<uint>()).ToDictionary(c => c.TerritoryId);
        var options = db.Appraisers.Where(a => available.ContainsKey(a.TerritoryId)).ToList();
        if (options.Count == 0)
        {
            Fail("You aren't attuned to Solution Nine or Radz-at-Han, where SellWise knows the appraisers.");
            return;
        }

        target = options.FirstOrDefault(a => a.TerritoryId == Plugin.ClientState.TerritoryType) ?? options[Random.Shared.Next(options.Count)];
        if (Plugin.ClientState.TerritoryType == target.TerritoryId)
        {
            Go(Step.WaitForZone);
            return;
        }

        var city = available[target.TerritoryId];
        var telepo = Telepo.Instance();
        if (telepo == null || !telepo->Teleport(city.AetheryteId, 0))
        {
            Fail($"The game refused the teleport to {city.Name}.");
            return;
        }
        Status = $"Teleporting to {city.Name} to turn in...";
        Go(Step.WaitForZone);
    }

    private void TickWalk()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || target == null) return;

        if (Vector3.Distance(player.Position, target.Position) <= InteractRange && NearbyAppraiser() != null)
        {
            StopWalking();
            Go(Step.Interact);
            return;
        }

        if (!walkRequested)
        {
            if (!BellNavigator.VnavmeshLoaded)
            {
                Fail("vnavmesh isn't loaded, so SellWise can't walk to the appraiser.");
                return;
            }
            walkRequested = moveCloseTo.InvokeFunc(target.Position, false, InteractRange - 1);
            if (!walkRequested) Fail("vnavmesh refused the path to the appraiser.");
            return;
        }

        if (!pathIsRunning.InvokeFunc() && !pathfindInProgress.InvokeFunc() && Since() > TimeSpan.FromSeconds(2))
            walkRequested = false; // stopped short; ask again
    }

    /// <summary>Position of an item in the current tab's list, counting only items (not the level group headers).</summary>
    private static int ItemIndex(AtkUnitBase* addon, string name)
    {
        if (name.Length == 0) return -1;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || (int)node->Type != 1028 || node->NodeId != 28) continue;
            var component = node->GetAsAtkComponentNode();
            if (component == null || component->Component == null) continue;

            var list = (AtkComponentTreeList*)component->Component;
            var index = 0;
            foreach (var pointer in list->Items)
            {
                var item = pointer.Value;
                if (item == null) continue;
#pragma warning disable CS0618 // the older header check is the one known to work with this window (GatherBuddy uses it too)
                var type = (AtkComponentTreeListItemType)((item->UIntValues.Count > 0 ? item->UIntValues[0] : 0) & 0xF);
                if (type is AtkComponentTreeListItemType.CollapsibleGroupHeader or AtkComponentTreeListItemType.GroupHeader) continue;
#pragma warning restore CS0618

                var label = item->StringValues.Count > 0 ? SeString.Parse(item->StringValues[0].Value).TextValue : "";
                if (label.Contains(name, StringComparison.OrdinalIgnoreCase)) return index;
                index++;
            }
        }
        return -1;
    }

    private IGameObject? NearbyAppraiser(float maxDistance = InteractRange + 2)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || scripDb() is not { } db) return null;
        return Plugin.ObjectTable
            .Where(o => o.ObjectKind == ObjectKind.EventNpc && db.AppraiserIds.Contains(o.BaseId))
            .Where(o => Vector3.Distance(o.Position, player.Position) <= maxDistance)
            .OrderBy(o => Vector3.Distance(o.Position, player.Position))
            .FirstOrDefault();
    }

    private static bool ShopReady() => TryAddon(ShopAddon, out _);

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

    private void Fail(string message)
    {
        StopWalking();
        Failed = true;
        Status = message;
        step = Step.Idle;
        Ended?.Invoke();
    }

    private void Go(Step next)
    {
        step = next;
        stepStarted = DateTime.UtcNow;
    }

    private TimeSpan Since() => DateTime.UtcNow - stepStarted;

    private static TimeSpan Timeout(Step s) => s switch
    {
        Step.Teleport => TimeSpan.FromSeconds(30),
        Step.WaitForZone => TimeSpan.FromSeconds(60),
        Step.Walk => TimeSpan.FromMinutes(3),
        Step.Open => TimeSpan.FromSeconds(15),
        _ => TimeSpan.FromSeconds(20),
    };
}
