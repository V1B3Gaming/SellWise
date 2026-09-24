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

    private readonly WindowSystem windows = new("SellWise");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly JobStatusWindow jobWindow;
    private CraftJob? lastJob;
    private IDtrBarEntry? dtrEntry;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Catalog = new ItemCatalog();
        Tracker = new InventoryTracker();
        Market = new MarketService();
        Advice = new AdviceService(Config, Tracker, Market, Catalog);
        Navigator = new BellNavigator();
        Scanner = new ProfitScanner(Config, Market, Catalog);
        Teleporter = new CityTeleporter();
        Repair = new RepairService(Config, Teleporter);
        Crafter = new CraftCoordinator(Tracker, Repair);
        Quality = new QualityService(Config, Market, Tracker, Scanner);
        Estimator = new JobEstimator(this);
        Cordials = new CordialService(Config);
        Theme.SetAccent(Config.Accent);
        Theme.InitFonts(PluginInterface.UiBuilder);
        Crafter.Finished += OnCraftFinished;

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        windows.AddWindow(mainWindow);
        windows.AddWindow(configWindow);
        jobWindow = new JobStatusWindow(this);
        windows.AddWindow(jobWindow);

        var info = new CommandInfo(OnCommand) { HelpMessage = "Open SellWise. \"/sellwise config\" opens settings, \"/sellwise city\" teleports to a random unlocked major city, \"/sellwise bell\" walks to the nearest summoning bell, \"/sellwise craft\" opens the profit finder, \"/sellwise repair\" repairs your gear, \"/sellwise stop\" stops everything SellWise started." };
        CommandManager.AddHandler(Command, info);
        CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand) { HelpMessage = "Alias for /sellwise.", ShowInHelp = false });

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        Framework.Update += OnFrameworkUpdate;
        ClientState.Login += Advice.OnHomeWorldChanged;
    }

    public void ToggleMain() => mainWindow.Toggle();
    public void ToggleConfig() => configWindow.Toggle();
    public void ShowCraft() => mainWindow.ShowCraftTab();

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
                Crafter.Stop();
                Navigator.Stop();
                break;
            case "craft":
                mainWindow.ShowCraftTab();
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
        Crafter.Update();

        // Pop the status window up when a new job starts.
        if (Crafter.Job != lastJob)
        {
            lastJob = Crafter.Job;
            if (lastJob != null && Config.ShowJobWindow) jobWindow.IsOpen = true;
        }
        Cordials.Update(Crafter.IsRunning && !(Crafter.Job?.WaitingForRepair ?? false));
        UpdateDtr();
    }

    private void OnCraftFinished(CraftJob job)
    {
        // Price what was just made right away, rather than waiting for the next auto refresh.
        Advice.RefreshItem(job.Opportunity.Item.Id);
        mainWindow.IsOpen = true;
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

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        Crafter.Finished -= OnCraftFinished;
        ClientState.Login -= Advice.OnHomeWorldChanged;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        CommandManager.RemoveHandler(Command);
        CommandManager.RemoveHandler(ShortCommand);

        dtrEntry?.Remove();
        windows.RemoveAllWindows();
        Theme.DisposeFonts();
        Scanner.Dispose();
        Market.Dispose();
        Tracker.Dispose();
        ECommonsMain.Dispose();
    }
}
