using SellWise.Core;
using Xunit;

namespace SellWise.Tests;

public class BuffPlannerTests
{
    private static readonly CraftRecipe Recipe = new(RecipeLevel: 100, JobLevel: 50, MaxProgress: 1000, MaxQuality: 6000, Durability: 80,
        ProgressDivider: 100, QualityDivider: 100, ProgressModifier: 100, QualityModifier: 100);

    private static Consumable Food(string name, int controlPct, int controlMax, int cpPct = 0, int cpMax = 0, bool medicine = false)
        => new(1, name, true, medicine, StatBonus.None, new StatBonus(controlPct, controlMax, true), new StatBonus(cpPct, cpMax, true));

    [Fact]
    public void RelativeBonusIsCapped()
    {
        Assert.Equal(50, new StatBonus(10, 50, true).For(1000));
        Assert.Equal(30, new StatBonus(10, 50, true).For(300));
        Assert.Equal(12, new StatBonus(12, 0, false).For(9999));
    }

    [Fact]
    public void FrontDropsDominatedOptions()
    {
        var stats = new CrafterStats(1000, 1000, 400, 100);
        var strong = Food("Strong", 10, 200);
        var weak = Food("Weak", 5, 50);
        var cp = Food("CP food", 0, 0, 20, 80);

        var front = BuffPlanner.Front(stats, [strong, weak, cp], 6);

        Assert.Contains(strong, front);
        Assert.Contains(cp, front);
        Assert.DoesNotContain(weak, front);
    }

    [Fact]
    public void PicksCheapestComboThatReachesTarget()
    {
        // Set the target just above what these stats reach unbuffed, so a control/CP boost is needed but enough.
        var stats = new CrafterStats(1000, 700, 300, 100);
        var unbuffed = CraftPlanner.Plan(stats, Recipe with { MaxQuality = 100_000 }).BestQuality;
        var recipe = Recipe with { MaxQuality = unbuffed * 105 / 100 };
        Assert.False(CraftPlanner.Plan(stats, recipe).Reaches(recipe.MaxQuality));

        var cheap = Food("Cheap tea", 20, 400, medicine: true);
        var pricey = Food("Pricey feast", 20, 400, 30, 150);
        var prices = new Dictionary<Consumable, long> { [cheap] = 500, [pricey] = 50_000 };

        var pick = BuffPlanner.FindCheapest(stats, recipe, recipe.MaxQuality, [cheap, pricey], c => prices[c]);

        Assert.NotNull(pick);
        Assert.True(pick!.Plan.Reaches(recipe.MaxQuality), $"best found {pick.Plan.BestQuality} of {recipe.MaxQuality}");
        Assert.Same(cheap, pick.Medicine); // the 500 gil tea alone is enough, so the feast isn't bought
        Assert.Null(pick.Food);
        Assert.Equal(500, pick.Cost);
    }

    [Fact]
    public void ReturnsClosestWhenNothingReaches()
    {
        var stats = new CrafterStats(1000, 200, 180, 100);
        var tiny = Food("Tiny", 1, 5);
        var pick = BuffPlanner.FindCheapest(stats, Recipe with { MaxQuality = 50000 }, 50000, [tiny], _ => 10);

        Assert.NotNull(pick);
        Assert.False(pick!.Plan.Reaches(50000));
    }
}
