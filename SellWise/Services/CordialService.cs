using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace SellWise.Services;

/// <summary>
/// Drinks cordials between gathering nodes while a SellWise craft job is gathering, the same way GatherBuddy
/// Reborn does when its own cordial option is on: no cast time, usable while mounted, only when off cooldown and
/// only if the GP won't overflow. Cordials share one game cooldown, so this can't double up with GatherBuddy.
/// </summary>
public sealed unsafe class CordialService
{
    /// <summary>Hi-Cordial, Cordial, Watered Cordial (the same set GatherBuddy Reborn recognises).</summary>
    private static readonly uint[] CordialIds = [12669, 6141, 16911];

    /// <summary>The game's recast group shared by all cordials.</summary>
    private const int CordialRecastGroup = 68;

    private static readonly ConditionFlag[] Busy =
    [
        ConditionFlag.Gathering, ConditionFlag.Casting, ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
        ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent, ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.Occupied39, ConditionFlag.InCombat,
    ];

    private readonly Configuration config;
    private readonly List<(uint ItemId, bool Hq, int Gp)> cordials = [];
    private DateTime nextCheck;

    public CordialService(Configuration config)
    {
        this.config = config;
        var items = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var id in CordialIds)
        {
            if (items.GetRowOrDefault(id) is not { } item || item.ItemAction.ValueNullable is not { } action) continue;
            if (action.Data.Count > 0) cordials.Add((id, false, action.Data[0]));
            if (item.CanBeHq && action.DataHQ.Count > 0) cordials.Add((id, true, action.DataHQ[0]));
        }
        cordials.Sort((a, b) => b.Gp.CompareTo(a.Gp));
    }

    public int Used { get; private set; }

    /// <summary>How many cordials are in your bags, e.g. "3 Hi-Cordial, 5 Cordial (HQ)".</summary>
    public string Stock()
    {
        var names = Plugin.DataManager.GetExcelSheet<Item>();
        var parts = new List<string>();
        foreach (var (id, hq, _) in cordials)
        {
            var count = InventoryManager.Instance()->GetInventoryItemCount(id, hq, false, false, 0);
            if (count > 0) parts.Add($"{count} {names.GetRowOrDefault(id)?.Name.ExtractText()}{(hq ? " (HQ)" : "")}");
        }
        return parts.Count == 0 ? "none in your bags" : string.Join(", ", parts);
    }

    /// <summary>Called from Framework.Update; <paramref name="gathering"/> is true while a job is gathering.</summary>
    public void Update(bool gathering)
    {
        if (!config.UseCordials || !gathering) return;
        var now = DateTime.UtcNow;
        if (now < nextCheck) return;
        nextCheck = now.AddSeconds(2);

        var player = Plugin.ObjectTable.LocalPlayer;
        var job = Plugin.PlayerState.ClassJob.RowId;
        if (player == null || job is < 16 or > 18 || player.MaxGp == 0 || player.CurrentGp >= player.MaxGp) return;
        if (Busy.Any(f => Plugin.Condition[f])) return;

        var am = ActionManager.Instance();
        var recast = am->GetRecastGroupDetail(CordialRecastGroup);
        if (recast != null && recast->Total - recast->Elapsed > 0) return;

        // The biggest cordial that won't waste GP.
        foreach (var (id, hq, gp) in cordials)
        {
            if (player.CurrentGp + gp > player.MaxGp) continue;
            if (InventoryManager.Instance()->GetInventoryItemCount(id, hq, false, false, 0) <= 0) continue;

            am->UseAction(ActionType.Item, hq ? id + 1_000_000 : id, extraParam: 65535);
            Used++;
            nextCheck = now.AddSeconds(5);
            return;
        }
    }
}
