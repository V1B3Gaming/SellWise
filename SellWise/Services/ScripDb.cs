using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>
/// Collectables the appraisers take (with their tiers and scrip rewards), where the appraisers stand, and what the
/// crafter scrip exchanges sell. Read once from the game data.
/// </summary>
public sealed class ScripDb
{
    /// <summary>ENpcData function row for a collectable appraiser.</summary>
    private const uint AppraiserFunction = 721585;

    /// <summary>SpecialShop currency type used by the scrip exchanges (costs are scrip codes, see <see cref="Scrips.FromCode"/>).</summary>
    private const byte ScripCurrencyType = 16;

    /// <summary>Crafter collectables by item ID. Job index = CraftType (0 CRP ... 7 CUL), same as the turn-in window's tabs.</summary>
    public IReadOnlyDictionary<uint, CollectableInfo> Collectables { get; }

    public IReadOnlyList<NpcSpot> Appraisers { get; }
    public IReadOnlySet<uint> AppraiserIds { get; }

    /// <summary>Everything the crafter scrip exchanges sell, one entry per item and scrip.</summary>
    public IReadOnlyList<ScripShopItem> ShopItems { get; }

    private ScripDb(Dictionary<uint, CollectableInfo> collectables, List<NpcSpot> appraisers, HashSet<uint> appraiserIds, List<ScripShopItem> shopItems)
    {
        Collectables = collectables;
        Appraisers = appraisers;
        AppraiserIds = appraiserIds;
        ShopItems = shopItems;
    }

    public static ScripDb Load(IEnumerable<uint> cityTerritories)
    {
        var data = Plugin.DataManager;

        var collectables = new Dictionary<uint, CollectableInfo>();
        var groups = data.GetSubrowExcelSheet<CollectablesShopItem>();
        foreach (var shop in data.GetExcelSheet<CollectablesShop>())
        {
            // Tabs 0-7 are the crafters; 8+ are gatherers and fishing.
            for (var job = 0; job < 8 && job < shop.ShopItems.Count; job++)
            {
                if (groups.GetRowOrDefault(shop.ShopItems[job].RowId) is not { } group) continue;
                foreach (var e in group)
                {
                    if (e.Item.RowId == 0 || collectables.ContainsKey(e.Item.RowId)) continue;
                    if (e.CollectablesShopRewardScrip.ValueNullable is not { } reward || Scrips.FromCode(reward.Currency) is not { } scrip) continue;
                    if (!Scrips.IsCrafter(scrip) || e.CollectablesShopRefine.ValueNullable is not { } tiers) continue;
                    collectables[e.Item.RowId] = new CollectableInfo(e.Item.RowId, job, e.LevelMin, e.LevelMax, scrip,
                        tiers.LowCollectability, tiers.MidCollectability, tiers.HighCollectability,
                        reward.LowReward, reward.MidReward, reward.HighReward);
                }
            }
        }

        var appraiserIds = data.GetExcelSheet<ENpcBase>()
            .Where(n => n.ENpcData.Any(d => d.RowId == AppraiserFunction))
            .Select(n => n.RowId)
            .ToHashSet();
        var appraisers = NpcLocations.Find(cityTerritories, appraiserIds);

        var shopItems = new Dictionary<(uint, ScripKind), ScripShopItem>();
        foreach (var shop in data.GetExcelSheet<SpecialShop>())
        {
            if (shop.UseCurrencyType != ScripCurrencyType) continue;
            var shopName = shop.Name.ExtractText();
            foreach (var entry in shop.Item)
            {
                var receive = entry.ReceiveItems.FirstOrDefault();
                var cost = entry.ItemCosts.FirstOrDefault();
                if (receive.Item.RowId == 0 || cost.CurrencyCost == 0) continue;
                if (Scrips.FromCode((int)cost.ItemCost.RowId) is not { } scrip || !Scrips.IsCrafter(scrip)) continue;
                shopItems.TryAdd((receive.Item.RowId, scrip),
                    new ScripShopItem(receive.Item.RowId, (int)receive.ReceiveCount, scrip, (int)cost.CurrencyCost, shopName));
            }
        }

        Plugin.Log.Information($"Scrips: {collectables.Count} crafter collectables, {appraisers.Count} appraisers in cities, {shopItems.Count} exchange items");
        return new ScripDb(collectables, appraisers, appraiserIds, shopItems.Values.ToList());
    }
}
