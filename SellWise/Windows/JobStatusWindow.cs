using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SellWise.Core;
using SellWise.Services;

namespace SellWise.Windows;

/// <summary>
/// A small window for a running craft job. It moves through phases: repairing, gathering (every material with a
/// meter that fills as it lands in your bags), crafting the parts, crafting, and done (with the price to list at).
/// </summary>
public sealed class JobStatusWindow : Window
{
    private const float Width = 420;

    private readonly Plugin plugin;
    private IDisposable? theme;

    public JobStatusWindow(Plugin plugin)
        : base("SellWise progress###SellWiseJob", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
    }

    public override bool DrawConditions() => plugin.Crafter.Job != null;

    public override void PreDraw() => theme = Theme.Push();

    public override void PostDraw()
    {
        theme?.Dispose();
        theme = null;
    }

    public override void Draw()
    {
        using var layout = Theme.PushLayout();
        if (plugin.Crafter.Job is not { } job) return;

        var o = job.Opportunity;
        var estimate = plugin.Estimator.Estimate(o, job.Crafts, job.Made, job.Backend);
        var phase = JobEstimator.Phase(job, estimate);

        ImGui.Dummy(new Vector2(Width, 0)); // fixes the window's width

        // Header: what's being made, and which phase it's in.
        Theme.Icon(o.Item.Icon, o.SellHq, 40);
        ImGui.SameLine(0, 10);
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted(Theme.Fit($"{o.Item.Name} ×{job.Crafts}", Width - 60));
            var (label, color) = phase switch
            {
                JobPhase.Repairing => ("Repairing gear", Theme.Hold),
                JobPhase.Gathering => ("Gathering materials", Theme.Gather),
                JobPhase.CraftingParts => ("Crafting the parts", Theme.Current.Color),
                JobPhase.Crafting => ("Crafting", Theme.Current.Color),
                JobPhase.Done => ("Done", Theme.Good),
                JobPhase.Stopped => ("Stopped", Theme.Hold),
                _ => ("Failed", Theme.Bad),
            };
            ImGui.TextColored(color, label);
        }

        ImGui.Separator();
        if (job is { Backend: CraftBackend.Vulcan, ArtisanPlan: null, State: CraftJobState.Running } && o.SellHq && plugin.GbrSettings.QualityProblem is { } problem)
        {
            CraftView.DrawQualityProblem(problem);
            Theme.Muted("Changing it now takes effect from the next craft.");
            ImGui.Separator();
        }
        switch (phase)
        {
            case JobPhase.Repairing:
                Theme.Wrapped(plugin.Repair.Status, Theme.Text2);
                break;
            case JobPhase.Gathering:
                DrawGathering(estimate);
                break;
            case JobPhase.CraftingParts:
            case JobPhase.Crafting:
                DrawCrafting(job, estimate, phase);
                break;
            case JobPhase.Done:
                DrawDone(job);
                break;
            default:
                Theme.Wrapped(job.Status, Theme.Text2);
                break;
        }

        ImGui.Separator();
        if (job.State == CraftJobState.Running)
        {
            if (ImGui.Button("Stop")) plugin.Crafter.Stop();
            ImGui.SameLine();
        }
        else
        {
            if (ImGui.Button("Close")) plugin.Crafter.Dismiss();
            ImGui.SameLine();
        }
        if (ImGui.Button("Open SellWise")) plugin.ShowCraft();
    }

    private void DrawGathering(JobTimeEstimate estimate)
    {
        var left = estimate.Materials.Where(m => !m.Done).ToList();
        ImGui.TextUnformatted(estimate.Gather > TimeSpan.Zero ? $"About {TimeEstimator.Format(estimate.Gather)} of gathering left" : "Nothing left to gather");
        ImGui.SameLine();
        Theme.Muted($"then ~{TimeEstimator.Format(estimate.Craft)} crafting");
        if (plugin.Crafter.Job?.Status is { Length: > 0 } status) Theme.Wrapped(status, Theme.Text3);
        ImGui.Spacing();

        // One row per material: icon, name, meter, count, and when it'll be done.
        foreach (var m in estimate.Materials)
        {
            var rowStart = ImGui.GetCursorScreenPos();
            Theme.Icon(m.Info?.Icon ?? 0, false, 24);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(m.Done ? Theme.Text3 : Theme.Text, Theme.Fit(m.Line.Name, 130));

            var x = rowStart.X;
            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(x + 168, ImGui.GetCursorScreenPos().Y));
            var fraction = m.Need > 0 ? m.Have / (float)m.Need : 1;
            Theme.Bar(fraction, m.Done ? Theme.Good : Theme.Gather, new Vector2(70, 6));

            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(x + 246, ImGui.GetCursorScreenPos().Y));
            ImGui.TextColored(m.Done ? Theme.Good : Theme.Text2, Theme.Fit($"{Theme.Compact(m.Have)}/{Theme.Compact(m.Need)}", 76));

            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(x + 326, ImGui.GetCursorScreenPos().Y));
            var when = JobEstimator.When(m);
            ImGui.TextColored(m.Done ? Theme.Good : Theme.Text3, Theme.Fit(when, Width - 326));
            if (ImGui.IsItemHovered() && m.Gather is { Timed: true })
                ImGui.SetTooltip("Only gatherable while its node is up (a timed node). SellWise counts one visit per spawn.");
        }

        if (left.Count == 0) Theme.Muted("Everything's gathered.");
        if (estimate.Materials.Any(m => !m.Done && m.Line.Source is MaterialSource.Buy or MaterialSource.Vendor && m.Have + m.OnRetainers < m.Need))
            Theme.Wrapped("Items marked buy or NPC aren't gathered; GatherBuddy buys what it can from vendors, the rest you'll need to pick up.", Theme.Text3);

        // Cordials: optional, off by default.
        ImGui.Spacing();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is { MaxGp: > 0 })
        {
            ImGui.TextUnformatted($"GP {player.CurrentGp}/{player.MaxGp}");
            ImGui.SameLine();
        }
        var use = plugin.Config.UseCordials;
        if (ImGui.Checkbox("Use cordials when ready", ref use))
        {
            plugin.Config.UseCordials = use;
            plugin.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Drinks the biggest cordial that won't waste GP whenever the cordial cooldown is ready,\n" +
                             "between nodes (never mid-gather). Uses Hi-Cordials, Cordials and Watered Cordials.");
        Theme.Muted($"Cordials: {plugin.Cordials.Stock()}" + (plugin.Cordials.Used > 0 ? $" · {plugin.Cordials.Used} used" : ""));
    }

    private void DrawCrafting(CraftJob job, JobTimeEstimate estimate, JobPhase phase)
    {
        if (phase == JobPhase.CraftingParts)
        {
            ImGui.TextUnformatted("Crafting the intermediate parts first");
            foreach (var p in estimate.Parts)
            {
                Theme.Icon(p.Info?.Icon ?? 0, false, 24);
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Theme.Fit(p.Line.Name, 150));
                ImGui.SameLine(190);
                Theme.Bar(p.Need > 0 ? p.Have / (float)p.Need : 1, p.Done ? Theme.Good : Theme.Current.Color, new Vector2(90, 6));
                ImGui.SameLine();
                ImGui.TextColored(p.Done ? Theme.Good : Theme.Text2, $"{Theme.Compact(p.Have)}/{Theme.Compact(p.Need)}");
            }
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.FrameBg, Theme.Raise2))
                ImGui.ProgressBar(job.Wanted > 0 ? Math.Clamp(job.Made / (float)job.Wanted, 0, 1) : 0, new Vector2(Width, 22), $"{job.Made} / {job.Wanted} made");
        }

        ImGui.TextUnformatted($"About {TimeEstimator.Format(estimate.Craft)} left");
        ImGui.SameLine();
        Theme.Muted($"({estimate.ActionsPerCraft} steps per craft)");
        if (job.Status.Length > 0) Theme.Wrapped(job.Status, Theme.Text3);
    }

    private void DrawDone(CraftJob job)
    {
        var o = job.Opportunity;
        if (job.GatherOnly)
        {
            ImGui.TextColored(Theme.Good, job.Status);
            if (ImGui.Button("Open the recipe")) plugin.ShowRecipe();
            return;
        }
        if (plugin.Scrips.Db?.Collectables.GetValueOrDefault(o.Item.Id) is { } collectable)
        {
            ImGui.TextColored(Theme.Good, $"Made {job.Made} of {job.Wanted}.");
            var turnIn = plugin.TurnIn;
            if (turnIn.Status.Length > 0) Theme.Wrapped(turnIn.Status, turnIn.Failed ? Theme.Bad : Theme.Text2);
            if (!turnIn.IsBusy && turnIn.InBags().Count > 0 && Theme.PrimaryButton("Turn in now")) turnIn.Start();
            Theme.Muted($"Worth about {collectable.HighReward * job.Made:N0} {Scrips.Short(collectable.Scrip)} scrips at the top tier.");
            return;
        }
        var stacks = plugin.Advice.Plan.Stacks;
        var rec = stacks.FirstOrDefault(r => r.Item.Id == o.Item.Id && r.Hq == o.SellHq) ?? stacks.FirstOrDefault(r => r.Item.Id == o.Item.Id);
        ImGui.TextColored(Theme.Good, $"Made {job.Made} of {job.Wanted}.");
        if (rec?.SuggestedPrice is { } price)
        {
            ImGui.TextUnformatted("List at");
            ImGui.SameLine();
            using (Theme.BigFont()) ImGui.TextUnformatted($"{price:N0}");
            ImGui.SameLine();
            if (Theme.PrimaryButton("Copy price")) ImGui.SetClipboardText(price.ToString());
            Theme.Muted($"{rec.NetTotal:N0} gil after tax for all {rec.Quantity:N0}");
        }
        else
        {
            Theme.Muted("Fetching a fresh price...");
        }
    }
}
