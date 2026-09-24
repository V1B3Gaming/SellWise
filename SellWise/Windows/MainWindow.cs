using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using SellWise.Core;
using SellWise.Services;

namespace SellWise.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1);
    private static readonly Vector4 Lime = new(0.75f, 0.85f, 0.40f, 1);
    private static readonly Vector4 Orange = new(1.00f, 0.65f, 0.30f, 1);
    private static readonly Vector4 Blue = new(0.50f, 0.75f, 1.00f, 1);
    private static readonly Vector4 Red = new(1.00f, 0.40f, 0.40f, 1);
    private static readonly Vector4 Yellow = new(1.00f, 0.85f, 0.35f, 1);
    private static readonly Vector4 Grey = new(0.60f, 0.60f, 0.60f, 1);

    private static readonly string[] SortModes = ["Recommended", "Net gil", "Gil per day", "Fastest to sell", "Name"];

    private readonly Plugin plugin;
    private readonly CraftTab craftTab;
    private bool selectCraftTab;
    private string search = "";
    private int sortMode;
    private bool showList = true, showSlow = true, showHold = true, showVendor = true, showOther;

    public MainWindow(Plugin plugin) : base("SellWise###SellWiseMain")
    {
        this.plugin = plugin;
        craftTab = new CraftTab(plugin);
        Size = new Vector2(1150, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(780, 360), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    private Configuration Config => plugin.Config;

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();

        using var tabs = ImRaii.TabBar("##sellwiseTabs");
        if (!tabs) return;

        var plan = plugin.Advice.Plan;
        using (var tab = ImRaii.TabItem("What to sell"))
            if (tab) DrawStacks(plan);

        var undercut = plan.Listings.Count(r => r.Verdict == Verdict.Relist);
        using (var tab = ImRaii.TabItem(undercut > 0 ? $"My listings ({undercut} undercut)###listings" : "My listings###listings"))
            if (tab) DrawListings(plan);

        var craftFlags = selectCraftTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        selectCraftTab = false;
        var craftLabel = plugin.Crafter.IsRunning ? "Craft for profit (running)###craft" : "Craft for profit###craft";
        using (var tab = ImRaii.TabItem(craftLabel, craftFlags))
            if (tab) craftTab.Draw();

        using (var tab = ImRaii.TabItem("Retainers"))
            if (tab) DrawRetainers();
    }

    public void ShowCraftTab()
    {
        IsOpen = true;
        selectCraftTab = true;
    }

    private void DrawHeader()
    {
        var advice = plugin.Advice;
        var market = plugin.Market;
        var tracker = plugin.Tracker;

        var world = advice.PricingWorld;
        ImGui.TextUnformatted(world.Length == 0 ? "Waiting for character data..." : $"Pricing on {world}" + (advice.DataCenter.Length > 0 ? $" ({advice.DataCenter})" : ""));
        ImGui.SameLine();
        ImGui.TextDisabled($"  {market.Status}" + (market.LastRefresh is { } t ? $" ({Ago(t.UtcDateTime)})" : ""));

        var retainers = tracker.Retainers;
        var scanned = retainers.Count(r => r.ScannedUtc != null);
        var known = tracker.KnownRetainerCount ?? retainers.Count;
        ImGui.TextUnformatted($"Retainers scanned: {scanned}/{known}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"  Free market slots: {tracker.FreeListingSlots}");
        if (known == 0 || scanned < known)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, known == 0
                ? "  Visit a summoning bell to load your retainers."
                : "  Open the missing retainers at a bell to include their items.");
        }

        using (ImRaii.Disabled(market.IsBusy || world.Length == 0))
        {
            if (ImGui.Button(market.IsBusy ? "Fetching..." : "Refresh prices"))
                advice.RefreshPrices(force: true);
        }

        ImGui.SameLine();
        if (ImGui.Button("Teleport to a city"))
            plugin.Teleporter.TeleportToRandomCity(Config.DisabledTeleportCities);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Teleports to a random major city you've unlocked (all have market boards and summoning bells).\nChoose which cities under Settings.");

        ImGui.SameLine();
        var nav = plugin.Navigator;
        if (nav.IsTravelling)
        {
            if (ImGui.Button("Stop walking")) nav.Stop();
        }
        else
        {
            using (ImRaii.Disabled(!BellNavigator.VnavmeshLoaded))
            {
                if (ImGui.Button("Walk to nearest bell")) nav.GoToNearestBell();
            }

            if (!BellNavigator.VnavmeshLoaded && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Install and enable vnavmesh to use this.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Settings")) plugin.ToggleConfig();

        if (ImGui.IsItemHovered() && BellNavigator.VnavmeshLoaded)
            ImGui.SetTooltip("Only moves when you click it: walks to the closest bell in your current zone.");

        var status = plugin.Teleporter.Status.Length > 0 && nav.Status.Length == 0 ? plugin.Teleporter.Status : nav.Status;
        if (status.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(status);
        }
    }

    private void DrawStacks(AdvicePlan plan)
    {
        var all = plan.Stacks;
        var list = all.Where(r => r.Verdict == Verdict.List).ToList();
        var vendor = all.Where(r => r.Verdict == Verdict.Vendor).ToList();
        ImGui.TextColored(Green, $"List now: {list.Count} ({list.Sum(r => r.NetTotal):N0} gil net)");
        ImGui.SameLine();
        ImGui.TextColored(Lime, $"  Slow: {all.Count(r => r.Verdict == Verdict.ListSlow)}");
        ImGui.SameLine();
        ImGui.TextColored(Orange, $"  Hold: {all.Count(r => r.Verdict == Verdict.Hold)}");
        ImGui.SameLine();
        ImGui.TextColored(Blue, $"  Vendor: {vendor.Count} ({vendor.Sum(r => r.VendorTotal):N0} gil)");
        ImGui.SameLine();
        ImGui.TextDisabled("  * = best picks for your free slots");

        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##search", "Search items", ref search, 100);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        using (var combo = ImRaii.Combo("##sort", SortModes[sortMode]))
        {
            if (combo)
            {
                for (var i = 0; i < SortModes.Length; i++)
                    if (ImGui.Selectable(SortModes[i], i == sortMode)) sortMode = i;
            }
        }

        ImGui.SameLine(); ImGui.Checkbox("List", ref showList);
        ImGui.SameLine(); ImGui.Checkbox("Slow", ref showSlow);
        ImGui.SameLine(); ImGui.Checkbox("Hold", ref showHold);
        ImGui.SameLine(); ImGui.Checkbox("Vendor", ref showVendor);
        ImGui.SameLine(); ImGui.Checkbox("Other", ref showOther);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("No data, untradable, and items that can't go on the market board.");

        var rows = Sort(all.Where(Visible)).ToList();

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
                                      ImGuiTableFlags.Resizable | ImGuiTableFlags.Hideable | ImGuiTableFlags.SizingFixedFit;
        using var table = ImRaii.Table("##stacks", 11, flags, new Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("Where");
        ImGui.TableSetupColumn("Verdict");
        ImGui.TableSetupColumn("List at");
        ImGui.TableSetupColumn("Net gil");
        ImGui.TableSetupColumn("Vendor");
        ImGui.TableSetupColumn("Days");
        ImGui.TableSetupColumn("Sold/day");
        ImGui.TableSetupColumn("Recent median");
        ImGui.TableSetupColumn("Lowest listing");
        ImGui.TableHeadersRow();

        foreach (var r in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawItemName(r, r.FitsFreeSlot ? "* " : "");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Quantity.ToString("N0"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Where);

            ImGui.TableNextColumn();
            DrawVerdict(r);

            ImGui.TableNextColumn();
            DrawPrice(r.SuggestedPrice, r);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.NetTotal > 0 ? r.NetTotal.ToString("N0") : "-");

            ImGui.TableNextColumn();
            if (r.VendorTotal > 0) ImGui.TextColored(r.Verdict == Verdict.Vendor ? Blue : Grey, r.VendorTotal.ToString("N0"));
            else ImGui.TextDisabled("-");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatDays(r.EstDays, r.SuggestedPrice != null));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Market?.HasData == true ? r.UnitsPerDay.ToString("0.##") : "-");

            ImGui.TableNextColumn();
            DrawMedian(r);

            ImGui.TableNextColumn();
            DrawLowest(r);
        }
    }

    private void DrawListings(AdvicePlan plan)
    {
        var listings = plan.Listings;
        if (listings.Count == 0)
        {
            ImGui.TextWrapped("No listings known. Open your retainers at a summoning bell (\"Sell items in your inventory on the market\") so SellWise can read them.");
            return;
        }

        var relist = listings.Count(r => r.Verdict == Verdict.Relist);
        var raise = listings.Count(r => r.Verdict == Verdict.Raise);
        ImGui.TextColored(relist > 0 ? Red : Green, $"{relist} undercut");
        ImGui.SameLine();
        ImGui.TextColored(raise > 0 ? Yellow : Grey, $"  {raise} could be priced higher");
        ImGui.SameLine();
        ImGui.TextDisabled($"  {listings.Count} listings, {listings.Sum(r => (long)r.Listing!.UnitPrice * r.Quantity):N0} gil on the board. Competitor prices come from Universalis and are only as fresh as the last upload.");

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
                                      ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
        using var table = ImRaii.Table("##listings", 9, flags, new Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Retainer");
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("Your price");
        ImGui.TableSetupColumn("Verdict");
        ImGui.TableSetupColumn("Change to");
        ImGui.TableSetupColumn("Lowest other");
        ImGui.TableSetupColumn("Recent median");
        ImGui.TableSetupColumn("Data age");
        ImGui.TableHeadersRow();

        foreach (var r in listings.OrderByDescending(r => r.Priority).ThenBy(r => r.Where).ThenBy(r => r.Item.Name))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Where);

            ImGui.TableNextColumn();
            DrawItemName(r, "");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Quantity.ToString("N0"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Listing!.UnitPrice.ToString("N0"));

            ImGui.TableNextColumn();
            DrawVerdict(r);

            ImGui.TableNextColumn();
            DrawPrice(r.SuggestedPrice, r);

            ImGui.TableNextColumn();
            DrawLowest(r);

            ImGui.TableNextColumn();
            DrawMedian(r);

            ImGui.TableNextColumn();
            if (r.Market is { HasData: true } m) ImGui.TextDisabled(Ago(m.LastUpload.UtcDateTime));
            else ImGui.TextDisabled("-");
        }
    }

    private void DrawRetainers()
    {
        var tracker = plugin.Tracker;
        ImGui.TextWrapped("The game only exposes a retainer's inventory while you're talking to it. Open each retainer at a summoning bell " +
                          "(the retainer menu is enough, and opening the sell list reads its listings too). SellWise saves what it sees " +
                          "until the next visit.");

        if (tracker.Character is { } character)
            ImGui.TextDisabled($"Saddlebag: {(character.SaddlebagScannedUtc is { } s ? $"scanned {Ago(s)}" : "not scanned yet (open it once)")}");

        using var table = ImRaii.Table("##retainers", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit);
        if (!table) return;

        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Last scanned");
        ImGui.TableSetupColumn("Item stacks");
        ImGui.TableSetupColumn("Listings");
        ImGui.TableSetupColumn("Gil");
        ImGui.TableSetupColumn("");
        ImGui.TableHeadersRow();

        foreach (var r in tracker.Retainers)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Name.Length > 0 ? r.Name : r.Id.ToString("X"));

            ImGui.TableNextColumn();
            if (r.ScannedUtc is { } t) ImGui.TextUnformatted(Ago(t));
            else ImGui.TextColored(Yellow, "never");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.ScannedUtc != null ? r.Items.Count.ToString() : "-");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{r.MarketItemCount}/{InventoryTracker.ListingSlotsPerRetainer}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Gil.ToString("N0"));

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Forget##{r.Id}")) tracker.ForgetRetainer(r.Id);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Drop this retainer's saved snapshot. It will be rescanned next time you open it.");
        }
    }

    private void DrawItemName(Recommendation r, string prefix)
    {
        var size = new Vector2(ImGui.GetTextLineHeight());
        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(r.Item.Icon, r.Hq)).GetWrapOrEmpty();
        ImGui.Image(icon.Handle, size);
        ImGui.SameLine();

        var label = $"{prefix}{r.Item.Name}{(r.Hq ? " (HQ)" : "")}##{r.Item.Id}{r.Hq}{r.Listing?.RetainerId}{r.Listing?.Slot}";
        ImGui.Selectable(label);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Right-click for options");

        using var popup = ImRaii.ContextPopupItem($"##ctx{label}");
        if (!popup) return;

        if (r.SuggestedPrice is { } p && ImGui.MenuItem($"Copy price ({p:N0})"))
            ImGui.SetClipboardText(p.ToString());
        if (ImGui.MenuItem("Copy item name"))
            ImGui.SetClipboardText(r.Item.Name);
        if (ImGui.MenuItem("Open on Universalis"))
            Util.OpenLink($"https://universalis.app/market/{r.Item.Id}");
        if (r.Listing == null && ImGui.MenuItem("Ignore this item"))
        {
            Config.IgnoredItems.Add(r.Item.Id);
            Config.Save();
        }
    }

    private static void DrawVerdict(Recommendation r)
    {
        var (text, color) = r.Verdict switch
        {
            Verdict.List => ("List", Green),
            Verdict.ListSlow => ("List (slow)", Lime),
            Verdict.Hold => ("Hold", Orange),
            Verdict.Vendor => ("Vendor", Blue),
            Verdict.NoData => ("No data", Grey),
            Verdict.Untradable => ("Untradable", Grey),
            Verdict.NotMarketable => ("Not on MB", Grey),
            Verdict.Relist => ("Undercut", Red),
            Verdict.Raise => ("Raise price", Yellow),
            Verdict.ListingOk => ("OK", Green),
            _ => (r.Verdict.ToString(), Grey),
        };

        ImGui.TextColored(color, text);
        if (ImGui.IsItemHovered() && r.Reason.Length > 0)
        {
            using var tip = ImRaii.Tooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
            ImGui.TextUnformatted(r.Reason);
            ImGui.PopTextWrapPos();
        }
    }

    private static void DrawPrice(uint? price, Recommendation r)
    {
        if (price is not { } p)
        {
            ImGui.TextDisabled("-");
            return;
        }

        if (ImGui.Selectable($"{p:N0}##price{r.Item.Id}{r.Hq}{r.Listing?.RetainerId}{r.Listing?.Slot}"))
            ImGui.SetClipboardText(p.ToString());
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to copy");
    }

    private static void DrawMedian(Recommendation r)
    {
        if (r.FairPrice is { } f)
        {
            ImGui.TextUnformatted(f.ToString("N0"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Median of {r.FairSampleSize} recent {(r.Hq ? "HQ " : "")}sales");
        }
        else
        {
            ImGui.TextDisabled("-");
        }
    }

    private static void DrawLowest(Recommendation r)
    {
        if (r.LowestCompetitor is { } l) ImGui.TextUnformatted(l.ToString("N0"));
        else ImGui.TextDisabled(r.Market?.HasData == true ? "none" : "-");

        if (r.Market?.DataCenter is { } dc && ImGui.IsItemHovered())
        {
            var (min, worldName) = r.Hq ? (dc.MinHq, dc.CheapestWorldHq) : (dc.MinNq, dc.CheapestWorldNq);
            ImGui.SetTooltip(min is { } m
                ? $"Cheapest on your data center: {m:N0}{(worldName != null ? $" on {worldName}" : "")}.\nBuyers who world-visit will compare against this."
                : "No listings of this quality on your data center.");
        }
    }

    private bool Visible(Recommendation r)
    {
        if (search.Length > 0 && !r.Item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            return false;

        var shown = r.Verdict switch
        {
            Verdict.List => showList,
            Verdict.ListSlow => showSlow,
            Verdict.Hold => showHold,
            Verdict.Vendor => showVendor,
            _ => showOther,
        };
        if (!shown) return false;

        return r.Verdict is not (Verdict.List or Verdict.ListSlow or Verdict.Hold or Verdict.Vendor)
               || Math.Max(r.NetTotal, r.VendorTotal) >= Config.MinValueGil;
    }

    private IEnumerable<Recommendation> Sort(IEnumerable<Recommendation> rows) => sortMode switch
    {
        1 => rows.OrderByDescending(r => Math.Max(r.NetTotal, r.VendorTotal)),
        2 => rows.OrderByDescending(r => r.Priority),
        3 => rows.OrderBy(r => r.EstDays).ThenByDescending(r => r.NetTotal),
        4 => rows.OrderBy(r => r.Item.Name),
        _ => rows.OrderBy(r => VerdictRank(r.Verdict)).ThenByDescending(r => r.Verdict == Verdict.Vendor ? r.VendorTotal : r.Priority),
    };

    private static int VerdictRank(Verdict v) => v switch
    {
        Verdict.List => 0,
        Verdict.ListSlow => 1,
        Verdict.Hold => 2,
        Verdict.Vendor => 3,
        Verdict.NoData => 4,
        _ => 5,
    };

    private static string FormatDays(double days, bool priced)
    {
        if (!priced) return "-";
        if (double.IsInfinity(days)) return "never?";
        return days < 1 ? "<1" : days.ToString("0");
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 0 || utc == DateTime.MinValue) return "unknown";
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }
}
