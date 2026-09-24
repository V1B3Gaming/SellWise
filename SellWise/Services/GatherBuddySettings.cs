using System;
using System.IO;
using System.Text.Json;

namespace SellWise.Services;

/// <summary>GatherBuddy Reborn's crafting solver, as saved in its settings (Vulcan &gt; Settings &gt; Solver Mode).</summary>
public enum VulcanSolver
{
    Unknown = -1,
    PureRaphael = 0,
    Standard = 1,
    ProgressOnly = 2,
}

/// <summary>
/// Reads (never writes) GatherBuddy Reborn's saved settings to see whether its crafter will go for quality.
/// GatherBuddy saves the file as soon as a setting changes, so this stays current.
/// </summary>
public sealed class GatherBuddySettings
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    private readonly FileInfo file;
    private DateTime lastCheck;
    private DateTime stamp;
    private VulcanSolver solver = VulcanSolver.Unknown;
    private bool goHomeWhenDone;
    private bool goHomeWhenIdle;

    public GatherBuddySettings(DirectoryInfo sellWiseConfigDir)
        => file = new FileInfo(Path.Combine(sellWiseConfigDir.Parent?.FullName ?? sellWiseConfigDir.FullName, "GatherBuddyReborn.json"));

    public VulcanSolver Solver
    {
        get
        {
            Refresh();
            return solver;
        }
    }

    /// <summary>
    /// Why GatherBuddy spends extra teleports during SellWise jobs, or null. Its auto-gather "Go home when done" (and
    /// "when idle") run Lifestream's '/li auto', which takes you to your house or the inn; SellWise then has to teleport
    /// again to the quest giver.
    /// </summary>
    public string? ExtraTeleports
    {
        get
        {
            Refresh();
            if (!goHomeWhenDone && !goHomeWhenIdle) return null;
            var which = goHomeWhenDone && goHomeWhenIdle ? "\"Go home when done\" and \"Go home when idle\" are" : goHomeWhenDone ? "\"Go home when done\" is" : "\"Go home when idle\" is";
            return $"GatherBuddy's {which} on, so after gathering it teleports you home (the inn, if you have no house) before SellWise " +
                   "teleports you again to the quest giver. Turn it off in /gbr, Config tab, to save the extra teleport.";
        }
    }

    /// <summary>Why Vulcan won't max quality with the current setting, or null if it will.</summary>
    public string? QualityProblem => Solver switch
    {
        VulcanSolver.PureRaphael =>
            "GatherBuddy's crafter is set to Pure Raphael. It plans each rotation for normal-quality materials, so when HQ parts " +
            "(like the ones it just crafted) go into a craft it finds no plan and falls back to Progress Only: no quality at all.",
        VulcanSolver.ProgressOnly =>
            "GatherBuddy's crafter is set to Progress Only, so nothing it crafts gets any quality.",
        _ => null,
    };

    public const string Fix = "Open /vulcan, go to Settings, and set Solver Mode to Standard Solver. It decides every step as it goes, so it maxes quality whatever the craft starts at.";

    private void Refresh()
    {
        var now = DateTime.UtcNow;
        if (now - lastCheck < CheckEvery) return;
        lastCheck = now;

        try
        {
            file.Refresh();
            if (!file.Exists)
            {
                solver = VulcanSolver.Unknown;
                return;
            }
            if (file.LastWriteTimeUtc == stamp) return;
            stamp = file.LastWriteTimeUtc;

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            solver = doc.RootElement.TryGetProperty("RaphaelSolverConfig", out var raphael)
                     && raphael.TryGetProperty("SolverMode", out var mode) && mode.TryGetInt32(out var value)
                ? (VulcanSolver)value
                : VulcanSolver.PureRaphael; // GatherBuddy's default when the setting has never been saved

            // Both default to on in GatherBuddy.
            goHomeWhenDone = goHomeWhenIdle = true;
            if (doc.RootElement.TryGetProperty("AutoGatherConfig", out var gather))
            {
                if (gather.TryGetProperty("GoHomeWhenDone", out var done) && done.ValueKind is JsonValueKind.True or JsonValueKind.False) goHomeWhenDone = done.GetBoolean();
                if (gather.TryGetProperty("GoHomeWhenIdle", out var idle) && idle.ValueKind is JsonValueKind.True or JsonValueKind.False) goHomeWhenIdle = idle.GetBoolean();
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Debug(e, "Couldn't read GatherBuddy's settings");
            solver = VulcanSolver.Unknown;
        }
    }
}
