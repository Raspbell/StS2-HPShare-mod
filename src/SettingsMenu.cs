using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Runs;

namespace HPShare;

internal static class SettingsMenu
{
    private const string RowName = "HPShareEnemyAttackCoefficientRow";

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(NSettingsScreen), nameof(NSettingsScreen._Ready)),
            postfix: new HarmonyMethod(typeof(SettingsMenu), nameof(SettingsReadyPostfix)));
    }

    private static void SettingsReadyPostfix(NSettingsScreen __instance)
    {
        try
        {
            if (__instance.GetNodeOrNull(RowName) is not null)
                return;

            bool runInProgress = IsRunInProgress();
            if (!runInProgress)
                ModConfig.RestoreLocalCoefficient();

            Node? divider = __instance.GetNodeOrNull("%ModdingDivider");
            Node parent = divider?.GetParent() ?? __instance;

            var row = new HBoxContainer
            {
                Name = RowName,
                CustomMinimumSize = new Vector2(620f, 46f),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = UiPatches.IsJapanese()
                    ? "敵の各攻撃ダメージに掛ける倍率です。実行中のランでは変更できません。マルチプレイではホストの値が全員に同期されます。"
                    : "Multiplier applied to each enemy attack. It cannot be changed during a run. In multiplayer, the host's value is synchronized to everyone.",
            };

            var title = new Label
            {
                Text = UiPatches.IsJapanese() ? "HPShare: 敵攻撃倍率" : "HPShare: Enemy attack multiplier",
                CustomMinimumSize = new Vector2(330f, 42f),
                VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            title.AddThemeFontSizeOverride("font_size", 18);

            var valueLabel = new Label
            {
                Text = $"×{ModConfig.EnemyAttackCoefficient:0.00}",
                CustomMinimumSize = new Vector2(72f, 42f),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            valueLabel.AddThemeFontSizeOverride("font_size", 18);

            var slider = new HSlider
            {
                MinValue = 0.50,
                MaxValue = 3.00,
                Step = 0.05,
                Value = (double)ModConfig.EnemyAttackCoefficient,
                CustomMinimumSize = new Vector2(200f, 42f),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Editable = !runInProgress,
            };
            slider.ValueChanged += value =>
            {
                decimal coefficient = Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);
                ModConfig.SetEnemyAttackCoefficient(coefficient);
                valueLabel.Text = $"×{ModConfig.EnemyAttackCoefficient:0.00}";
            };

            row.AddChild(title);
            row.AddChild(slider);
            row.AddChild(valueLabel);
            parent.AddChild(row);
            if (divider is not null)
                parent.MoveChild(row, Math.Min(parent.GetChildCount() - 1, divider.GetIndex() + 1));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[HPShare] Failed to add settings row: {ex.Message}");
        }
    }

    private static bool IsRunInProgress()
    {
        try
        {
            return RunManager.Instance?.IsInProgress == true;
        }
        catch
        {
            return false;
        }
    }
}
