using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace HPShare;

/// <summary>Entry point for the Share Everything gameplay mod.</summary>
[ModInitializer(nameof(Initialize))]
public static class HPShareMod
{
    /// <summary>The Harmony patch owner identifier.</summary>
    public const string HarmonyId = "kyosh.shareeverything";
    internal static Harmony Harmony { get; private set; } = null!;

    /// <summary>Loads settings and installs all runtime patches.</summary>
    public static void Initialize()
    {
        ModConfig.Load();
        Harmony = new Harmony(HarmonyId);

        SharedVitalsPatches.Apply(Harmony);
        NetworkConfigSync.Apply(Harmony);
        SharedOptionsPatches.Apply(Harmony);
        SharedEnergyPatches.Apply(Harmony);
        SharedInventoryPatches.Apply(Harmony);
        DamagePatches.Apply(Harmony);
        HealingPatches.Apply(Harmony);
        DeathPreventionPatches.Apply(Harmony);
        OstyPatches.Apply(Harmony);
        UiPatches.Apply(Harmony);
        DescriptionPatches.Apply(Harmony);
        SettingsMenu.Apply(Harmony);

        Console.WriteLine(
            $"[ShareEverything] Loaded. Enemy attack coefficient={ModConfig.EnemyAttackCoefficient:0.00}, " +
            $"HP={ModConfig.ShareHp}, Block={ModConfig.ShareBlock}, powers={ModConfig.ShareBuffsAndDebuffs}, " +
            $"gold={ModConfig.ShareGold}, deck={ModConfig.ShareDeck}, energy={ModConfig.ShareEnergy}, " +
            $"relics={ModConfig.ShareRelics}, potions={ModConfig.SharePotions}");
    }
}
