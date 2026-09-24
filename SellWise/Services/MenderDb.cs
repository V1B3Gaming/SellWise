using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace SellWise.Services;

/// <summary>A mender NPC and where it stands. <paramref name="MenuIndex"/> is its "Repair" entry in the talk menu.</summary>
public sealed record Mender(uint NpcId, string Name, uint TerritoryId, Vector3 Position, int MenuIndex);

/// <summary>Mender locations read from each city's layout file (see <see cref="NpcLocations"/>).</summary>
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
        var byTerritory = NpcLocations.Find(territories, menuIndex.Keys.ToHashSet())
            .GroupBy(s => s.TerritoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Mender>)g
                .Select(s => new Mender(s.NpcId, residents.GetRowOrDefault(s.NpcId)?.Singular.ExtractText() ?? "Mender", s.TerritoryId, s.Position, menuIndex[s.NpcId]))
                .ToList());

        Plugin.Log.Information($"Found menders in {byTerritory.Count} cities ({menuIndex.Count} repair NPCs overall)");
        return new MenderDb(byTerritory, menuIndex);
    }
}
