using System;
using System.Linq;
using Dalamud.Plugin.Ipc;

namespace SellWise.Services;

/// <summary>
/// Turns a rotation plugin's auto-rotation on while SellWise hunts, set to attack the monster SellWise targets.
/// WrathCombo is preferred (it lends control through a lease and restores your settings afterwards); RotationSolver
/// Reborn's Manual mode is the fallback. SellWise never fights on its own.
/// </summary>
public sealed class CombatAssist
{
    // Values from WrathCombo.API: AutoRotationConfigOption, DPSRotationMode, SetResult.
    private enum WrathOption { InCombatOnly = 0, DPSRotationMode = 1, OnlyAttackInCombat = 13 }
    private enum WrathResult { Ignored = -1, Okay = 0, OkayWorking = 1, Duplicate = 13 }
    private const int WrathDpsManual = 0;

    // RotationSolver Reborn's StateCommandType.
    private const byte RsrOff = 0, RsrManual = 3;

    private readonly ICallGateSubscriber<string, string, Guid?> wrathRegister;
    private readonly ICallGateSubscriber<Guid, bool, WrathResult> wrathSetState;
    private readonly ICallGateSubscriber<Guid, WrathResult> wrathJobReady;
    private readonly ICallGateSubscriber<Guid, WrathOption, object, WrathResult> wrathConfig;
    private readonly ICallGateSubscriber<Guid, object> wrathRelease;
    private readonly ICallGateSubscriber<byte, object> rsrMode;
    private Guid? lease;
    private string? active;

    public CombatAssist()
    {
        var pi = Plugin.PluginInterface;
        wrathRegister = pi.GetIpcSubscriber<string, string, Guid?>("WrathCombo.RegisterForLease");
        wrathSetState = pi.GetIpcSubscriber<Guid, bool, WrathResult>("WrathCombo.SetAutoRotationState");
        wrathJobReady = pi.GetIpcSubscriber<Guid, WrathResult>("WrathCombo.SetCurrentJobAutoRotationReady");
        wrathConfig = pi.GetIpcSubscriber<Guid, WrathOption, object, WrathResult>("WrathCombo.SetAutoRotationConfigState");
        wrathRelease = pi.GetIpcSubscriber<Guid, object>("WrathCombo.ReleaseControl");
        rsrMode = pi.GetIpcSubscriber<byte, object>("RotationSolverReborn.ChangeOperatingMode");
    }

    private static bool Loaded(string name) => Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == name && p.IsLoaded);

    public static bool WrathAvailable => Loaded("WrathCombo");
    public static bool RsrAvailable => Loaded("RotationSolver");
    public static bool Available => WrathAvailable || RsrAvailable;

    /// <summary>Which plugin is fighting, for status text.</summary>
    public string? Active => active;

    /// <summary>Starts auto-rotation on the current target. Returns an error, or null when it's on.</summary>
    public string? Enable()
    {
        if (active != null) return null;
        if (WrathAvailable)
        {
            try
            {
                // Always start from a fresh lease: WrathCombo hands a plugin back its old lease when it registers
                // again, and a lease left half set up (by an earlier failed start) throws on the next use.
                if (wrathRegister.InvokeFunc("SellWise", "SellWise") is { } leftover) wrathRelease.InvokeAction(leftover);
                lease = wrathRegister.InvokeFunc("SellWise", "SellWise");
                if (lease is not { } l) return "WrathCombo wouldn't lend its auto-rotation (check its IPC settings, or whether you revoked SellWise).";

                // Order matters: WrathCombo must be told to turn auto-rotation on before any of its options are set,
                // or it throws (it looks up the on/off entry as soon as a lease controls an option).
                var result = wrathSetState.InvokeFunc(l, true);
                if (result is not (WrathResult.Okay or WrathResult.OkayWorking or WrathResult.Duplicate))
                {
                    ReleaseWrath();
                    return $"WrathCombo refused to turn on auto-rotation ({result}).";
                }
                wrathJobReady.InvokeFunc(l);
                wrathConfig.InvokeFunc(l, WrathOption.InCombatOnly, false);
                wrathConfig.InvokeFunc(l, WrathOption.OnlyAttackInCombat, false);
                wrathConfig.InvokeFunc(l, WrathOption.DPSRotationMode, WrathDpsManual); // attack what SellWise targets
                active = "WrathCombo";
                return null;
            }
            catch (Exception e)
            {
                Plugin.Log.Warning(e, "WrathCombo IPC failed");
                ReleaseWrath(); // a half-set-up lease would fail the same way next time
                if (!RsrAvailable)
                    return $"WrathCombo wouldn't start its auto-rotation ({(e.InnerException ?? e).Message}). " +
                           "Try again, or check WrathCombo's auto-rotation settings.";
            }
        }
        if (RsrAvailable)
        {
            try
            {
                rsrMode.InvokeAction(RsrManual); // attacks whatever is targeted
                active = "RotationSolver";
                return null;
            }
            catch (Exception e)
            {
                Plugin.Log.Warning(e, "RotationSolver IPC failed");
                return $"Couldn't talk to RotationSolver: {e.Message}";
            }
        }
        return "Hunting needs WrathCombo or RotationSolver Reborn to do the fighting.";
    }

    /// <summary>Gives the WrathCombo lease back, so the next attempt starts from a clean one.</summary>
    private void ReleaseWrath()
    {
        if (lease is not { } l) return;
        try
        {
            wrathRelease.InvokeAction(l);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Releasing the WrathCombo lease failed");
        }
        lease = null;
    }

    /// <summary>Hands auto-rotation back (WrathCombo restores your own settings).</summary>
    public void Disable()
    {
        try
        {
            if (active == "WrathCombo" && lease is { } l)
            {
                wrathRelease.InvokeAction(l);
                lease = null;
            }
            else if (active == "RotationSolver")
            {
                rsrMode.InvokeAction(RsrOff);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Turning auto-rotation off failed");
        }
        active = null;
    }
}
