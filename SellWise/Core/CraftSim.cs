using System;
using System.Collections.Generic;
using System.Linq;

namespace SellWise.Core;

/// <summary>Crafter stats for one job.</summary>
public sealed record CrafterStats(int Craftsmanship, int Control, int CP, int Level)
{
    public CrafterStats With(int craftsmanship = 0, int control = 0, int cp = 0)
        => this with { Craftsmanship = Craftsmanship + craftsmanship, Control = Control + control, CP = CP + cp };
}

/// <summary>What the simulator needs to know about a recipe.</summary>
public sealed record CraftRecipe(
    int RecipeLevel,
    int JobLevel,
    int MaxProgress,
    int MaxQuality,
    int Durability,
    int ProgressDivider,
    int QualityDivider,
    int ProgressModifier,
    int QualityModifier,
    int RequiredCraftsmanship = 0,
    int RequiredControl = 0);

public enum CraftAction
{
    BasicSynthesis, CarefulSynthesis, Groundwork, PrudentSynthesis, MuscleMemory, DelicateSynthesis,
    BasicTouch, StandardTouch, AdvancedTouch, PrudentTouch, PreparatoryTouch, Reflect, TrainedFinesse, RefinedTouch, ByregotsBlessing,
    Veneration, Innovation, GreatStrides, WasteNot, WasteNotII, Manipulation, MastersMend, ImmaculateMend, TrainedPerfection,
}

/// <summary>
/// Deterministic crafting simulation (no conditions: Normal every step), following the formulas used by
/// the Teamcraft simulator for Dawntrail. Expert recipes are out of scope.
/// </summary>
public readonly record struct CraftState(
    int Progress, int Quality, int Durability, int CP, int Step, int InnerQuiet,
    byte Veneration, byte Innovation, byte GreatStrides, byte MuscleMemory, byte WasteNot, byte Manipulation,
    bool TrainedPerfectionActive, bool TrainedPerfectionUsed, CraftAction? Last, bool LastWasCombo);

public static class CraftSim
{
    private static readonly Dictionary<int, int> LevelToRlvl = new()
    {
        [51] = 120, [52] = 125, [53] = 130, [54] = 133, [55] = 136, [56] = 139, [57] = 142, [58] = 145, [59] = 148, [60] = 150,
        [61] = 260, [62] = 265, [63] = 270, [64] = 273, [65] = 276, [66] = 279, [67] = 282, [68] = 285, [69] = 288, [70] = 290,
        [71] = 390, [72] = 395, [73] = 400, [74] = 403, [75] = 406, [76] = 409, [77] = 412, [78] = 415, [79] = 418, [80] = 420,
        [81] = 517, [82] = 520, [83] = 525, [84] = 530, [85] = 535, [86] = 540, [87] = 545, [88] = 550, [89] = 555, [90] = 560,
        [91] = 650, [92] = 653, [93] = 656, [94] = 660, [95] = 665, [96] = 670, [97] = 675, [98] = 680, [99] = 685, [100] = 690,
    };

    public static readonly IReadOnlyDictionary<CraftAction, int> LevelRequirement = new Dictionary<CraftAction, int>
    {
        [CraftAction.BasicSynthesis] = 1, [CraftAction.BasicTouch] = 5, [CraftAction.MastersMend] = 7, [CraftAction.WasteNot] = 15,
        [CraftAction.Veneration] = 15, [CraftAction.StandardTouch] = 18, [CraftAction.GreatStrides] = 21, [CraftAction.Innovation] = 26,
        [CraftAction.WasteNotII] = 47, [CraftAction.ByregotsBlessing] = 50, [CraftAction.MuscleMemory] = 54, [CraftAction.CarefulSynthesis] = 62,
        [CraftAction.Manipulation] = 65, [CraftAction.PrudentTouch] = 66, [CraftAction.AdvancedTouch] = 68, [CraftAction.Reflect] = 69,
        [CraftAction.PreparatoryTouch] = 71, [CraftAction.Groundwork] = 72, [CraftAction.DelicateSynthesis] = 76, [CraftAction.PrudentSynthesis] = 88,
        [CraftAction.TrainedFinesse] = 90, [CraftAction.RefinedTouch] = 92, [CraftAction.ImmaculateMend] = 98, [CraftAction.TrainedPerfection] = 100,
    };

    public static CraftState Start(CrafterStats stats, CraftRecipe recipe)
        => new(0, 0, recipe.Durability, stats.CP, 0, 0, 0, 0, 0, 0, 0, 0, false, false, null, false);

    public static double BaseProgress(CrafterStats s, CraftRecipe r)
    {
        var baseValue = s.Craftsmanship * 10.0 / r.ProgressDivider + 2;
        return AppliesModifier(s, r) ? (float)(baseValue * r.ProgressModifier * 0.01f) : Math.Floor(baseValue);
    }

    public static double BaseQuality(CrafterStats s, CraftRecipe r)
    {
        var baseValue = s.Control * 10.0 / r.QualityDivider + 35;
        return AppliesModifier(s, r) ? (float)(baseValue * r.QualityModifier * 0.01f) : Math.Floor(baseValue);
    }

    private static bool AppliesModifier(CrafterStats s, CraftRecipe r)
        => LevelToRlvl.TryGetValue(s.Level, out var rlvl) && rlvl <= r.RecipeLevel;

    public static int CpCost(CraftAction a, in CraftState st) => a switch
    {
        CraftAction.CarefulSynthesis => 7,
        CraftAction.Groundwork => 18,
        CraftAction.PrudentSynthesis => 18,
        CraftAction.MuscleMemory => 6,
        CraftAction.DelicateSynthesis => 32,
        CraftAction.BasicTouch => 18,
        CraftAction.StandardTouch => st.Last == CraftAction.BasicTouch ? 18 : 32,
        CraftAction.AdvancedTouch => st.Last == CraftAction.StandardTouch && st.LastWasCombo ? 18 : 46,
        CraftAction.PrudentTouch => 25,
        CraftAction.PreparatoryTouch => 40,
        CraftAction.Reflect => 6,
        CraftAction.TrainedFinesse => 32,
        CraftAction.RefinedTouch => 24,
        CraftAction.ByregotsBlessing => 24,
        CraftAction.Veneration => 18,
        CraftAction.Innovation => 18,
        CraftAction.GreatStrides => 32,
        CraftAction.WasteNot => 56,
        CraftAction.WasteNotII => 98,
        CraftAction.Manipulation => 96,
        CraftAction.MastersMend => 88,
        CraftAction.ImmaculateMend => 112,
        _ => 0,
    };

    private static int BaseDurabilityCost(CraftAction a) => a switch
    {
        CraftAction.Groundwork or CraftAction.PreparatoryTouch => 20,
        CraftAction.PrudentSynthesis or CraftAction.PrudentTouch => 5,
        CraftAction.TrainedFinesse or CraftAction.Veneration or CraftAction.Innovation or CraftAction.GreatStrides or CraftAction.WasteNot
            or CraftAction.WasteNotII or CraftAction.Manipulation or CraftAction.MastersMend or CraftAction.ImmaculateMend
            or CraftAction.TrainedPerfection => 0,
        _ => 10,
    };

    public static int DurabilityCost(CraftAction a, in CraftState st)
    {
        var cost = BaseDurabilityCost(a);
        if (cost == 0 || st.TrainedPerfectionActive) return 0;
        return st.WasteNot > 0 ? (int)Math.Ceiling(cost / 2.0) : cost;
    }

    public static bool CanUse(CraftAction a, in CraftState st, CrafterStats s)
    {
        if (!LevelRequirement.TryGetValue(a, out var level) || s.Level < level) return false;
        if (CpCost(a, st) > st.CP) return false;
        return a switch
        {
            CraftAction.MuscleMemory or CraftAction.Reflect => st.Step == 0,
            CraftAction.PrudentSynthesis or CraftAction.PrudentTouch => st.WasteNot == 0,
            CraftAction.TrainedFinesse => st.InnerQuiet == 10,
            CraftAction.ByregotsBlessing => st.InnerQuiet > 0,
            CraftAction.TrainedPerfection => !st.TrainedPerfectionUsed,
            _ => true,
        };
    }

    /// <summary>Applies one action. Returns the new state; progress/quality are not capped.</summary>
    public static CraftState Apply(CraftAction a, CraftState st, CrafterStats s, CraftRecipe r)
    {
        var cp = st.CP - CpCost(a, st);
        var durCost = DurabilityCost(a, st);
        var progress = st.Progress;
        var quality = st.Quality;
        var iq = st.InnerQuiet;
        var ven = st.Veneration; var inno = st.Innovation; var gs = st.GreatStrides; var mm = st.MuscleMemory;
        var wn = st.WasteNot; var manip = st.Manipulation;
        var tpActive = st.TrainedPerfectionActive && durCost == 0 && BaseDurabilityCost(a) == 0; // unused TP carries over non-durability actions
        var tpUsed = st.TrainedPerfectionUsed;
        var comboUsed = false;

        void AddProgress(int potency)
        {
            var mod = 1.0 + (mm > 0 ? 1.0 : 0) + (ven > 0 ? 0.5 : 0);
            mm = 0;
            progress += (int)Math.Floor(Math.Floor(BaseProgress(s, r)) * potency * mod / 100);
        }

        void AddQuality(int potency)
        {
            var buffMult = 1.0 + (gs > 0 ? 1.0 : 0) + (inno > 0 ? 0.5 : 0);
            gs = 0;
            var efficiency = (float)(potency * (buffMult * (100 + iq * 10) / 100.0));
            quality += (int)Math.Floor(Math.Floor(BaseQuality(s, r)) * efficiency / 100);
            if (s.Level >= 11) iq = Math.Min(10, iq + 1);
        }

        switch (a)
        {
            case CraftAction.BasicSynthesis: AddProgress(s.Level >= 31 ? 120 : 100); break;
            case CraftAction.CarefulSynthesis: AddProgress(s.Level >= 82 ? 180 : 150); break;
            case CraftAction.Groundwork:
            {
                var potency = s.Level >= 86 ? 360 : 300;
                AddProgress(st.Durability >= durCost ? potency : potency / 2);
                break;
            }
            case CraftAction.PrudentSynthesis: AddProgress(180); break;
            case CraftAction.MuscleMemory: AddProgress(300); mm = 6; break; // 5 steps after this one
            case CraftAction.DelicateSynthesis: AddProgress(s.Level >= 94 ? 150 : 100); AddQuality(100); break;
            case CraftAction.BasicTouch: AddQuality(100); break;
            case CraftAction.StandardTouch: comboUsed = st.Last == CraftAction.BasicTouch; AddQuality(125); break;
            case CraftAction.AdvancedTouch: AddQuality(150); break;
            case CraftAction.PrudentTouch: AddQuality(100); break;
            case CraftAction.PreparatoryTouch: AddQuality(200); iq = Math.Min(10, iq + 1); break;
            case CraftAction.Reflect: AddQuality(300); iq = Math.Min(10, iq + 1); break;
            case CraftAction.TrainedFinesse: AddQuality(100); break;
            case CraftAction.RefinedTouch: AddQuality(100); if (st.Last == CraftAction.BasicTouch) iq = Math.Min(10, iq + 1); break;
            case CraftAction.ByregotsBlessing: AddQuality(Math.Min(100 + st.InnerQuiet * 20, 300)); iq = 0; break;
            case CraftAction.Veneration: ven = 5; break;
            case CraftAction.Innovation: inno = 5; break;
            case CraftAction.GreatStrides: gs = 4; break;
            case CraftAction.WasteNot: wn = 5; break;
            case CraftAction.WasteNotII: wn = 9; break;
            case CraftAction.Manipulation: manip = 9; break;
            case CraftAction.TrainedPerfection: tpActive = true; tpUsed = true; break;
        }

        var durability = st.Durability - durCost;
        if (a == CraftAction.MastersMend) durability = Math.Min(r.Durability, durability + 30);
        if (a == CraftAction.ImmaculateMend) durability = r.Durability;
        if (st.TrainedPerfectionActive && BaseDurabilityCost(a) > 0) tpActive = false; // consumed

        // Manipulation restores after the action, except on the step it was cast; then every buff ticks down.
        if (st.Manipulation > 0 && a != CraftAction.Manipulation && durability > 0 && progress < r.MaxProgress)
            durability = Math.Min(r.Durability, durability + 5);

        static byte Tick(byte v) => v > 0 ? (byte)(v - 1) : (byte)0;
        return new CraftState(progress, quality, durability, cp, st.Step + 1, iq,
            Tick(ven), Tick(inno), Tick(gs), Tick(mm), Tick(wn), Tick(manip),
            tpActive, tpUsed, a, comboUsed);
    }
}

/// <summary>Result of looking for the best rotation for a recipe.</summary>
public sealed record QualityPlan(bool CanCraft, int BestQuality, int MaxQuality, IReadOnlyList<CraftAction> Rotation, string? Problem)
{
    public double QualityPercent => MaxQuality > 0 ? Math.Min(100.0, 100.0 * BestQuality / MaxQuality) : 0;
    public bool Reaches(int target) => CanCraft && BestQuality >= target;
}

/// <summary>
/// Beam search for a rotation that finishes the craft with as much quality as possible. Every kept state must be
/// finishable by a greedy progress finisher, so the search always has a valid rotation in hand and "reaches the
/// target" is reliable; a miss only means this search didn't find one (a full solver might squeeze out a bit more).
/// </summary>
public static class CraftPlanner
{
    private static readonly CraftAction[] Actions = Enum.GetValues<CraftAction>();

    public static QualityPlan Plan(CrafterStats stats, CraftRecipe recipe, int targetQuality = int.MaxValue, int beamWidth = 200, int maxSteps = 40)
    {
        if (stats.Craftsmanship < recipe.RequiredCraftsmanship || stats.Control < recipe.RequiredControl)
            return new QualityPlan(false, 0, recipe.MaxQuality, [], "Below the recipe's minimum craftsmanship or control.");

        var target = Math.Min(targetQuality, recipe.MaxQuality);
        var baseQ = Math.Max(1, CraftSim.BaseQuality(stats, recipe));
        var start = CraftSim.Start(stats, recipe);

        (int Quality, List<CraftAction> Path)? best = null;
        void Consider(CraftState st, List<CraftAction> path)
        {
            if (Finish(st, stats, recipe) is not { } done) return;
            var q = Math.Min(done.State.Quality, recipe.MaxQuality);
            if (best == null || q > best.Value.Quality)
                best = (q, [.. path, .. done.Steps]);
        }

        Consider(start, []);
        var beam = new List<(CraftState State, List<CraftAction> Path)> { (start, []) };

        for (var depth = 0; depth < maxSteps && beam.Count > 0; depth++)
        {
            if (best is { } b && b.Quality >= target) break;

            var next = new List<(CraftState, List<CraftAction>, double)>();
            foreach (var (st, path) in beam)
            {
                foreach (var a in Actions)
                {
                    if (!CraftSim.CanUse(a, st, stats) || Wasteful(a, st)) continue;

                    var ns = CraftSim.Apply(a, st, stats, recipe);
                    List<CraftAction> newPath = [.. path, a];
                    if (ns.Progress >= recipe.MaxProgress)
                    {
                        var q = Math.Min(ns.Quality, recipe.MaxQuality);
                        if (best == null || q > best.Value.Quality) best = (q, newPath);
                        continue;
                    }
                    if (ns.Durability <= 0) continue;

                    // Only keep states we know how to finish; finishing also gives a real candidate result.
                    var before = best;
                    Consider(ns, newPath);
                    if (best == null || (before == best && Finish(ns, stats, recipe) == null)) continue;

                    next.Add((ns, newPath, Score(ns, recipe, baseQ)));
                }
            }

            beam = next.OrderByDescending(x => x.Item3).Take(beamWidth).Select(x => (x.Item1, x.Item2)).ToList();
        }

        return best is { } found
            ? new QualityPlan(true, found.Quality, recipe.MaxQuality, found.Path, null)
            : new QualityPlan(false, 0, recipe.MaxQuality, [], "No rotation finishes this craft with these stats.");
    }

    /// <summary>
    /// Greedy progress-only finish from a state: Veneration, then the strongest synthesis the durability allows,
    /// mending when the next step would break the item. Null if it can't finish.
    /// </summary>
    public static (CraftState State, List<CraftAction> Steps)? Finish(CraftState st, CrafterStats s, CraftRecipe r)
    {
        var steps = new List<CraftAction>();
        for (var guard = 0; guard < 25; guard++)
        {
            if (st.Progress >= r.MaxProgress) return (st, steps);
            if (st.Durability <= 0) return null;

            CraftAction? pick = null;

            // 1. Anything that finishes right now, cheapest durability first.
            foreach (var a in (ReadOnlySpan<CraftAction>)[CraftAction.PrudentSynthesis, CraftAction.BasicSynthesis, CraftAction.CarefulSynthesis, CraftAction.Groundwork])
            {
                if (!CraftSim.CanUse(a, st, s)) continue;
                if (CraftSim.Apply(a, st, s, r).Progress >= r.MaxProgress) { pick = a; break; }
            }

            if (pick == null)
            {
                var remainingSteps = (r.MaxProgress - st.Progress) / Math.Max(1.0, CraftSim.BaseProgress(s, r) * 1.8);
                if (st.Veneration == 0 && remainingSteps > 1.5 && CraftSim.CanUse(CraftAction.Veneration, st, s))
                    pick = CraftAction.Veneration;
                else
                {
                    // Strongest synthesis that leaves the item intact.
                    foreach (var a in (ReadOnlySpan<CraftAction>)[CraftAction.Groundwork, CraftAction.CarefulSynthesis, CraftAction.PrudentSynthesis, CraftAction.BasicSynthesis])
                    {
                        if (CraftSim.CanUse(a, st, s) && st.Durability - CraftSim.DurabilityCost(a, st) > 0) { pick = a; break; }
                    }

                    // Nothing safe: restore durability if CP allows.
                    pick ??= CraftSim.CanUse(CraftAction.MastersMend, st, s) ? CraftAction.MastersMend
                        : CraftSim.CanUse(CraftAction.ImmaculateMend, st, s) ? CraftAction.ImmaculateMend
                        : null;
                }
            }

            if (pick is not { } action) return null;
            st = CraftSim.Apply(action, st, s, r);
            steps.Add(action);
        }

        return null;
    }

    /// <summary>Skips moves no sensible rotation makes, to keep the beam focused.</summary>
    private static bool Wasteful(CraftAction a, in CraftState st) => a switch
    {
        CraftAction.Veneration => st.Veneration > 1,
        CraftAction.Innovation => st.Innovation > 1,
        CraftAction.GreatStrides => st.GreatStrides > 0,
        CraftAction.WasteNot or CraftAction.WasteNotII => st.WasteNot > 1,
        CraftAction.Manipulation => st.Manipulation > 1,
        CraftAction.MastersMend or CraftAction.ImmaculateMend => st.Durability > 40,
        CraftAction.TrainedPerfection => st.Step == 0,
        _ => false,
    };

    private static double Score(in CraftState st, CraftRecipe r, double baseQ)
    {
        var q = Math.Min(st.Quality, r.MaxQuality);
        var progressShare = Math.Min(1.0, (double)st.Progress / r.MaxProgress);
        // Unused resources are worth something: roughly what a touch would add with them.
        return q
               + progressShare * baseQ * 4
               + st.CP * baseQ / 18.0 * 0.5
               + st.Durability * baseQ / 10.0 * 0.3
               + st.InnerQuiet * baseQ * 0.3
               + (st.Innovation > 0 ? baseQ * 0.3 : 0)
               + (st.GreatStrides > 0 ? baseQ * 0.3 : 0)
               + (st.Veneration > 0 || st.MuscleMemory > 0 ? baseQ * 0.2 : 0);
    }
}
