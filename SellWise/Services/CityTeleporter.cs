using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace SellWise.Services;

public sealed record City(uint AetheryteId, uint TerritoryId, string Name);

/// <summary>Teleports to a random major city (with a market board and summoning bells) whose aetheryte you've unlocked.</summary>
public sealed class CityTeleporter
{
    /// <summary>Aetheryte and territory IDs are from the game's Aetheryte sheet.</summary>
    public static readonly City[] Cities =
    [
        new(8, 129, "Limsa Lominsa"),
        new(2, 132, "Gridania"),
        new(9, 130, "Ul'dah"),
        new(70, 418, "Ishgard"),
        new(111, 628, "Kugane"),
        new(133, 819, "The Crystarium"),
        new(182, 962, "Old Sharlayan"),
        new(183, 963, "Radz-at-Han"),
        new(216, 1185, "Tuliyollal"),
        new(217, 1186, "Solution Nine"),
    ];

    private static readonly ConditionFlag[] BlockingConditions =
    [
        ConditionFlag.InCombat, ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95,
        ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.Casting, ConditionFlag.Occupied,
        ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedSummoningBell,
        ConditionFlag.Crafting, ConditionFlag.Gathering, ConditionFlag.Jumping, ConditionFlag.WatchingCutscene,
    ];

    public string Status { get; private set; } = "";

    public static bool IsInCity => Cities.Any(c => c.TerritoryId == Plugin.ClientState.TerritoryType);

    /// <summary>Cities that are enabled in settings and whose aetheryte this character has attuned to.</summary>
    public unsafe List<City> Available(IReadOnlySet<uint> disabled)
    {
        var ui = UIState.Instance();
        return Cities.Where(c => !disabled.Contains(c.AetheryteId) && ui != null && ui->IsAetheryteUnlocked(c.AetheryteId)).ToList();
    }

    /// <summary>Must be called on the framework thread.</summary>
    public unsafe void TeleportToRandomCity(IReadOnlySet<uint> disabled)
    {
        if (BlockingConditions.FirstOrDefault(f => Plugin.Condition[f]) is var blocking && blocking != default)
        {
            Status = $"Can't teleport right now ({blocking}).";
            return;
        }

        // Don't "teleport" to the city you're already standing in.
        var options = Available(disabled).Where(c => c.TerritoryId != Plugin.ClientState.TerritoryType).ToList();
        if (options.Count == 0)
        {
            Status = IsInCity ? "You're already in the only city available." : "No enabled city aetherytes are unlocked.";
            return;
        }

        var city = options[Random.Shared.Next(options.Count)];
        var telepo = Telepo.Instance();
        if (telepo == null || !telepo->Teleport(city.AetheryteId, 0))
        {
            Status = $"The game refused the teleport to {city.Name}.";
            return;
        }

        Status = $"Teleporting to {city.Name}...";
    }
}
