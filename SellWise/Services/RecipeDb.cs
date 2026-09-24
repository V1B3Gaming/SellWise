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

    /// <summary>Crafting foods and medicines (NQ and HQ versions).</summary>
    public IReadOnlyList<Consumable> Consumables { get; }

    private RecipeDb(List<RecipeInfo> recipes, HashSet<uint> gatherable, HashSet<uint> vendorSold, List<Consumable> consumables)
    {
        Consumables = consumables;
        Recipes = recipes;
        ByResult = recipes.GroupBy(r => r.ResultItemId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Level).First());
        Gatherable = gatherable;
        VendorSold = vendorSold;
    }

    public static RecipeDb Load()
    {
        var data = Plugin.DataManager;

        var refine = data.GetExcelSheet<CollectablesShopRefine>();
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

            CraftRecipe? craft = null;
            if (r.RecipeLevelTable.ValueNullable is { } t)
            {
                craft = new CraftRecipe((int)t.RowId, t.ClassJobLevel,
                    t.Difficulty * r.DifficultyFactor / 100, (int)(t.Quality * r.QualityFactor / 100), t.Durability * r.DurabilityFactor / 100,
                    t.ProgressDivider, t.QualityDivider, t.ProgressModifier, t.QualityModifier, r.RequiredCraftsmanship, r.RequiredControl);
            }

            // Scrip collectables: collectability = quality / 10. Other collectable kinds just aim for max quality.
            int[]? collectable = null;
            if (r.CollectableMetadataKey == 1 && refine.GetRowOrDefault(r.CollectableMetadata.RowId) is { HighCollectability: > 0 } th)
                collectable = [th.LowCollectability * 10, th.MidCollectability * 10, th.HighCollectability * 10];

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
                r.Quest.RowId,
                craft,
                collectable));
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

        var consumables = LoadConsumables();
        Plugin.Log.Information($"Loaded {recipes.Count} recipes, {gatherable.Count} gatherable items, {vendorSold.Count} vendor items, {consumables.Count} crafting consumables");
        return new RecipeDb(recipes, gatherable, vendorSold, consumables);
    }

    private const uint ParamCraftsmanship = 70, ParamControl = 71, ParamCP = 11;
    private const uint ActionFood = 844, ActionFoodAlt = 845, ActionMedicine = 846;

    private static List<Consumable> LoadConsumables()
    {
        var data = Plugin.DataManager;
        var foods = data.GetExcelSheet<ItemFood>();
        var result = new List<Consumable>();
        foreach (var item in data.GetExcelSheet<Item>())
        {
            if (item.ItemAction.ValueNullable is not { } action) continue;
            var type = action.Action.RowId;
            if (type != ActionFood && type != ActionFoodAlt && type != ActionMedicine) continue;
            if (action.Data.Count < 2 || foods.GetRowOrDefault(action.Data[1]) is not { } food) continue;

            StatBonus Bonus(uint param, bool hq)
            {
                foreach (var p in food.Params)
                {
                    if (p.BaseParam.RowId != param) continue;
                    return hq ? new StatBonus(p.ValueHQ, p.MaxHQ, p.IsRelative) : new StatBonus(p.Value, p.Max, p.IsRelative);
                }
                return StatBonus.None;
            }

            if (Bonus(ParamCraftsmanship, false) == StatBonus.None && Bonus(ParamControl, false) == StatBonus.None && Bonus(ParamCP, false) == StatBonus.None)
                continue;

            var name = item.Name.ExtractText();
            var medicine = type == ActionMedicine;
            result.Add(new Consumable(item.RowId, name, false, medicine, Bonus(ParamCraftsmanship, false), Bonus(ParamControl, false), Bonus(ParamCP, false)));
            if (item.CanBeHq)
                result.Add(new Consumable(item.RowId, name, true, medicine, Bonus(ParamCraftsmanship, true), Bonus(ParamControl, true), Bonus(ParamCP, true)));
        }

        return result;
    }
}
