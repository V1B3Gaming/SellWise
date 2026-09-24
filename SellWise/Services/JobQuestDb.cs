using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>An item a job quest wants handed in.</summary>
/// <param name="Count">How many, as read from the quest text; null when the text doesn't say.</param>
/// <param name="FromQuest">Handed to you during the quest (special materials), so there's nothing to make.</param>
public sealed record QuestItem(uint ItemId, string Name, int? Count, bool Hq, bool FromQuest);

/// <param name="JobIndex">0-7 crafters (same as recipes), 8 MIN, 9 BTN, 10 FSH.</param>
public sealed record JobQuest(uint QuestId, string Name, int JobIndex, int Level, string Place, IReadOnlyList<uint> PreviousQuests, bool NeedsAllPrevious, IReadOnlyList<QuestItem> Items);

public enum QuestStatus
{
    /// <summary>In your journal now.</summary>
    Accepted,
    /// <summary>You can pick it up.</summary>
    Ready,
    Locked,
    Done,
}

/// <summary>Crafter and gatherer job quests that need items handed in, read from the game data.</summary>
public sealed class JobQuestDb
{
    public static readonly string[] JobNames = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL", "MIN", "BTN", "FSH"];

    /// <summary>ClassJob row for a job index (crafters 8-15, gatherers 16-18).</summary>
    public static uint ClassJobRow(int jobIndex) => (uint)(8 + jobIndex);

    public IReadOnlyList<JobQuest> Quests { get; }
    private readonly Dictionary<uint, string> names;

    private JobQuestDb(List<JobQuest> quests, Dictionary<uint, string> names)
    {
        Quests = quests;
        this.names = names;
    }

    public string QuestName(uint questId) => names.TryGetValue(questId, out var n) ? n : $"quest #{questId}";

    public static JobQuestDb Load()
    {
        var data = Plugin.DataManager;
        var items = data.GetExcelSheet<Item>();
        var hasRecipe = data.GetExcelSheet<Recipe>().Where(r => r.ItemResult.RowId != 0).Select(r => r.ItemResult.RowId).ToHashSet();
        var quests = new List<JobQuest>();
        var names = new Dictionary<uint, string>();

        foreach (var q in data.GetExcelSheet<Quest>())
        {
            if (q.ClassJobCategory0.ValueNullable is not { } c) continue;
            bool[] jobs = [c.CRP, c.BSM, c.ARM, c.GSM, c.LTW, c.WVR, c.ALC, c.CUL, c.MIN, c.BTN, c.FSH];
            if (jobs.Count(j => j) != 1 || c.GLA || c.PGL || c.MRD || c.ACN) continue;
            var job = Array.IndexOf(jobs, true);

            var itemIds = q.QuestParams
                .Where(p => p.ScriptInstruction.ExtractText().StartsWith("RITEM", StringComparison.Ordinal))
                .Select(p => p.ScriptArg)
                .Where(id => items.GetRowOrDefault(id) is { } it && !it.Name.IsEmpty)
                .Distinct()
                .ToList();
            if (itemIds.Count == 0) continue;

            var texts = QuestTexts(q);
            var questItems = itemIds.Select(id =>
            {
                var it = items.GetRow(id);
                var name = it.Name.ExtractText();
                var fromQuest = it.IsUntradable && !hasRecipe.Contains(id);
                var (count, hq) = QuestText.Requirement(texts, name, it.Singular.ExtractText(), it.Plural.ExtractText(), itemIds.Count == 1);
                return new QuestItem(id, name, count, hq, fromQuest);
            }).ToList();

            var previous = q.PreviousQuest.Select(p => p.RowId).Where(id => id != 0).ToList();
            var questName = q.Name.ExtractText();
            names[q.RowId] = questName;
            quests.Add(new JobQuest(q.RowId, questName, job, q.ClassJobLevel[0], q.PlaceName.ValueNullable?.Name.ExtractText() ?? "",
                previous, q.PreviousQuestJoin != 2, questItems));
        }

        // Previous quests are often outside this set (the class unlock quest), so remember every quest's name.
        foreach (var q in quests.SelectMany(q => q.PreviousQuests).Distinct().Where(id => !names.ContainsKey(id)))
            names[q] = data.GetExcelSheet<Quest>().GetRowOrDefault(q)?.Name.ExtractText() ?? $"quest #{q}";

        Plugin.Log.Information($"Job quests: {quests.Count} crafter/gatherer quests need items");
        return new JobQuestDb(quests.OrderBy(q => q.JobIndex).ThenBy(q => q.Level).ToList(), names);
    }

    /// <summary>The quest's journal and objective lines, which say how many of each item it wants.</summary>
    private static List<string> QuestTexts(Quest q)
    {
        var id = q.Id.ExtractText();
        var underscore = id.IndexOf('_');
        if (underscore < 0 || id.Length < underscore + 4) return [];
        try
        {
            var sheet = Plugin.DataManager.Excel.GetSheet<RawRow>(name: $"quest/{id[(underscore + 1)..(underscore + 4)]}/{id}");
            return sheet
                .Select(r => (Key: r.ReadStringColumn(0).ExtractText(), Text: r.ReadStringColumn(1).ExtractText()))
                .Where(t => (t.Key.Contains("_SEQ_") || t.Key.Contains("_TODO_")) && t.Text.Length > 1)
                .Select(t => t.Text)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Where you stand with a quest, and why it's locked. Call on the framework thread.</summary>
    public unsafe (QuestStatus Status, string? Reason) Status(JobQuest q, int[] levels)
    {
        if (QuestManager.IsQuestComplete(q.QuestId)) return (QuestStatus.Done, null);
        var qm = QuestManager.Instance();
        if (qm != null && qm->IsQuestAccepted(q.QuestId)) return (QuestStatus.Accepted, null);

        var level = levels[q.JobIndex];
        if (level == 0) return (QuestStatus.Locked, $"Unlock {JobNames[q.JobIndex]} first");

        var done = q.PreviousQuests.Where(p => QuestManager.IsQuestComplete(p)).ToList();
        var previousOk = q.PreviousQuests.Count == 0 || (q.NeedsAllPrevious ? done.Count == q.PreviousQuests.Count : done.Count > 0);
        if (!previousOk)
        {
            var missing = q.PreviousQuests.Except(done).First();
            return (QuestStatus.Locked, $"Finish \"{QuestName(missing)}\" first");
        }
        if (level < q.Level) return (QuestStatus.Locked, $"Reach {JobNames[q.JobIndex]} {q.Level} (you're {level})");
        return (QuestStatus.Ready, null);
    }

    /// <summary>Levels for all 11 crafter and gatherer jobs. Call on the framework thread.</summary>
    public static int[] ReadLevels()
    {
        var levels = new int[JobNames.Length];
        var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        for (var i = 0; i < levels.Length; i++)
            if (sheet.GetRowOrDefault(ClassJobRow(i)) is { } job)
                levels[i] = Plugin.PlayerState.GetClassJobLevel(job);
        return levels;
    }
}
