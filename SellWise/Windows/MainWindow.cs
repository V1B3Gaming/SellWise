using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using SellWise.Core;
using SellWise.Services;

namespace SellWise.Windows;

/// <summary>
/// Sidebar on the left (Sell, Listings, Craft, Retainers, plus city / repair / settings tools),
/// and a list-plus-detail layout for each screen.
/// </summary>
public sealed class MainWindow : Window
{
    private enum View { Sell, Listings, Craft, Retainers }

    private const float NavWidth = 90;
    private const float ListWidth = 430;
    private const float RowHeight = 58;

    private static readonly string[] SellSorts = ["Recommended", "Net gil", "Gil per day", "Fastest to sell", "Name"];

    private readonly Plugin plugin;
    private readonly CraftView craftView;
    private IDisposable? theme;
    private IDisposable? edgeToEdge;
    private View view = View.Sell;

    private string search = "";
    private int sellFilter; // 0 all, 1 list, 2 slow, 3 hold, 4 vendor, 5 other
    private int sellSort;
    private string? selectedSell;
    private string? selectedListing;

    public MainWindow(Plugin plugin) : base("SellWise###SellWiseMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        craftView = new CraftView(plugin);
        Size = new Vector2(1180, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(900, 480), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    private Configuration Config => plugin.Config;

    public void ShowCraftTab()
    {
        IsOpen = true;
        view = View.Craft;
    }

    public override void PreDraw()
    {
        Theme.SetAccent(Config.Accent);
        var world = plugin.Advice.PricingWorld;
        var prices = plugin.Market.LastRefresh is { } t ? $"prices {Theme.Ago(t.UtcDateTime)}" : "no prices yet";
        WindowName = world.Length == 0 ? "SellWise###SellWiseMain" : $"SellWise  ·  {world}  ·  {prices}###SellWiseMain";
        theme = Theme.Push();
        // The main window's own frame has no padding so the sidebar and panes run edge to edge.
        // Popped as soon as Draw starts, so tooltips and popups keep normal padding.
        edgeToEdge = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void PostDraw()
    {
        edgeToEdge?.Dispose();
        edgeToEdge = null;
        theme?.Dispose();
        theme = null;
    }

    public override void Draw()
    {
        edgeToEdge?.Dispose();
        edgeToEdge = null;

        var avail = ImGui.GetContentRegionAvail();
        DrawNav(avail.Y);
        ImGui.SameLine(0, 0);

        using var content = ImRaii.Child("##content", new Vector2(avail.X - NavWidth, avail.Y), false, ImGuiWindowFlags.NoScrollbar);
        if (!content) return;

        switch (view)
        {
            case View.Sell: DrawSell(); break;
            case View.Listings: DrawListings(); break;
            case View.Craft: craftView.Draw(); break;
            case View.Retainers: DrawRetainers(); break;
        }
    }

    // ---- Sidebar ------------------------------------------------------------------------------------

    private void DrawNav(float height)
    {
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, Theme.Bg0);
        using var nav = ImRaii.Child("##nav", new Vector2(NavWidth, height), false, ImGuiWindowFlags.NoScrollbar);
        if (!nav) return;

        var undercut = plugin.Advice.Plan.Listings.Count(r => r.Verdict == Verdict.Relist);
        ImGui.SetCursorPos(new Vector2(7, 10));
        NavItem(View.Sell, FontAwesomeIcon.Coins, "Sell", null);
        NavItem(View.Listings, FontAwesomeIcon.ListUl, "Listings", undercut > 0 ? undercut.ToString() : null);
        NavItem(View.Craft, FontAwesomeIcon.Hammer, "Craft", plugin.Crafter.IsRunning ? "•" : null);
        NavItem(View.Retainers, FontAwesomeIcon.Users, "Retainers", null);

        ImGui.SetCursorPos(new Vector2(7, height - 3 * 60 - 8));
        var cond = RepairService.MinCondition();
        var condColor = plugin.Repair.IsBusy ? Theme.Current.Color : cond < Config.RepairThreshold ? Theme.Bad : cond < 50 ? Theme.Hold : Theme.Text2;

        if (ToolItem("city", FontAwesomeIcon.MapMarkerAlt, "City", Theme.Text2))
            plugin.Teleporter.TeleportToRandomCity(Config.DisabledTeleportCities);
        Tooltip("Teleport to a random major city you've attuned to (market board and summoning bells).\n" +
                (plugin.Teleporter.Status.Length > 0 ? plugin.Teleporter.Status : "Choose cities in Settings."));

        if (ToolItem("repair", FontAwesomeIcon.Wrench, plugin.Repair.IsBusy ? "Stop" : $"{cond}%", condColor))
        {
            if (plugin.Repair.IsBusy) plugin.Repair.Stop();
            else plugin.Repair.Start();
        }
        Tooltip($"Lowest gear durability: {cond}%. Click to repair now.\nAuto-repair below {Config.RepairThreshold}% is {(Config.AutoRepair ? "on" : "off")}."
                + (plugin.Repair.Status.Length > 0 ? $"\n{plugin.Repair.Status}" : ""));

        if (ToolItem("settings", FontAwesomeIcon.Cog, "Settings", Theme.Text2))
            plugin.ToggleConfig();
    }

    private void NavItem(View target, FontAwesomeIcon icon, string label, string? badge)
    {
        var active = view == target;
        if (ToolItem(label, icon, label, active ? Theme.Current.Color : Theme.Text2, active, badge))
            view = target;
    }

    /// <summary>A sidebar button: icon over a small label, filled when active.</summary>
    private static bool ToolItem(string id, FontAwesomeIcon icon, string label, Vector4 color, bool active = false, string? badge = null)
    {
        var size = new Vector2(76, 54);
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##nav{id}", size);
        var hovered = ImGui.IsItemHovered();
        var after = ImGui.GetCursorScreenPos();

        var dl = ImGui.GetWindowDrawList();
        if (active || hovered) dl.AddRectFilled(pos, pos + size, Theme.U32(active ? Theme.Raise : Theme.Panel2), 3f);

        var glyph = Theme.GlyphSize(icon);
        ImGui.SetCursorScreenPos(pos + new Vector2((size.X - glyph.X) / 2, 9));
        Theme.Glyph(icon, color);
        var labelSize = ImGui.CalcTextSize(label);
        dl.AddText(pos + new Vector2((size.X - labelSize.X) / 2, 31), Theme.U32(active ? Theme.Text : Theme.Text2), label);

        if (badge != null)
        {
            var bs = ImGui.CalcTextSize(badge);
            var bpos = pos + new Vector2(size.X - bs.X - 8, 4);
            dl.AddRectFilled(bpos - new Vector2(3, 1), bpos + bs + new Vector2(3, 1), Theme.U32(Theme.Bad), 2f);
            dl.AddText(bpos, Theme.U32(Theme.Bg0), badge);
        }

        ImGui.SetCursorScreenPos(after);
        ImGui.Dummy(new Vector2(0, 0)); // settle the cursor on a real item before the next button
        return clicked;
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }

    // ---- Shared list/detail pieces ------------------------------------------------------------------

    private static ImRaii.ChildDisposable Pane(string id, Vector2 size, Vector4 bg)
    {
        using var c = ImRaii.PushColor(ImGuiCol.ChildBg, bg);
        using var s = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        return ImRaii.Child(id, size, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
    }

    private static void Divider(float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(0, height), Theme.U32(Theme.Line));
    }

    /// <summary>One row in a left-hand list: icon, name and subtitle on the left, value and tag on the right.</summary>
    private static bool ListRow(string id, ushort icon, bool hq, string name, string sub, Vector4 subColor, string value, Vector4 valueColor,
        string? tag, Vector4 tagColor, bool selected, bool dim = false, bool marker = false)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, RowHeight));
        var hovered = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        if (selected)
        {
            dl.AddRectFilled(pos, pos + new Vector2(width, RowHeight), Theme.U32(Theme.Current.Selected), 3f);
            dl.AddRect(pos, pos + new Vector2(width, RowHeight), Theme.U32(Theme.Current.LineColor), 3f);
        }
        else if (hovered)
        {
            dl.AddRectFilled(pos, pos + new Vector2(width, RowHeight), Theme.U32(Theme.Panel2), 3f);
        }

        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon, hq)).GetWrapOrEmpty();
        var iconMin = pos + new Vector2(8, 9);
        dl.AddImage(tex.Handle, iconMin, iconMin + new Vector2(40), Vector2.Zero, Vector2.One, dim ? Theme.U32(new Vector4(1, 1, 1, 0.5f)) : uint.MaxValue);

        var valueSize = ImGui.CalcTextSize(value);
        var right = pos.X + width - 10;
        dl.AddText(new Vector2(right - valueSize.X, pos.Y + 10), Theme.U32(valueColor), value);
        var textRight = right - valueSize.X - 12;

        if (tag != null)
        {
            var ts = ImGui.CalcTextSize(tag);
            var tmin = new Vector2(right - ts.X - 10, pos.Y + 32);
            dl.AddRect(tmin, tmin + ts + new Vector2(10, 2), Theme.U32(tagColor with { W = 0.85f }), 2f);
            dl.AddText(tmin + new Vector2(5, 1), Theme.U32(tagColor), tag);
            textRight = Math.Min(textRight, tmin.X - 8);
        }

        var textLeft = pos.X + 58;
        dl.PushClipRect(new Vector2(textLeft, pos.Y), new Vector2(textRight, pos.Y + RowHeight), true);
        if (marker) dl.AddCircleFilled(new Vector2(textLeft + 4, pos.Y + 18), 3.5f, Theme.U32(Theme.Current.Color));
        dl.AddText(new Vector2(textLeft + (marker ? 12 : 0), pos.Y + 10), Theme.U32(dim ? Theme.Text2 : Theme.Text), name);
        dl.AddText(new Vector2(textLeft, pos.Y + 31), Theme.U32(subColor), sub);
        dl.PopClipRect();

        return clicked;
    }

    private static bool Chip(string label, bool active)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Button, active ? Theme.Raise2 : Theme.Field)
            .Push(ImGuiCol.Text, active ? Theme.Text : Theme.Text2);
        using var s = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(8, 4));
        return ImGui.Button(label);
    }

    private static void ItemHeader(ushort icon, bool hq, string name, Action tags)
    {
        Theme.Icon(icon, hq, 64);
        ImGui.SameLine(0, 14);
        using (ImRaii.Group())
        {
            ImGui.Dummy(new Vector2(0, 4));
            using (Theme.HeadingFont()) ImGui.TextUnformatted(name);
            tags();
        }
    }

    private void RecommendationMenu(Recommendation r, string id)
    {
        using var popup = ImRaii.ContextPopupItem(id);
        if (!popup) return;
        if (r.SuggestedPrice is { } p && ImGui.MenuItem($"Copy price ({p:N0})")) ImGui.SetClipboardText(p.ToString());
        if (ImGui.MenuItem("Copy item name")) ImGui.SetClipboardText(r.Item.Name);
        if (ImGui.MenuItem("Open on Universalis")) Util.OpenLink($"https://universalis.app/market/{r.Item.Id}");
        if (r.Listing == null && ImGui.MenuItem("Ignore this item"))
        {
            Config.IgnoredItems.Add(r.Item.Id);
            Config.Save();
        }
    }

    private static string Key(Recommendation r) => $"{r.Item.Id}:{r.Hq}:{r.Listing?.RetainerId}:{r.Listing?.Slot}";

    // ---- Sell ---------------------------------------------------------------------------------------

    private void DrawSell()
    {
        var avail = ImGui.GetContentRegionAvail();
        var all = plugin.Advice.Plan.Stacks;
        var rows = SortSell(all.Where(SellVisible)).ToList();
        var selected = rows.FirstOrDefault(r => Key(r) == selectedSell) ?? rows.FirstOrDefault();

        using (var left = Pane("##sellList", new Vector2(ListWidth, avail.Y), Theme.Bg1))
        {
            if (left) DrawSellList(all, rows, selected);
        }
        ImGui.SameLine(0, 0);
        Divider(avail.Y);
        using (var right = Pane("##sellDetail", new Vector2(avail.X - ListWidth, avail.Y), Theme.Bg1))
        {
            if (!right) return;
            if (selected == null)
            {
                Theme.Muted(all.Count == 0
                    ? "Nothing to show yet. Open your inventory and retainers, then press Refresh."
                    : "Nothing matches the current filter.");
                return;
            }
            DrawSellDetail(selected);
        }
    }

    private void DrawSellList(IReadOnlyList<Recommendation> all, List<Recommendation> rows, Recommendation? selected)
    {
        using (Theme.HeadingFont()) ImGui.TextUnformatted("What to sell");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 70);
        using (ImRaii.Disabled(plugin.Market.IsBusy))
        {
            if (ImGui.Button(plugin.Market.IsBusy ? "..." : "Refresh", new Vector2(70, 0)))
                plugin.Advice.RefreshPrices(force: true);
        }

        var tracker = plugin.Tracker;
        var scanned = tracker.Retainers.Count(r => r.ScannedUtc != null);
        var known = tracker.KnownRetainerCount ?? tracker.Retainers.Count;
        Theme.Muted($"{scanned}/{known} retainers scanned · {tracker.FreeListingSlots} free market slots");
        if (known == 0 || scanned < known)
            Theme.Wrapped(known == 0 ? "Visit a summoning bell to load your retainers." : "Open the missing retainers at a bell to include them.", Theme.Hold);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search items", ref search, 100);

        int Count(params Verdict[] v) => all.Count(r => v.Contains(r.Verdict));
        (string Label, int Filter)[] chips =
        [
            ($"All {all.Count}", 0), ($"List {Count(Verdict.List)}", 1), ($"Slow {Count(Verdict.ListSlow)}", 2),
            ($"Hold {Count(Verdict.Hold)}", 3), ($"Vendor {Count(Verdict.Vendor)}", 4),
        ];
        for (var i = 0; i < chips.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 4);
            if (Chip(chips[i].Label, sellFilter == chips[i].Filter)) sellFilter = chips[i].Filter;
        }

        var list = all.Where(r => r.Verdict == Verdict.List).ToList();
        Theme.Secondary($"List now: {list.Count} for {Theme.Short(list.Sum(r => r.NetTotal))} gil after tax");
        ImGui.SameLine();
        Theme.Muted("· dot = best picks");

        ImGui.SetNextItemWidth(170);
        using (var combo = ImRaii.Combo("##sellSort", SellSorts[sellSort]))
        {
            if (combo)
            {
                for (var i = 0; i < SellSorts.Length; i++)
                    if (ImGui.Selectable(SellSorts[i], i == sellSort)) sellSort = i;
            }
        }

        ImGui.Spacing();
        using var scroll = ImRaii.Child("##sellRows", Vector2.Zero);
        if (!scroll) return;
        foreach (var r in rows)
        {
            var (tag, color) = Theme.Describe(r.Verdict);
            var value = r.Verdict == Verdict.Vendor ? r.VendorTotal.ToString("N0")
                : r.SuggestedPrice is { } p ? p.ToString("N0") : "-";
            var valueColor = r.Verdict == Verdict.Vendor ? Theme.Vendor : Theme.Text;
            var key = Key(r);
            if (ListRow($"##s{key}", r.Item.Icon, r.Hq, r.Item.Name + (r.Hq ? "  HQ" : ""), $"{r.Quantity:N0} · {r.Where}", Theme.Text2,
                    value, valueColor, tag, color, selected == r, marker: r.FitsFreeSlot))
                selectedSell = key;
            RecommendationMenu(r, $"##sctx{key}");
        }
    }

    private void DrawSellDetail(Recommendation r)
    {
        ItemHeader(r.Item.Icon, r.Hq, r.Item.Name, () =>
        {
            if (r.Hq) { Theme.Tag("HQ", Theme.Current.Color, small: true); ImGui.SameLine(); }
            Theme.Secondary($"{r.Quantity:N0} owned · {r.Where}");
        });
        ImGui.Spacing();

        // The answer, big.
        var (label, color) = Theme.Describe(r.Verdict);
        var width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var height = 96f;
        Theme.Box(pos, pos + new Vector2(width, height), r.Verdict == Verdict.List ? Theme.OkBg : Theme.Panel, r.Verdict == Verdict.List ? Theme.OkLine : Theme.Line);
        ImGui.SetCursorScreenPos(pos + new Vector2(18, 14));
        using (ImRaii.Group())
        {
            ImGui.TextColored(color, r.Verdict switch
            {
                Verdict.List => "List now",
                Verdict.ListSlow => "List when you have a spare slot",
                Verdict.Hold => "Hold, or list behind the dump",
                Verdict.Vendor => "Sell to a vendor",
                _ => label,
            });
            if (r.Verdict == Verdict.Vendor)
            {
                using (Theme.BigFont()) ImGui.TextColored(Theme.Vendor, $"{r.VendorTotal:N0} gil");
                Theme.Muted("from any NPC merchant");
            }
            else if (r.SuggestedPrice is { } price)
            {
                using (Theme.BigFont()) ImGui.TextUnformatted($"{price:N0}");
                ImGui.SameLine();
                Theme.Muted("each");
                Theme.Secondary($"{r.NetTotal:N0} gil after tax" + (double.IsInfinity(r.EstDays) ? "" : r.EstDays < 1 ? " · sells in under a day" : $" · about {r.EstDays:0} days to sell"));
            }
            else
            {
                Theme.Muted("No price to suggest.");
            }
        }

        if (r.SuggestedPrice is { } copy)
        {
            ImGui.SetCursorScreenPos(pos + new Vector2(width - 140, (height - 34) / 2));
            if (Theme.PrimaryButton("Copy price", new Vector2(122, 34))) ImGui.SetClipboardText(copy.ToString());
        }
        ImGui.SetCursorScreenPos(pos + new Vector2(0, height + 12));

        DrawStatTiles(r);
        ImGui.Spacing();
        DrawSalesChart(r);
        ImGui.Spacing();
        Theme.Wrapped(r.Reason, Theme.Text2);
        ImGui.Spacing();

        if (ImGui.Button("Open on Universalis")) Util.OpenLink($"https://universalis.app/market/{r.Item.Id}");
        ImGui.SameLine();
        if (ImGui.Button("Ignore item"))
        {
            Config.IgnoredItems.Add(r.Item.Id);
            Config.Save();
        }
    }

    private static void DrawStatTiles(Recommendation r, string? firstLabel = null, string? firstValue = null)
    {
        var w = (ImGui.GetContentRegionAvail().X - 3 * 10) / 4;
        var dc = r.Market?.DataCenter;
        var (dcMin, dcWorld) = r.Hq ? (dc?.MinHq, dc?.CheapestWorldHq) : (dc?.MinNq, dc?.CheapestWorldNq);

        Theme.Tile(firstLabel ?? "Lowest listing", firstValue ?? (r.LowestCompetitor is { } l ? l.ToString("N0") : "none"), w);
        ImGui.SameLine(0, 10);
        Theme.Tile("Recent median", r.FairPrice is { } f ? $"{f:N0}  ({r.FairSampleSize})" : "-", w);
        ImGui.SameLine(0, 10);
        Theme.Tile("Sold per day", r.Market?.HasData == true ? r.UnitsPerDay.ToString("0.#") : "-", w);
        ImGui.SameLine(0, 10);
        Theme.Tile("Cheapest on DC", dcMin is { } m ? $"{m:N0}  {dcWorld}" : "-", w);
    }

    private static void DrawSalesChart(Recommendation r)
    {
        var sales = r.Market?.History.Where(h => !r.Item.CanBeHq || h.Hq == r.Hq).Take(30).Reverse().ToList() ?? [];
        var width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var height = 128f;
        Theme.Box(pos, pos + new Vector2(width, height), Theme.Field, Theme.Field);

        var dl = ImGui.GetWindowDrawList();
        dl.AddText(pos + new Vector2(14, 10), Theme.U32(Theme.Text), "Recent sales");
        var caption = sales.Count == 0 ? "no sales on record" : $"last {sales.Count}{(r.Item.CanBeHq ? r.Hq ? " · HQ only" : " · NQ only" : "")}";
        var cs = ImGui.CalcTextSize(caption);
        dl.AddText(pos + new Vector2(width - cs.X - 14, 10), Theme.U32(Theme.Text3), caption);

        if (sales.Count > 0)
        {
            var max = Math.Max(1u, sales.Max(s => s.UnitPrice));
            var area = new Vector2(width - 28, height - 44);
            var origin = pos + new Vector2(14, height - 12);
            var bw = area.X / sales.Count;
            for (var i = 0; i < sales.Count; i++)
            {
                var h = Math.Max(2, area.Y * sales[i].UnitPrice / max);
                var x = origin.X + i * bw;
                var col = i == sales.Count - 1 ? Theme.Good : new Vector4(0.30f, 0.38f, 0.33f, 1);
                dl.AddRectFilled(new Vector2(x + 1, origin.Y - h), new Vector2(x + bw - 2, origin.Y), Theme.U32(col), 1.5f);
            }

            if (r.FairPrice is { } fair)
            {
                var y = origin.Y - area.Y * fair / max;
                dl.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + area.X, y), Theme.U32(Theme.Text3 with { W = 0.6f }));
                dl.AddText(new Vector2(origin.X + 2, y - ImGui.GetTextLineHeight()), Theme.U32(Theme.Text3), $"median {fair:N0}");
            }
        }

        ImGui.Dummy(new Vector2(width, height));
    }

    private bool SellVisible(Recommendation r)
    {
        if (search.Length > 0 && !r.Item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        var shown = sellFilter switch
        {
            1 => r.Verdict == Verdict.List,
            2 => r.Verdict == Verdict.ListSlow,
            3 => r.Verdict == Verdict.Hold,
            4 => r.Verdict == Verdict.Vendor,
            _ => r.Verdict is Verdict.List or Verdict.ListSlow or Verdict.Hold or Verdict.Vendor,
        };
        return shown && Math.Max(r.NetTotal, r.VendorTotal) >= Config.MinValueGil;
    }

    private IEnumerable<Recommendation> SortSell(IEnumerable<Recommendation> rows) => sellSort switch
    {
        1 => rows.OrderByDescending(r => Math.Max(r.NetTotal, r.VendorTotal)),
        2 => rows.OrderByDescending(r => r.Priority),
        3 => rows.OrderBy(r => r.EstDays).ThenByDescending(r => r.NetTotal),
        4 => rows.OrderBy(r => r.Item.Name),
        _ => rows.OrderBy(r => r.Verdict switch { Verdict.List => 0, Verdict.ListSlow => 1, Verdict.Hold => 2, _ => 3 })
                 .ThenByDescending(r => r.Verdict == Verdict.Vendor ? r.VendorTotal : r.Priority),
    };

    // ---- Listings -----------------------------------------------------------------------------------

    private void DrawListings()
    {
        var avail = ImGui.GetContentRegionAvail();
        var listings = plugin.Advice.Plan.Listings.OrderByDescending(r => r.Priority).ThenBy(r => r.Where).ThenBy(r => r.Item.Name).ToList();
        var selected = listings.FirstOrDefault(r => Key(r) == selectedListing) ?? listings.FirstOrDefault();

        using (var left = Pane("##listList", new Vector2(ListWidth, avail.Y), Theme.Bg1))
        {
            if (left)
            {
                using (Theme.HeadingFont()) ImGui.TextUnformatted("My listings");
                var undercut = listings.Count(r => r.Verdict == Verdict.Relist);
                var raise = listings.Count(r => r.Verdict == Verdict.Raise);
                ImGui.TextColored(undercut > 0 ? Theme.Bad : Theme.Good, $"{undercut} undercut");
                ImGui.SameLine();
                ImGui.TextColored(raise > 0 ? Theme.Hold : Theme.Text3, $"· {raise} could be higher");
                Theme.Muted($"{listings.Count} listings · {Theme.Short(listings.Sum(r => (long)r.Listing!.UnitPrice * r.Quantity))} gil on the board");
                ImGui.Spacing();

                using var scroll = ImRaii.Child("##listRows", Vector2.Zero);
                if (scroll)
                {
                    foreach (var r in listings)
                    {
                        var (tag, color) = Theme.Describe(r.Verdict);
                        var key = Key(r);
                        if (ListRow($"##l{key}", r.Item.Icon, r.Hq, r.Item.Name + (r.Hq ? "  HQ" : ""), $"{r.Where} · {r.Quantity:N0}", Theme.Text2,
                                r.Listing!.UnitPrice.ToString("N0"), Theme.Text, tag, color, selected == r))
                            selectedListing = key;
                        RecommendationMenu(r, $"##lctx{key}");
                    }
                }
            }
        }

        ImGui.SameLine(0, 0);
        Divider(avail.Y);
        using var right = Pane("##listDetail", new Vector2(avail.X - ListWidth, avail.Y), Theme.Bg1);
        if (!right) return;
        if (selected == null)
        {
            Theme.Wrapped("No listings known yet. Open each retainer's sell list at a summoning bell so SellWise can read your prices.", Theme.Text3);
            return;
        }

        var l = selected.Listing!;
        ItemHeader(selected.Item.Icon, selected.Hq, selected.Item.Name, () =>
        {
            if (selected.Hq) { Theme.Tag("HQ", Theme.Current.Color, small: true); ImGui.SameLine(); }
            Theme.Secondary($"{selected.Quantity:N0} listed by {l.RetainerName}");
        });
        ImGui.Spacing();

        var (label, color2) = Theme.Describe(selected.Verdict);
        var width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        Theme.Box(pos, pos + new Vector2(width, 96), Theme.Panel, Theme.Line);
        ImGui.SetCursorScreenPos(pos + new Vector2(18, 14));
        using (ImRaii.Group())
        {
            ImGui.TextColored(color2, selected.Verdict switch
            {
                Verdict.Relist => "Undercut: change your price",
                Verdict.Raise => "Priced low: you can ask more",
                _ => "Your price is fine",
            });
            using (Theme.BigFont()) ImGui.TextUnformatted($"{selected.SuggestedPrice ?? l.UnitPrice:N0}");
            ImGui.SameLine();
            Theme.Muted(selected.SuggestedPrice != null ? $"was {l.UnitPrice:N0}" : "each");
        }
        if (selected.SuggestedPrice is { } p)
        {
            ImGui.SetCursorScreenPos(pos + new Vector2(width - 140, 31));
            if (Theme.PrimaryButton("Copy price", new Vector2(122, 34))) ImGui.SetClipboardText(p.ToString());
        }
        ImGui.SetCursorScreenPos(pos + new Vector2(0, 108));

        DrawStatTiles(selected, "Lowest other seller", selected.LowestCompetitor is { } lc ? lc.ToString("N0") : "none");
        ImGui.Spacing();
        DrawSalesChart(selected);
        ImGui.Spacing();
        Theme.Wrapped(selected.Reason, Theme.Text2);
        if (selected.Market is { HasData: true } m)
            Theme.Muted($"Competitor prices from Universalis, last uploaded {Theme.Ago(m.LastUpload.UtcDateTime)}.");
    }

    // ---- Retainers ----------------------------------------------------------------------------------

    private void DrawRetainers()
    {
        var avail = ImGui.GetContentRegionAvail();
        using var pane = Pane("##retainers", avail, Theme.Bg1);
        if (!pane) return;

        var tracker = plugin.Tracker;
        using (Theme.HeadingFont()) ImGui.TextUnformatted("Retainers");
        Theme.Wrapped("The game only lets plugins read a retainer's inventory while you're talking to it. Open each retainer at a summoning bell " +
                      "(opening its sell list also reads its listings). SellWise saves what it sees until the next visit.", Theme.Text2);
        if (tracker.Character is { } character)
            Theme.Muted($"Saddlebag: {(character.SaddlebagScannedUtc is { } s ? $"scanned {Theme.Ago(s)}" : "not scanned yet (open it once)")}");
        ImGui.Spacing();

        using var table = ImRaii.Table("##retainerTable", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX);
        if (!table) return;
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Last scanned");
        ImGui.TableSetupColumn("Item stacks");
        ImGui.TableSetupColumn("Market slots");
        ImGui.TableSetupColumn("Gil");
        ImGui.TableSetupColumn("");
        ImGui.TableHeadersRow();

        foreach (var r in tracker.Retainers)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 34);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(r.Name.Length > 0 ? r.Name : r.Id.ToString("X"));
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (r.ScannedUtc is { } t) Theme.Secondary(Theme.Ago(t));
            else ImGui.TextColored(Theme.Hold, "never");
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Theme.Secondary(r.ScannedUtc != null ? r.Items.Count.ToString() : "-");
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Theme.Bar(r.MarketItemCount / (float)InventoryTracker.ListingSlotsPerRetainer, Theme.Current.Color, new Vector2(60, 5));
            ImGui.SameLine();
            Theme.Secondary($"{r.MarketItemCount}/{InventoryTracker.ListingSlotsPerRetainer}");
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Theme.Secondary(r.Gil.ToString("N0"));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Forget##{r.Id}")) tracker.ForgetRetainer(r.Id);
            Tooltip("Drop this retainer's saved snapshot. It's rescanned next time you open it.");
        }
    }
}
