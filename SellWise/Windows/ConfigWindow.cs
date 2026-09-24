using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace SellWise.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private IDisposable? theme;

    public ConfigWindow(Plugin plugin) : base("SellWise Settings###SellWiseConfig")
    {
        this.plugin = plugin;
        Size = new Vector2(520, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void PreDraw() => theme = Theme.Push();

    public override void PostDraw()
    {
        theme?.Dispose();
        theme = null;
    }

    public override void Draw()
    {
        using var layout = Theme.PushLayout();
        using var pad = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        using var body = ImRaii.Child("##settings", Vector2.Zero, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        if (!body) return;

        var c = plugin.Config;
        ImGui.TextUnformatted("Look");
        ImGui.Separator();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Accent colour");
        foreach (var accent in Theme.Accents)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton(accent.Name, c.Accent == accent.Name))
            {
                c.Accent = accent.Name;
                Theme.SetAccent(accent.Name);
                c.Save();
            }
        }
        ImGui.Spacing();

        var a = c.Advisor;
        var changed = false;

        ImGui.TextUnformatted("Pricing");
        ImGui.Separator();

        var undercutGil = a.UndercutGil;
        if (ImGui.InputInt("Undercut by (gil)", ref undercutGil))
        {
            a.UndercutGil = System.Math.Max(0, undercutGil);
            changed = true;
        }

        var undercutPct = a.UndercutPercent;
        if (ImGui.SliderFloat("Undercut by (%)", ref undercutPct, 0f, 10f, "%.1f%%"))
        {
            a.UndercutPercent = undercutPct;
            changed = true;
        }
        Help("If above 0, undercut by a percentage instead of a flat amount.");

        var floor = a.FloorFraction * 100;
        if (ImGui.SliderFloat("Price floor (% of recent median)", ref floor, 30f, 100f, "%.0f%%"))
        {
            a.FloorFraction = floor / 100;
            changed = true;
        }
        Help("Never suggest a price below this share of what the item has actually sold for recently. Stops you chasing one dumped listing to the bottom.");

        var markup = (a.NoCompetitionMarkup - 1) * 100;
        if (ImGui.SliderFloat("Markup when nobody is selling", ref markup, 0f, 50f, "+%.0f%%"))
        {
            a.NoCompetitionMarkup = 1 + markup / 100;
            changed = true;
        }

        var tax = a.TaxRate * 100;
        if (ImGui.SliderFloat("Market tax", ref tax, 0f, 5f, "%.0f%%"))
        {
            a.TaxRate = tax / 100;
            changed = true;
        }

        var vendor = a.VendorMargin;
        if (ImGui.SliderFloat("Vendor margin", ref vendor, 1f, 3f, "%.2fx"))
        {
            a.VendorMargin = vendor;
            changed = true;
        }
        Help("Only list on the market if it would pay at least this multiple of the NPC vendor price. Otherwise it's not worth a listing slot.");

        var window = a.HistoryWindowDays;
        if (ImGui.SliderInt("Sales history window (days)", ref window, 3, 30))
        {
            a.HistoryWindowDays = window;
            changed = true;
        }

        var slow = a.SlowDays;
        if (ImGui.SliderFloat("\"Slow\" after (days)", ref slow, 1f, 60f, "%.0f"))
        {
            a.SlowDays = slow;
            changed = true;
        }

        var raise = a.RaiseThreshold * 100;
        if (ImGui.SliderFloat("Flag underpriced listings below", ref raise, 50f, 99f, "%.0f%%"))
        {
            a.RaiseThreshold = raise / 100;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Inventory");
        ImGui.Separator();
        changed |= Check("Include retainers", c.IncludeRetainers, v => c.IncludeRetainers = v);
        changed |= Check("Include your chocobo saddlebag", c.IncludeSaddlebags, v => c.IncludeSaddlebags = v);
        changed |= Check("Include crystals and shards", c.IncludeCrystals, v => c.IncludeCrystals = v);
        changed |= Check("Include armoury chest", c.IncludeArmory, v => c.IncludeArmory = v);

        var minValue = c.MinValueGil;
        if (ImGui.InputInt("Hide stacks worth less than (gil)", ref minValue, 100, 1000))
        {
            c.MinValueGil = System.Math.Max(0, minValue);
            changed = true;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Market data");
        ImGui.Separator();

        var worldOverride = c.WorldOverride;
        if (ImGui.InputTextWithHint("World", "Home world", ref worldOverride, 32))
        {
            c.WorldOverride = worldOverride;
            changed = true;
        }
        Help("Leave empty to price against your home world. You can only list on your home world, so leave this empty unless you know why you're changing it.");

        changed |= Check("Also check data center prices", c.FetchDataCenter, v => c.FetchDataCenter = v);
        changed |= Check("Refresh automatically while open", c.AutoRefresh, v => c.AutoRefresh = v);

        var cache = c.CacheMinutes;
        if (ImGui.SliderInt("Refetch prices after (minutes)", ref cache, 1, 120))
        {
            c.CacheMinutes = cache;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Craft profit finder");
        ImGui.Separator();
        var cr = c.Craft;
        changed |= Check("Price crafts as HQ when possible", cr.AssumeHq, v => cr.AssumeHq = v);
        Help("Artisan and Vulcan usually reach HQ with decent gear. Turn off if you mostly get NQ.");
        changed |= Check("Gather materials when possible", cr.GatherWhenPossible, v => cr.GatherWhenPossible = v);
        Help("For the \"Cheapest mix\" materials choice. Pick how to get materials on each recipe's page.");
        changed |= Check("Count gathered materials at market value", cr.ValueGatheredAtMarket, v => cr.ValueGatheredAtMarket = v);
        Help("On: ranks by true profit, since you could sell the mats instead. Off: gathered mats are free.");
        changed |= Check("Include expert recipes", cr.IncludeExpert, v => cr.IncludeExpert = v);
        changed |= Check("Include specialist-only recipes", cr.IncludeSpecialist, v => cr.IncludeSpecialist = v);

        var minVel = (float)cr.MinUnitsPerDay;
        if (ImGui.SliderFloat("Min sold per day", ref minVel, 0.1f, 20f, "%.1f"))
        {
            cr.MinUnitsPerDay = minVel;
            changed = true;
        }

        var minSale = (int)cr.MinSalePrice;
        if (ImGui.InputInt("Min sale price", ref minSale, 100, 1000))
        {
            cr.MinSalePrice = (uint)System.Math.Max(0, minSale);
            changed = true;
        }

        var days = cr.DaysOfSupply;
        if (ImGui.SliderFloat("Craft this many days of sales", ref days, 0.5f, 7f, "%.1f"))
        {
            cr.DaysOfSupply = days;
            changed = true;
        }
        Help("The suggested quantity. Crafting more than the market absorbs just means undercutting yourself.");

        var depth = cr.MaxIntermediateDepth;
        if (ImGui.SliderInt("Intermediate craft depth", ref depth, 0, 4))
        {
            cr.MaxIntermediateDepth = depth;
            changed = true;
        }
        Help("How many layers of parts \"Cheapest mix\" will consider crafting. \"Gather & craft\" always makes every part.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Gear");
        ImGui.Separator();
        changed |= Check("Repair automatically around craft jobs", c.AutoRepair, v => c.AutoRepair = v);
        Help("Checked before every job and between Artisan steps. During a Vulcan run GatherBuddy repairs on its own; set its repair threshold in GatherBuddy's settings.");
        var threshold = c.RepairThreshold;
        if (ImGui.SliderInt("Repair below", ref threshold, 1, 99, "%d%%"))
        {
            c.RepairThreshold = threshold;
            changed = true;
        }
        changed |= Check("Self-repair with Dark Matter when possible", c.AllowSelfRepair, v => c.AllowSelfRepair = v);
        changed |= Check("Otherwise teleport to a city mender", c.AllowNpcRepair, v => c.AllowNpcRepair = v);
        Help("Picks a random enabled city (from the teleport list below) that has a mender, walks there with vnavmesh and repairs everything.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Other");
        ImGui.Separator();
        changed |= Check("Show undercut count in server info bar", c.ShowDtr, v => c.ShowDtr = v);
        changed |= Check("Target the bell after walking to it", c.TargetBellOnArrival, v => c.TargetBellOnArrival = v);
        changed |= Check("Open the crafting status window when a job starts", c.ShowJobWindow, v => c.ShowJobWindow = v);
        changed |= Check("Use cordials while a craft job is gathering", c.UseCordials, v => c.UseCordials = v);
        changed |= Check("Let Artisan do the crafting after GatherBuddy gathers", c.FinishWithArtisan, v => c.FinishWithArtisan = v);
        Help("For max quality every craft. Once GatherBuddy has gathered everything, SellWise stops it (never mid-craft) and hands the parts and the item to Artisan. Needs Artisan installed.");
        Help("Drinks the biggest cordial that won't waste GP whenever the cordial cooldown is ready, between nodes. GatherBuddy Reborn can do this too (auto-gather preset > Consumables); they share one cooldown, so both on is safe.");

        ImGui.Spacing();
        ImGui.TextUnformatted("\"Teleport to a city\" picks randomly from:");
        var unlocked = plugin.Teleporter.Available(new System.Collections.Generic.HashSet<uint>()).Select(x => x.AetheryteId).ToHashSet();
        var col = 0;
        foreach (var city in Services.CityTeleporter.Cities)
        {
            if (col++ % 3 != 0) ImGui.SameLine(ImGui.GetFontSize() * 11 * ((col - 1) % 3));
            var on = !c.DisabledTeleportCities.Contains(city.AetheryteId);
            var label = unlocked.Contains(city.AetheryteId) ? city.Name : $"{city.Name} (not attuned)";
            if (ImGui.Checkbox($"{label}##city{city.AetheryteId}", ref on))
            {
                if (on) c.DisabledTeleportCities.Remove(city.AetheryteId);
                else c.DisabledTeleportCities.Add(city.AetheryteId);
                changed = true;
            }
        }

        if (c.IgnoredItems.Count > 0)
        {
            ImGui.Spacing();
            using var node = ImRaii.TreeNode($"Ignored items ({c.IgnoredItems.Count})");
            if (node)
            {
                foreach (var id in c.IgnoredItems.ToList())
                {
                    var name = plugin.Catalog.Get(id)?.Name ?? $"Item {id}";
                    if (ImGui.SmallButton($"Unignore##{id}"))
                    {
                        c.IgnoredItems.Remove(id);
                        changed = true;
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted(name);
                }
            }
        }

        if (changed) c.Save();
    }

    private static bool Check(string label, bool value, System.Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value)) return false;
        set(value);
        return true;
    }

    private static void Help(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }
}
