using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Runs;

namespace HPShare;

internal static class SettingsMenu
{
    private const string ContainerName = "HPShareSettingsContainer";

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(NSettingsScreen), nameof(NSettingsScreen._Ready)),
            postfix: new HarmonyMethod(typeof(SettingsMenu), nameof(SettingsReadyPostfix)));
    }

    private static void SettingsReadyPostfix(NSettingsScreen __instance)
    {
        try
        {
            if (__instance.GetNodeOrNull(ContainerName) is not null)
                return;

            bool runInProgress = IsRunInProgress();
            if (!runInProgress)
                ModConfig.RestoreLocalSettings();

            Node? divider = __instance.GetNodeOrNull("%ModdingDivider");
            Node parent = divider?.GetParent() ?? __instance;
            var container = new VBoxContainer
            {
                Name = ContainerName,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };

            AddEnemyMultiplierRow(container, runInProgress);
            AddShareToggle(
                container, runInProgress,
                "HPを共有", "Share HP",
                "全プレイヤーのHPと最大HPを合算し、1つの共有HPとして扱います。オスティの共有もこの設定に従います。",
                "Combines every player's current and maximum HP into one pool. Shared Osty HP also follows this setting.",
                ModConfig.ShareHp, ModConfig.SetShareHp);
            AddShareToggle(
                container, runInProgress,
                "ブロックを共有", "Share Block",
                "全プレイヤーのブロックを合算して共有します。各プレイヤーの貢献量は内部で保持されます。",
                "Combines every player's Block while retaining each player's contribution internally.",
                ModConfig.ShareBlock, ModConfig.SetShareBlock);
            AddToggleRow(
                container,
                UiPatches.IsJapanese() ? "Share Everything: バフ・デバフを共有" : "Share Everything: Share buffs and debuffs",
                UiPatches.IsJapanese()
                    ? "プレイヤーが受けたバフ・デバフを全プレイヤーに適用します。実行中のランでは変更できません。マルチプレイではホストの設定が全員に同期されます。"
                    : "Applies player buffs and debuffs to every player. It cannot be changed during a run. In multiplayer, the host's setting is synchronized to everyone.",
                ModConfig.ShareBuffsAndDebuffs,
                runInProgress,
                ModConfig.SetShareBuffsAndDebuffs);
            AddToggleRow(
                container,
                UiPatches.IsJapanese() ? "Share Everything: ゴールドを共有" : "Share Everything: Share gold",
                UiPatches.IsJapanese()
                    ? "全プレイヤーが同じゴールド残高を使用します。誰かの獲得・支払いは全員に反映されます。実行中のランでは変更できません。マルチプレイではホストの設定が全員に同期されます。"
                    : "Makes every player use the same gold balance. Any gain or payment affects everyone. It cannot be changed during a run. In multiplayer, the host's setting is synchronized to everyone.",
                ModConfig.ShareGold,
                runInProgress,
                ModConfig.SetShareGold);
            AddShareToggle(
                container, runInProgress,
                "デッキを共有", "Share deck",
                "全プレイヤーが同じデッキを使用します。開始時はストライク3枚、防御3枚、参加キャラクターごとの固有スターターカードで構成されます。カードの追加・削除・変化は全員に同期されます。",
                "Makes every player use the same deck. The starting deck contains three Strikes, three Defends, and each participant's character-specific starter cards. Card additions, removals, and transformations are synchronized.",
                ModConfig.ShareDeck, ModConfig.SetShareDeck);
            AddShareToggle(
                container, runInProgress,
                "エナジーを共有", "Share energy",
                "現在エナジーと最大エナジーを共有します。最大値は全プレイヤーの最大エナジーの合計です。",
                "Shares current energy. Maximum energy is the sum of every player's maximum energy.",
                ModConfig.ShareEnergy, ModConfig.SetShareEnergy);
            AddShareToggle(
                container, runInProgress,
                "レリックを共有", "Share relics",
                "全参加キャラの初期レリックを統合し、入手・削除を全員へ反映します。",
                "Merges starting relics and mirrors relic acquisition and removal to all players.",
                ModConfig.ShareRelics, ModConfig.SetShareRelics);
            AddShareToggle(
                container, runInProgress,
                "ポーションを共有", "Share potions",
                "全プレイヤーのポーション枠を合算し、入手・使用・破棄を全員へ反映します。",
                "Combines every player's potion slots and mirrors procurement, use, and discard to all players.",
                ModConfig.SharePotions, ModConfig.SetSharePotions);

            parent.AddChild(container);
            if (divider is not null)
                parent.MoveChild(container, Math.Min(parent.GetChildCount() - 1, divider.GetIndex() + 1));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ShareEverything] Failed to add settings rows: {ex.Message}");
        }
    }

    private static void AddShareToggle(
        VBoxContainer container,
        bool runInProgress,
        string japaneseTitle,
        string englishTitle,
        string japaneseTooltip,
        string englishTooltip,
        bool enabled,
        Action<bool, bool> setter)
    {
        bool japanese = UiPatches.IsJapanese();
        string synchronizedNote = japanese
            ? " 実行中のランでは変更できません。マルチプレイではホストの設定が全員に同期されます。"
            : " It cannot be changed during a run. In multiplayer, the host's setting is synchronized to everyone.";
        AddToggleRow(
            container,
            $"Share Everything: {(japanese ? japaneseTitle : englishTitle)}",
            (japanese ? japaneseTooltip : englishTooltip) + synchronizedNote,
            enabled,
            runInProgress,
            setter);
    }

    private static void AddEnemyMultiplierRow(VBoxContainer container, bool runInProgress)
    {
        var row = CreateRow(UiPatches.IsJapanese()
            ? "敵の各攻撃ダメージに掛ける倍率です。実行中のランでは変更できません。マルチプレイではホストの値が全員に同期されます。"
            : "Multiplier applied to each enemy attack. It cannot be changed during a run. In multiplayer, the host's value is synchronized to everyone.");
        var title = CreateTitle(UiPatches.IsJapanese() ? "Share Everything: 敵攻撃倍率" : "Share Everything: Enemy attack multiplier");
        var valueLabel = new Label
        {
            Text = $"x{ModConfig.EnemyAttackCoefficient:0.00}",
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
            valueLabel.Text = $"x{ModConfig.EnemyAttackCoefficient:0.00}";
        };

        row.AddChild(title);
        row.AddChild(slider);
        row.AddChild(valueLabel);
        container.AddChild(row);
    }

    private static void AddToggleRow(
        VBoxContainer container,
        string titleText,
        string tooltip,
        bool enabled,
        bool runInProgress,
        Action<bool, bool> setter)
    {
        var row = CreateRow(tooltip);
        var title = CreateTitle(titleText);
        var toggle = new CheckButton
        {
            ButtonPressed = enabled,
            Disabled = runInProgress,
            CustomMinimumSize = new Vector2(200f, 42f),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            Text = UiPatches.IsJapanese() ? (enabled ? "オン" : "オフ") : (enabled ? "On" : "Off"),
        };
        toggle.AddThemeFontSizeOverride("font_size", 18);
        toggle.Toggled += value =>
        {
            setter(value, true);
            toggle.Text = UiPatches.IsJapanese() ? (value ? "オン" : "オフ") : (value ? "On" : "Off");
        };
        row.AddChild(title);
        row.AddChild(toggle);
        container.AddChild(row);
    }

    private static HBoxContainer CreateRow(string tooltip)
        => new()
        {
            CustomMinimumSize = new Vector2(620f, 46f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = tooltip,
        };

    private static Label CreateTitle(string text)
    {
        var title = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(390f, 42f),
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        title.AddThemeFontSizeOverride("font_size", 18);
        return title;
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
