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

/// <summary>Craft for profit: recipe list, recipe detail with HQ check, and the gather-to-sell pipeline while a job runs.</summary>
public sealed class CraftView
{
    private const float ListWidth = 430;
    private const float RowHeight = 58;
    private static readonly string[] Sorts = ["Batch profit", "Profit per craft", "Cash profit per craft", "Sold per day", "Market gil per day"];

    private readonly Plugin plugin;
    private string search = "";
    private int sort;
    private bool unlockedOnly;
    private bool showUnprofitable;
    private uint selectedRecipe;
    private int quantity = 1;
    private string? startError;

    public CraftView(Plugin plugin) => this.plugin = plugin;

    private Configuration Config => plugin.Config;

    public void Draw()
    {
        var avail = ImGui.GetContentRegionAvail();
        var rows = Filter(plugin.Scanner.Results).ToList();
        var selected = rows.FirstOrDefault(o => o.Recipe.RecipeId == selectedRecipe)
                       ?? plugin.Scanner.Results.FirstOrDefault(o => o.Recipe.RecipeId == selectedRecipe)
                       ?? rows.FirstOrDefault();
        if (selected != null && selected.Recipe.RecipeId != selectedRecipe)
        {
            selectedRecipe = selected.Recipe.RecipeId;
            quantity = selected.SuggestedCrafts;
        }

        using (var left = Pane("##craftList", new Vector2(ListWidth, avail.Y)))
        {
            if (left) DrawList(rows, selected);
        }

        ImGui.SameLine(0, 0);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(0, avail.Y), Theme.U32(Theme.Line));

        using var right = Pane("##craftDetail", new Vector2(avail.X - ListWidth, avail.Y));
        if (!right) return;

        if (plugin.Crafter.Job is { } job)
        {
            DrawPipeline(job);
            return;
        }

        if (selected == null)
        {
            Theme.Wrapped(plugin.Scanner.Results.Count == 0
                ? "Press Scan to price every recipe you could make on your world. It takes about a minute the first time."
                : "Nothing matches the current filters.", Theme.Text3);
            return;
        }

        DrawDetail(selected);
    }

    private static ImRaii.ChildDisposable Pane(string id, Vector2 size)
    {
        using var c = ImRaii.PushColor(ImGuiCol.ChildBg, Theme.Bg1);
        using var s = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        return ImRaii.Child(id, size, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
    }

    // ---- List ---------------------------------------------------------------------------------------

    private void DrawList(List<CraftOpportunity> rows, CraftOpportunity? selected)
    {
        var scanner = plugin.Scanner;
        using (Theme.HeadingFont()) ImGui.TextUnformatted("Craft for profit");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 76);
        using (ImRaii.Disabled(scanner.IsScanning || plugin.Advice.PricingWorld.Length == 0))
        {
            if (Theme.PrimaryButton(scanner.IsScanning ? "..." : "Scan", new Vector2(76, 0)))
                scanner.Start(plugin.Advice.PricingWorld);
        }
        Theme.Wrapped(scanner.Status + (scanner.LastScanUtc is { } t ? $" ({Theme.Ago(t)})" : ""), Theme.Text3);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##craftSearch", "Search recipes", ref search, 100);

        var hidden = Config.HiddenCraftJobs.Count;
        if (ImGui.Button(hidden == 0 ? "All jobs" : $"{8 - hidden} jobs")) ImGui.OpenPopup("##jobs");
        using (var popup = ImRaii.Popup("##jobs"))
        {
            if (popup)
            {
                for (var i = 0; i < RecipeInfo.JobAbbreviations.Length; i++)
                {
                    var shown = !Config.HiddenCraftJobs.Contains(i);
                    var level = scanner.JobLevels[i];
                    if (ImGui.Checkbox($"{RecipeInfo.JobAbbreviations[i]}  {(level > 0 ? $"lv {level}" : "-")}##job{i}", ref shown))
                    {
                        if (shown) Config.HiddenCraftJobs.Remove(i);
                        else Config.HiddenCraftJobs.Add(i);
                        Config.Save();
                    }
                }
            }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        using (var combo = ImRaii.Combo("##craftSort", Sorts[sort]))
        {
            if (combo)
            {
                for (var i = 0; i < Sorts.Length; i++)
                    if (ImGui.Selectable(Sorts[i], i == sort)) sort = i;
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("Unlocked", ref unlockedOnly);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only recipes you can craft right now.");

        ImGui.Spacing();
        using var scroll = ImRaii.Child("##craftRows", Vector2.Zero);
        if (!scroll) return;
        foreach (var o in rows)
        {
            if (Row(o, o == selected))
            {
                selectedRecipe = o.Recipe.RecipeId;
                quantity = o.SuggestedCrafts;
                startError = null;
            }
        }
    }

    private static bool Row(CraftOpportunity o, bool selected)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##r{o.Recipe.RecipeId}", new Vector2(width, RowHeight));
        var dl = ImGui.GetWindowDrawList();
        if (selected)
        {
            dl.AddRectFilled(pos, pos + new Vector2(width, RowHeight), Theme.U32(Theme.Current.Selected), 3f);
            dl.AddRect(pos, pos + new Vector2(width, RowHeight), Theme.U32(Theme.Current.LineColor), 3f);
        }
        else if (ImGui.IsItemHovered())
        {
            dl.AddRectFilled(pos, pos + new Vector2(width, RowHeight), Theme.U32(Theme.Panel2), 3f);
        }

        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(o.Item.Icon, o.SellHq)).GetWrapOrEmpty();
        var tint = o.Unlocked ? uint.MaxValue : Theme.U32(new Vector4(1, 1, 1, 0.5f));
        dl.AddImage(tex.Handle, pos + new Vector2(8, 9), pos + new Vector2(48, 49), Vector2.Zero, Vector2.One, tint);

        var right = pos.X + width - 10;
        var profit = Theme.Short(o.BatchProfit);
        var ps = ImGui.CalcTextSize(profit);
        dl.AddText(new Vector2(right - ps.X, pos.Y + 10), Theme.U32(o.BatchProfit > 0 ? (o.Unlocked ? Theme.Good : Theme.Text2) : Theme.Bad), profit);
        var crafts = $"{o.SuggestedCrafts} crafts";
        var cs = ImGui.CalcTextSize(crafts);
        dl.AddText(new Vector2(right - cs.X, pos.Y + 31), Theme.U32(Theme.Text3), crafts);

        var textRight = right - Math.Max(ps.X, cs.X) - 12;
        dl.PushClipRect(new Vector2(pos.X + 58, pos.Y), new Vector2(textRight, pos.Y + RowHeight), true);
        var name = o.Item.Name + (o.SellHq ? "  HQ" : "") + (o.Recipe.Yield > 1 ? $"  ×{o.Recipe.Yield}" : "");
        dl.AddText(new Vector2(pos.X + 58, pos.Y + 10), Theme.U32(o.Unlocked ? Theme.Text : Theme.Text2), name);
        if (o.LockedReason is { } locked)
            dl.AddText(new Vector2(pos.X + 58, pos.Y + 31), Theme.U32(Theme.Bad), locked);
        else
            dl.AddText(new Vector2(pos.X + 58, pos.Y + 31), Theme.U32(Theme.Text2), $"{o.Recipe.Job} {o.Recipe.Level} · {o.UnitsPerDay:0.#} sold/day");
        dl.PopClipRect();
        return clicked;
    }

    private IEnumerable<CraftOpportunity> Filter(IEnumerable<CraftOpportunity> all)
    {
        var rows = all.Where(o => !Config.HiddenCraftJobs.Contains(o.Recipe.CraftType))
                      .Where(o => showUnprofitable || o.ProfitPerCraft > 0)
                      .Where(o => !unlockedOnly || o.Unlocked)
                      .Where(o => search.Length == 0 || o.Item.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        return sort switch
        {
            1 => rows.OrderByDescending(o => o.ProfitPerCraft),
            2 => rows.OrderByDescending(o => o.CashProfitPerCraft),
            3 => rows.OrderByDescending(o => o.UnitsPerDay),
            4 => rows.OrderByDescending(o => o.DailyProfit),
            _ => rows.OrderByDescending(o => o.BatchProfit),
        };
    }

    // ---- Detail -------------------------------------------------------------------------------------

    private void DrawDetail(CraftOpportunity o)
    {
        Theme.Icon(o.Item.Icon, o.SellHq, 64);
        ImGui.SameLine(0, 14);
        using (ImRaii.Group())
        {
            ImGui.Dummy(new Vector2(0, 4));
            using (Theme.HeadingFont()) ImGui.TextUnformatted(o.Item.Name);
            if (o.Unlocked) Theme.Tag("Unlocked", Theme.Good, small: true);
            else Theme.Tag("Locked", Theme.Bad, small: true);
            ImGui.SameLine();
            if (o.SellHq) { Theme.Tag("HQ", Theme.Current.Color, small: true); ImGui.SameLine(); }
            Theme.Secondary($"{o.Recipe.Job} {o.Recipe.Level} · makes {o.Recipe.Yield} per craft");
        }
        if (o.LockedReason is { } locked) Theme.Wrapped(locked, Theme.Bad);
        if (o.Warning is { } warn) Theme.Wrapped(warn, Theme.Hold);
        ImGui.Spacing();

        var w = (ImGui.GetContentRegionAvail().X - 30) / 4;
        Theme.Tile("Sells at", o.SalePrice.ToString("N0"), w);
        ImGui.SameLine(0, 10);
        Theme.Tile("Materials", o.MaterialValue.ToString("N0"), w);
        ImGui.SameLine(0, 10);
        Theme.Tile("Profit per craft", o.ProfitPerCraft.ToString("N0"), w, o.ProfitPerCraft > 0 ? Theme.Good : Theme.Bad);
        ImGui.SameLine(0, 10);
        Theme.Tile("Sold per day", o.UnitsPerDay.ToString("0.#"), w);
        ImGui.Spacing();

        DrawQualityCheck(o);
        ImGui.Spacing();

        // Leave room for the action bar at the bottom.
        var barHeight = ImGui.GetFrameHeight() + 20;
        using (var mats = ImRaii.Child("##mats", new Vector2(0, ImGui.GetContentRegionAvail().Y - barHeight)))
        {
            if (mats) DrawMaterials(o);
        }
        DrawActionBar(o);
    }

    private void DrawQualityCheck(CraftOpportunity o)
    {
        var report = plugin.Quality.Get(o, plugin.Advice.PricingWorld);
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(start + new Vector2(14, 12));
        using (ImRaii.Group())
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - 28);
            var title = report is { Target: > 0 } ? $"Quality check · aiming for {report.TargetLabel}" : "Quality check";
            ImGui.TextUnformatted(title);

            if (report == null)
            {
                Theme.Muted("Working it out...");
            }
            else if (report.Note is { } note && report.Unbuffed == null)
            {
                Theme.Muted(note);
            }
            else if (report.Stats is { } s && report.Unbuffed is { } plan)
            {
                Theme.Muted($"Your {o.Recipe.Job}: {s.Craftsmanship:N0} craftsmanship · {s.Control:N0} control · {s.CP} CP · lv {s.Level}"
                            + (report.StatsSavedUtc is { } saved ? $" (seen {Theme.Ago(saved)})" : ""));

                if (report.ReachesUnbuffed)
                {
                    ImGui.TextColored(Theme.Good, $"Reaches {report.TargetLabel} without food or medicine.");
                }
                else
                {
                    var pct = report.Target > 0 ? 100.0 * plan.BestQuality / report.Target : 0;
                    ImGui.TextColored(Theme.Hold, plan.CanCraft
                        ? $"Gets to about {pct:0}% of the quality needed ({plan.BestQuality:N0} of {report.Target:N0})."
                        : plan.Problem ?? "Can't finish this craft with these stats.");

                    if (report.Buffs is { } buffs)
                    {
                        var names = new[] { buffs.Food, buffs.Medicine }.OfType<Consumable>().ToList();
                        if (buffs.Plan.Reaches(report.Target))
                        {
                            ImGui.TextColored(Theme.Good, "With these it reaches the target:");
                            DrawConsumables(names);
                        }
                        else if (names.Count > 0)
                        {
                            var bpct = report.Target > 0 ? 100.0 * buffs.Plan.BestQuality / report.Target : 0;
                            ImGui.TextColored(Theme.Bad, $"Even the best food and medicine only reach about {bpct:0}%. Better gear or HQ materials are needed.");
                            DrawConsumables(names);
                        }
                    }
                }

                if (o.Recipe.CollectableQuality is { Count: 3 } tiers)
                {
                    var best = Math.Max(plan.BestQuality, report.Buffs?.Plan.BestQuality ?? 0) / 10;
                    Theme.Muted($"Collectability tiers {tiers[0] / 10} / {tiers[1] / 10} / {tiers[2] / 10}. Estimated best: {best}.");
                }

                var rotation = (report.ReachesUnbuffed ? plan : report.Buffs?.Plan ?? plan).Rotation;
                if (rotation.Count > 0)
                {
                    using var node = ImRaii.TreeNode($"Estimated rotation ({rotation.Count} steps)##rot");
                    if (node) Theme.Wrapped(string.Join(" › ", rotation.Select(Spaced)), Theme.Text2);
                }
            }
            ImGui.PopTextWrapPos();
        }

        var end = ImGui.GetCursorScreenPos();
        dl.ChannelsSetCurrent(0);
        Theme.Box(start, new Vector2(start.X + width, end.Y + 6), Theme.Panel, Theme.Line);
        dl.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 12));
    }

    private void DrawConsumables(List<Consumable> items)
    {
        foreach (var c in items)
        {
            var icon = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(c.ItemId)?.Icon ?? 0;
            Theme.Icon(icon, c.Hq, 24);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(c.Label);
            ImGui.SameLine();
            var owned = plugin.Tracker.CountInBags(c.ItemId);
            var price = plugin.Market.GetAggregated(c.ItemId) is { } p ? (c.Hq ? p.Hq.MinListing : p.Nq.MinListing) : null;
            Theme.Muted(owned > 0 ? $"· {owned} in your bags" : price is { } gil ? $"· about {gil:N0} gil on the market" : "· price unknown");
        }
    }

    private static string Spaced(CraftAction a)
        => string.Concat(a.ToString().Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString()));

    private void DrawMaterials(CraftOpportunity o)
    {
        Theme.Secondary($"Materials for {quantity} craft{(quantity == 1 ? "" : "s")}");
        var tracker = plugin.Tracker;
        var i = 0;
        foreach (var m in o.Materials)
        {
            i++;
            var need = (int)Math.Ceiling(m.AmountPerCraft * quantity);
            var bags = tracker.CountInBags(m.ItemId);
            var retainers = tracker.CountOnRetainers(m.ItemId);
            var info = plugin.Catalog.Get(m.ItemId);

            ImGui.Dummy(new Vector2(m.Depth * 22, 0));
            ImGui.SameLine(0, 0);
            Theme.Icon(info?.Icon ?? 0, false, m.Depth == 0 ? 28 : 24);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(m.Name);

            var col = ImGui.GetContentRegionAvail().X;
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 330);
            ImGui.AlignTextToFramePadding();
            var (src, srcColor) = m.Source switch
            {
                MaterialSource.Gather => ("Gather", Theme.Gather),
                MaterialSource.Craft => ("Craft", Theme.Current.Color),
                MaterialSource.Buy => ("Market", Theme.Vendor),
                MaterialSource.Vendor => ("NPC", Theme.Text2),
                _ => ("Unknown", Theme.Bad),
            };
            Theme.Tag(src, srcColor, small: true);

            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 255);
            var fraction = need > 0 ? bags / (float)need : 1;
            Theme.Bar(fraction, fraction >= 1 ? Theme.Good : srcColor, new Vector2(70, 5));
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(bags >= need ? Theme.Good : bags + retainers >= need ? Theme.Hold : Theme.Text2, $"{bags:N0}/{need:N0}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{bags:N0} in your bags, {retainers:N0} on retainers." + (bags < need && bags + retainers >= need ? "\nVulcan can pull these; for Artisan, withdraw them first." : ""));

            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 80);
            if (bags < need)
            {
                if (m.Source == MaterialSource.Gather && CraftCoordinator.VulcanAvailable)
                {
                    if (ImGui.SmallButton($"Gather##g{i}")) Plugin.CommandManager.ProcessCommand($"/gather {m.Name}");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("GatherBuddy teleports you to the nearest node and marks it.");
                }
                else if (m.Source == MaterialSource.Buy)
                {
                    if (ImGui.SmallButton($"Prices##u{i}")) Util.OpenLink($"https://universalis.app/market/{m.ItemId}");
                }
            }
            else
            {
                ImGui.NewLine();
            }
            _ = col;
        }
    }

    private void DrawActionBar(CraftOpportunity o)
    {
        var crafter = plugin.Crafter;
        ImGui.Separator();
        ImGui.AlignTextToFramePadding();
        Theme.Secondary("Crafts");
        ImGui.SameLine();
        if (ImGui.Button("−##less", new Vector2(ImGui.GetFrameHeight()))) quantity = Math.Max(1, quantity - 1);
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(46);
        if (ImGui.InputInt("##qty", ref quantity, 0, 0)) quantity = Math.Clamp(quantity, 1, 999);
        ImGui.SameLine(0, 4);
        if (ImGui.Button("+##more", new Vector2(ImGui.GetFrameHeight()))) quantity = Math.Min(999, quantity + 1);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        Theme.Muted($"suggested {o.SuggestedCrafts} ·");
        ImGui.SameLine();
        ImGui.TextColored(o.ProfitPerCraft > 0 ? Theme.Good : Theme.Bad, $"{o.ProfitPerCraft * quantity:N0} profit");

        var missing = crafter.MissingForArtisan(o, quantity);
        var gatherLabel = "Gather + craft";
        var artisanLabel = "Craft with Artisan";
        var style = ImGui.GetStyle();
        var buttonsWidth = ImGui.CalcTextSize(gatherLabel).X + ImGui.CalcTextSize(artisanLabel).X + style.FramePadding.X * 4 + style.ItemSpacing.X;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - buttonsWidth);

        using (ImRaii.Disabled(crafter.IsRunning || !o.Unlocked || !CraftCoordinator.ArtisanAvailable || missing.Count > 0))
        {
            if (ImGui.Button(artisanLabel)) startError = crafter.Start(o, quantity, CraftBackend.Artisan);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!CraftCoordinator.ArtisanAvailable ? "Install and enable Artisan to use this."
                : missing.Count > 0 ? "Artisan needs everything in your bags. Missing:\n" + string.Join("\n", missing.Select(m => $"  {m.Line.Name} ×{m.Missing}"))
                : "Artisan crafts any intermediates you're short of, then the item.");

        ImGui.SameLine();
        using (ImRaii.Disabled(crafter.IsRunning || !o.Unlocked || !CraftCoordinator.VulcanAvailable))
        {
            if (Theme.PrimaryButton(gatherLabel)) startError = crafter.Start(o, quantity, CraftBackend.Vulcan);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(CraftCoordinator.VulcanAvailable
                ? "GatherBuddy Reborn (Vulcan) gathers missing materials, pulls from retainers, then crafts."
                : "Install and enable GatherBuddy Reborn to use this.");

        if (startError != null) ImGui.TextColored(Theme.Bad, startError);
    }

    // ---- Running job: pipeline ----------------------------------------------------------------------

    private void DrawPipeline(CraftJob job)
    {
        var o = job.Opportunity;
        var tracker = plugin.Tracker;

        Theme.Icon(o.Item.Icon, o.SellHq, 48);
        ImGui.SameLine(0, 12);
        using (ImRaii.Group())
        {
            using (Theme.HeadingFont()) ImGui.TextUnformatted($"{o.Item.Name} ×{job.Crafts}");
            var (stateText, stateColor) = job.State switch
            {
                CraftJobState.Running => (job.WaitingForRepair ? "Repairing" : "Running", Theme.Current.Color),
                CraftJobState.Finished => ("Finished", Theme.Good),
                CraftJobState.Stopped => ("Stopped", Theme.Hold),
                _ => ("Failed", Theme.Bad),
            };
            Theme.Tag(stateText, stateColor, small: true);
            ImGui.SameLine();
            Theme.Muted($"via {(job.Backend == CraftBackend.Vulcan ? "GatherBuddy" : "Artisan")} · started {Theme.Ago(job.StartedUtc)}");
        }

        var buttons = job.State == CraftJobState.Running ? 190f : 90f;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - buttons);
        if (job.State == CraftJobState.Running)
        {
            if (ImGui.Button("Stop")) plugin.Crafter.Stop();
            ImGui.SameLine();
            if (ImGui.Button("Mark finished")) plugin.Crafter.MarkFinished();
        }
        else if (ImGui.Button("Close"))
        {
            plugin.Crafter.Dismiss();
        }

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Theme.Raise2))
            ImGui.ProgressBar(job.Wanted > 0 ? Math.Clamp(job.Made / (float)job.Wanted, 0, 1) : 0, new Vector2(-1, 22), job.Status);
        ImGui.Spacing();

        // Work out which stage we're in from what's in the bags.
        var leaves = o.Materials.Where(m => m.Source != MaterialSource.Craft).GroupBy(m => m.ItemId)
            .Select(g => (Line: g.First(), Need: (int)Math.Ceiling(g.Sum(m => m.AmountPerCraft) * job.Crafts))).ToList();
        var parts = o.Materials.Where(m => m.Source == MaterialSource.Craft)
            .Select(m => (Line: m, Need: (int)Math.Ceiling(m.AmountPerCraft * job.Crafts))).ToList();
        var gathered = leaves.All(l => tracker.CountInBags(l.Line.ItemId) >= l.Need) || job.Made > 0;
        var partsDone = parts.All(p => tracker.CountInBags(p.Line.ItemId) >= p.Need) || job.Made > 0;
        var stage = job.State == CraftJobState.Finished ? 4 : !gathered ? 1 : !partsDone ? 2 : 3;

        var avail = ImGui.GetContentRegionAvail();
        var arrow = 22f;
        float[] weights = [1.7f, 0.9f, 0.8f, 1.0f];
        var total = avail.X - arrow * 3;
        var height = Math.Min(avail.Y - 10, 330);
        var widths = weights.Select(x => total * x / weights.Sum()).ToArray();

        StageCard("##st1", "1  Gather", stage, 1, widths[0], height, () =>
        {
            var perRow = Math.Max(1, (int)((widths[0] - 28) / 64));
            var firstMissing = leaves.FindIndex(l => tracker.CountInBags(l.Line.ItemId) < l.Need);
            for (var i = 0; i < leaves.Count; i++)
            {
                if (i % perRow != 0) ImGui.SameLine(0, 8);
                var (line, need) = leaves[i];
                MaterialCell(line.ItemId, need, i == firstMissing && stage == 1, line.Source == MaterialSource.Gather ? Theme.Gather : Theme.Vendor);
            }
        });
        Arrow(arrow, height);
        StageCard("##st2", "2  Craft parts", stage, 2, widths[1], height, () =>
        {
            if (parts.Count == 0) Theme.Muted("No intermediate crafts.");
            for (var i = 0; i < parts.Count; i++)
            {
                if (i % 2 != 0) ImGui.SameLine(0, 8);
                MaterialCell(parts[i].Line.ItemId, parts[i].Need, false, Theme.Current.Color);
            }
        });
        Arrow(arrow, height);
        StageCard("##st3", "3  Craft", stage, 3, widths[2], height, () =>
        {
            MaterialCell(o.Item.Id, job.Wanted, stage == 3, Theme.Current.Color, job.Made);
        });
        Arrow(arrow, height);
        StageCard("##st4", "4  Sell", stage, 4, widths[3], height, () =>
        {
            var stacks = plugin.Advice.Plan.Stacks;
            var rec = stacks.FirstOrDefault(r => r.Item.Id == o.Item.Id && r.Hq == o.SellHq) ?? stacks.FirstOrDefault(r => r.Item.Id == o.Item.Id);
            if (job.State == CraftJobState.Finished && rec?.SuggestedPrice is { } price)
            {
                using (Theme.BigFont()) ImGui.TextUnformatted($"{price:N0}");
                Theme.Muted("each, fresh price");
                Theme.Secondary($"{rec.NetTotal:N0} gil after tax");
                if (Theme.PrimaryButton("Copy price")) ImGui.SetClipboardText(price.ToString());
            }
            else
            {
                using (Theme.BigFont()) ImGui.TextUnformatted($"{o.SalePrice:N0}");
                Theme.Muted("each, estimated");
                ImGui.TextColored(Theme.Good, $"{o.ProfitPerCraft * job.Crafts:N0} expected profit");
                Theme.Muted("A fresh price is fetched when the crafts land.");
            }
        });

        ImGui.NewLine();
        if (job.WaitingForRepair || plugin.Repair.IsBusy)
            ImGui.TextColored(Theme.Hold, $"Repair: {plugin.Repair.Status}");
    }

    private void MaterialCell(uint itemId, int need, bool active, Vector4 color, int? haveOverride = null)
    {
        var info = plugin.Catalog.Get(itemId);
        var have = haveOverride ?? plugin.Tracker.CountInBags(itemId);
        using (ImRaii.Group())
        {
            var pos = ImGui.GetCursorScreenPos();
            Theme.Icon(info?.Icon ?? 0, false, 44);
            var dl = ImGui.GetWindowDrawList();
            if (active) dl.AddRect(pos - new Vector2(2), pos + new Vector2(46), Theme.U32(Theme.Current.Color), 3f, ImDrawFlags.None, 2f);
            var text = $"{have}/{need}";
            var ts = ImGui.CalcTextSize(text);
            var tpos = pos + new Vector2((44 - ts.X) / 2, 44 - ts.Y + 2);
            dl.AddRectFilled(tpos - new Vector2(3, 0), tpos + ts + new Vector2(3, 0), Theme.U32(Theme.Bg0), 2f);
            dl.AddText(tpos, Theme.U32(have >= need ? Theme.Good : color), text);
            ImGui.Dummy(new Vector2(56, 2));
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{info?.Name ?? "Item"}: {have:N0} of {need:N0}");
    }

    private static void StageCard(string id, string title, int currentStage, int index, float width, float height, Action body)
    {
        var active = currentStage == index;
        var done = currentStage > index;
        using var c = ImRaii.PushColor(ImGuiCol.ChildBg, active ? Theme.Current.Selected : Theme.Panel)
            .Push(ImGuiCol.Border, active ? Theme.Current.LineColor : Theme.Line);
        using var s = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14, 12)).Push(ImGuiStyleVar.ChildBorderSize, 1f);
        using (var child = ImRaii.Child(id, new Vector2(width, height), true, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            if (child)
            {
                ImGui.TextColored(active ? Theme.Current.Color : done ? Theme.Good : Theme.Text2, title + (done ? "  done" : active ? "  now" : ""));
                ImGui.Spacing();
                body();
            }
        }
        ImGui.SameLine(0, 0);
    }

    private static void Arrow(float width, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var mid = pos + new Vector2(width / 2, height / 2);
        var dl = ImGui.GetWindowDrawList();
        var col = Theme.U32(Theme.Line3);
        dl.AddLine(mid - new Vector2(6, 0), mid + new Vector2(6, 0), col, 2f);
        dl.AddTriangleFilled(mid + new Vector2(7, 0), mid + new Vector2(1, -5), mid + new Vector2(1, 5), col);
        ImGui.Dummy(new Vector2(width, height));
        ImGui.SameLine(0, 0);
    }
}
