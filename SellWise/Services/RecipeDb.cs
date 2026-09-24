using System;
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

    /// <summary>How each gatherable item is gathered: always-up or timed nodes, level and spawn windows.</summary>
    public IReadOnlyDictionary<uint, GatherInfo> GatherInfo { get; }

    /// <summary>Crafting foods and medicines (NQ and HQ versions).</summary>
    public IReadOnlyList<Consumable> Consumables { get; }

    private RecipeDb(List<RecipeInfo> recipes, Dictionary<uint, GatherInfo> gatherInfo, HashSet<uint> vendorSold, List<Consumable> consumables)
    {
        GatherInfo = gatherInfo;
        var gatherable = gatherInfo.Keys.ToHashSet();
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

        var gatherInfo = LoadGatherInfo();

        var vendorSold = new HashSet<uint>();
        foreach (var shop in data.GetSubrowExcelSheet<GilShopItem>())
        {
            foreach (var entry in shop)
            {
                if (entry.Item.RowId != 0) vendorSold.Add(entry.Item.RowId);
            }
        }

        var consumables = LoadConsumables();
        Plugin.Log.Information($"Loaded {recipes.Count} recipes, {gatherInfo.Count} gatherable items, {vendorSold.Count} vendor items, {consumables.Count} crafting consumables");
        return new RecipeDb(recipes, gatherInfo, vendorSold, consumables);
    }

    private const uint DiademUse = 47;

    /// <summary>
    /// Which items can be gathered in the open world, and whether from always-up nodes or only timed ones
    /// (unspoiled, legendary, ephemeral). Diadem-only items don't count: GatherBuddy can't farm them for a craft.
    /// </summary>
    private static Dictionary<uint, GatherInfo> LoadGatherInfo()
    {
        var data = Plugin.DataManager;
        var itemByGatherRow = new Dictionary<uint, uint>();
        foreach (var g in data.GetExcelSheet<GatheringItem>())
            if (g.Item.RowId != 0) itemByGatherRow[g.RowId] = g.Item.RowId;

        var transients = data.GetExcelSheet<GatheringPointTransient>();
        var pointsByBase = data.GetExcelSheet<GatheringPoint>()
            .Where(p => p.GatheringPointBase.RowId != 0 && p.TerritoryType.RowId != 0
                        && p.TerritoryType.ValueNullable?.TerritoryIntendedUse.RowId != DiademUse)
            .GroupBy(p => p.GatheringPointBase.RowId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.RowId).ToList());

        static int Minutes(int hhmm) => hhmm / 100 * 60 + hhmm % 100;

        var found = new Dictionary<uint, (bool Regular, int Level, HashSet<SpawnWindow> Windows)>();
        var jobs = new Dictionary<uint, int>();
        foreach (var node in data.GetExcelSheet<GatheringPointBase>())
        {
            if (!pointsByBase.TryGetValue(node.RowId, out var pointIds)) continue;

            var windows = new HashSet<SpawnWindow>();
            var anyRegular = false;
            foreach (var pointId in pointIds)
            {
                var pointWindows = new List<SpawnWindow>();
                if (transients.GetRowOrDefault(pointId) is { } t)
                {
                    if (t.GatheringRarePopTimeTable.ValueNullable is { RowId: > 0 } rare)
                    {
                        for (var i = 0; i < rare.StartTime.Count && i < rare.Duration.Count; i++)
                            if (rare.Duration[i] > 0) pointWindows.Add(new SpawnWindow(Minutes(rare.StartTime[i]), Minutes(rare.Duration[i])));
                    }
                    if (t.EphemeralStartTime != 65535)
                    {
                        var start = Minutes(t.EphemeralStartTime);
                        pointWindows.Add(new SpawnWindow(start, (Minutes(t.EphemeralEndTime) - start + 1440) % 1440));
                    }
                }
                if (pointWindows.Count == 0) anyRegular = true;
                windows.UnionWith(pointWindows);
            }

            foreach (var slot in node.Item)
            {
                if (!itemByGatherRow.TryGetValue(slot.RowId, out var itemId)) continue;
                var prev = found.TryGetValue(itemId, out var p) ? p : (false, int.MaxValue, new HashSet<SpawnWindow>());
                prev.Item3.UnionWith(windows);
                found[itemId] = (prev.Item1 || anyRegular, Math.Min(prev.Item2, node.GatheringLevel), prev.Item3);
                // Mining and quarrying are Miner's, logging and harvesting Botanist's.
                if (node.GatheringType.RowId <= 3) jobs.TryAdd(itemId, node.GatheringType.RowId <= 1 ? 8 : 9);
            }
        }

        return found.ToDictionary(
            kv => kv.Key,
            kv => new GatherInfo(kv.Key, kv.Value.Regular ? NodeKind.Regular : NodeKind.Timed, kv.Value.Level,
                kv.Value.Regular ? [] : kv.Value.Windows.OrderBy(w => w.StartMinute).ToList(), jobs.GetValueOrDefault(kv.Key, -1)));
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
