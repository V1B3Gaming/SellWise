using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace SellWise.Services;

/// <summary>
/// Farms a material that only drops from monsters: switches to your best combat gearset, travels to where the
/// monster roams, then targets and walks up to one at a time while WrathCombo or RotationSolver does the fighting,
/// until you have enough. Stops if you're defeated, the monsters are too high a level, or none turn up.
/// </summary>
public sealed unsafe class MobHunter
{
    private const float SearchRadius = 80f;
    private const float MeleeRange = 3f;
    private const int MaxLevelAbove = 3;

    private enum Step
    {
        Idle,
        Gearset,
        Travel,
        Search,
        Approach,
        Fight,
        Loot,
    }

    private readonly QuestTravel travel;
    private readonly CombatAssist combat;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly HashSet<ulong> skipped = [];

    private Step step = Step.Idle;
    private DateTime stepStarted;
    private DateTime nextTick;
    private DateTime lastSeen;
    private DateTime lastPath;
    private IGameObject? mob;
    private Vector3 home;

    public MobHunter(QuestTravel travel, CombatAssist combat)
    {
        this.travel = travel;
        this.combat = combat;
        var pi = Plugin.PluginInterface;
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public bool IsBusy => step != Step.Idle;
    public bool Failed { get; private set; }
    public string Status { get; private set; } = "";
    public MobSpot? Spot { get; private set; }
    public uint ItemId { get; private set; }
    public string ItemName { get; private set; } = "";
    public int Wanted { get; private set; }
    public int Kills { get; private set; }

    /// <summary>Something to show (a hunt running, or the result of the last one) until dismissed.</summary>
    public bool HasResult { get; private set; }

    public int Have => ItemId == 0 ? 0 : InventoryManager.Instance()->GetInventoryItemCount(ItemId) + InventoryManager.Instance()->GetInventoryItemCount(ItemId, true);

    /// <summary>The easiest spot you can reach: an attuned zone, with monsters not far above your best combat level.</summary>
    public static (MobSpot? Spot, string? Problem) Choose(IReadOnlyList<MobSpot> spots)
    {
        if (spots.Count == 0) return (null, "Nothing in the open world is known to drop this.");
        var level = BestCombatGearset() is { } g ? g.Level : 0;
        var ui = UIState.Instance();
        var aetherytes = Plugin.DataManager.GetExcelSheet<Aetheryte>();
        var reachable = spots.Where(s => aetherytes.Any(a => a.IsAetheryte && a.Territory.RowId == s.TerritoryId && ui->IsAetheryteUnlocked(a.RowId))).ToList();
        if (reachable.Count == 0) return (null, $"You haven't attuned to an aetheryte where it drops ({string.Join(", ", spots.Select(s => s.ZoneName).Distinct())}).");
        var fair = reachable.Where(s => s.Mob.Level <= level + MaxLevelAbove).ToList();
        if (fair.Count == 0)
            return (null, level == 0 ? "You need a combat job gearset to hunt." : $"The monsters that drop it are level {reachable.Min(s => s.Mob.Level)}+; your best combat job is level {level}.");
        return (fair.OrderBy(s => s.Mob.Level).First(), null);
    }

    /// <summary>Must be called on the framework thread.</summary>
    public string? Start(uint itemId, string itemName, int more, MobSpot spot)
    {
        if (IsBusy) return "Already hunting.";
        if (!CombatAssist.Available) return "Hunting needs WrathCombo or RotationSolver Reborn to do the fighting.";
        if (!BellNavigator.VnavmeshLoaded) return "Hunting needs vnavmesh to move around.";

        ItemId = itemId;
        ItemName = itemName;
        Wanted = Have + Math.Max(1, more);
        Spot = spot;
        Kills = 0;
        Failed = false;
        HasResult = true;
        skipped.Clear();
        mob = null;
        home = new Vector3(spot.Position.X, float.NaN, spot.Position.Y);
        Status = "Getting ready...";
        Go(Step.Gearset);
        return null;
    }

    public void Stop()
    {
        if (!IsBusy) return;
        End("Hunt stopped.", failed: false);
    }

    public void Dismiss()
    {
        if (!IsBusy) HasResult = false;
    }

    /// <summary>Called from Framework.Update.</summary>
    public void Update()
    {
        if (step == Step.Idle) return;
        var now = DateTime.UtcNow;
        if (now < nextTick) return;
        nextTick = now.AddMilliseconds(250);

        try
        {
            Tick(now);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Hunt step failed");
            End($"Hunt failed: {e.Message}", failed: true);
        }
    }

    private void Tick(DateTime now)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;
        if (player.CurrentHp == 0 && step is not (Step.Gearset or Step.Travel))
        {
            End($"You were defeated after {Kills} kills. Hunt stopped.", failed: true);
            return;
        }

        switch (step)
        {
            case Step.Gearset:
                if (IsCombatJob(Plugin.PlayerState.ClassJob.RowId))
                {
                    if (Spot!.Mob.Level > Plugin.PlayerState.Level + MaxLevelAbove)
                    {
                        End($"{Spot.Mob.Name} is level {Spot.Mob.Level}; your current job is level {Plugin.PlayerState.Level}.", failed: true);
                        return;
                    }
                    var target = new TravelTarget(Spot.TerritoryId, home, 0, $"the {Spot.Mob.Name} in {Spot.ZoneName}");
                    travel.Start(target, "Hunt stopped.");
                    Go(Step.Travel);
                    break;
                }
                if (Since() < TimeSpan.FromSeconds(0.5)) break;
                if (BestCombatGearset() is not { } best)
                {
                    End("You need a combat job gearset to hunt.", failed: true);
                    return;
                }
                if (Since() > TimeSpan.FromSeconds(8))
                {
                    End("Couldn't switch to a combat job.", failed: true);
                    return;
                }
                RaptureGearsetModule.Instance()->EquipGearset(best.Index);
                Status = "Switching to a combat job...";
                nextTick = now.AddSeconds(2);
                break;

            case Step.Travel:
                if (travel.IsBusy)
                {
                    Status = travel.Status;
                    break;
                }
                if (travel.Failed && !QuestTravel.IsNear(new TravelTarget(Spot!.TerritoryId, home, 0, ""), SearchRadius))
                {
                    End(travel.Status, failed: true);
                    return;
                }
                home = player.Position;
                var error = combat.Enable();
                if (error != null)
                {
                    End(error, failed: true);
                    return;
                }
                lastSeen = now;
                Go(Step.Search);
                break;

            case Step.Search:
                if (Have >= Wanted)
                {
                    End($"Got {Wanted:N0} {ItemName} ({Kills} kills).", failed: false);
                    return;
                }
                mob = FindMob(player);
                if (mob != null)
                {
                    lastSeen = now;
                    Plugin.TargetManager.Target = mob;
                    Status = $"Going after a {Spot!.Mob.Name} ({Have:N0}/{Wanted:N0} {ItemName})";
                    RequestPath(mob.Position, now);
                    Go(Step.Approach);
                    break;
                }
                if (now - lastSeen > TimeSpan.FromMinutes(3))
                {
                    End($"No {Spot!.Mob.Name} turned up for three minutes. Hunt stopped.", failed: true);
                    return;
                }
                Status = $"Waiting for a {Spot!.Mob.Name} to appear ({Have:N0}/{Wanted:N0} {ItemName})";
                if (Vector3.Distance(player.Position, home) > 30 && now - lastPath > TimeSpan.FromSeconds(5)) RequestPath(home, now);
                break;

            case Step.Approach:
                if (!Alive(mob))
                {
                    Go(Step.Search);
                    break;
                }
                Plugin.TargetManager.Target = mob;
                if (Plugin.Condition[ConditionFlag.InCombat] || Vector3.Distance(player.Position, mob!.Position) <= MeleeRange)
                {
                    StopPath();
                    Go(Step.Fight);
                    break;
                }
                if (Since() > TimeSpan.FromSeconds(40))
                {
                    skipped.Add(mob.GameObjectId); // can't reach it; try another
                    StopPath();
                    Go(Step.Search);
                    break;
                }
                if (now - lastPath > TimeSpan.FromSeconds(2) && !SafePathRunning()) RequestPath(mob.Position, now);
                break;

            case Step.Fight:
                if (!Alive(mob))
                {
                    // Something else still hitting you? Deal with it before moving on.
                    if (Plugin.Condition[ConditionFlag.InCombat] && Attacker(player) is { } attacker)
                    {
                        mob = attacker;
                        Plugin.TargetManager.Target = attacker;
                        Go(Step.Fight);
                        break;
                    }
                    Kills++;
                    Go(Step.Loot);
                    break;
                }
                Plugin.TargetManager.Target = mob;
                Status = $"Fighting a {Spot!.Mob.Name} with {combat.Active} ({Have:N0}/{Wanted:N0} {ItemName})";
                // Melee jobs need to be close; the rotation plugin doesn't move you.
                if (Vector3.Distance(player.Position, mob!.Position) > MeleeRange + 1 && now - lastPath > TimeSpan.FromSeconds(1.5))
                    RequestPath(mob.Position, now);
                if (Since() > TimeSpan.FromSeconds(90))
                {
                    skipped.Add(mob.GameObjectId);
                    Go(Step.Search);
                }
                break;

            case Step.Loot:
                // Drops land in your bags a moment after the kill.
                if (Since() > TimeSpan.FromSeconds(1.5)) Go(Step.Search);
                break;
        }
    }

    /// <summary>The nearest living, unclaimed monster of the right kind near the hunting spot.</summary>
    private IGameObject? FindMob(IGameObject player)
    {
        var nameId = Spot!.Mob.NameId;
        return Plugin.ObjectTable
            .OfType<IBattleNpc>()
            .Where(b => b.NameId == nameId && b.BattleNpcKind == BattleNpcSubKind.Combatant && b.IsTargetable && b.CurrentHp > 0)
            .Where(b => !skipped.Contains(b.GameObjectId))
            .Where(b => Vector3.Distance(b.Position, home) <= SearchRadius || Vector3.Distance(b.Position, player.Position) <= 30)
            // Already fighting someone else: leave it to them.
            .Where(b => (b.StatusFlags & StatusFlags.InCombat) == 0 || b.TargetObjectId == player.GameObjectId)
            .OrderBy(b => Vector3.Distance(b.Position, player.Position))
            .FirstOrDefault();
    }

    private static IGameObject? Attacker(IGameObject player)
        => Plugin.ObjectTable.OfType<IBattleNpc>()
            .Where(b => b.BattleNpcKind == BattleNpcSubKind.Combatant && b.CurrentHp > 0 && b.TargetObjectId == player.GameObjectId)
            .OrderBy(b => Vector3.Distance(b.Position, player.Position))
            .FirstOrDefault();

    private static bool Alive(IGameObject? o) => o is IBattleNpc b && b.IsValid() && b.CurrentHp > 0;

    private static bool IsCombatJob(uint classJob) => classJob != 0 && classJob is not (>= 8 and <= 18);

    /// <summary>Your highest-level combat gearset.</summary>
    public static (byte Index, int Level)? BestCombatGearset()
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null) return null;
        var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
        (byte, int)? best = null;
        for (var i = 0; i < 100; i++)
        {
            var entry = module->GetGearset(i);
            if (entry == null || !entry->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists) || !IsCombatJob(entry->ClassJob)) continue;
            if (jobs.GetRowOrDefault(entry->ClassJob) is not { } job) continue;
            var level = Plugin.PlayerState.GetClassJobLevel(job);
            if (best == null || level > best.Value.Item2) best = ((byte)i, level);
        }
        return best;
    }

    private void RequestPath(Vector3 to, DateTime now)
    {
        lastPath = now;
        try { moveCloseTo.InvokeFunc(to, false, MeleeRange - 0.5f); } catch { /* vnavmesh not loaded */ }
    }

    private bool SafePathRunning()
    {
        try { return pathIsRunning.InvokeFunc(); } catch { return false; }
    }

    private void StopPath()
    {
        try { pathStop.InvokeAction(); } catch { /* vnavmesh not loaded */ }
    }

    private void End(string message, bool failed)
    {
        StopPath();
        travel.Stop();
        combat.Disable();
        Failed = failed;
        Status = message;
        step = Step.Idle;
        mob = null;
    }

    private void Go(Step next)
    {
        step = next;
        stepStarted = DateTime.UtcNow;
    }

    private TimeSpan Since() => DateTime.UtcNow - stepStarted;
}
