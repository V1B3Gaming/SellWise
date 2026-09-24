using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace SellWise.Services;

/// <summary>An event NPC placed in a zone's layout.</summary>
public sealed record NpcSpot(uint NpcId, uint TerritoryId, Vector3 Position);

/// <summary>
/// Reads where NPCs stand from each zone's layout file. The game only spawns NPCs near you, so after a teleport
/// SellWise needs to know where to walk before the NPC exists in the object table.
/// </summary>
public static class NpcLocations
{
    public static List<NpcSpot> Find(IEnumerable<uint> territories, IReadOnlySet<uint> npcIds)
    {
        var data = Plugin.DataManager;
        var territorySheet = data.GetExcelSheet<TerritoryType>();
        var found = new List<NpcSpot>();
        foreach (var territoryId in territories.Distinct())
        {
            var bg = territorySheet.GetRowOrDefault(territoryId)?.Bg.ExtractText() ?? "";
            var levelAt = bg.IndexOf("/level/");
            if (levelAt < 0) continue;

            var lgb = data.GetFile<LgbFile>($"bg/{bg[..(levelAt + 1)]}level/planevent.lgb");
            if (lgb == null) continue;

            foreach (var layer in lgb.Layers)
            {
                foreach (var instance in layer.InstanceObjects)
                {
                    if (instance.AssetType != LayerEntryType.EventNPC) continue;
                    var npcId = ((LayerCommon.ENPCInstanceObject)instance.Object).ParentData.ParentData.BaseId;
                    if (!npcIds.Contains(npcId)) continue;

                    var t = instance.Transform.Translation;
                    found.Add(new NpcSpot(npcId, territoryId, new Vector3(t.X, t.Y, t.Z)));
                }
            }
        }
        return found;
    }
}
