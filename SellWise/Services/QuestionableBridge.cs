using System;
using System.Globalization;
using System.Linq;
using Dalamud.Plugin.Ipc;

namespace SellWise.Services;

/// <summary>
/// Hands a quest to Questionable, which walks, talks, accepts and turns it in (and runs any gathering or crafting
/// steps its route has). SellWise only tells it which quest and watches for the quest to complete.
/// </summary>
public sealed class QuestionableBridge
{
    private readonly ICallGateSubscriber<string, bool> startSingle;
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<string, bool> stop;

    public QuestionableBridge()
    {
        var pi = Plugin.PluginInterface;
        startSingle = pi.GetIpcSubscriber<string, bool>("Questionable.StartSingleQuest");
        isRunning = pi.GetIpcSubscriber<bool>("Questionable.IsRunning");
        stop = pi.GetIpcSubscriber<string, bool>("Questionable.Stop");
    }

    public static bool Available => Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == "Questionable" && p.IsLoaded);

    /// <summary>Questionable's quest IDs are the Quest row minus 65536 ("Supplies for the Sick" is 140).</summary>
    public static string IdOf(uint questRow) => (questRow - 65536).ToString(CultureInfo.InvariantCulture);

    /// <summary>Starts one quest. False when Questionable has no route for it (or isn't loaded).</summary>
    public bool Start(uint questRow)
    {
        try
        {
            return startSingle.InvokeFunc(IdOf(questRow));
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Questionable IPC failed");
            return false;
        }
    }

    public bool IsRunning
    {
        get
        {
            try { return isRunning.InvokeFunc(); } catch { return false; }
        }
    }

    public void Stop()
    {
        try { stop.InvokeFunc("SellWise"); } catch { /* not loaded */ }
    }
}
