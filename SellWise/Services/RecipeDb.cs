using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>Recipes, gatherable items and vendor-sold items, read once from the game sheets.</summary>
public sealed class RecipeDb
{
    public IReadOnlyList<RecipeInfo> Recipes { get; }

    /// <summary>The lowest-level recipe for each craftable item.</summary>
    public IReadOnlyDictionary<uint, RecipeInfo> ByResult { get; }

    public IReadOnlySet<uint> Gatherable { get; }
    public IReadOnlySet<uint> VendorSold { get; }

    private RecipeDb(List<RecipeInfo> recipes, HashSet<uint> gatherable, HashSet<uint> vendorSold)
    {
        Recipes = recipes;
        ByResult = recipes.GroupBy(r => r.ResultItemId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Level).First());
        Gatherable = gatherable;
        VendorSold = vendorSold;
    }

    public static RecipeDb Load()
    {
        var data = Plugin.DataManager;

        var recipes = new List<RecipeInfo>();
        foreach (var r in data.GetExcelSheet<Recipe>())
        {
            if (r.ItemResult.RowId == 0 || r.AmountResult == 0) continue;

            var ingredients = new List<Ingredient>();
            for (var i = 0; i < r.Ingredient.Count && i < r.AmountIngredient.Count; i++)
            {
                var id = r.Ingredient[i].RowId;
                var amount = r.AmountIngredient[i];
                if (id != 0 && amount > 0) ingredients.Add(new Ingredient(id, amount));
            }
            if (ingredients.Count == 0) continue;

            recipes.Add(new RecipeInfo(
                r.RowId,
                r.ItemResult.RowId,
                r.AmountResult,
                (int)r.CraftType.RowId,
                r.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0,
                r.CanHq,
                r.IsExpert,
                r.IsSpecializationRequired,
                r.SecretRecipeBook.RowId,
                ingredients,
                r.Quest.RowId));
        }

        var gatherable = new HashSet<uint>();
        foreach (var g in data.GetExcelSheet<GatheringItem>())
        {
            if (g.Item.RowId != 0) gatherable.Add(g.Item.RowId);
        }

        var vendorSold = new HashSet<uint>();
        foreach (var shop in data.GetSubrowExcelSheet<GilShopItem>())
        {
            foreach (var entry in shop)
            {
                if (entry.Item.RowId != 0) vendorSold.Add(entry.Item.RowId);
            }
        }

        Plugin.Log.Information($"Loaded {recipes.Count} recipes, {gatherable.Count} gatherable items, {vendorSold.Count} vendor items");
        return new RecipeDb(recipes, gatherable, vendorSold);
    }
}
