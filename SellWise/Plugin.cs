using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using SellWise.Core;
using SellWise.Services;
using SellWise.Windows;

namespace SellWise;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/sellwise";
    private const string ShortCommand = "/sw";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;

    public Configuration Config { get; }
    public ItemCatalog Catalog { get; }
    public InventoryTracker Tracker { get; }
    public MarketService Market { get; }
    public AdviceService Advice { get; }
    public BellNavigator Navigator { get; }
    public ProfitScanner Scanner { get; }
    public CraftCoordinator Crafter { get; }
    public CityTeleporter Teleporter { get; }
    public RepairService Repair { get; }
    public QualityService Quality { get; }
    public JobEstimator Estimator { get; }
    public CordialService Cordials { get; }
    public GatherBuddySettings GbrSettings { get; }
    public ScripService Scrips { get; }
    public ScripTracker ScripTracker { get; }
    public TurnInService TurnIn { get; }
    public JobQuestService JobQuests { get; }
    public QuestTravel Travel { get; }
    public MobDropService MobDrops { get; }
    public CombatAssist Combat { get; }
    public MobHunter Hunter { get; }
    public JobRunner Runner { get; }

    private readonly WindowSystem windows = new("SellWise");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly JobStatusWindow jobWindow;
    private CraftJob? lastJob;
    private CraftJobState? lastJobState;
    private IDtrBarEntry? dtrEntry;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);
        try
        {
            Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Catalog = new ItemCatalog();
            Tracker = new InventoryTracker();
            Market = new MarketService();
            Advice = new AdviceService(Config, Tracker, Market, Catalog);
            Navigator = new BellNavigator();
            Scanner = new ProfitScanner(Config, Market, Catalog);
            Teleporter = new CityTeleporter();
            Repair = new RepairService(Config, Teleporter);
            Travel = new QuestTravel();
            MobDrops = new MobDropService(PluginInterface.ConfigDirectory);
            Combat = new CombatAssist();
            Hunter = new MobHunter(Travel, Combat);
            Crafter = new CraftCoordinator(Tracker, Repair, Travel);
            Crafter.Busy = () => Hunter.IsBusy ? "Stop the hunt first." : null;
            Runner = new JobRunner(Crafter, Hunter, MobDrops, Tracker);
            Quality = new QualityService(Config, Market, Tracker, Scanner);
            Estimator = new JobEstimator(this);
            Cordials = new CordialService(Config);
            GbrSettings = new GatherBuddySettings(PluginInterface.ConfigDirectory);
            Scrips = new ScripService(Config, Scanner, Market, Catalog);
            ScripTracker = new ScripTracker(Config, Tracker, () => Scrips.Db);
            TurnIn = new TurnInService(Teleporter, () => Scrips.Db);
            Scrips.EnsureLoaded();
            JobQuests = new JobQuestService(Scanner, Market, Tracker);
            Theme.SetAccent(Config.Accent);
            Theme.InitFonts(PluginInterface.UiBuilder);
            Crafter.Finished += OnCraftFinished;

            mainWindow = new MainWindow(this);
            configWindow = new ConfigWindow(this);
            windows.AddWindow(mainWindow);
            windows.AddWindow(configWindow);
            jobWindow = new JobStatusWindow(this);
            windows.AddWindow(jobWindow);

            var info = new CommandInfo(OnCommand) { HelpMessage = "Open SellWise. \"/sellwise config\" opens settings, \"/sellwise city\" teleports to a random unlocked major city, \"/sellwise bell\" walks to the nearest summoning bell, \"/sellwise craft\" opens the profit finder, \"/sellwise recipe\" opens any-recipe crafting, \"/sellwise quests\" opens job quests, \"/sellwise repair\" repairs your gear, \"/sellwise scrips\" opens scrip farming, \"/sellwise turnin\" turns in crafter collectables, \"/sellwise stop\" stops everything SellWise started." };
            CommandManager.AddHandler(Command, info);
            CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand) { HelpMessage = "Alias for /sellwise.", ShowInHelp = false });

            PluginInterface.UiBuilder.Draw += windows.Draw;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMain;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
            Framework.Update += OnFrameworkUpdate;
            ClientState.Login += Advice.OnHomeWorldChanged;
    
        }
        catch
        {
            // Dalamud never calls Dispose on a plugin that failed to start, so undo what did start
            // (ECommons, hooks, handlers) before passing the error on; otherwise it lingers in the game.
            Shutdown();
            throw;
        }
    }

    public void ToggleMain() => mainWindow.Toggle();
    public void ToggleConfig() => configWindow.Toggle();
    public void ShowCraft() => mainWindow.ShowCraftTab();
    public void ShowScrips() => mainWindow.ShowScripTab();
    public void ShowRecipe() => mainWindow.ShowRecipeTab();

    /// <summary>A job has started: tuck the main window away and follow along in the small progress window.</summary>
    public void MinimizeToJob()
    {
        mainWindow.IsOpen = false;
        jobWindow.IsOpen = true;
    }

    /// <summary>
    /// Starts a gather-and-craft job: hunts any monster-only materials that aren't in your bags first, then runs
    /// <paramref name="launch"/> (GatherBuddy and Artisan). Opens the progress window when it's under way.
    /// </summary>
    public string? StartJob(string what, IReadOnlyList<(CraftOpportunity Plan, int Crafts)> plans, Func<string?> launch,
        IReadOnlyList<QueuedJob>? gatherAllFirst = null)
    {
        var error = Runner.Start(what, plans, launch, gatherAllFirst);
        if (error == null) MinimizeToJob();
        return error;
    }

    /// <summary>
    /// Hunts monsters for a material that only drops from them: picks the easiest spot you can reach and goes.
    /// Returns an error, or null once the hunt has started.
    /// </summary>
    public string? StartHunt(uint itemId, string itemName, int more)
    {
        if (Crafter.IsRunning) return "Finish or stop the craft job first.";
        if (MobDrops.Spots(itemId) is not { } spots) return "Still looking up which monsters drop it...";
        var (spot, problem) = MobHunter.Choose(spots);
        if (spot == null) return problem;
        var error = Hunter.Start(itemId, itemName, more, spot);
        if (error == null) MinimizeToJob();
        return error;
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
            case "settings":
                ToggleConfig();
                break;
            case "bell":
                Navigator.GoToNearestBell();
                mainWindow.IsOpen = true;
                break;
            case "city":
                Teleporter.TeleportToRandomCity(Config.DisabledTeleportCities);
                break;
            case "repair":
                Repair.Start();
                break;
            case "stop":
                Repair.Stop();
                TurnIn.Stop();
                Runner.Stop();
                Hunter.Stop();
                Crafter.Stop();
                Navigator.Stop();
                break;
            case "craft":
                mainWindow.ShowCraftTab();
                break;
            case "scrips":
                mainWindow.ShowScripTab();
                break;
            case "quests":
                mainWindow.ShowQuestTab();
                break;
            case "recipe":
                mainWindow.ShowRecipeTab();
                break;
            case "turnin":
                TurnIn.Start();
                break;
            case "refresh":
                Advice.RefreshPrices(force: true);
                mainWindow.IsOpen = true;
                break;
            default:
                ToggleMain();
                break;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn) return;

        Tracker.Update();
        Advice.Update(mainWindow.IsOpen);
        Navigator.Update(Config.TargetBellOnArrival);
        Repair.Update();
        Quality.Update();
        Travel.Update();
        Hunter.Update();
        Runner.Update();
        Crafter.Update();
        TurnIn.Update();
        ScripTracker.Update();
        JobQuests.Update();

        // Pop the status window up when a new job starts, and bring SellWise back if it fails.
        if (Crafter.Job != lastJob)
        {
            lastJob = Crafter.Job;
            if (lastJob != null && Config.ShowJobWindow) jobWindow.IsOpen = true;
        }
        var state = Crafter.Job?.State;
        if (state == CraftJobState.Failed && lastJobState != CraftJobState.Failed) mainWindow.ShowCraftTab();
        lastJobState = state;
        Cordials.Update(Crafter.IsRunning && !(Crafter.Job?.WaitingForRepair ?? false));
        UpdateDtr();
    }

    private void OnCraftFinished(CraftJob job)
    {
        if (Runner.IsBusy) return; // part of a bigger job (gathering for several items): keep going

        if (Crafter.Queued > 0) return; // more queued: carry on without popping SellWise back up

        if (job.Opportunity.Recipe.CollectableQuality != null)
        {
            // Scrip collectables: take them to the appraiser.
            if (Config.TurnInAfterScripJob && job.State == CraftJobState.Finished) TurnIn.Start();
            mainWindow.ShowScripTab();
            return;
        }

        if (job.GatherOnly)
        {
            mainWindow.ShowRecipeTab();
            return;
        }

        // Price what was just made right away, rather than waiting for the next auto refresh.
        Advice.RefreshItem(job.Opportunity.Item.Id);
        mainWindow.ShowCraftTab();
    }

    private void UpdateDtr()
    {
        var undercut = Advice.Plan.Listings.Count(r => r.Verdict == Verdict.Relist);
        if (!Config.ShowDtr || undercut == 0)
        {
            if (dtrEntry != null) dtrEntry.Shown = false;
            return;
        }

        dtrEntry ??= DtrBar.Get("SellWise");
        dtrEntry.Shown = true;
        dtrEntry.Text = $"SellWise: {undercut} undercut";
        dtrEntry.Tooltip = "Some of your retainer listings have been undercut. Click to open SellWise.";
        dtrEntry.OnClick ??= _ => mainWindow.IsOpen = true;
    }

    public void Dispose() => Shutdown();

    /// <summary>Tears down everything that was set up. Safe on a partly constructed plugin; one failing step doesn't stop the rest.</summary>
    private void Shutdown()
    {
        Step(() => Framework.Update -= OnFrameworkUpdate);
        Step(() => { if (Crafter != null) Crafter.Finished -= OnCraftFinished; });
        Step(() => { if (Advice != null) ClientState.Login -= Advice.OnHomeWorldChanged; });
        Step(() => PluginInterface.UiBuilder.Draw -= windows.Draw);
        Step(() => PluginInterface.UiBuilder.OpenMainUi -= ToggleMain);
        Step(() => PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig);
        Step(() => CommandManager.RemoveHandler(Command));
        Step(() => CommandManager.RemoveHandler(ShortCommand));

        Step(() => dtrEntry?.Remove());
        Step(() => windows.RemoveAllWindows());
        Step(Theme.DisposeFonts);
        Step(() => Hunter?.Stop());
        Step(() => MobDrops?.Dispose());
        Step(() => Scrips?.Dispose());
        Step(() => Scanner?.Dispose());
        Step(() => Market?.Dispose());
        Step(() => Tracker?.Dispose());
        Step(ECommonsMain.Dispose);
    }

    private static void Step(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Warning(e, "SellWise shutdown step failed");
        }
    }
}
