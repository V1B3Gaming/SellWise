using SellWise.Core;
using Xunit;

namespace SellWise.Tests;

public class CraftSimTests
{
    // Dividers/modifiers of 100 and 100% keep the arithmetic easy to check by hand.
    private static readonly CraftRecipe Easy = new(RecipeLevel: 100, JobLevel: 50, MaxProgress: 1000, MaxQuality: 5000, Durability: 80,
        ProgressDivider: 100, QualityDivider: 100, ProgressModifier: 100, QualityModifier: 100);

    private static readonly CrafterStats Lv100 = new(Craftsmanship: 1000, Control: 1000, CP: 500, Level: 100);

    private static CraftState Run(CrafterStats s, CraftRecipe r, params CraftAction[] actions)
    {
        var st = CraftSim.Start(s, r);
        foreach (var a in actions)
        {
            Assert.True(CraftSim.CanUse(a, st, s), $"{a} should be usable");
            st = CraftSim.Apply(a, st, s, r);
        }
        return st;
    }

    [Fact]
    public void BaseValuesFollowTheFormula()
    {
        // (1000*10/100 + 2) = 102 progress; (1000*10/100 + 35) = 135 quality. Level 100 maps to rlvl 690 > 100, so no modifier.
        Assert.Equal(102, CraftSim.BaseProgress(Lv100, Easy));
        Assert.Equal(135, CraftSim.BaseQuality(Lv100, Easy));
    }

    [Fact]
    public void ModifierAppliesWhenRecipeIsAtOrAboveCrafterLevel()
    {
        var hard = Easy with { RecipeLevel = 690, ProgressModifier = 80, QualityModifier = 70 };
        Assert.Equal(102 * 0.8, CraftSim.BaseProgress(Lv100, hard), 3);
        Assert.Equal(135 * 0.7, CraftSim.BaseQuality(Lv100, hard), 3);
    }

    [Fact]
    public void MuscleMemoryAndVenerationStackOnNextSynthesis()
    {
        var st = Run(Lv100, Easy, CraftAction.MuscleMemory, CraftAction.Veneration, CraftAction.BasicSynthesis);
        // MM: 102*3 = 306. Then Basic Synthesis with MM (+100%) and Veneration (+50%): floor(102 * 120 * 2.5 / 100) = 306.
        Assert.Equal(306 + 306, st.Progress);
        Assert.Equal(80 - 20, st.Durability);
    }

    [Fact]
    public void InnerQuietAndBuffsMultiplyQuality()
    {
        // Reflect: 135*3 = 405, IQ -> 2. Innovation. Basic Touch: 135 * 100 * 1.5 * 1.2 / 100 = 243.
        var st = Run(Lv100, Easy, CraftAction.Reflect, CraftAction.Innovation, CraftAction.BasicTouch);
        Assert.Equal(405 + 243, st.Quality);
        Assert.Equal(3, st.InnerQuiet);
    }

    [Fact]
    public void TouchCombosReduceCost()
    {
        var st = Run(Lv100, Easy, CraftAction.BasicTouch, CraftAction.StandardTouch, CraftAction.AdvancedTouch);
        Assert.Equal(500 - 18 - 18 - 18, st.CP);
    }

    [Fact]
    public void WasteNotHalvesDurabilityRoundingUp()
    {
        var st = Run(Lv100, Easy, CraftAction.WasteNot, CraftAction.BasicTouch, CraftAction.PreparatoryTouch);
        Assert.Equal(80 - 5 - 10, st.Durability);
    }

    [Fact]
    public void ManipulationRestoresFromTheNextStep()
    {
        var st = Run(Lv100, Easy, CraftAction.Manipulation, CraftAction.BasicTouch);
        Assert.Equal(80 - 10 + 5, st.Durability);
    }

    [Fact]
    public void ByregotsScalesWithInnerQuietThenClearsIt()
    {
        var st = Run(Lv100, Easy, CraftAction.Reflect, CraftAction.ByregotsBlessing);
        // Byregot's at IQ 2: potency 140, IQ multiplier 1.2 -> floor(135 * 140 * 1.2 / 100) = 226.
        Assert.Equal(405 + 226, st.Quality);
        Assert.Equal(0, st.InnerQuiet);
    }

    [Fact]
    public void GroundworkIsHalvedWhenDurabilityIsShort()
    {
        var lowDurability = Easy with { Durability = 15 };
        var st = Run(Lv100, lowDurability, CraftAction.Groundwork);
        Assert.Equal((int)(102 * 180 / 100.0), st.Progress);
    }

    [Fact]
    public void PlannerFindsHqOnAnEasyRecipe()
    {
        var plan = CraftPlanner.Plan(Lv100, Easy with { MaxQuality = 2000 });
        Assert.True(plan.CanCraft);
        Assert.Equal(2000, plan.BestQuality);
        Assert.True(plan.Reaches(2000));

        // The returned rotation really does finish the craft.
        var st = Run(Lv100, Easy with { MaxQuality = 2000 }, plan.Rotation.ToArray());
        Assert.True(st.Progress >= 1000);
    }

    [Fact]
    public void PlannerReportsShortfallWhenStatsAreTooLow()
    {
        var weak = new CrafterStats(1000, 300, 180, 100);
        var plan = CraftPlanner.Plan(weak, Easy with { MaxQuality = 20000 });
        Assert.True(plan.CanCraft);
        Assert.False(plan.Reaches(20000));
        Assert.InRange(plan.QualityPercent, 1, 99);
    }

    [Fact]
    public void PlannerRespectsMinimumStats()
    {
        var plan = CraftPlanner.Plan(Lv100, Easy with { RequiredCraftsmanship = 2000 });
        Assert.False(plan.CanCraft);
        Assert.NotNull(plan.Problem);
    }
}
