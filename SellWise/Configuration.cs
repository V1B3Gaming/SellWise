using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using SellWise.Core;

namespace SellWise;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public AdvisorSettings Advisor { get; set; } = new();

    /// <summary>World to price against. Empty = your home world.</summary>
    public string WorldOverride { get; set; } = "";

    /// <summary>Also fetch the cheapest listing across your data center.</summary>
    public bool FetchDataCenter { get; set; } = true;

    public int CacheMinutes { get; set; } = 15;
    public bool AutoRefresh { get; set; } = true;

    public bool IncludeCrystals { get; set; } = true;
    public bool IncludeSaddlebags { get; set; } = true;
    public bool IncludeArmory { get; set; } = false;
    public bool IncludeRetainers { get; set; } = true;

    /// <summary>Hide stack recommendations worth less than this (net gil, or vendor gil).</summary>
    public int MinValueGil { get; set; } = 500;

    public bool ShowDtr { get; set; } = true;
    public bool TargetBellOnArrival { get; set; } = true;

    /// <summary>Open the small crafting status window when a craft job starts.</summary>
    public bool ShowJobWindow { get; set; } = true;

    /// <summary>Drink cordials between gathering nodes while a craft job is gathering.</summary>
    public bool UseCordials { get; set; } = false;

    /// <summary>
    /// GatherBuddy gathers, then SellWise stops it and has Artisan do the crafting, so quality is always pushed to the
    /// max (GatherBuddy's own solver can skip quality). Only when Artisan is installed.
    /// </summary>
    public bool FinishWithArtisan { get; set; } = true;

    /// <summary>After a scrip craft job, take the collectables to an appraiser and turn them in.</summary>
    public bool TurnInAfterScripJob { get; set; } = true;

    /// <summary>For job quest items: go to the quest giver before crafting, so they can be handed straight in.</summary>
    public bool CraftAtQuestGiver { get; set; } = true;

    /// <summary>Scrip exchange items you're saving for, per character (content ID → item ID → how many).</summary>
    public Dictionary<ulong, Dictionary<uint, int>> ScripGoals { get; set; } = [];

    /// <summary>Scrip exchange purchases SellWise has seen, per character (content ID → item ID → how many bought).</summary>
    public Dictionary<ulong, Dictionary<uint, int>> ScripPurchases { get; set; } = [];

    public HashSet<uint> IgnoredItems { get; set; } = [];

    public CraftSettings Craft { get; set; } = new();

    /// <summary>Crafting jobs (by CraftType index) hidden in the profit finder.</summary>
    public HashSet<int> HiddenCraftJobs { get; set; } = [];

    /// <summary>Repair gear before and during craft jobs when any piece drops below <see cref="RepairThreshold"/>%.</summary>
    public bool AutoRepair { get; set; } = true;
    public int RepairThreshold { get; set; } = 30;
    public bool AllowSelfRepair { get; set; } = true;
    public bool AllowNpcRepair { get; set; } = true;

    /// <summary>Crafter stats per character (content id) and job (0 = CRP … 7 = CUL), saved when seen without buffs.</summary>
    public Dictionary<ulong, Dictionary<int, Services.SavedCrafterStats>> CrafterStats { get; set; } = [];

    /// <summary>UI accent: violet, teal or silver.</summary>
    public string Accent { get; set; } = "violet";

    /// <summary>City aetherytes excluded from "Teleport to a city". Stored as exclusions so new cities default to on.</summary>
    public HashSet<uint> DisabledTeleportCities { get; set; } = [];

    /// <summary>Incremented on save so services know to recompute.</summary>
    [NonSerialized] public int Revision;

    public void Save()
    {
        Revision++;
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
