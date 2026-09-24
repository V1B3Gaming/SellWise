using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using SellWise.Core;

namespace SellWise.Services;

public sealed class SavedStack
{
    public uint ItemId { get; set; }
    public bool Hq { get; set; }
    public int Quantity { get; set; }
    public StackSource Source { get; set; }
}

public sealed class SavedListing
{
    public uint ItemId { get; set; }
    public bool Hq { get; set; }
    public int Quantity { get; set; }
    public uint UnitPrice { get; set; }
    public int Slot { get; set; }
}

public sealed class RetainerSnapshot
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime? ScannedUtc { get; set; }
    public int MarketItemCount { get; set; }
    public uint Gil { get; set; }
    public List<SavedStack> Items { get; set; } = [];
    public List<SavedListing> Listings { get; set; } = [];
}

public sealed class CharacterData
{
    public string Name { get; set; } = "";
    public Dictionary<ulong, RetainerSnapshot> Retainers { get; set; } = [];
    public List<SavedStack> Saddlebag { get; set; } = [];
    public DateTime? SaddlebagScannedUtc { get; set; }
}

public sealed class InventoryStore
{
    public Dictionary<ulong, CharacterData> Characters { get; set; } = [];
}

/// <summary>
/// Reads the player's containers every second, and snapshots retainer containers while a retainer is open.
/// The game only keeps a retainer's inventory in memory while you're talking to it, so retainer and saddlebag
/// snapshots are saved to disk and reused until you open them again.
/// </summary>
public sealed unsafe class InventoryTracker : IDisposable
{
    public const int ListingSlotsPerRetainer = 20;

    private static readonly InventoryType[] BagTypes =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    private static readonly InventoryType[] SaddlebagTypes =
        [InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2];

    private static readonly InventoryType[] ArmoryTypes =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    private static readonly InventoryType[] RetainerPageTypes =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
    ];

    /// <summary>After switching retainers the old retainer's items can linger briefly; wait this long before trusting the containers.</summary>
    private static readonly TimeSpan RetainerSettleTime = TimeSpan.FromSeconds(1.5);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string storePath;
    private readonly InventoryStore store;
    private DateTime nextPoll = DateTime.MinValue;
    private DateTime lastSave = DateTime.MinValue;
    private bool dirty;

    private ulong activeRetainerId;
    private DateTime activeRetainerSince;

    private List<SavedStack> playerStacks = [];
    private int playerSignature;

    /// <summary>Increments whenever anything the advisor uses changes.</summary>
    public int Version { get; private set; }

    public ulong ContentId { get; private set; }

    /// <summary>Retainer count reported by the game this session, or null if the retainer list hasn't loaded yet.</summary>
    public int? KnownRetainerCount { get; private set; }

    public InventoryTracker()
    {
        storePath = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "inventory.json");
        store = Load(storePath);
    }

    public CharacterData? Character => ContentId != 0 && store.Characters.TryGetValue(ContentId, out var c) ? c : null;

    public IReadOnlyList<RetainerSnapshot> Retainers
        => Character?.Retainers.Values.OrderBy(r => r.Name).ToList() ?? [];

    public int FreeListingSlots
        => Retainers.Sum(r => Math.Max(0, ListingSlotsPerRetainer - r.MarketItemCount));

    public IReadOnlySet<string> RetainerNames
        => Retainers.Select(r => r.Name).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every stack the advisor should consider, filtered by the user's source settings.</summary>
    public List<OwnedStack> GetStacks(Configuration config)
    {
        var result = new List<OwnedStack>();
        foreach (var s in playerStacks)
        {
            if (s.Source == StackSource.Crystals && !config.IncludeCrystals) continue;
            if (s.Source == StackSource.Armory && !config.IncludeArmory) continue;
            result.Add(new OwnedStack(s.ItemId, s.Hq, s.Quantity, s.Source));
        }

        if (Character is not { } character)
            return result;

        if (config.IncludeSaddlebags)
            result.AddRange(character.Saddlebag.Select(s => new OwnedStack(s.ItemId, s.Hq, s.Quantity, StackSource.Saddlebag)));

        if (config.IncludeRetainers)
        {
            foreach (var r in character.Retainers.Values)
            {
                foreach (var s in r.Items)
                {
                    if (s.Source == StackSource.Crystals && !config.IncludeCrystals) continue;
                    result.Add(new OwnedStack(s.ItemId, s.Hq, s.Quantity, StackSource.Retainer, r.Name));
                }
            }
        }

        return result;
    }

    /// <summary>Units in your bags and crystal pouch (both qualities), i.e. what a crafting plugin can use right now.</summary>
    public int CountInBags(uint itemId)
        => playerStacks.Where(s => s.ItemId == itemId && s.Source is StackSource.Bag or StackSource.Crystals).Sum(s => s.Quantity);

    /// <summary>Units in your bags, only high quality ones when <paramref name="hqOnly"/>.</summary>
    public int CountInBags(uint itemId, bool hqOnly)
        => hqOnly ? playerStacks.Where(s => s.ItemId == itemId && s.Hq && s.Source == StackSource.Bag).Sum(s => s.Quantity) : CountInBags(itemId);

    /// <summary>Units held by your retainers as of their last scan.</summary>
    public int CountOnRetainers(uint itemId)
        => Character?.Retainers.Values.Sum(r => r.Items.Where(s => s.ItemId == itemId).Sum(s => s.Quantity)) ?? 0;

    public List<OwnListing> GetListings()
        => Character?.Retainers.Values
               .SelectMany(r => r.Listings.Select(l => new OwnListing(l.ItemId, l.Hq, l.Quantity, l.UnitPrice, r.Id, r.Name, l.Slot)))
               .ToList()
           ?? [];

    /// <summary>Called from Framework.Update.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextPoll) return;
        nextPoll = now.AddSeconds(1);

        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
            return;

        if (ContentId != Plugin.PlayerState.ContentId)
        {
            ContentId = Plugin.PlayerState.ContentId;
            KnownRetainerCount = null;
            playerSignature = 0;
            Version++;
        }

        if (!store.Characters.TryGetValue(ContentId, out var character))
            store.Characters[ContentId] = character = new CharacterData();
        character.Name = Plugin.PlayerState.CharacterName;

        var inventory = InventoryManager.Instance();
        if (inventory == null) return;

        ScanPlayer(inventory);
        ScanSaddlebag(inventory, character, now);
        ScanRetainers(inventory, character, now);

        if (dirty && now - lastSave > TimeSpan.FromSeconds(5))
            Save();
    }

    private void ScanPlayer(InventoryManager* inventory)
    {
        var stacks = new List<SavedStack>();
        Read(inventory, BagTypes, StackSource.Bag, stacks);
        Read(inventory, [InventoryType.Crystals], StackSource.Crystals, stacks);
        Read(inventory, ArmoryTypes, StackSource.Armory, stacks);

        var signature = Signature(stacks);
        if (signature == playerSignature) return;
        playerSignature = signature;
        playerStacks = stacks;
        Version++;
    }

    private void ScanSaddlebag(InventoryManager* inventory, CharacterData character, DateTime now)
    {
        var first = inventory->GetInventoryContainer(InventoryType.SaddleBag1);
        if (first == null || !first->IsLoaded) return;

        var stacks = new List<SavedStack>();
        Read(inventory, SaddlebagTypes, StackSource.Saddlebag, stacks);
        if (Signature(stacks) == Signature(character.Saddlebag)) return;

        character.Saddlebag = stacks;
        character.SaddlebagScannedUtc = now;
        MarkChanged();
    }

    private void ScanRetainers(InventoryManager* inventory, CharacterData character, DateTime now)
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return;

        // Keep the retainer roster (names, listing counts) current whenever the game has it loaded.
        var count = rm->GetRetainerCount();
        if (count > 0)
        {
            KnownRetainerCount = count;
            var seen = new HashSet<ulong>();
            for (uint i = 0; i < count; i++)
            {
                var r = rm->GetRetainerBySortedIndex(i);
                if (r == null || r->RetainerId == 0) continue;
                seen.Add(r->RetainerId);
                if (!character.Retainers.TryGetValue(r->RetainerId, out var snap))
                    character.Retainers[r->RetainerId] = snap = new RetainerSnapshot { Id = r->RetainerId };

                var name = r->NameString;
                if (snap.Name != name || snap.MarketItemCount != r->MarketItemCount || snap.Gil != r->Gil)
                {
                    snap.Name = name;
                    snap.MarketItemCount = r->MarketItemCount;
                    snap.Gil = r->Gil;
                    MarkChanged();
                }
            }

            foreach (var gone in character.Retainers.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                character.Retainers.Remove(gone);
                MarkChanged();
            }
        }

        if (!Plugin.Condition[ConditionFlag.OccupiedSummoningBell])
        {
            activeRetainerId = 0;
            return;
        }

        var active = rm->GetActiveRetainer();
        if (active == null || active->RetainerId == 0)
        {
            activeRetainerId = 0;
            return;
        }

        if (active->RetainerId != activeRetainerId)
        {
            activeRetainerId = active->RetainerId;
            activeRetainerSince = now;
            return;
        }

        if (now - activeRetainerSince < RetainerSettleTime) return;

        var page1 = inventory->GetInventoryContainer(InventoryType.RetainerPage1);
        var market = inventory->GetInventoryContainer(InventoryType.RetainerMarket);
        var itemsLoaded = page1 != null && page1->IsLoaded;
        var marketLoaded = market != null && market->IsLoaded;
        if (!itemsLoaded && !marketLoaded) return;

        if (!character.Retainers.TryGetValue(active->RetainerId, out var snapshot))
            character.Retainers[active->RetainerId] = snapshot = new RetainerSnapshot { Id = active->RetainerId, Name = active->NameString };

        // Refresh the scan time even when nothing changed, so the UI shows the data is current.
        snapshot.ScannedUtc = now;

        if (itemsLoaded)
        {
            var items = new List<SavedStack>();
            Read(inventory, RetainerPageTypes, StackSource.Retainer, items);
            Read(inventory, [InventoryType.RetainerCrystals], StackSource.Crystals, items);
            if (Signature(items) != Signature(snapshot.Items))
            {
                snapshot.Items = items;
                MarkChanged();
            }
        }

        if (marketLoaded)
        {
            var listings = new List<SavedListing>();
            for (var i = 0; i < market->Size; i++)
            {
                var slot = market->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                listings.Add(new SavedListing
                {
                    ItemId = slot->GetBaseItemId(),
                    Hq = slot->IsHighQuality(),
                    Quantity = slot->Quantity,
                    UnitPrice = (uint)inventory->GetRetainerMarketPrice((short)i),
                    Slot = i,
                });
            }

            var changed = listings.Count != snapshot.Listings.Count
                          || listings.Zip(snapshot.Listings).Any(p => p.First.ItemId != p.Second.ItemId || p.First.UnitPrice != p.Second.UnitPrice || p.First.Quantity != p.Second.Quantity);
            if (changed)
            {
                snapshot.Listings = listings;
                snapshot.MarketItemCount = listings.Count;
                MarkChanged();
            }
        }
    }

    private static void Read(InventoryManager* inventory, IEnumerable<InventoryType> types, StackSource source, List<SavedStack> into)
    {
        foreach (var type in types)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0) continue;
                if ((slot->Flags & InventoryItem.ItemFlags.Collectable) != 0) continue;
                into.Add(new SavedStack
                {
                    ItemId = slot->GetBaseItemId(),
                    Hq = slot->IsHighQuality(),
                    Quantity = slot->Quantity,
                    Source = source,
                });
            }
        }
    }

    private static int Signature(List<SavedStack> stacks)
    {
        var hash = new HashCode();
        hash.Add(stacks.Count);
        foreach (var s in stacks)
        {
            hash.Add(s.ItemId);
            hash.Add(s.Hq);
            hash.Add(s.Quantity);
            hash.Add(s.Source);
        }
        return hash.ToHashCode();
    }

    private void MarkChanged()
    {
        dirty = true;
        Version++;
    }

    public void ForgetRetainer(ulong id)
    {
        if (Character?.Retainers.Remove(id) == true)
            MarkChanged();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(storePath, JsonSerializer.Serialize(store, JsonOptions));
            dirty = false;
            lastSave = DateTime.UtcNow;
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to save inventory snapshot");
        }
    }

    private static InventoryStore Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<InventoryStore>(File.ReadAllText(path)) ?? new InventoryStore();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to load inventory snapshot; starting fresh");
        }

        return new InventoryStore();
    }

    public void Dispose()
    {
        if (dirty) Save();
    }
}
