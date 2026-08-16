using System.Reflection;
using System.Text.Json;

namespace HPShare;

internal static class ModConfig
{
    private const decimal DefaultEnemyAttackCoefficient = 1.10m;
    private const decimal MinimumEnemyAttackCoefficient = 0.50m;
    private const decimal MaximumEnemyAttackCoefficient = 3.00m;

    public static decimal EnemyAttackCoefficient { get; private set; } = DefaultEnemyAttackCoefficient;
    public static decimal LocalEnemyAttackCoefficient { get; private set; } = DefaultEnemyAttackCoefficient;
    public static bool ShareHp { get; private set; } = true;
    public static bool LocalShareHp { get; private set; } = true;
    public static bool ShareBlock { get; private set; } = true;
    public static bool LocalShareBlock { get; private set; } = true;
    public static bool ShareBuffsAndDebuffs { get; private set; }
    public static bool LocalShareBuffsAndDebuffs { get; private set; }
    public static bool ShareGold { get; private set; }
    public static bool LocalShareGold { get; private set; }
    public static bool ShareDeck { get; private set; }
    public static bool LocalShareDeck { get; private set; }
    public static bool ShareEnergy { get; private set; }
    public static bool LocalShareEnergy { get; private set; }
    public static bool ShareRelics { get; private set; }
    public static bool LocalShareRelics { get; private set; }
    public static bool SharePotions { get; private set; }
    public static bool LocalSharePotions { get; private set; }

    public static string SettingsPath
    {
        get
        {
            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            return Path.Combine(directory, "shareeverything.settings.json");
        }
    }

    public static void SetEnemyAttackCoefficient(decimal value, bool save = true)
    {
        EnemyAttackCoefficient = Math.Clamp(value, MinimumEnemyAttackCoefficient, MaximumEnemyAttackCoefficient);
        if (save)
            LocalEnemyAttackCoefficient = EnemyAttackCoefficient;
        if (save)
            Save();
    }

    public static void ApplyHostCoefficient(decimal value)
        => EnemyAttackCoefficient = Math.Clamp(value, MinimumEnemyAttackCoefficient, MaximumEnemyAttackCoefficient);

    public static void SetShareHp(bool value, bool save = true)
        => SetBoolean(value, save, v => ShareHp = v, v => LocalShareHp = v);

    public static void SetShareBlock(bool value, bool save = true)
        => SetBoolean(value, save, v => ShareBlock = v, v => LocalShareBlock = v);

    public static void SetShareBuffsAndDebuffs(bool value, bool save = true)
    {
        ShareBuffsAndDebuffs = value;
        if (save)
        {
            LocalShareBuffsAndDebuffs = value;
            Save();
        }
    }

    public static void SetShareGold(bool value, bool save = true)
    {
        ShareGold = value;
        if (save)
        {
            LocalShareGold = value;
            Save();
        }
    }

    public static void SetShareDeck(bool value, bool save = true)
        => SetBoolean(value, save, v => ShareDeck = v, v => LocalShareDeck = v);

    public static void SetShareEnergy(bool value, bool save = true)
        => SetBoolean(value, save, v => ShareEnergy = v, v => LocalShareEnergy = v);

    public static void SetShareRelics(bool value, bool save = true)
        => SetBoolean(value, save, v => ShareRelics = v, v => LocalShareRelics = v);

    public static void SetSharePotions(bool value, bool save = true)
        => SetBoolean(value, save, v => SharePotions = v, v => LocalSharePotions = v);

    public static void ApplyHostSettings(
        decimal coefficient,
        bool shareHp,
        bool shareBlock,
        bool shareBuffsAndDebuffs,
        bool shareGold,
        bool shareDeck,
        bool shareEnergy,
        bool shareRelics,
        bool sharePotions)
    {
        ApplyHostCoefficient(coefficient);
        ShareHp = shareHp;
        ShareBlock = shareBlock;
        ShareBuffsAndDebuffs = shareBuffsAndDebuffs;
        ShareGold = shareGold;
        ShareDeck = shareDeck;
        ShareEnergy = shareEnergy;
        ShareRelics = shareRelics;
        SharePotions = sharePotions;
    }

    public static void RestoreLocalSettings()
    {
        EnemyAttackCoefficient = LocalEnemyAttackCoefficient;
        ShareHp = LocalShareHp;
        ShareBlock = LocalShareBlock;
        ShareBuffsAndDebuffs = LocalShareBuffsAndDebuffs;
        ShareGold = LocalShareGold;
        ShareDeck = LocalShareDeck;
        ShareEnergy = LocalShareEnergy;
        ShareRelics = LocalShareRelics;
        SharePotions = LocalSharePotions;
    }

    public static void Load()
    {
        EnemyAttackCoefficient = DefaultEnemyAttackCoefficient;
        LocalEnemyAttackCoefficient = DefaultEnemyAttackCoefficient;
        ShareHp = true;
        LocalShareHp = true;
        ShareBlock = true;
        LocalShareBlock = true;
        ShareBuffsAndDebuffs = false;
        LocalShareBuffsAndDebuffs = false;
        ShareGold = false;
        LocalShareGold = false;
        ShareDeck = false;
        LocalShareDeck = false;
        ShareEnergy = false;
        LocalShareEnergy = false;
        ShareRelics = false;
        LocalShareRelics = false;
        SharePotions = false;
        LocalSharePotions = false;
        try
        {
            string legacyPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                "hpshare.settings.json");
            string path = File.Exists(SettingsPath) ? SettingsPath : legacyPath;
            if (!File.Exists(path))
                return;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("enemyAttackCoefficient", out JsonElement value)
                && value.TryGetDecimal(out decimal coefficient))
            {
                SetEnemyAttackCoefficient(coefficient, save: false);
                LocalEnemyAttackCoefficient = EnemyAttackCoefficient;
            }
            if (document.RootElement.TryGetProperty("shareBuffsAndDebuffs", out JsonElement sharePowers)
                && sharePowers.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                SetShareBuffsAndDebuffs(sharePowers.GetBoolean(), save: false);
                LocalShareBuffsAndDebuffs = ShareBuffsAndDebuffs;
            }
            if (document.RootElement.TryGetProperty("shareGold", out JsonElement shareGold)
                && shareGold.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                SetShareGold(shareGold.GetBoolean(), save: false);
                LocalShareGold = ShareGold;
            }
            LoadBoolean(document.RootElement, "shareHp", SetShareHp, value => LocalShareHp = value);
            LoadBoolean(document.RootElement, "shareBlock", SetShareBlock, value => LocalShareBlock = value);
            LoadBoolean(document.RootElement, "shareDeck", SetShareDeck, value => LocalShareDeck = value);
            LoadBoolean(document.RootElement, "shareEnergy", SetShareEnergy, value => LocalShareEnergy = value);
            LoadBoolean(document.RootElement, "shareRelics", SetShareRelics, value => LocalShareRelics = value);
            LoadBoolean(document.RootElement, "sharePotions", SetSharePotions, value => LocalSharePotions = value);
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[ShareEverything] Failed to read settings: {ex.Message}");
        }
    }

    public static void Save()
    {
        try
        {
            var data = new Dictionary<string, object>
            {
                ["enemyAttackCoefficient"] = EnemyAttackCoefficient,
                ["shareHp"] = ShareHp,
                ["shareBlock"] = ShareBlock,
                ["shareBuffsAndDebuffs"] = ShareBuffsAndDebuffs,
                ["shareGold"] = ShareGold,
                ["shareDeck"] = ShareDeck,
                ["shareEnergy"] = ShareEnergy,
                ["shareRelics"] = ShareRelics,
                ["sharePotions"] = SharePotions,
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[ShareEverything] Failed to save settings: {ex.Message}");
        }
    }

    private static void SetBoolean(
        bool value,
        bool save,
        Action<bool> setEffective,
        Action<bool> setLocal)
    {
        setEffective(value);
        if (!save)
            return;
        setLocal(value);
        Save();
    }

    private static void LoadBoolean(
        JsonElement root,
        string propertyName,
        Action<bool, bool> setter,
        Action<bool> setLocal)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return;
        bool value = element.GetBoolean();
        setter(value, false);
        setLocal(value);
    }
}
