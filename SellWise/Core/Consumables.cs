using System;
using System.Collections.Generic;
using System.Linq;

namespace SellWise.Core;

/// <summary>One stat bonus from a food or medicine: either a percentage up to a cap, or a flat amount.</summary>
public readonly record struct StatBonus(int Value, int Max, bool Relative)
{
    public static readonly StatBonus None = new(0, 0, true);

    public int For(int stat) => Relative ? Math.Min(stat * Value / 100, Max) : Value;
}

/// <summary>A crafting food or medicine at one quality.</summary>
public sealed record Consumable(
    uint ItemId,
    string Name,
    bool Hq,
    bool IsMedicine,
    StatBonus Craftsmanship,
    StatBonus Control,
    StatBonus CP)
{
    public string Label => Hq ? $"{Name} (HQ)" : Name;

    public CrafterStats ApplyTo(CrafterStats s)
        => s.With(Craftsmanship.For(s.Craftsmanship), Control.For(s.Control), CP.For(s.CP));
}

/// <summary>A food/medicine combination and what it gets the crafter to.</summary>
public sealed record BuffSuggestion(Consumable? Food, Consumable? Medicine, CrafterStats Stats, QualityPlan Plan, long Cost)
{
    public bool IsEmpty => Food == null && Medicine == null;
}

public static class BuffPlanner
{
    /// <summary>
    /// Finds the cheapest food + medicine combination that reaches <paramref name="target"/> quality.
    /// If none does, returns the combination that gets closest. <paramref name="unitCost"/> gives the gil cost of one
    /// (0 if owned, null if unknown and not owned; unknown is treated as expensive but still considered).
    /// </summary>
    public static BuffSuggestion? FindCheapest(
        CrafterStats stats,
        CraftRecipe recipe,
        int target,
        IEnumerable<Consumable> consumables,
        Func<Consumable, long?> unitCost,
        int perCategory = 6)
    {
        var all = consumables.ToList();
        var foods = Front(stats, all.Where(c => !c.IsMedicine), perCategory);
        var meds = Front(stats, all.Where(c => c.IsMedicine), perCategory);

        const long unknownCost = 10_000_000;
        long Cost(Consumable? c) => c == null ? 0 : unitCost(c) ?? unknownCost;

        var combos = new List<(Consumable? Food, Consumable? Med, long Cost)>();
        foreach (var f in foods.Prepend(null))
            foreach (var m in meds.Prepend(null))
                if (f != null || m != null)
                    combos.Add((f, m, Cost(f) + Cost(m)));

        BuffSuggestion? closest = null;
        foreach (var (food, med, cost) in combos.OrderBy(c => c.Cost))
        {
            var buffed = stats;
            if (food != null) buffed = food.ApplyTo(buffed);
            if (med != null) buffed = med.ApplyTo(buffed);

            var plan = CraftPlanner.Plan(buffed, recipe, target, beamWidth: 120);
            var suggestion = new BuffSuggestion(food, med, buffed, plan, cost);
            if (plan.Reaches(target)) return suggestion;
            if (closest == null || plan.BestQuality > closest.Plan.BestQuality) closest = suggestion;
        }

        return closest;
    }

    /// <summary>
    /// The consumables worth trying for these stats: drop any that another option beats (or matches) on every stat,
    /// then keep the strongest few.
    /// </summary>
    public static List<Consumable> Front(CrafterStats stats, IEnumerable<Consumable> options, int keep)
    {
        var scored = options
            .Select(c => (c, bonus: (c.Craftsmanship.For(stats.Craftsmanship), c.Control.For(stats.Control), c.CP.For(stats.CP))))
            .Where(x => x.bonus.Item1 + x.bonus.Item2 + x.bonus.Item3 > 0)
            .ToList();

        var front = scored.Where(x => !scored.Any(o => !ReferenceEquals(o.c, x.c) && Dominates(o.bonus, x.bonus)))
            .GroupBy(x => x.bonus).Select(g => g.First()) // identical bonuses: keep one
            .OrderByDescending(x => x.bonus.Item2 * 2 + x.bonus.Item3 * 6 + x.bonus.Item1)
            .Take(keep)
            .Select(x => x.c)
            .ToList();
        return front;
    }

    private static bool Dominates((int C, int K, int P) a, (int C, int K, int P) b)
        => a.C >= b.C && a.K >= b.K && a.P >= b.P && (a.C > b.C || a.K > b.K || a.P > b.P);
}
