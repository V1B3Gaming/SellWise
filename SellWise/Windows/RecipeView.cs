using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using SellWise.Core;
using SellWise.Services;

namespace SellWise.Windows;

/// <summary>
/// Any recipe, no market or quest attached: search for it, then have GatherBuddy gather the materials and Artisan
/// craft it, or just gather the materials and stop.
/// </summary>
public sealed class RecipeView
{
    private const float ListWidth = 430;
    private const float RowHeight = 46;
    private const int MaxRows = 200;

    private readonly Plugin plugin;
    private readonly CraftView craftView;
    private string search = "";
    private int job = -1;
    private bool unlockedOnly = true;
    private uint selectedRecipe;
    private int quantity = 1;
    private string? startError;
    private RecipeUnlocks? unlocks;
    private DateTime nextUnlocks;
    private (uint RecipeId, CraftOpportunity Opp, CraftOpportunity Plan, DateTime At)? costed;
    private (string Search, int Job, bool Unlocked, RecipeUnlocks? Unlocks, List<RecipeInfo> Rows)? rowCache;
    private readonly HashSet<uint> priced = [];

    public RecipeView(Plugin plugin, CraftView craftView)
    {
        this.plugin = plugin;
        this.craftView = craftView;
    }

    private Configuration Config => plugin.Config;

    public void Draw()
    {
        var avail = ImGui.GetContentRegionAvail();
        var dbTask = plugin.Scanner.LoadDb();
        if (!dbTask.IsCompletedSuccessfully)
        {
            using var loading = Pane("##rloading", avail);
            Theme.Muted("Loading recipes...");
            return;
        }
        var db = dbTask.Result;
        var now = DateTime.UtcNow;
        if (now >= nextUnlocks)
        {
            nextUnlocks = now.AddSeconds(30);
            unlocks = ProfitScanner.ReadUnlocks(db);
        }

        if (rowCache is not { } rc || rc.Search != search || rc.Job != job || rc.Unlocked != unlockedOnly || !ReferenceEquals(rc.Unlocks, unlocks))
            rowCache = rc = (search, job, unlockedOnly, unlocks, Rows(db).ToList());
        var rows = rc.Rows;
        var selected = rows.FirstOrDefault(r => r.RecipeId == selectedRecipe)
                       ?? db.Recipes.FirstOrDefault(r => r.RecipeId == selectedRecipe);

        using (var left = Pane("##recipeList", new Vector2(ListWidth, avail.Y)))
        {
            if (left) DrawList(rows, selected);
        }
        ImGui.SameLine(0, 0);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(0, avail.Y), Theme.U32(Theme.Line));

        using var right = Pane("##recipeDetail", new Vector2(avail.X - ListWidth, avail.Y));
        if (!right) return;
        if (plugin.Crafter.Job is { } running) DrawRunningBanner(running);
        if (selected == null)
        {
            Theme.Wrapped("Search for anything you can craft. SellWise lists its materials, has GatherBuddy gather them and Artisan craft it.", Theme.Text3);
            return;
        }
        DrawDetail(selected, db);
    }

    private static ImRaii.ChildDisposable Pane(string id, Vector2 size)
    {
        using var c = ImRaii.PushColor(ImGuiCol.ChildBg, Theme.Bg1);
        using var s = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        return ImRaii.Child(id, size, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
    }

    private IEnumerable<RecipeInfo> Rows(RecipeDb db)
    {
        if (search.Trim().Length < 2) return [];
        var term = search.Trim();
        return db.Recipes
            .Where(r => r.CraftType is >= 0 and < 8 && (job < 0 || r.CraftType == job))
            .Where(r => !unlockedOnly || unlocks?.IsUnlocked(r) != false)
            .Select(r => (Recipe: r, Name: plugin.Catalog.Get(r.ResultItemId)?.Name ?? ""))
            .Where(x => x.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(x => x.Recipe.Level)
            .Take(MaxRows)
            .Select(x => x.Recipe);
    }

    private void DrawList(List<RecipeInfo> rows, RecipeInfo? selected)
    {
        using (Theme.HeadingFont()) ImGui.TextUnformatted("Any recipe");
        Theme.Wrapped("Gather the materials for anything you can craft, then craft it. No market or quest needed.", Theme.Text3);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##recipeSearch", "Search recipes (at least 2 letters)", ref search, 100);
        ImGui.SetNextItemWidth(110);
        using (var combo = ImRaii.Combo("##rjob", job < 0 ? "All jobs" : RecipeInfo.JobAbbreviations[job]))
        {
            if (combo)
            {
                if (ImGui.Selectable("All jobs", job < 0)) job = -1;
                for (var i = 0; i < RecipeInfo.JobAbbreviations.Length; i++)
                    if (ImGui.Selectable(RecipeInfo.JobAbbreviations[i], job == i)) job = i;
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("Unlocked", ref unlockedOnly);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only recipes you can craft right now.");

        ImGui.Spacing();
        using var scroll = ImRaii.Child("##recipeRows", Vector2.Zero);
        if (!scroll) return;
        if (rows.Count == MaxRows) Theme.Muted($"Showing the first {MaxRows}. Type more to narrow it down.");
        foreach (var r in rows)
        {
            if (Row(r, r == selected))
            {
                selectedRecipe = r.RecipeId;
                quantity = 1;
                startError = null;
            }
        }
    }

    private bool Row(RecipeInfo r, bool selected)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##rr{r.RecipeId}", new Vector2(width, RowHeight));
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

        var item = plugin.Catalog.Get(r.ResultItemId);
        var locked = unlocks?.Describe(r);
        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item?.Icon ?? 0)).GetWrapOrEmpty();
        dl.AddImage(tex.Handle, pos + new Vector2(8, 7), pos + new Vector2(40, 39), Vector2.Zero, Vector2.One,
            locked == null ? uint.MaxValue : Theme.U32(new Vector4(1, 1, 1, 0.5f)));

        dl.PushClipRect(new Vector2(pos.X + 50, pos.Y), new Vector2(pos.X + width - 8, pos.Y + RowHeight), true);
        dl.AddText(new Vector2(pos.X + 50, pos.Y + 6), Theme.U32(locked == null ? Theme.Text : Theme.Text2), item?.Name ?? $"Item {r.ResultItemId}");
        dl.AddText(new Vector2(pos.X + 50, pos.Y + 25), Theme.U32(locked == null ? Theme.Text3 : Theme.Bad),
            locked ?? $"{r.Job} {r.Level}" + (r.Yield > 1 ? $" · makes {r.Yield}" : "") + (r.IsExpert ? " · expert" : ""));
        dl.PopClipRect();
        return clicked;
    }

    /// <summary>The recipe costed with the current material choices (refreshed every few seconds as prices arrive).</summary>
    private CraftOpportunity? Costed(RecipeInfo recipe, RecipeDb db)
    {
        if (priced.Add(recipe.RecipeId) && plugin.Advice.PricingWorld.Length > 0)
        {
            var ids = new HashSet<uint>();
            ProfitScanner.CollectMaterials(recipe, db, 5, ids);
            _ = plugin.Market.FetchAggregatedAsync(ids.ToList(), plugin.Advice.PricingWorld, TimeSpan.FromMinutes(30), null, default);
        }

        var now = DateTime.UtcNow;
        if (costed is { } c && c.RecipeId == recipe.RecipeId && now - c.At < TimeSpan.FromSeconds(5)) return c.Opp;
        if (plugin.Scanner.NewCalculator()?.Cost(recipe) is not { } o) return null;
        o.LockedReason = unlocks?.Describe(recipe);
        costed = (recipe.RecipeId, o, plugin.Scanner.VulcanPlan(o), now);
        return o;
    }

    private void DrawDetail(RecipeInfo recipe, RecipeDb db)
    {
        if (Costed(recipe, db) is not { } baseOpp) return;
        var o = craftView.Priced(baseOpp);

        Theme.Icon(o.Item.Icon, false, 56);
        ImGui.SameLine(0, 14);
        using (ImRaii.Group())
        {
            using (Theme.HeadingFont()) ImGui.TextUnformatted(Theme.Fit(o.Item.Name, ImGui.GetContentRegionAvail().X));
            Theme.Tag(o.Unlocked ? "Unlocked" : "Locked", o.Unlocked ? Theme.Good : Theme.Bad, small: true);
            ImGui.SameLine();
            Theme.Secondary($"{recipe.Job} {recipe.Level} · makes {recipe.Yield} per craft · materials about {o.MaterialValue:N0} gil each");
        }
        if (o.LockedReason is { } locked) Theme.Wrapped(locked, Theme.Bad);
        if (o.Warning is { } warn) Theme.Wrapped(warn, Theme.Hold);
        ImGui.Spacing();

        craftView.DrawMaterialMode(o);
        if (recipe.CanHq || recipe.CollectableQuality != null) craftView.DrawQualityCheck(o);
        ImGui.Spacing();

        var toBuy = craftView.ToBuy(o, quantity);
        var barHeight = ImGui.GetFrameHeight() + 20 + (toBuy.Count > 0 ? ImGui.GetTextLineHeightWithSpacing() : 0);
        using (var mats = ImRaii.Child("##recipeMats", new Vector2(0, ImGui.GetContentRegionAvail().Y - barHeight)))
        {
            if (mats) craftView.DrawMaterials(o, quantity);
        }
        DrawActionBar(o, toBuy);
    }

    private void DrawActionBar(CraftOpportunity o, List<(MaterialLine Line, int Missing)> toBuy)
    {
        var crafter = plugin.Crafter;
        if (toBuy.Count > 0)
        {
            ImGui.TextColored(Theme.Hold, Theme.Fit("Buy first: " + string.Join(", ", toBuy.Select(b => $"{b.Line.Name} x{b.Missing}")), ImGui.GetContentRegionAvail().X));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("SellWise never buys for you. Anything not in your bags, GatherBuddy gathers or crafts instead.");
        }
        ImGui.Separator();
        ImGui.AlignTextToFramePadding();
        Theme.Secondary("Crafts");
        ImGui.SameLine();
        if (ImGui.Button("-##rless", new Vector2(ImGui.GetFrameHeight()))) quantity = Math.Max(1, quantity - 1);
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(46);
        if (ImGui.InputInt("##rqty", ref quantity, 0, 0)) quantity = Math.Clamp(quantity, 1, 999);
        ImGui.SameLine(0, 4);
        if (ImGui.Button("+##rmore", new Vector2(ImGui.GetFrameHeight()))) quantity = Math.Min(999, quantity + 1);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        Theme.Muted($"= {quantity * Math.Max(1, o.Recipe.Yield)} {(quantity * o.Recipe.Yield == 1 ? "item" : "items")}");

        var plan = costed?.Plan ?? o;
        var missing = crafter.MissingForArtisan(o, quantity);
        var nothingToGather = crafter.MissingForArtisan(plan, quantity).Count == 0;
        const string gatherOnlyLabel = "Gather only";
        const string artisanLabel = "Craft with Artisan";
        const string bothLabel = "Gather + craft";
        var style = ImGui.GetStyle();
        var width = ImGui.CalcTextSize(gatherOnlyLabel).X + ImGui.CalcTextSize(artisanLabel).X + ImGui.CalcTextSize(bothLabel).X
                    + style.FramePadding.X * 6 + style.ItemSpacing.X * 2;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - width);

        using (ImRaii.Disabled(crafter.IsRunning || !CraftCoordinator.VulcanAvailable || nothingToGather))
        {
            if (ImGui.Button(gatherOnlyLabel))
            {
                var crafts = quantity;
                startError = plugin.StartJob(o.Item.Name, [(plan, crafts)], () => crafter.StartGatherOnly(o, crafts, plan));
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!CraftCoordinator.VulcanAvailable ? "Install and enable GatherBuddy Reborn to use this."
                : nothingToGather ? "You already have everything this needs."
                : "GatherBuddy gathers the materials (and pulls them from your retainers), then stops. Nothing gets crafted.");

        ImGui.SameLine();
        using (ImRaii.Disabled(crafter.IsRunning || !o.Unlocked || !CraftCoordinator.ArtisanAvailable || missing.Count > 0))
        {
            if (ImGui.Button(artisanLabel) && (startError = crafter.Start(o, quantity, CraftBackend.Artisan)) == null) plugin.MinimizeToJob();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!CraftCoordinator.ArtisanAvailable ? "Install and enable Artisan to use this."
                : missing.Count > 0 ? "Artisan needs everything in your bags. Missing:\n" + string.Join("\n", missing.Select(m => $"  {m.Line.Name} x{m.Missing}"))
                : "Artisan crafts any parts you're short of, then the item.");

        ImGui.SameLine();
        var finishWithArtisan = Config.FinishWithArtisan && CraftCoordinator.ArtisanAvailable;
        using (ImRaii.Disabled(crafter.IsRunning || !o.Unlocked || !CraftCoordinator.VulcanAvailable))
        {
            if (Theme.PrimaryButton(bothLabel))
            {
                var crafts = quantity;
                startError = plugin.StartJob(o.Item.Name, [(plan, crafts)],
                    () => crafter.Start(o, crafts, CraftBackend.Vulcan, finishWithArtisan ? plan : null));
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!CraftCoordinator.VulcanAvailable ? "Install and enable GatherBuddy Reborn to use this."
                : finishWithArtisan ? "GatherBuddy gathers the materials, then Artisan crafts the parts and the item, going for max quality."
                : "GatherBuddy gathers the materials and crafts it.");

        if (startError != null) ImGui.TextColored(Theme.Bad, startError);
    }

    private void DrawRunningBanner(CraftJob job)
    {
        var text = job.State == CraftJobState.Running
            ? (job.GatherOnly ? $"Gathering for {job.Opportunity.Item.Name}" : $"Making {job.Opportunity.Item.Name}: {job.Made}/{job.Wanted}")
            : job.Status;
        ImGui.TextColored(Theme.Current.Color, Theme.Fit(text, ImGui.GetContentRegionAvail().X - 90));
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 80);
        if (job.State == CraftJobState.Running)
        {
            if (ImGui.Button("Stop##rstop", new Vector2(80, 0))) plugin.Crafter.Stop();
        }
        else if (ImGui.Button("Close##rclose", new Vector2(80, 0)))
        {
            plugin.Crafter.Dismiss();
        }
        ImGui.Separator();
        ImGui.Spacing();
    }
}
