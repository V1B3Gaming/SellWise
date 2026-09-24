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
/// Scrip farming: which crafter collectables to make (only ones your stats take to the top tier), a turn-in trip
/// to the appraiser, and the scrip exchange's stock with what you already own and what you're saving for.
/// </summary>
public sealed class ScripView
{
    private const float ListWidth = 430;
    private const float RowHeight = 58;
    private static readonly string[] Sorts = ["Scrips per hour", "Level", "Gil per scrip"];
    private static readonly string[] KindFilters = ["Purple and orange", "Purple only", "Orange only"];

    private readonly Plugin plugin;
    private readonly CraftView craftView;
    private int sort;
    private int kindFilter;
    private bool showLocked;
    private uint selectedItem;
    private int quantity = 10;
    private string? startError;

    // Exchange tab
    private ScripKind exchangeKind = ScripKind.PurpleCrafter;
    private string exchangeSearch = "";
    private bool hideOwned = true;
    private bool goalsOnly;

    public ScripView(Plugin plugin, CraftView craftView)
    {
        this.plugin = plugin;
        this.craftView = craftView;
    }

    private Configuration Config => plugin.Config;
    private string World => plugin.Advice.PricingWorld;

    public void Draw()
    {
        var scrips = plugin.Scrips;
        scrips.EnsureLoaded();
        if (scrips.RefreshedUtc == null && !scrips.IsRefreshing && World.Length > 0) scrips.Refresh(World);

        var avail = ImGui.GetContentRegionAvail();
        using var pane = Pane("##scrips", avail);
        if (!pane) return;

        DrawHeader();
        ImGui.Spacing();
        using (var bar = ImRaii.TabBar("##scriptabs"))
        {
            if (!bar) return;
            using (var farm = ImRaii.TabItem("Farm"))
                if (farm) DrawFarm();
            using (var exchange = ImRaii.TabItem("Scrip exchange"))
                if (exchange) DrawExchange();
        }
    }

    private static ImRaii.ChildDisposable Pane(string id, Vector2 size)
    {
        using var c = ImRaii.PushColor(ImGuiCol.ChildBg, Theme.Bg1);
        using var s = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        return ImRaii.Child(id, size, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
    }

    // ---- Header: balances and turn-in -------------------------------------------------------------

    private void DrawHeader()
    {
        using (Theme.HeadingFont()) ImGui.TextUnformatted("Scrips");
        foreach (var kind in Scrips.Crafter)
        {
            ImGui.SameLine(0, 24);
            DrawBalance(kind);
        }

        var turnIn = plugin.TurnIn;
        var inBags = turnIn.InBags().Sum(x => x.Count);
        var label = turnIn.IsBusy ? "Stop turn-in" : inBags > 0 ? $"Turn in {inBags}" : "Turn in";
        var width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - width);
        using (ImRaii.Disabled(!turnIn.IsBusy && inBags == 0))
        {
            if (Theme.PrimaryButton(label))
            {
                if (turnIn.IsBusy) turnIn.Stop();
                else turnIn.Start();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Teleports to Solution Nine or Radz-at-Han, walks to the collectable appraiser and turns in\n" +
                             "every crafter collectable in your bags. Stops before going over the scrip cap.");

        var status = turnIn.Status.Length > 0 ? turnIn.Status : plugin.Scrips.Status;
        if (status.Length > 0) Theme.Wrapped(status, turnIn.Failed ? Theme.Bad : Theme.Text3);
    }

    private void DrawBalance(ScripKind kind)
    {
        var balance = ScripTracker.Balance(kind);
        var cap = ScripTracker.Cap(kind);
        Theme.Icon(plugin.Catalog.Get(Scrips.ItemId(kind))?.Icon ?? 0, false, 22);
        ImGui.SameLine(0, 6);
        ImGui.AlignTextToFramePadding();
        var nearCap = cap > 0 && balance >= cap * 0.9;
        ImGui.TextColored(nearCap ? Theme.Hold : Theme.Text, cap > 0 ? $"{balance:N0} / {cap:N0}" : $"{balance:N0}");
        if (ImGui.IsItemHovered())
        {
            var needed = plugin.ScripTracker.StillNeeded(kind);
            ImGui.SetTooltip(Scrips.Name(kind) + (nearCap ? "\nNearly capped: spend some before turning in more." : "")
                             + (needed > 0 ? $"\n{needed:N0} more needed for your goals." : ""));
        }
    }

    // ---- Farm -------------------------------------------------------------------------------------

    private void DrawFarm()
    {
        var rows = FarmRows().ToList();
        var selected = rows.FirstOrDefault(o => o.Collectable.ItemId == selectedItem) ?? rows.FirstOrDefault();
        if (selected != null) selectedItem = selected.Collectable.ItemId;

        var avail = ImGui.GetContentRegionAvail();
        using (var left = ImRaii.Child("##farmList", new Vector2(ListWidth, avail.Y)))
        {
            if (left) DrawFarmList(rows, selected);
        }
        ImGui.SameLine(0, 0);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(0, avail.Y), Theme.U32(Theme.Line));
        ImGui.SameLine(0, 16);

        using var right = ImRaii.Child("##farmDetail", new Vector2(0, avail.Y));
        if (!right) return;
        if (plugin.Crafter.Job is { } job && job.Opportunity.Recipe.CollectableQuality != null)
        {
            DrawRunning(job);
            return;
        }
        if (selected == null)
        {
            Theme.Wrapped(plugin.Scrips.IsRefreshing ? "Loading collectables..." : "Nothing matches. Try showing locked recipes or the other scrip.", Theme.Text3);
            return;
        }
        DrawFarmDetail(selected);
    }

    /// <summary>Options that pass the filters, with their top-tier check and speed filled in.</summary>
    private IEnumerable<ScripOption> FarmRows()
    {
        var rows = plugin.Scrips.Options
            .Where(o => !Config.HiddenCraftJobs.Contains(o.Collectable.JobIndex))
            .Where(o => kindFilter == 0 || (kindFilter == 1) == (o.Collectable.Scrip == ScripKind.PurpleCrafter))
            .Where(o => showLocked || o.Opportunity.Unlocked)
            .ToList();

        foreach (var o in rows.Where(o => o.Opportunity.Unlocked)) Assess(o);
        var shown = rows.Where(o => o.TopTier != TopTier.Short || !o.Opportunity.Unlocked);

        return sort switch
        {
            1 => shown.OrderByDescending(o => o.Collectable.LevelMin),
            2 => shown.OrderBy(o => o.GilPerScrip ?? double.MaxValue),
            _ => shown.OrderByDescending(o => o.TopTier is TopTier.Reaches or TopTier.NeedsBuffs).ThenByDescending(o => o.ScripsPerHour),
        };
    }

    /// <summary>Top-tier check (from the craft simulator and your saved stats) and time per craft.</summary>
    private void Assess(ScripOption o)
    {
        var report = plugin.Quality.Get(o.Opportunity, World);
        if (report == null || report.Stats == null)
        {
            o.TopTier = TopTier.Unknown;
            o.SecondsPerCraft = TimeEstimator.Craft(1).TotalSeconds;
            return;
        }
        o.TopTier = report.ReachesUnbuffed ? TopTier.Reaches : report.ReachesWithBuffs ? TopTier.NeedsBuffs : TopTier.Short;
        var plan = report.ReachesUnbuffed ? report.Unbuffed : report.Buffs?.Plan ?? report.Unbuffed;
        o.SecondsPerCraft = TimeEstimator.Craft(1, plan is { Rotation.Count: > 0 } p ? p.Rotation.Count : TimeEstimator.DefaultCraftActions).TotalSeconds;
    }

    private void DrawFarmList(List<ScripOption> rows, ScripOption? selected)
    {
        var scrips = plugin.Scrips;
        ImGui.SetNextItemWidth(160);
        ImGui.Combo("##kind", ref kindFilter, KindFilters, KindFilters.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        ImGui.Combo("##scripsort", ref sort, Sorts, Sorts.Length);
        ImGui.SameLine();
        ImGui.Checkbox("Locked", ref showLocked);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Also show collectables you can't craft yet, and why.");

        var hidden = scrips.Options.Count(o => o.Opportunity.Unlocked && o.TopTier == TopTier.Short);
        if (hidden > 0) Theme.Muted($"{hidden} hidden: your stats don't reach their top tier.");
        if (scrips.Options.Any(o => o.Opportunity.Unlocked && o.TopTier == TopTier.Unknown))
            Theme.Muted("Switch to each crafter once so SellWise can read its stats.");

        ImGui.Spacing();
        using var scroll = ImRaii.Child("##scripRows", Vector2.Zero);
        if (!scroll) return;
        foreach (var o in rows)
        {
            if (Row(o, o == selected))
            {
                selectedItem = o.Collectable.ItemId;
                startError = null;
            }
        }
    }

    private bool Row(ScripOption o, bool selected)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##s{o.Collectable.ItemId}", new Vector2(width, RowHeight));
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

        var unlocked = o.Opportunity.Unlocked;
        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(o.Opportunity.Item.Icon)).GetWrapOrEmpty();
        dl.AddImage(tex.Handle, pos + new Vector2(8, 9), pos + new Vector2(48, 49), Vector2.Zero, Vector2.One,
            unlocked ? uint.MaxValue : Theme.U32(new Vector4(1, 1, 1, 0.5f)));

        var right = pos.X + width - 10;
        var reward = $"{o.Reward} {Scrips.Short(o.Collectable.Scrip)}";
        var rs = ImGui.CalcTextSize(reward);
        dl.AddText(new Vector2(right - rs.X, pos.Y + 10), Theme.U32(ScripColor(o.Collectable.Scrip)), reward);
        var (second, secondColor) = !unlocked ? ("", Theme.Text3)
            : o.TopTier switch
            {
                TopTier.Reaches => ($"{Theme.Compact((long)o.ScripsPerHour)}/hr", Theme.Text3),
                TopTier.NeedsBuffs => ("needs food", Theme.Hold),
                _ => ("checking...", Theme.Text3),
            };
        var ss = ImGui.CalcTextSize(second);
        dl.AddText(new Vector2(right - ss.X, pos.Y + 31), Theme.U32(secondColor), second);

        var textRight = right - Math.Max(rs.X, ss.X) - 12;
        dl.PushClipRect(new Vector2(pos.X + 58, pos.Y), new Vector2(textRight, pos.Y + RowHeight), true);
        dl.AddText(new Vector2(pos.X + 58, pos.Y + 10), Theme.U32(unlocked ? Theme.Text : Theme.Text2), o.Opportunity.Item.Name);
        if (o.Opportunity.LockedReason is { } locked)
            dl.AddText(new Vector2(pos.X + 58, pos.Y + 31), Theme.U32(Theme.Bad), locked);
        else
            dl.AddText(new Vector2(pos.X + 58, pos.Y + 31), Theme.U32(Theme.Text2),
                $"{o.Opportunity.Recipe.Job} {o.Opportunity.Recipe.Level}" + (o.GilPerScrip is { } g ? $" · {g:N0} gil/scrip" : ""));
        dl.PopClipRect();
        return clicked;
    }

    private static Vector4 ScripColor(ScripKind kind) => kind is ScripKind.PurpleCrafter or ScripKind.PurpleGatherer ? Hex(0xc59af7) : Hex(0xf2ab62);

    private static Vector4 Hex(uint rgb) => new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1);

    private void DrawFarmDetail(ScripOption option)
    {
        var o = craftView.Priced(option.Opportunity);
        var c = option.Collectable;
        Theme.Icon(o.Item.Icon, false, 64);
        ImGui.SameLine(0, 14);
        using (ImRaii.Group())
        {
            ImGui.Dummy(new Vector2(0, 4));
            using (Theme.HeadingFont()) ImGui.TextUnformatted(Theme.Fit(o.Item.Name, ImGui.GetContentRegionAvail().X));
            Theme.Tag(o.Unlocked ? "Unlocked" : "Locked", o.Unlocked ? Theme.Good : Theme.Bad, small: true);
            ImGui.SameLine();
            Theme.Tag(Scrips.Short(c.Scrip), ScripColor(c.Scrip), small: true);
            ImGui.SameLine();
            Theme.Secondary($"{o.Recipe.Job} {o.Recipe.Level} · turn in at levels {c.LevelMin}-{c.LevelMax}");
        }
        if (o.LockedReason is { } locked) Theme.Wrapped(locked, Theme.Bad);
        if (o.Warning is { } warn) Theme.Wrapped(warn, Theme.Hold);
        ImGui.Spacing();

        var w = (ImGui.GetContentRegionAvail().X - 30) / 4;
        Theme.Tile("Top tier pays", $"{option.Reward} scrips", w, ScripColor(c.Scrip));
        ImGui.SameLine(0, 10);
        Theme.Tile("Crafting speed", option.TopTier == TopTier.Unknown ? "-" : $"{option.ScripsPerHour:N0}/hr", w);
        ImGui.SameLine(0, 10);
        Theme.Tile("Materials", $"{o.MaterialValue:N0} gil", w);
        ImGui.SameLine(0, 10);
        Theme.Tile("Gil per scrip", ScripMath.GilPerScrip(o.MaterialValue, option.Reward) is { } g ? $"{g:N1}" : "-", w);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Market value of the materials for one craft, divided by the scrips it earns.\nLower is better.");
        ImGui.Spacing();

        Theme.Secondary($"Collectability tiers: {c.LowCollectability} / {c.MidCollectability} / {c.HighCollectability}  pays  {c.LowReward} / {c.MidReward} / {c.HighReward}");
        craftView.DrawQualityCheck(o);
        ImGui.Spacing();

        var barHeight = ImGui.GetFrameHeight() * 2 + 30;
        using (var mats = ImRaii.Child("##scripMats", new Vector2(0, ImGui.GetContentRegionAvail().Y - barHeight)))
        {
            if (mats) DrawMaterials(o);
        }
        DrawActionBar(option, o);
    }

    private void DrawMaterials(CraftOpportunity o)
    {
        Theme.Secondary($"Materials for {quantity} craft{(quantity == 1 ? "" : "s")}");
        foreach (var m in o.Materials)
        {
            var need = (int)Math.Ceiling(m.AmountPerCraft * quantity);
            var have = plugin.Tracker.CountInBags(m.ItemId);
            ImGui.Dummy(new Vector2(m.Depth * 22, 0));
            ImGui.SameLine(0, 0);
            Theme.Icon(plugin.Catalog.Get(m.ItemId)?.Icon ?? 0, false, 24);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            var tagColumn = ImGui.GetWindowContentRegionMax().X - 190;
            ImGui.TextUnformatted(Theme.Fit(m.Name, tagColumn - ImGui.GetCursorPosX() - 10));
            ImGui.SameLine(tagColumn);
            var (src, color) = m.Source switch
            {
                MaterialSource.Gather => ("Gather", Theme.Gather),
                MaterialSource.Craft => ("Craft", Theme.Current.Color),
                MaterialSource.Buy => ("Market", Theme.Vendor),
                MaterialSource.Vendor => ("NPC", Theme.Text2),
                _ => ("Unknown", Theme.Bad),
            };
            Theme.Tag(src, color, small: true);
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 100);
            ImGui.TextColored(have >= need ? Theme.Good : Theme.Text2, $"{have:N0}/{need:N0}");
        }
    }

    private void DrawActionBar(ScripOption option, CraftOpportunity o)
    {
        var crafter = plugin.Crafter;
        var c = option.Collectable;
        var balance = ScripTracker.Balance(c.Scrip);
        var cap = ScripTracker.Cap(c.Scrip);
        var room = ScripMath.CraftsBeforeCap(balance, cap, option.Reward);

        ImGui.Separator();
        var earned = option.Reward * quantity;
        if (cap > 0 && balance + earned > cap)
            ImGui.TextColored(Theme.Hold, Theme.Fit($"{quantity} crafts would pass the {cap:N0} cap. {room} fit; spend some scrips first or make fewer.", ImGui.GetContentRegionAvail().X));
        else
            Theme.Muted(Theme.Fit($"{quantity} crafts earn about {earned:N0} scrips (you'd have {balance + earned:N0}" + (cap > 0 ? $" of {cap:N0})." : ")."), ImGui.GetContentRegionAvail().X));

        ImGui.AlignTextToFramePadding();
        Theme.Secondary("Crafts");
        ImGui.SameLine();
        if (ImGui.Button("-##sless", new Vector2(ImGui.GetFrameHeight()))) quantity = Math.Max(1, quantity - 1);
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(46);
        if (ImGui.InputInt("##sqty", ref quantity, 0, 0)) quantity = Math.Clamp(quantity, 1, 999);
        ImGui.SameLine(0, 4);
        if (ImGui.Button("+##smore", new Vector2(ImGui.GetFrameHeight()))) quantity = Math.Min(999, quantity + 1);
        ImGui.SameLine();
        if (room is > 0 and < 999 && ImGui.SmallButton($"Fill to cap ({room})")) quantity = room;
        ImGui.SameLine();
        var turnIn = Config.TurnInAfterScripJob;
        if (ImGui.Checkbox("Turn in when done", ref turnIn))
        {
            Config.TurnInAfterScripJob = turnIn;
            Config.Save();
        }

        var missing = crafter.MissingForArtisan(o, quantity);
        var gatherLabel = "Gather + craft";
        var artisanLabel = "Craft with Artisan";
        var style = ImGui.GetStyle();
        var buttonsWidth = ImGui.CalcTextSize(gatherLabel).X + ImGui.CalcTextSize(artisanLabel).X + style.FramePadding.X * 4 + style.ItemSpacing.X;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - buttonsWidth);
        var blocked = crafter.IsRunning || !o.Unlocked || option.TopTier == TopTier.Short;

        using (ImRaii.Disabled(blocked || !CraftCoordinator.ArtisanAvailable || missing.Count > 0))
        {
            if (ImGui.Button(artisanLabel) && (startError = crafter.Start(o, quantity, CraftBackend.Artisan)) == null) plugin.MinimizeToJob();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!CraftCoordinator.ArtisanAvailable ? "Install and enable Artisan to use this."
                : missing.Count > 0 ? "Artisan needs everything in your bags. Missing:\n" + string.Join("\n", missing.Select(m => $"  {m.Line.Name} x{m.Missing}"))
                : "Artisan crafts the collectables, aiming for the top tier.");

        ImGui.SameLine();
        using (ImRaii.Disabled(blocked || !CraftCoordinator.VulcanAvailable || !CraftCoordinator.ArtisanAvailable))
        {
            if (Theme.PrimaryButton(gatherLabel))
            {
                var plan = plugin.Scanner.VulcanPlan(o);
                var crafts = quantity;
                startError = plugin.StartJob(o.Item.Name, [(plan, crafts)], () => crafter.Start(o, crafts, CraftBackend.Vulcan, plan));
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(CraftCoordinator.VulcanAvailable && CraftCoordinator.ArtisanAvailable
                ? "GatherBuddy gathers the materials, then Artisan crafts the collectables for the top tier."
                : "Needs both GatherBuddy Reborn and Artisan.");

        if (startError != null) ImGui.TextColored(Theme.Bad, startError);
    }

    private void DrawRunning(CraftJob job)
    {
        var o = job.Opportunity;
        Theme.Icon(o.Item.Icon, false, 48);
        ImGui.SameLine(0, 12);
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted($"{o.Item.Name} x{job.Crafts}");
            Theme.Muted(job.Status);
        }
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Theme.Raise2))
            ImGui.ProgressBar(job.Wanted > 0 ? Math.Clamp(job.Made / (float)job.Wanted, 0, 1) : 0, new Vector2(-1, 22), $"{job.Made} / {job.Wanted} made");
        ImGui.Spacing();
        if (job.State == CraftJobState.Running)
        {
            if (ImGui.Button("Stop")) plugin.Crafter.Stop();
        }
        else if (ImGui.Button("Close"))
        {
            plugin.Crafter.Dismiss();
        }
        ImGui.SameLine();
        Theme.Muted(Config.TurnInAfterScripJob ? "Turns them in when done." : "Turn-in after the job is off.");
    }

    // ---- Scrip exchange ---------------------------------------------------------------------------

    private void DrawExchange()
    {
        if (plugin.Scrips.Db is not { } db)
        {
            Theme.Muted("Loading the scrip exchange...");
            return;
        }
        var tracker = plugin.ScripTracker;

        foreach (var kind in Scrips.Crafter)
        {
            if (ImGui.RadioButton($"{char.ToUpper(Scrips.Short(kind)[0])}{Scrips.Short(kind)[1..]}##ex{kind}", exchangeKind == kind)) exchangeKind = kind;
            ImGui.SameLine();
        }
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##exsearch", "Search", ref exchangeSearch, 100);
        ImGui.SameLine();
        ImGui.Checkbox("Hide what you have", ref hideOwned);
        ImGui.SameLine();
        ImGui.Checkbox("Goals only", ref goalsOnly);

        DrawGoalSummary(exchangeKind);
        ImGui.Spacing();

        var goals = tracker.Goals;
        var purchases = tracker.Purchases;
        var items = db.ShopItems
            .Where(i => i.Scrip == exchangeKind)
            .Select(i => (Item: i, Info: plugin.Catalog.Get(i.ItemId), Own: tracker.Get(i.ItemId)))
            .Where(x => x.Info != null)
            .Where(x => exchangeSearch.Length == 0 || x.Info!.Name.Contains(exchangeSearch, StringComparison.OrdinalIgnoreCase))
            .Where(x => !hideOwned || x.Own is not (Ownership.Unlocked or Ownership.Owned or Ownership.NotUsed))
            .Where(x => !goalsOnly || goals.ContainsKey(x.Item.ItemId))
            .OrderByDescending(x => goals.ContainsKey(x.Item.ItemId))
            .ThenBy(x => x.Own == Ownership.Repeatable)
            .ThenBy(x => x.Item.ShopName)
            .ThenBy(x => x.Item.Cost)
            .ToList();

        using var table = ImRaii.Table("##exchange", 6, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Goal", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("You have", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Bought", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Gil/scrip", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableHeadersRow();

        foreach (var (item, info, own) in items)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var goal = goals.ContainsKey(item.ItemId);
            if (ImGui.Checkbox($"##goal{item.ItemId}", ref goal)) tracker.SetGoal(item.ItemId, goal ? 1 : 0);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Saving for this.");

            ImGui.TableNextColumn();
            Theme.Icon(info!.Icon, false, 24);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Theme.Fit(item.Count > 1 ? $"{info.Name} x{item.Count}" : info.Name, ImGui.GetContentRegionAvail().X));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{info.Name}\n{item.ShopName}");

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ScripTracker.Balance(item.Scrip) >= item.Cost ? Theme.Text : Theme.Text3, $"{item.Cost:N0}");

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            var (text, color) = own switch
            {
                Ownership.Unlocked => ("Unlocked", Theme.Good),
                Ownership.NotUsed => ("Bought, not used yet", Theme.Hold),
                Ownership.Missing => ("Not unlocked", Theme.Text2),
                Ownership.Owned => ("Owned", Theme.Good),
                Ownership.NotOwned => ("Not owned", Theme.Text2),
                _ => (tracker.Held(item.ItemId) is var held and > 0 ? $"{held:N0} held" : "-", Theme.Text3),
            };
            ImGui.TextColored(color, text);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Theme.Muted(purchases.TryGetValue(item.ItemId, out var bought) ? $"{bought:N0}" : "-");

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (plugin.Scrips.GilPerScrip(item) is { } gps) ImGui.TextUnformatted($"{gps:N0}");
            else Theme.Muted("-");
        }
    }

    private void DrawGoalSummary(ScripKind kind)
    {
        var needed = plugin.ScripTracker.StillNeeded(kind);
        var goals = plugin.ScripTracker.Goals.Count;
        if (goals == 0)
        {
            Theme.Muted("Tick items you're saving for, and SellWise works out how many crafts that takes. Purchases you make at the exchange are counted.");
            return;
        }
        if (needed == 0)
        {
            ImGui.TextColored(Theme.Good, $"You have enough {Scrips.Short(kind)} scrips for your goals.");
            return;
        }

        var best = plugin.Scrips.Options
            .Where(o => o.Collectable.Scrip == kind && o.Opportunity.Unlocked && o.TopTier == TopTier.Reaches)
            .OrderByDescending(o => o.ScripsPerHour)
            .FirstOrDefault();
        var plan = best == null ? "" :
            $" That's about {ScripMath.CraftsFor(needed, best.Reward)} crafts of {best.Opportunity.Item.Name}" +
            $" ({TimeEstimator.Format(TimeSpan.FromHours(needed / Math.Max(1, best.ScripsPerHour)))} of crafting).";
        Theme.Wrapped($"{needed:N0} more {Scrips.Short(kind)} scrips needed for your goals.{plan}", Theme.Text2);
    }
}
