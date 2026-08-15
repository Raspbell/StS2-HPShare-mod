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

    public static string SettingsPath
    {
        get
        {
            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            return Path.Combine(directory, "hpshare.settings.json");
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

    public static void RestoreLocalCoefficient()
        => EnemyAttackCoefficient = LocalEnemyAttackCoefficient;

    public static void Load()
    {
        EnemyAttackCoefficient = DefaultEnemyAttackCoefficient;
        LocalEnemyAttackCoefficient = DefaultEnemyAttackCoefficient;
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (document.RootElement.TryGetProperty("enemyAttackCoefficient", out JsonElement value)
                && value.TryGetDecimal(out decimal coefficient))
            {
                SetEnemyAttackCoefficient(coefficient, save: false);
                LocalEnemyAttackCoefficient = EnemyAttackCoefficient;
            }
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[HPShare] Failed to read settings: {ex.Message}");
        }
    }

    public static void Save()
    {
        try
        {
            var data = new Dictionary<string, decimal>
            {
                ["enemyAttackCoefficient"] = EnemyAttackCoefficient,
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[HPShare] Failed to save settings: {ex.Message}");
        }
    }
}
