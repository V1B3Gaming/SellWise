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

/// <summary>
/// Crafter and gatherer job quests: which you can do now, what each wants handed in, and one click to make it all
/// (GatherBuddy gathers, Artisan crafts).
/// </summary>
public sealed class JobQuestView
{
    private const float ListWidth = 430;
    private const float RowHeight = 58;
    private static readonly string[] Shows = ["Ready and in your journal", "Everything not done", "Everything"];

    private readonly Plugin plugin;
    private readonly CraftView craftView;
    private int job = -1;
    private int show;
    private uint selectedQuest;
    private string? startError;
    private readonly Dictionary<(uint Quest, uint Item), int> quantities = [];

    public JobQuestView(Plugin plugin, CraftView craftView)
    {
        this.plugin = plugin;
        this.craftView = craftView;
    }

    private string World => plugin.Advice.PricingWorld;

    public void Draw()
    {
        var service = plugin.JobQuests;
        service.EnsureLoaded();
        var avail = ImGui.GetContentRegionAvail();
        if (service.Db is not { } db)
        {
            using var loading = Pane("##qloading", avail);
            Theme.Muted("Reading job quests...");
            return;
        }

        var rows = Rows(db).ToList();
        var selected = rows.FirstOrDefault(q => q.QuestId == selectedQuest) ?? rows.FirstOrDefault();
        if (selected != null) selectedQuest = selected.QuestId;

        using (var left = Pane("##questList", new Vector2(ListWidth, avail.Y)))
        {
            if (left) DrawList(rows, selected);
        }
        ImGui.SameLine(0, 0);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(0, avail.Y), Theme.U32(Theme.Line));

        using var right = Pane("##questDetail", new Vector2(avail.X - ListWidth, avail.Y));
        if (!right) return;
        if (plugin.Crafter.Job is { } running)
            DrawRunningBanner(running);
        if (selected == null)
        {
            Theme.Wrapped(show == 0 ? "No job quests need items right now. Level up, or show everything not done." : "Nothing matches.", Theme.Text3);
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

    private IEnumerable<JobQuest> Rows(JobQuestDb db)
    {
        var service = plugin.JobQuests;
        return db.Quests
            .Where(q => job < 0 || q.JobIndex == job)
            .Where(q => show switch
            {
                0 => service.Status(q).Status is QuestStatus.Ready or QuestStatus.Accepted,
                1 => service.Status(q).Status != QuestStatus.Done,
                _ => true,
            })
            .OrderBy(q => service.Status(q).Status switch { QuestStatus.Accepted => 0, QuestStatus.Ready => 1, QuestStatus.Locked => 2, _ => 3 })
            .ThenBy(q => q.Level)
            .ThenBy(q => q.JobIndex);
    }

    private void DrawList(List<JobQuest> rows, JobQuest? selected)
    {
        using (Theme.HeadingFont()) ImGui.TextUnformatted("Job quests");
        Theme.Wrapped("Crafter and gatherer quests that need items handed in, and whether you can take them yet.", Theme.Text3);

        ImGui.SetNextItemWidth(110);
        using (var combo = ImRaii.Combo("##qjob", job < 0 ? "All jobs" : JobQuestDb.JobNames[job]))
        {
            if (combo)
            {
                if (ImGui.Selectable("All jobs", job < 0)) job = -1;
                for (var i = 0; i < JobQuestDb.JobNames.Length; i++)
                {
                    var level = plugin.JobQuests.Levels[i];
                    if (ImGui.Selectable($"{JobQuestDb.JobNames[i]}  {(level > 0 ? $"lv {level}" : "-")}", job == i)) job = i;
                }
            }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.Combo("##qshow", ref show, Shows, Shows.Length);

        var ready = rows.Where(q => plugin.JobQuests.Status(q).Status is QuestStatus.Ready or QuestStatus.Accepted).ToList();
        if (ready.Count > 0)
        {
            var jobs = ready.SelectMany(q => CraftJobs(q)).ToList();
            using (ImRaii.Disabled(jobs.Count == 0 || plugin.Crafter.IsRunning))
            {
                if (Theme.PrimaryButton($"Make everything for {ready.Count} quest{(ready.Count == 1 ? "" : "s")}", new Vector2(-1, 0)))
                    Start(jobs);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(jobs.Count == 0 ? "Nothing craftable is missing for the quests shown."
                    : $"Crafts {jobs.Count} item{(jobs.Count == 1 ? "" : "s")} one after another (GatherBuddy gathers, Artisan crafts).\nGathered-only items still need a trip with GatherBuddy's Gather buttons.");
        }

        ImGui.Spacing();
        using var scroll = ImRaii.Child("##questRows", Vector2.Zero);
        if (!scroll) return;
        foreach (var q in rows)
        {
            if (Row(q, q == selected))
            {
                selectedQuest = q.QuestId;
                startError = null;
            }
        }
    }

    private bool Row(JobQuest q, bool selected)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##q{q.QuestId}", new Vector2(width, RowHeight));
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

        var (status, reason) = plugin.JobQuests.Status(q);
        var first = q.Items.FirstOrDefault(i => !i.FromQuest) ?? q.Items[0];
        var icon = plugin.Catalog.Get(first.ItemId)?.Icon ?? 0;
        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
        var dim = status is QuestStatus.Locked or QuestStatus.Done;
        dl.AddImage(tex.Handle, pos + new Vector2(8, 9), pos + new Vector2(48, 49), Vector2.Zero, Vector2.One, dim ? Theme.U32(new Vector4(1, 1, 1, 0.5f)) : uint.MaxValue);

        var right = pos.X + width - 10;
        var (label, color) = StatusLabel(status);
        var ls = ImGui.CalcTextSize(label);
        dl.AddText(new Vector2(right - ls.X, pos.Y + 10), Theme.U32(color), label);
        var plans = plugin.JobQuests.Plan(q, World);
        var itemsText = status == QuestStatus.Done ? "" : $"{plans.Count(p => p.Missing == 0 || p.Item.FromQuest)}/{plans.Count} items";
        var its = ImGui.CalcTextSize(itemsText);
        dl.AddText(new Vector2(right - its.X, pos.Y + 31), Theme.U32(Theme.Text3), itemsText);

        var textRight = right - Math.Max(ls.X, its.X) - 12;
        dl.PushClipRect(new Vector2(pos.X + 58, pos.Y), new Vector2(textRight, pos.Y + RowHeight), true);
        dl.AddText(new Vector2(pos.X + 58, pos.Y + 10), Theme.U32(dim ? Theme.Text2 : Theme.Text), q.Name);
        dl.AddText(new Vector2(pos.X + 58, pos.Y + 31), Theme.U32(reason != null && status == QuestStatus.Locked ? Theme.Bad : Theme.Text2),
            reason ?? $"{JobQuestDb.JobNames[q.JobIndex]} {q.Level}" + (q.Place.Length > 0 ? $" · {q.Place}" : ""));
        dl.PopClipRect();
        return clicked;
    }

    private static (string, Vector4) StatusLabel(QuestStatus status) => status switch
    {
        QuestStatus.Accepted => ("In journal", Theme.Current.Color),
        QuestStatus.Ready => ("Ready", Theme.Good),
        QuestStatus.Done => ("Done", Theme.Text3),
        _ => ("Locked", Theme.Hold),
    };

    private void DrawDetail(JobQuest q)
    {
        var (status, reason) = plugin.JobQuests.Status(q);
        using (Theme.HeadingFont()) ImGui.TextUnformatted(Theme.Fit(q.Name, ImGui.GetContentRegionAvail().X));
        var (label, color) = StatusLabel(status);
        Theme.Tag(label, color, small: true);
        ImGui.SameLine();
        Theme.Secondary($"{JobQuestDb.JobNames[q.JobIndex]} {q.Level}" + (q.Place.Length > 0 ? $" · {q.Place}" : ""));
        if (reason != null) Theme.Wrapped(reason, status == QuestStatus.Locked ? Theme.Hold : Theme.Text3);
        if (q.PreviousQuests.Count > 0 && plugin.JobQuests.Db is { } db)
            Theme.Muted(Theme.Fit("After: " + string.Join(q.NeedsAllPrevious ? " and " : " or ", q.PreviousQuests.Select(db.QuestName)), ImGui.GetContentRegionAvail().X));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        Theme.Secondary("Hand in");
        var plans = plugin.JobQuests.Plan(q, World);
        var i = 0;
        foreach (var p in plans)
        {
            i++;
            DrawItem(q, p, i);
        }

        ImGui.Spacing();
        if (plans.Any(p => p.Item.Count == null && !p.Item.FromQuest))
            Theme.Wrapped("\"?\" means the quest text doesn't say how many; set the number yourself. Counts are read from the quest's journal text.", Theme.Text3);

        var jobs = CraftJobs(q);
        if (status != QuestStatus.Done && jobs.Count > 0)
        {
            ImGui.Spacing();
            using (ImRaii.Disabled(plugin.Crafter.IsRunning))
            {
                if (Theme.PrimaryButton(jobs.Count == 1 ? "Make it" : $"Make all {jobs.Count}")) Start(jobs);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("GatherBuddy gathers the materials and Artisan crafts, going for max quality." + (jobs.Count > 1 ? "\nThe items are made one after another." : ""));
        }
        if (startError != null) ImGui.TextColored(Theme.Bad, startError);
    }

    private void DrawItem(JobQuest q, QuestItemPlan p, int index)
    {
        var info = plugin.Catalog.Get(p.Item.ItemId);
        Theme.Icon(info?.Icon ?? 0, p.Item.Hq, 32);
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted(Theme.Fit(p.Item.Name, ImGui.GetContentRegionAvail().X - 250));
            if (p.Item.Hq) { ImGui.SameLine(); Theme.Tag("HQ", Theme.Current.Color, small: true); }

            if (p.Item.FromQuest)
            {
                Theme.Muted("Given to you during the quest.");
                return;
            }

            var need = Need(q, p);
            var have = p.Have;
            ImGui.TextColored(have >= need ? Theme.Good : Theme.Text2, $"{have:N0} / ");
            ImGui.SameLine(0, 0);
            ImGui.SetNextItemWidth(60);
            var edit = need;
            if (ImGui.InputInt($"##need{index}", ref edit, 0, 0))
                quantities[(q.QuestId, p.Item.ItemId)] = Math.Clamp(edit, 1, 999);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(p.Item.Count == null ? "The quest text doesn't say how many; 1 is a guess." : "Read from the quest text. Change it if the quest asks for a different number.");
            if (p.Item.Count == null) { ImGui.SameLine(); ImGui.TextColored(Theme.Hold, "?"); }
            ImGui.SameLine();
            Theme.Muted(p.Item.Hq ? "HQ in your bags" : "in your bags");
        }

        if (p.Item.FromQuest) return;
        var missing = Math.Max(0, Need(q, p) - p.Have);
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 220);
        using (ImRaii.Group())
        {
            if (p.Craft is { } craft)
            {
                var crafts = CraftsFor(craft, missing);
                using (ImRaii.Disabled(missing == 0 || plugin.Crafter.IsRunning || craft.LockedReason != null))
                {
                    if (ImGui.Button($"Make {crafts}##make{index}")) Start([Job(craft, crafts)]);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip($"{craft.Recipe.Job} {craft.Recipe.Level} recipe · materials about {craft.MaterialValue * crafts:N0} gil at market value." +
                                     (craft.LockedReason is { } locked ? $"\nYou can't craft it yet: {locked}." : ""));
            }
            if (p.Gatherable || p.Fish)
            {
                if (p.Craft != null) ImGui.SameLine();
                using (ImRaii.Disabled(missing == 0 || !CraftCoordinator.VulcanAvailable))
                {
                    if (ImGui.Button($"Gather##gather{index}"))
                        Plugin.CommandManager.ProcessCommand(p.Fish ? $"/gatherfish {p.Item.Name}" : $"/gather {p.Item.Name}");
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("GatherBuddy teleports you to the nearest spot and marks it." + (p.Item.Hq ? "\nThe quest wants high quality ones." : ""));
            }
            if (p.Craft == null && !p.Gatherable && !p.Fish)
            {
                if (ImGui.Button($"Prices##buy{index}")) Util.OpenLink($"https://universalis.app/market/{p.Item.ItemId}");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Not craftable or gatherable: buy it on the market board or from a vendor.");
            }
        }
        ImGui.Spacing();
    }

    private int Need(JobQuest q, QuestItemPlan p) => quantities.TryGetValue((q.QuestId, p.Item.ItemId), out var n) ? n : p.Need;

    private static int CraftsFor(CraftOpportunity craft, int missing) => missing <= 0 ? 0 : (int)Math.Ceiling(missing / (double)Math.Max(1, craft.Recipe.Yield));

    /// <summary>Craft jobs for everything craftable this quest is still missing.</summary>
    private List<(CraftOpportunity Opp, int Crafts, CraftBackend Backend, CraftOpportunity? Plan)> CraftJobs(JobQuest q)
    {
        var jobs = new List<(CraftOpportunity, int, CraftBackend, CraftOpportunity?)>();
        if (plugin.JobQuests.Status(q).Status == QuestStatus.Done) return jobs;
        foreach (var p in plugin.JobQuests.Plan(q, World))
        {
            if (p.Item.FromQuest || p.Craft is not { } craft || craft.LockedReason != null) continue;
            var crafts = CraftsFor(craft, Math.Max(0, Need(q, p) - p.Have));
            if (crafts > 0) jobs.Add(Job(craft, crafts));
        }
        return jobs;
    }

    private (CraftOpportunity, int, CraftBackend, CraftOpportunity?) Job(CraftOpportunity craft, int crafts)
    {
        var o = craftView.Priced(craft);
        if (CraftCoordinator.VulcanAvailable)
            return (o, crafts, CraftBackend.Vulcan, Config.FinishWithArtisan && CraftCoordinator.ArtisanAvailable ? plugin.Scanner.VulcanPlan(o) : null);
        return (o, crafts, CraftBackend.Artisan, null);
    }

    private Configuration Config => plugin.Config;

    private void Start(List<(CraftOpportunity Opp, int Crafts, CraftBackend Backend, CraftOpportunity? Plan)> jobs)
    {
        startError = plugin.Crafter.StartAll(jobs);
        if (startError == null) plugin.MinimizeToJob();
    }

    private void DrawRunningBanner(CraftJob job)
    {
        var text = job.State == CraftJobState.Running
            ? $"Making {job.Opportunity.Item.Name}: {job.Made}/{job.Wanted}" + (plugin.Crafter.Queued > 0 ? $" · {plugin.Crafter.Queued} more queued" : "")
            : job.Status;
        ImGui.TextColored(Theme.Current.Color, Theme.Fit(text, ImGui.GetContentRegionAvail().X - 90));
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 80);
        if (job.State == CraftJobState.Running)
        {
            if (ImGui.Button("Stop##qstop", new Vector2(80, 0))) plugin.Crafter.Stop();
        }
        else if (ImGui.Button("Close##qclose", new Vector2(80, 0)))
        {
            plugin.Crafter.Dismiss();
        }
        ImGui.Separator();
        ImGui.Spacing();
    }
}
