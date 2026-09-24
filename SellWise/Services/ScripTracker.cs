using System;
using System.Collections.Generic;
using System.Linq;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>Whether you already have something the scrip exchange sells.</summary>
public enum Ownership
{
    /// <summary>A mount, minion, orchestrion roll, emote, hairstyle, card... that you've unlocked.</summary>
    Unlocked,
    /// <summary>An unlockable you've bought but not used yet: it's sitting in your inventory.</summary>
    NotUsed,
    /// <summary>An unlockable you don't have.</summary>
    Missing,
    /// <summary>Gear or furniture you have somewhere (bags, armoury, retainers, glamour dresser, armoire).</summary>
    Owned,
    /// <summary>Gear or furniture you don't have.</summary>
    NotOwned,
    /// <summary>Materia, materials and other things you buy again and again.</summary>
    Repeatable,
}

/// <summary>
/// Scrip balances and caps, what you own of the scrip exchange's stock, what you've bought there (watched while the
/// exchange is open), and what you're saving for.
/// </summary>
public sealed unsafe class ScripTracker
{
    private const string ExchangeAddon = "ShopExchangeCurrency";

    private readonly Configuration config;
    private readonly InventoryTracker tracker;
    private readonly Func<ScripDb?> scripDb;
    private DateTime nextPoll;
    private readonly Dictionary<ScripKind, int> lastBalance = [];
    private Dictionary<uint, int>? shopSnapshot;
    private readonly Dictionary<uint, Ownership> ownershipCache = [];
    private int ownershipVersion = -1;

    public ScripTracker(Configuration config, InventoryTracker tracker, Func<ScripDb?> scripDb)
    {
        this.config = config;
        this.tracker = tracker;
        this.scripDb = scripDb;
    }

    public static int Balance(ScripKind kind)
    {
        var cm = CurrencyManager.Instance();
        return cm == null ? 0 : (int)cm->GetItemCount(Scrips.ItemId(kind));
    }

    public static int Cap(ScripKind kind)
    {
        var cm = CurrencyManager.Instance();
        return cm == null ? 0 : (int)cm->GetItemMaxCount(Scrips.ItemId(kind));
    }

    public Dictionary<uint, int> Goals => PerCharacter(config.ScripGoals);
    public Dictionary<uint, int> Purchases => PerCharacter(config.ScripPurchases);

    public void SetGoal(uint itemId, int count)
    {
        if (count <= 0) Goals.Remove(itemId);
        else Goals[itemId] = count;
        config.Save();
    }

    /// <summary>Scrips still needed for your goals of this kind, after your current balance (things you own don't count).</summary>
    public int StillNeeded(ScripKind kind)
    {
        if (scripDb() is not { } db) return 0;
        var goals = Goals;
        var wanted = db.ShopItems
            .Where(i => i.Scrip == kind && goals.ContainsKey(i.ItemId) && Get(i.ItemId) is not (Ownership.Unlocked or Ownership.NotUsed or Ownership.Owned))
            .Select(i => (i.Cost, goals[i.ItemId]));
        return ScripMath.StillNeeded(wanted, Balance(kind));
    }

    public Ownership Get(uint itemId)
    {
        if (ownershipVersion != tracker.Version)
        {
            ownershipCache.Clear();
            ownershipVersion = tracker.Version;
        }
        if (ownershipCache.TryGetValue(itemId, out var cached)) return cached;
        var result = Compute(itemId);
        ownershipCache[itemId] = result;
        return result;
    }

    /// <summary>How many you hold across bags, armoury, what you're wearing, chocobo saddlebag and retainers.</summary>
    public int Held(uint itemId)
    {
        var im = InventoryManager.Instance();
        var equippedAndArmoury = im->GetInventoryItemCount(itemId, false, true, true) - im->GetInventoryItemCount(itemId);
        return tracker.CountInBags(itemId) + equippedAndArmoury + tracker.CountOnRetainers(itemId)
               + (tracker.Character?.Saddlebag.Where(s => s.ItemId == itemId).Sum(s => s.Quantity) ?? 0);
    }

    private Ownership Compute(uint itemId)
    {
        if (Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId) is not { } item) return Ownership.Repeatable;

        if (Plugin.UnlockState.IsItemUnlockable(item))
        {
            if (Plugin.UnlockState.IsItemUnlocked(item)) return Ownership.Unlocked;
            return tracker.CountInBags(itemId) > 0 ? Ownership.NotUsed : Ownership.Missing;
        }

        var keep = item.EquipSlotCategory.RowId != 0 || IsFurniture(item);
        if (!keep) return Ownership.Repeatable;
        return Held(itemId) > 0 || InGlamourDresser(itemId) || InArmoire(itemId) ? Ownership.Owned : Ownership.NotOwned;
    }

    /// <summary>Housing items (furnishings, tabletop, wall-mounted, outdoor...) share this filter group.</summary>
    private static bool IsFurniture(Item item) => item.FilterGroup == 14;

    private static bool InGlamourDresser(uint itemId)
    {
        var mirage = MirageManager.Instance();
        if (mirage == null || !mirage->PrismBoxLoaded) return false;
        foreach (var id in mirage->PrismBoxItemIds)
            if (id % 1_000_000 == itemId) return true;
        return false;
    }

    private static bool InArmoire(uint itemId)
    {
        var ui = UIState.Instance();
        if (ui == null || !ui->Cabinet.IsCabinetLoaded()) return false;
        foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>())
            if (row.Item.RowId == itemId) return ui->Cabinet.IsItemInCabinet(row.RowId);
        return false;
    }

    /// <summary>Called from Framework.Update: watches the scrip exchange to log what you buy there.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextPoll || tracker.ContentId == 0) return;
        nextPoll = now.AddMilliseconds(500);
        if (scripDb() is not { } db) return;

        var open = GenericHelpers.TryGetAddonByName<AtkUnitBase>(ExchangeAddon, out var addon) && addon->IsVisible;
        if (!open)
        {
            shopSnapshot = null;
            foreach (var kind in Scrips.Crafter) lastBalance[kind] = Balance(kind);
            return;
        }

        shopSnapshot ??= BagCounts(db);
        foreach (var kind in Scrips.Crafter)
        {
            var balance = Balance(kind);
            if (lastBalance.TryGetValue(kind, out var before) && balance < before)
                LogPurchases(db, kind);
            lastBalance[kind] = balance;
        }
    }

    private void LogPurchases(ScripDb db, ScripKind kind)
    {
        var after = BagCounts(db);
        var purchases = Purchases;
        foreach (var item in db.ShopItems.Where(i => i.Scrip == kind))
        {
            var gained = after.GetValueOrDefault(item.ItemId) - shopSnapshot!.GetValueOrDefault(item.ItemId);
            if (gained <= 0) continue;
            var times = Math.Max(1, gained / Math.Max(1, item.Count));
            purchases[item.ItemId] = purchases.GetValueOrDefault(item.ItemId) + times;
            Plugin.Log.Information($"[SellWise] Bought {item.ItemId} x{times} with {Scrips.Name(kind)}");
        }
        shopSnapshot = after;
        ownershipCache.Clear();
        config.Save();
    }

    private static Dictionary<uint, int> BagCounts(ScripDb db)
    {
        var im = InventoryManager.Instance();
        var counts = new Dictionary<uint, int>();
        foreach (var id in db.ShopItems.Select(i => i.ItemId).Distinct())
            counts[id] = im->GetInventoryItemCount(id) + im->GetInventoryItemCount(id, true);
        return counts;
    }

    private Dictionary<uint, int> PerCharacter(Dictionary<ulong, Dictionary<uint, int>> store)
    {
        if (!store.TryGetValue(tracker.ContentId, out var mine)) store[tracker.ContentId] = mine = [];
        return mine;
    }
}
