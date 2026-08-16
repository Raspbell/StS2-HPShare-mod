using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace HPShare;

internal static class UiPatches
{
    private const string ContributionNodeName = "HPShareContribution";
    private static readonly FieldInfo HealthBarCreature = AccessTools.Field(typeof(NHealthBar), "_creature");
    private static readonly FieldInfo HealthBarHpLabel = AccessTools.Field(typeof(NHealthBar), "_hpLabel");
    private static readonly FieldInfo BlockContainer = AccessTools.Field(typeof(NHealthBar), "_blockContainer");
    private static readonly FieldInfo RosterHealthBar = AccessTools.Field(typeof(NMultiplayerPlayerState), "_healthBar");
    private static readonly FieldInfo IntentField = AccessTools.Field(typeof(NIntent), "_intent");
    private static readonly FieldInfo IntentTargetsField = AccessTools.Field(typeof(NIntent), "_targets");
    private static readonly FieldInfo IntentOwnerField = AccessTools.Field(typeof(NIntent), "_owner");
    private static readonly FieldInfo IntentValueLabelField = AccessTools.Field(typeof(NIntent), "_valueLabel");
    private static readonly MethodInfo CurrentHpGetter = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp));
    private static readonly MethodInfo MaxHpGetter = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp));
    private static readonly MethodInfo BlockGetter = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.Block));
    private static readonly MethodInfo DisplayCurrentHpMethod = AccessTools.Method(typeof(UiPatches), nameof(GetDisplayedCurrentHp));
    private static readonly MethodInfo DisplayMaxHpMethod = AccessTools.Method(typeof(UiPatches), nameof(GetDisplayedMaxHp));
    private static readonly MethodInfo DisplayBlockMethod = AccessTools.Method(typeof(UiPatches), nameof(GetDisplayedBlock));
    private static readonly ConditionalWeakTable<NIntent, IntentTooltipState> IntentTooltipStates = new();

    public static void Apply(Harmony harmony)
    {
        MethodInfo vitalTranspiler = AccessTools.Method(typeof(UiPatches), nameof(VitalDisplayTranspiler));
        MethodInfo[] healthBarVitalReaders =
        [
            AccessTools.Method(typeof(NHealthBar), nameof(NHealthBar.SetCreature)),
            AccessTools.Method(typeof(NHealthBar), "UpdateWidthRelativeToReferenceValue"),
            AccessTools.Method(typeof(NHealthBar), "SetHpBarContainerSizeWithOffsetsImmediately"),
            AccessTools.Method(typeof(NHealthBar), "RefreshMiddleground"),
            AccessTools.Method(typeof(NHealthBar), "RefreshForeground"),
            AccessTools.Method(typeof(NHealthBar), "RefreshBlockUi"),
            AccessTools.Method(typeof(NHealthBar), "RefreshText"),
            AccessTools.Method(typeof(NHealthBar), "IsPoisonLethal"),
            AccessTools.Method(typeof(NHealthBar), "IsDoomLethal"),
            AccessTools.Method(typeof(NHealthBar), "GetFgWidth", [typeof(int), typeof(float)]),
        ];
        foreach (MethodInfo method in healthBarVitalReaders)
            harmony.Patch(method, transpiler: new HarmonyMethod(vitalTranspiler));

        harmony.Patch(AccessTools.Method(typeof(NTopBarHp), "UpdateHealth"),
            transpiler: new HarmonyMethod(vitalTranspiler));

        foreach (string methodName in new[] { nameof(NHealthBar.SetCreature), nameof(NHealthBar.RefreshValues) })
        {
            MethodInfo method = AccessTools.Method(typeof(NHealthBar), methodName);
            harmony.Patch(method,
                postfix: new HarmonyMethod(typeof(UiPatches), nameof(HealthBarPostfix)));
        }

        harmony.Patch(AccessTools.Method(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState._Ready)),
            postfix: new HarmonyMethod(typeof(UiPatches), nameof(HideRosterHealthBar)));
        harmony.Patch(AccessTools.Method(typeof(NMultiplayerPlayerState), "RefreshValues"),
            postfix: new HarmonyMethod(typeof(UiPatches), nameof(HideRosterHealthBar)));
        harmony.Patch(AccessTools.Method(typeof(NIntent), "UpdateVisuals"),
            postfix: new HarmonyMethod(typeof(UiPatches), nameof(IntentVisualsPostfix)));
    }

    private static IEnumerable<CodeInstruction> VitalDisplayTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(CurrentHpGetter))
            {
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = DisplayCurrentHpMethod;
            }
            else if (instruction.Calls(MaxHpGetter))
            {
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = DisplayMaxHpMethod;
            }
            else if (instruction.Calls(BlockGetter))
            {
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = DisplayBlockMethod;
            }
            yield return instruction;
        }
    }

    private static int GetDisplayedCurrentHp(Creature creature)
    {
        MarkPartyReadyForDisplay(creature);
        return SharedVitals.IsSharedHpPlayer(creature)
            || SharedVitals.IsSharedOsty(creature)
                ? SharedVitals.SharedCurrentHp(creature)
                : SharedVitals.RawCurrentHp(creature);
    }

    private static int GetDisplayedMaxHp(Creature creature)
    {
        MarkPartyReadyForDisplay(creature);
        return SharedVitals.IsSharedHpPlayer(creature)
            || SharedVitals.IsSharedOsty(creature)
                ? SharedVitals.SharedMaxHp(creature)
                : SharedVitals.RawMaxHp(creature);
    }

    private static int GetDisplayedBlock(Creature creature)
    {
        MarkPartyReadyForDisplay(creature);
        return SharedVitals.IsSharedBlockPlayer(creature)
            ? SharedVitals.SharedBlock(creature)
            : SharedVitals.RawBlock(creature);
    }

    private static void MarkPartyReadyForDisplay(Creature creature)
    {
        if (SharedVitals.IsPartyPlayer(creature))
            SharedVitals.MarkPartyReadyFromUi(creature);
    }

    private static void HealthBarPostfix(NHealthBar __instance)
    {
        if (HealthBarCreature.GetValue(__instance) is Creature displayed)
            UpdateContributionLabel(__instance, displayed);
    }

    private static void UpdateContributionLabel(NHealthBar healthBar, Creature creature)
    {
        bool isPlayer = SharedVitals.IsSharedBlockPlayer(creature);
        bool isOsty = SharedVitals.IsSharedOsty(creature);
        // The player contribution label is reparented under the Block container.
        // Search recursively so RefreshValues reuses it instead of creating a new
        // overlapping label on every refresh.
        Label? label = healthBar.FindChild(
            ContributionNodeName,
            recursive: true,
            owned: false) as Label;
        if (!isPlayer && !isOsty)
        {
            if (label is not null)
                label.Visible = false;
            return;
        }

        if (label is null)
        {
            label = new Label
            {
                Name = ContributionNodeName,
                MouseFilter = Control.MouseFilterEnum.Stop,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                ZIndex = 20,
            };
            label.AddThemeFontSizeOverride("font_size", 13);
            label.AddThemeColorOverride("font_color", new Color("8dd9ff"));
            label.AddThemeColorOverride("font_outline_color", Colors.Black);
            label.AddThemeConstantOverride("outline_size", 3);
            healthBar.AddChild(label);
        }

        bool japanese = IsJapanese();
        if (isOsty)
        {
            if (!ReferenceEquals(label.GetParent(), healthBar))
                label.Reparent(healthBar, keepGlobalTransform: false);
            int hpFontSize = GetHpFontSize(healthBar);
            label.AddThemeFontSizeOverride("font_size", hpFontSize);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Text = $"◆ {SharedVitals.RawCurrentHp(creature)}/{SharedVitals.RawMaxHp(creature)}";
            label.TooltipText = japanese
                ? "自分が共有オスティへ加えたHP / 最大HPの貢献量です。オスティのHPを参照するカードは、この値だけを参照します。"
                : "Your HP / Max HP contribution to shared Osty. Cards that refer to Osty's HP use only this contribution.";
            Control? hpBar = healthBar.HpBarContainer;
            float width = Math.Max(120f, hpBar?.Size.X ?? 0f);
            Vector2 origin = hpBar?.Position ?? new Vector2(-width, 0f);
            label.Position = origin + new Vector2(0f, -hpFontSize - 10f);
            label.Size = new Vector2(width, hpFontSize + 8f);
            label.Visible = true;
            return;
        }

        int blockContributionFontSize = GetHpFontSize(healthBar);
        label.AddThemeFontSizeOverride("font_size", blockContributionFontSize);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        int ownBlock = SharedVitals.RawBlock(creature);
        int sharedBlock = SharedVitals.SharedBlock(creature);
        label.Text = $"◆ {ownBlock}";
        label.TooltipText = japanese
            ? "このプレイヤーが共有ブロックへ加えた貢献量です。「あなたのブロック」を参照する効果は、この値だけを参照します。"
            : "This player's contribution to shared Block. Effects that refer to your Block use only this value.";
        Control? block = BlockContainer.GetValue(healthBar) as Control;
        float labelHeight = blockContributionFontSize + 8f;
        label.Size = new Vector2(76f, labelHeight);
        if (block is not null)
        {
            if (!ReferenceEquals(label.GetParent(), block))
                label.Reparent(block, keepGlobalTransform: false);
            label.Position = new Vector2(-label.Size.X - 2f, (block.Size.Y - labelHeight) * 0.5f);
        }
        else
        {
            if (!ReferenceEquals(label.GetParent(), healthBar))
                label.Reparent(healthBar, keepGlobalTransform: false);
            label.Position = new Vector2(-label.Size.X - 2f, 0f);
        }
        label.Visible = sharedBlock > 0;
    }

    private static int GetHpFontSize(NHealthBar healthBar)
    {
        if (HealthBarHpLabel.GetValue(healthBar) is Control hpText)
            return Math.Max(1, hpText.GetThemeFontSize("font_size"));
        return 18;
    }

    private static void HideRosterHealthBar(NMultiplayerPlayerState __instance)
    {
        if (RosterHealthBar.GetValue(__instance) is NHealthBar healthBar)
            healthBar.Visible = !ModConfig.ShareHp && !ModConfig.ShareBlock;
    }

    private static void IntentVisualsPostfix(NIntent __instance)
    {
        IntentTooltipState tooltipState = IntentTooltipStates.GetValue(
            __instance,
            static _ => new IntentTooltipState());
        string baseTooltip = RemovePreviousIntentTooltip(__instance.TooltipText, tooltipState.LastAddedText);
        tooltipState.LastAddedText = string.Empty;
        __instance.TooltipText = baseTooltip;
        if (IntentField.GetValue(__instance) is not AttackIntent attack
            || IntentOwnerField.GetValue(__instance) is not Creature owner
            || IntentTargetsField.GetValue(__instance) is not IEnumerable<Creature> targetEnumerable)
            return;

        List<Creature> targets = targetEnumerable.ToList();
        if (!targets.Any(SharedVitals.IsSharedHpPlayer))
            return;

        var breakdown = new List<(Creature Target, long PerHit)>();
        try
        {
            IRunState runState = IRunState.GetFrom(targets);
            foreach (Creature target in targets.Where(SharedVitals.IsSharedHpPlayer))
            {
                if (breakdown.Any(item => ReferenceEquals(item.Target, target)))
                    continue;

                decimal modified = Hook.ModifyDamage(
                    runState,
                    target.CombatState,
                    target,
                    owner,
                    attack.DamageCalc?.Invoke() ?? 0m,
                    // AttackIntent.GetSingleDamage uses this exact ValueProp value in v0.111.0.
                    (ValueProp)8,
                    null,
                    null,
                    ModifyDamageHookType.All,
                    CardPreviewMode.None,
                    out IEnumerable<AbstractModel> _);
                long targetPerHit = decimal.ToInt64(decimal.Truncate(
                    Math.Clamp(modified, 0m, 999_999_999m)));
                breakdown.Add((target, targetPerHit));
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ShareEverything] Could not calculate party-wide intent preview: {ex.Message}");
            return;
        }

        long combinedPerHit = SaturatingSum(breakdown.Select(item => item.PerHit));
        int repeats = Math.Max(1, attack.Repeats);
        long total = SaturatingMultiply(combinedPerHit, repeats);

        if (IntentValueLabelField.GetValue(__instance) is MegaRichTextLabel label)
            label.Text = FormatIntentLabel(repeats, combinedPerHit);

        string details = string.Join("\n", breakdown.Select(item =>
            $"{item.Target.Name}: {FormatIntentLabel(repeats, item.PerHit)}"));
        string hpShareTooltip = IsJapanese()
            ? $"全プレイヤーの合計予定ダメージ: {total}\n{details}"
            : $"Total projected damage to all players: {total}\n{details}";
        __instance.TooltipText = string.IsNullOrWhiteSpace(baseTooltip)
            ? hpShareTooltip
            : $"{baseTooltip}\n{hpShareTooltip}";
        tooltipState.LastAddedText = hpShareTooltip;
    }

    private static string RemovePreviousIntentTooltip(string? tooltip, string previousAddedText)
    {
        if (string.IsNullOrEmpty(tooltip))
            return string.Empty;
        if (string.IsNullOrEmpty(previousAddedText))
            return tooltip;
        if (string.Equals(tooltip, previousAddedText, StringComparison.Ordinal))
            return string.Empty;
        string suffix = $"\n{previousAddedText}";
        return tooltip.EndsWith(suffix, StringComparison.Ordinal)
            ? tooltip[..^suffix.Length]
            : tooltip;
    }

    internal static bool IsJapanese()
        => LocManager.Instance?.Language is "jpn" or "ja";

    internal static string FormatIntentLabel(int repeats, long perHit)
    {
        int hitCount = Math.Max(1, repeats);
        long total = SaturatingMultiply(perHit, hitCount);
        return hitCount > 1 ? $"{perHit}x{hitCount} ({total})" : total.ToString();
    }

    private static long SaturatingMultiply(long value, int multiplier)
    {
        if (value <= 0 || multiplier <= 0)
            return 0;
        return value > long.MaxValue / multiplier ? long.MaxValue : value * multiplier;
    }

    private static long SaturatingSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (long value in values)
        {
            if (value <= 0)
                continue;
            total = value > long.MaxValue - total ? long.MaxValue : total + value;
        }
        return total;
    }

    private sealed class IntentTooltipState
    {
        public string LastAddedText { get; set; } = string.Empty;
    }
}
