using System;
using System.Collections.Generic;
using System.Linq;

namespace SellWise.Core;

/// <summary>
/// Works out what a recipe costs to make and what it earns. Every way of getting each material is costed
/// (gathering, NPC vendor, market board, crafting it, recursively), and the one used follows the player's
/// <see cref="MaterialMode"/> or a per-material override.
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
    private readonly IReadOnlyDictionary<uint, MaterialSource> overrides;
    private readonly Dictionary<(uint, int), Resolved> memo = [];

    private sealed record Resolved(MaterialSource Source, double Cash, double Value, bool Unknown, RecipeInfo? SubRecipe);

    public ProfitCalculator(
        Func<uint, ItemInfo?> items,
        Func<uint, AggregatedPrice?> prices,
        Func<uint, RecipeInfo?> recipeFor,
        IReadOnlySet<uint> gatherable,
        IReadOnlySet<uint> vendorSold,
        CraftSettings cs,
        AdvisorSettings adv,
        IReadOnlyDictionary<uint, MaterialSource>? overrides = null)
    {
        this.overrides = overrides ?? new Dictionary<uint, MaterialSource>();
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

    /// <summary>A way of getting a material and what it costs per unit (cash spent, and market value).</summary>
    public readonly record struct Choice(MaterialSource Source, double Cash, double Value);

    /// <summary>Every way this material can be obtained, for letting the player pick.</summary>
    public IReadOnlyList<Choice> Choices(uint itemId)
        => Options(itemId, 1).Select(o => new Choice(o.Source, o.Cash, o.Value)).ToList();

    private int CraftDepthLimit => cs.MaterialMode == MaterialMode.GatherAndCraft ? Math.Max(cs.MaxIntermediateDepth, 5) : cs.MaxIntermediateDepth;

    private List<(MaterialSource Source, double Cash, double Value, RecipeInfo? Sub)> Options(uint itemId, int depth)
    {
        var item = items(itemId);
        var options = new List<(MaterialSource Source, double Cash, double Value, RecipeInfo? Sub)>();

        if (vendorSold.Contains(itemId) && item is { VendorBuyPrice: > 0 })
            options.Add((MaterialSource.Vendor, item.VendorBuyPrice, item.VendorBuyPrice, null));
        if (prices(itemId)?.CheapestListing is { } buy)
            options.Add((MaterialSource.Buy, buy, buy, null));
        if (depth <= CraftDepthLimit && recipeFor(itemId) is { } r)
        {
            double c = 0, v = 0;
            foreach (var ing in r.Ingredients)
            {
                var child = Resolve(ing.ItemId, depth + 1);
                c += child.Cash * ing.Amount;
                v += child.Value * ing.Amount;
            }
            options.Add((MaterialSource.Craft, c / Math.Max(1, r.Yield), v / Math.Max(1, r.Yield), r));
        }
        if (gatherable.Contains(itemId))
        {
            // Gathering costs no gil; its value is what you could otherwise sell or buy it for.
            var worth = options.Count > 0 ? options.Min(o => o.Value) : 0;
            options.Add((MaterialSource.Gather, 0, worth, null));
        }

        return options;
    }

    private Resolved Resolve(uint itemId, int depth)
    {
        if (memo.TryGetValue((itemId, depth), out var cached)) return cached;
        memo[(itemId, depth)] = new Resolved(MaterialSource.Unknown, 0, 0, true, null); // guards against recipe loops

        var options = Options(itemId, depth);
        (MaterialSource Source, double Cash, double Value, RecipeInfo? Sub)? pick = null;

        if (overrides.TryGetValue(itemId, out var wanted))
            pick = options.FirstOrDefault(o => o.Source == wanted) is { Source: var found } chosen && found == wanted ? chosen : null;

        pick ??= cs.MaterialMode switch
        {
            MaterialMode.GatherAndCraft => First(options, MaterialSource.Gather, MaterialSource.Craft, MaterialSource.Vendor, MaterialSource.Buy),
            MaterialMode.BuyAll => First(options, MaterialSource.Buy, MaterialSource.Vendor, MaterialSource.Craft, MaterialSource.Gather),
            _ => cs.GatherWhenPossible && options.Any(o => o.Source == MaterialSource.Gather)
                ? First(options, MaterialSource.Gather)
                : options.Where(o => o.Source != MaterialSource.Gather).OrderBy(o => o.Value).Cast<(MaterialSource, double, double, RecipeInfo?)?>().FirstOrDefault()
                  ?? First(options, MaterialSource.Gather),
        };

        var result = pick is { } p
            ? new Resolved(p.Source, p.Cash, p.Value, false, p.Source == MaterialSource.Craft ? p.Sub : null)
            : new Resolved(MaterialSource.Unknown, 0, 0, true, null);

        memo[(itemId, depth)] = result;
        return result;
    }

    private static (MaterialSource Source, double Cash, double Value, RecipeInfo? Sub)? First(
        List<(MaterialSource Source, double Cash, double Value, RecipeInfo? Sub)> options, params MaterialSource[] order)
    {
        foreach (var source in order)
            foreach (var o in options)
                if (o.Source == source) return o;
        return null;
    }
}
