using System;
using System.Collections.Generic;
using System.Linq;

namespace SellWise.Core;

/// <summary>
/// Works out what a recipe costs to make and what it earns. Each material is costed at the cheapest of
/// gathering, NPC vendor, market board, or crafting it (recursively, up to a depth limit).
/// </summary>
public sealed class ProfitCalculator
{
    private readonly Func<uint, ItemInfo?> items;
    private readonly Func<uint, AggregatedPrice?> prices;
    private readonly Func<uint, RecipeInfo?> recipeFor;
    private readonly IReadOnlySet<uint> gatherable;
    private readonly IReadOnlySet<uint> vendorSold;
    private readonly CraftSettings cs;
    private readonly AdvisorSettings adv;
    private readonly Dictionary<(uint, int), Resolved> memo = [];

    private sealed record Resolved(MaterialSource Source, double Cash, double Value, bool Unknown, RecipeInfo? SubRecipe);

    public ProfitCalculator(
        Func<uint, ItemInfo?> items,
        Func<uint, AggregatedPrice?> prices,
        Func<uint, RecipeInfo?> recipeFor,
        IReadOnlySet<uint> gatherable,
        IReadOnlySet<uint> vendorSold,
        CraftSettings cs,
        AdvisorSettings adv)
    {
        this.items = items;
        this.prices = prices;
        this.recipeFor = recipeFor;
        this.gatherable = gatherable;
        this.vendorSold = vendorSold;
        this.cs = cs;
        this.adv = adv;
    }

    /// <summary>Returns null if the result doesn't sell well enough to be worth considering.</summary>
    public CraftOpportunity? Evaluate(RecipeInfo recipe)
    {
        if (items(recipe.ResultItemId) is not { Marketable: true, Tradable: true } item) return null;
        if (prices(recipe.ResultItemId) is not { } price) return null;

        var sellHq = cs.AssumeHq && recipe.CanHq && item.CanBeHq;
        var q = sellHq ? price.Hq : price.Nq;
        if (q.UnitsPerDay < cs.MinUnitsPerDay) return null;

        uint? salePrice = (q.MinListing, q.AverageSale) switch
        {
            ({ } min, { } avg) => Math.Min(SellAdvisor.Undercut(min, adv), (uint)Math.Round(avg)),
            ({ } min, null) => SellAdvisor.Undercut(min, adv),
            (null, { } avg) => (uint)Math.Round(avg),
            _ => null,
        };
        if (salePrice is not { } sale || sale < cs.MinSalePrice) return null;

        var lines = new List<MaterialLine>();
        double value = 0, cash = 0;
        var unknown = new List<string>();
        foreach (var ing in recipe.Ingredients)
        {
            var r = Resolve(ing.ItemId, 1);
            value += r.Value * ing.Amount;
            cash += r.Cash * ing.Amount;
            AddLines(lines, ing.ItemId, ing.Amount, r, 0, unknown);
        }

        var revenue = (double)SellAdvisor.NetUnit(sale, adv) * recipe.Yield;
        var profit = revenue - (cs.ValueGatheredAtMarket ? value : cash);
        var yieldUnits = Math.Max(1, recipe.Yield);

        return new CraftOpportunity
        {
            Recipe = recipe,
            Item = item,
            SellHq = sellHq,
            SalePrice = sale,
            UnitsPerDay = q.UnitsPerDay,
            MaterialValue = value,
            CashCost = cash,
            ProfitPerCraft = profit,
            CashProfitPerCraft = revenue - cash,
            DailyProfit = profit / yieldUnits * q.UnitsPerDay,
            SuggestedCrafts = (int)Math.Clamp(Math.Ceiling(q.UnitsPerDay * cs.DaysOfSupply / yieldUnits), 1, 99),
            Materials = lines,
            Warning = unknown.Count > 0 ? $"No known source or price for: {string.Join(", ", unknown.Distinct())}. Costs may be understated." : null,
        };
    }

    private void AddLines(List<MaterialLine> lines, uint itemId, double amount, Resolved r, int depth, List<string> unknown)
    {
        var name = items(itemId)?.Name ?? $"Item {itemId}";
        if (r.Unknown) unknown.Add(name);
        lines.Add(new MaterialLine(itemId, name, amount, r.Source, r.Cash, r.Value, depth, r.SubRecipe?.RecipeId, r.SubRecipe?.Yield ?? 1));

        if (r.Source != MaterialSource.Craft || r.SubRecipe is not { } sub) return;
        foreach (var ing in sub.Ingredients)
            AddLines(lines, ing.ItemId, amount * ing.Amount / Math.Max(1, sub.Yield), Resolve(ing.ItemId, depth + 2), depth + 1, unknown);
    }

    private Resolved Resolve(uint itemId, int depth)
    {
        if (memo.TryGetValue((itemId, depth), out var cached)) return cached;

        var item = items(itemId);
        double? buy = prices(itemId)?.CheapestListing;
        double? vendor = vendorSold.Contains(itemId) && item is { VendorBuyPrice: > 0 } ? item.VendorBuyPrice : null;

        double? craftCash = null, craftValue = null;
        RecipeInfo? sub = null;
        if (depth <= cs.MaxIntermediateDepth && recipeFor(itemId) is { } r)
        {
            sub = r;
            double c = 0, v = 0;
            foreach (var ing in r.Ingredients)
            {
                var child = Resolve(ing.ItemId, depth + 1);
                c += child.Cash * ing.Amount;
                v += child.Value * ing.Amount;
            }
            craftCash = c / Math.Max(1, r.Yield);
            craftValue = v / Math.Max(1, r.Yield);
        }

        var options = new List<(MaterialSource Source, double Cash, double Value)>();
        if (vendor is { } vp) options.Add((MaterialSource.Vendor, vp, vp));
        if (buy is { } bp) options.Add((MaterialSource.Buy, bp, bp));
        if (craftCash is { } cc) options.Add((MaterialSource.Craft, cc, craftValue!.Value));
        var marketValue = options.Count > 0 ? options.Min(o => o.Value) : (double?)null;

        Resolved result;
        if (gatherable.Contains(itemId) && cs.GatherWhenPossible)
            result = new Resolved(MaterialSource.Gather, 0, marketValue ?? 0, false, null);
        else if (options.Count > 0)
        {
            var best = options.MinBy(o => o.Value);
            result = new Resolved(best.Source, best.Cash, best.Value, false, best.Source == MaterialSource.Craft ? sub : null);
        }
        else if (gatherable.Contains(itemId))
            result = new Resolved(MaterialSource.Gather, 0, 0, false, null);
        else
            result = new Resolved(MaterialSource.Unknown, 0, 0, true, null);

        memo[(itemId, depth)] = result;
        return result;
    }
}
