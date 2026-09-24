using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using SellWise.Core;
using SellWise.Services;

namespace SellWise.Windows;

/// <summary>The "Craft for profit" tab: find profitable recipes, hand them to Vulcan/Artisan, then price the results.</summary>
public sealed class CraftTab
{
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1);
    private static readonly Vector4 Red = new(1.00f, 0.40f, 0.40f, 1);
    private static readonly Vector4 Yellow = new(1.00f, 0.85f, 0.35f, 1);
    private static readonly Vector4 Grey = new(0.60f, 0.60f, 0.60f, 1);

    private static readonly string[] SortModes = ["Batch profit", "Profit per craft", "Cash profit per craft", "Sold per day", "Market gil per day"];

    private readonly Plugin plugin;
    private string search = "";
    private int sortMode;
    private bool showUnprofitable;
    private bool unlockedOnly;
    private uint selectedRecipe;
    private int quantity = 1;
    private string? startError;

    public CraftTab(Plugin plugin) => this.plugin = plugin;

    private Configuration Config => plugin.Config;

    public void Draw()
    {
        DrawJob();
        DrawControls();

        var rows = Filter(plugin.Scanner.Results).ToList();
        var selected = rows.FirstOrDefault(o => o.Recipe.RecipeId == selectedRecipe)
                       ?? plugin.Scanner.Results.FirstOrDefault(o => o.Recipe.RecipeId == selectedRecipe);

        var avail = ImGui.GetContentRegionAvail().Y;
        DrawTable(rows, selected != null ? avail * 0.45f : avail);

        if (selected != null)
        {
            ImGui.Separator();
            using var child = ImRaii.Child("##craftDetail", Vector2.Zero);
            if (child) DrawDetail(selected);
        }
    }

    private void DrawJob()
    {
        if (plugin.Crafter.Job is not { } job) return;

        var name = job.Opportunity.Item.Name + (job.Opportunity.SellHq ? " (HQ)" : "");
        var color = job.State switch
        {
            CraftJobState.Running => Yellow,
            CraftJobState.Finished => Green,
            _ => Red,
        };

        ImGui.TextColored(color, $"{job.State}: {name} x{job.Crafts} via {job.Backend}");
        ImGui.SameLine();
        ImGui.TextDisabled($"({job.Made}/{job.Wanted} made, started {(int)(DateTime.UtcNow - job.StartedUtc).TotalMinutes}m ago)");
        ImGui.ProgressBar(job.Wanted > 0 ? Math.Clamp(job.Made / (float)job.Wanted, 0, 1) : 0, new Vector2(-1, 0), job.Status);

        if (job.State == CraftJobState.Running)
        {
            if (ImGui.Button("Stop")) plugin.Crafter.Stop();
            ImGui.SameLine();
            if (ImGui.Button("Mark finished")) plugin.Crafter.MarkFinished();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stop watching and price what you have now.");
        }
        else
        {
            if (job.State == CraftJobState.Finished) DrawSellAdvice(job);
            if (ImGui.Button("Dismiss")) plugin.Crafter.Dismiss();
        }

        ImGui.Separator();
    }

    private void DrawSellAdvice(CraftJob job)
    {
        var id = job.Opportunity.Item.Id;
        var stacks = plugin.Advice.Plan.Stacks;
        var rec = stacks.FirstOrDefault(r => r.Item.Id == id && r.Hq == job.Opportunity.SellHq) ?? stacks.FirstOrDefault(r => r.Item.Id == id);
        if (rec == null || rec.Verdict == Verdict.NoData)
        {
            ImGui.TextDisabled("Fetching current prices for what you made...");
            return;
        }

        ImGui.TextUnformatted($"You have {rec.Quantity:N0}{(rec.Hq ? " HQ" : "")}.");
        ImGui.SameLine();
        if (rec.SuggestedPrice is { } p)
        {
            ImGui.TextColored(Green, $"List at {p:N0} each");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy price")) ImGui.SetClipboardText(p.ToString());
            ImGui.SameLine();
            ImGui.TextDisabled($"= {rec.NetTotal:N0} gil after tax");
        }
        ImGui.TextWrapped($"{rec.Verdict}: {rec.Reason}");
    }

    private void DrawControls()
    {
        var scanner = plugin.Scanner;
        using (ImRaii.Disabled(scanner.IsScanning || plugin.Advice.PricingWorld.Length == 0))
        {
            if (ImGui.Button(scanner.IsScanning ? "Scanning..." : "Scan recipes"))
                scanner.Start(plugin.Advice.PricingWorld);
        }
        ImGui.SameLine();
        ImGui.TextDisabled(scanner.Status + (scanner.LastScanUtc is { } t ? $" ({(int)(DateTime.UtcNow - t).TotalMinutes}m ago)" : ""));

        for (var i = 0; i < RecipeInfo.JobAbbreviations.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            var shown = !Config.HiddenCraftJobs.Contains(i);
            var level = scanner.JobLevels[i];
            if (ImGui.Checkbox($"{RecipeInfo.JobAbbreviations[i]} {(level > 0 ? level.ToString() : "-")}##job{i}", ref shown))
            {
                if (shown) Config.HiddenCraftJobs.Remove(i);
                else Config.HiddenCraftJobs.Add(i);
                Config.Save();
            }
        }

        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##craftSearch", "Search items", ref search, 100);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(170);
        using (var combo = ImRaii.Combo("##craftSort", SortModes[sortMode]))
        {
            if (combo)
            {
                for (var i = 0; i < SortModes.Length; i++)
                    if (ImGui.Selectable(SortModes[i], i == sortMode)) sortMode = i;
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("Unlocked only", ref unlockedOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Show unprofitable", ref showUnprofitable);
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Batch profit = profit per craft x the crafts your world's market absorbs in a couple of days (max 99).\n" +
                             "Market gil per day assumes you capture every sale on your world, so treat it as an upper bound.\n" +
                             "Cash profit counts gathered materials as free; profit counts them at market value, since you could sell them instead.\n" +
                             "Tune thresholds under Settings > Craft profit finder.");
    }

    private IEnumerable<CraftOpportunity> Filter(IEnumerable<CraftOpportunity> all)
    {
        var rows = all.Where(o => !Config.HiddenCraftJobs.Contains(o.Recipe.CraftType))
                      .Where(o => showUnprofitable || o.ProfitPerCraft > 0)
                      .Where(o => !unlockedOnly || o.Unlocked)
                      .Where(o => search.Length == 0 || o.Item.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        return sortMode switch
        {
            1 => rows.OrderByDescending(o => o.ProfitPerCraft),
            2 => rows.OrderByDescending(o => o.CashProfitPerCraft),
            3 => rows.OrderByDescending(o => o.UnitsPerDay),
            4 => rows.OrderByDescending(o => o.DailyProfit),
            _ => rows.OrderByDescending(o => o.BatchProfit),
        };
    }

    private void DrawTable(List<CraftOpportunity> rows, float height)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
                                      ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
        using var table = ImRaii.Table("##crafts", 10, flags, new Vector2(0, height));
        if (!table) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Job");
        ImGui.TableSetupColumn("Recipe");
        ImGui.TableSetupColumn("Sells at");
        ImGui.TableSetupColumn("Sold/day");
        ImGui.TableSetupColumn("Mat cost");
        ImGui.TableSetupColumn("Profit/craft");
        ImGui.TableSetupColumn("Cash profit");
        ImGui.TableSetupColumn("Batch profit");
        ImGui.TableSetupColumn("Materials");
        ImGui.TableHeadersRow();

        foreach (var o in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(o.Item.Icon, o.SellHq)).GetWrapOrEmpty();
            ImGui.Image(icon.Handle, new Vector2(ImGui.GetTextLineHeight()));
            ImGui.SameLine();
            var label = $"{o.Item.Name}{(o.SellHq ? " (HQ)" : "")}{(o.Recipe.Yield > 1 ? $" x{o.Recipe.Yield}" : "")}{(o.Warning != null ? " (!)" : "")}##r{o.Recipe.RecipeId}";
            if (ImGui.Selectable(label, o.Recipe.RecipeId == selectedRecipe, ImGuiSelectableFlags.SpanAllColumns))
            {
                selectedRecipe = o.Recipe.RecipeId;
                quantity = o.SuggestedCrafts;
                startError = null;
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{o.Recipe.Job} {o.Recipe.Level}");
            ImGui.TableNextColumn();
            if (o.LockedReason is { } locked)
            {
                ImGui.TextColored(Red, "Locked");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(locked);
            }
            else
            {
                ImGui.TextColored(Green, "Unlocked");
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(o.SalePrice.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(o.UnitsPerDay.ToString("0.#"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(o.MaterialValue.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextColored(o.ProfitPerCraft > 0 ? Green : Red, o.ProfitPerCraft.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(o.CashProfitPerCraft.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextColored(o.BatchProfit > 0 ? Green : Red, $"{o.BatchProfit:N0} ({o.SuggestedCrafts})");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{o.SuggestedCrafts} crafts x {o.ProfitPerCraft:N0}. If you captured every sale on your world: {o.DailyProfit:N0} gil/day.");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(SummarizeSources(o));
        }
    }

    private void DrawDetail(CraftOpportunity o)
    {
        var tracker = plugin.Tracker;
        var crafter = plugin.Crafter;

        ImGui.TextUnformatted($"{o.Item.Name}{(o.SellHq ? " (HQ)" : "")}");
        ImGui.SameLine();
        ImGui.TextDisabled($"{o.Recipe.Job} lv{o.Recipe.Level}, makes {o.Recipe.Yield} per craft{(o.Recipe.IsExpert ? ", expert recipe" : "")}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Universalis")) Util.OpenLink($"https://universalis.app/market/{o.Item.Id}");

        ImGui.TextUnformatted($"Sells for ~{o.SalePrice:N0} each, {o.UnitsPerDay:0.#} sold per day. Profit per craft {o.ProfitPerCraft:N0} (cash {o.CashProfitPerCraft:N0}).");
        if (o.LockedReason is { } lockReason) ImGui.TextColored(Red, $"Locked: {lockReason}");
        if (o.Warning != null) ImGui.TextColored(Yellow, o.Warning);
        if (o.SellHq) ImGui.TextDisabled("Priced as HQ. If your gear can't HQ this, the real price will be lower.");

        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Crafts", ref quantity)) quantity = Math.Clamp(quantity, 1, 999);
        ImGui.SameLine();
        ImGui.TextDisabled($"Suggested {o.SuggestedCrafts} (~{Config.Craft.DaysOfSupply:0.#} days of sales). Expected profit {o.ProfitPerCraft * quantity:N0}.");

        DrawMaterials(o, tracker);

        // Start buttons.
        var running = crafter.IsRunning || !o.Unlocked;
        using (ImRaii.Disabled(running || !CraftCoordinator.VulcanAvailable))
        {
            if (ImGui.Button("Gather + craft with GatherBuddy (Vulcan)"))
                startError = crafter.Start(o, quantity, CraftBackend.Vulcan);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(CraftCoordinator.VulcanAvailable
                ? "Runs /vulcan craft: GatherBuddy Reborn gathers missing materials, pulls from retainers, then crafts."
                : "Install and enable GatherBuddy Reborn to use this.");

        ImGui.SameLine();
        var missing = crafter.MissingForArtisan(o, quantity);
        using (ImRaii.Disabled(running || !CraftCoordinator.ArtisanAvailable || missing.Count > 0))
        {
            if (ImGui.Button("Craft with Artisan"))
                startError = crafter.Start(o, quantity, CraftBackend.Artisan);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(!CraftCoordinator.ArtisanAvailable ? "Install and enable Artisan to use this."
                : missing.Count > 0 ? "Artisan needs everything in your bags. Missing:\n" + string.Join("\n", missing.Select(m => $"  {m.Line.Name} x{m.Missing}"))
                : "Artisan crafts any intermediates you're short of, then the item.");
        }

        if (startError != null) ImGui.TextColored(Red, startError);
    }

    private void DrawMaterials(CraftOpportunity o, InventoryTracker tracker)
    {
        using var table = ImRaii.Table("##mats", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit);
        if (!table) return;

        ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Need");
        ImGui.TableSetupColumn("Bags / retainers");
        ImGui.TableSetupColumn("Source");
        ImGui.TableSetupColumn("Unit cost");
        ImGui.TableSetupColumn("");
        ImGui.TableHeadersRow();

        var i = 0;
        foreach (var m in o.Materials)
        {
            i++;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(new string(' ', m.Depth * 4) + m.Name);

            ImGui.TableNextColumn();
            var need = (int)Math.Ceiling(m.AmountPerCraft * quantity);
            ImGui.TextUnformatted(need.ToString("N0"));

            ImGui.TableNextColumn();
            var bags = tracker.CountInBags(m.ItemId);
            var retainers = tracker.CountOnRetainers(m.ItemId);
            ImGui.TextColored(bags >= need ? Green : bags + retainers >= need ? Yellow : Grey, $"{bags:N0} / {retainers:N0}");
            if (ImGui.IsItemHovered() && bags < need && bags + retainers >= need)
                ImGui.SetTooltip("Enough on your retainers. Vulcan can pull them; for Artisan, withdraw them first.");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(m.Source switch
            {
                MaterialSource.Gather => "Gather",
                MaterialSource.Vendor => "NPC vendor",
                MaterialSource.Buy => "Market board",
                MaterialSource.Craft => "Craft",
                _ => "Unknown",
            });

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(m.Source == MaterialSource.Gather
                ? m.UnitValue > 0 ? $"free (worth {m.UnitValue:N0})" : "free"
                : m.UnitCash.ToString("N0"));

            ImGui.TableNextColumn();
            if (m.Source == MaterialSource.Gather && CraftCoordinator.VulcanAvailable && bags < need)
            {
                if (ImGui.SmallButton($"Go gather##g{i}"))
                    Plugin.CommandManager.ProcessCommand($"/gather {m.Name}");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("GatherBuddy teleports you to the nearest node and marks it.");
            }
            else if (m.Source == MaterialSource.Buy && bags < need)
            {
                if (ImGui.SmallButton($"Universalis##u{i}")) Util.OpenLink($"https://universalis.app/market/{m.ItemId}");
            }
        }
    }

    private static string SummarizeSources(CraftOpportunity o)
    {
        var top = o.Materials.Where(m => m.Depth == 0).GroupBy(m => m.Source).OrderBy(g => g.Key);
        return string.Join(", ", top.Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));
    }
}
