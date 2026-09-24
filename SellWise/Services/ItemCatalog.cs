using System.Collections.Concurrent;
using Lumina.Excel.Sheets;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>Cached item sheet lookups.</summary>
public sealed class ItemCatalog
{
    private readonly ConcurrentDictionary<uint, ItemInfo?> cache = new();

    public ItemInfo? Get(uint itemId) => cache.GetOrAdd(itemId, Load);

    private static ItemInfo? Load(uint id)
    {
        if (Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id) is not { } row || row.RowId == 0)
            return null;

        var name = row.Name.ExtractText();
        if (string.IsNullOrEmpty(name))
            return null;

        return new ItemInfo(
            row.RowId,
            name,
            row.PriceLow,
            Marketable: row.ItemSearchCategory.RowId != 0,
            Tradable: !row.IsUntradable,
            CanBeHq: row.CanBeHq,
            Collectable: row.AlwaysCollectable,
            Icon: row.Icon,
            VendorBuyPrice: row.PriceMid);
    }
}
