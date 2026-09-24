using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace SellWise.Services;

/// <summary>A mender NPC and where it stands. <paramref name="MenuIndex"/> is its "Repair" entry in the talk menu.</summary>
public sealed record Mender(uint NpcId, string Name, uint TerritoryId, Vector3 Position, int MenuIndex);

/// <summary>
/// Mender locations read from each city's layout file. The game only spawns NPCs near you, so after a teleport
/// SellWise needs to know where to walk before the mender exists in the object table.
/// </summary>
public sealed class MenderDb
{
    /// <summary>ENpcData function row that means "this NPC repairs gear".</summary>
    private const uint RepairFunction = 720915;

    public IReadOnlyDictionary<uint, IReadOnlyList<Mender>> ByTerritory { get; }

    /// <summary>Every NPC in the game that repairs, so one standing nearby in any zone can be used.</summary>
    public IReadOnlyDictionary<uint, int> MenuIndexByNpc { get; }

    private MenderDb(Dictionary<uint, IReadOnlyList<Mender>> byTerritory, Dictionary<uint, int> menuIndex)
    {
        ByTerritory = byTerritory;
        MenuIndexByNpc = menuIndex;
    }

    public static MenderDb Load(IEnumerable<uint> territories)
    {
        var data = Plugin.DataManager;
        var menuIndex = new Dictionary<uint, int>();
        foreach (var npc in data.GetExcelSheet<ENpcBase>())
        {
            for (var i = 0; i < npc.ENpcData.Count; i++)
            {
                if (npc.ENpcData[i].RowId != RepairFunction) continue;
                menuIndex[npc.RowId] = i;
                break;
            }
        }

        var residents = data.GetExcelSheet<ENpcResident>();
        var territorySheet = data.GetExcelSheet<TerritoryType>();
        var byTerritory = new Dictionary<uint, IReadOnlyList<Mender>>();
        foreach (var territoryId in territories.Distinct())
        {
            var bg = territorySheet.GetRowOrDefault(territoryId)?.Bg.ExtractText() ?? "";
            var levelAt = bg.IndexOf("/level/");
            if (levelAt < 0) continue;

            var lgb = data.GetFile<LgbFile>($"bg/{bg[..(levelAt + 1)]}level/planevent.lgb");
            if (lgb == null) continue;

            var found = new List<Mender>();
            foreach (var layer in lgb.Layers)
            {
                foreach (var instance in layer.InstanceObjects)
                {
                    if (instance.AssetType != LayerEntryType.EventNPC) continue;
                    var npcId = ((LayerCommon.ENPCInstanceObject)instance.Object).ParentData.ParentData.BaseId;
                    if (!menuIndex.TryGetValue(npcId, out var index)) continue;

                    var t = instance.Transform.Translation;
                    var name = residents.GetRowOrDefault(npcId)?.Singular.ExtractText() ?? "Mender";
                    found.Add(new Mender(npcId, name, territoryId, new Vector3(t.X, t.Y, t.Z), index));
                }
            }

            if (found.Count > 0) byTerritory[territoryId] = found;
        }

        Plugin.Log.Information($"Found menders in {byTerritory.Count} cities ({menuIndex.Count} repair NPCs overall)");
        return new MenderDb(byTerritory, menuIndex);
    }
}
