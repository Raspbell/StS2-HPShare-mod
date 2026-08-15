using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
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
        return SharedVitals.IsSharedPlayer(creature)
            || SharedVitals.IsOsty(creature) && SharedVitals.TryGetParty(creature, out _)
                ? SharedVitals.SharedCurrentHp(creature)
                : SharedVitals.RawCurrentHp(creature);
    }

    private static int GetDisplayedMaxHp(Creature creature)
    {
        MarkPartyReadyForDisplay(creature);
        return SharedVitals.IsSharedPlayer(creature)
            || SharedVitals.IsOsty(creature) && SharedVitals.TryGetParty(creature, out _)
                ? SharedVitals.SharedMaxHp(creature)
                : SharedVitals.RawMaxHp(creature);
    }

    private static int GetDisplayedBlock(Creature creature)
    {
        MarkPartyReadyForDisplay(creature);
        return SharedVitals.IsSharedPlayer(creature)
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
        bool isPlayer = SharedVitals.IsSharedPlayer(creature);
        bool isOsty = SharedVitals.IsOsty(creature) && SharedVitals.TryGetParty(creature, out _);
        Label? label = healthBar.GetNodeOrNull<Label>(ContributionNodeName);
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
            int hpFontSize = 18;
            if (HealthBarHpLabel.GetValue(healthBar) is Control hpText)
                hpFontSize = Math.Max(1, hpText.GetThemeFontSize("font_size"));
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

        label.AddThemeFontSizeOverride("font_size", 13);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        int ownBlock = SharedVitals.RawBlock(creature);
        int sharedBlock = SharedVitals.SharedBlock(creature);
        label.Text = $"◆ {ownBlock}";
        label.TooltipText = japanese
            ? "このプレイヤーが共有ブロックへ加えた貢献量です。「あなたのブロック」を参照する効果は、この値だけを参照します。"
            : "This player's contribution to shared Block. Effects that refer to your Block use only this value.";
        Control? block = BlockContainer.GetValue(healthBar) as Control;
        label.Position = (block?.Position ?? Vector2.Zero) + new Vector2(-68f, -2f);
        label.Size = new Vector2(62f, 24f);
        label.Visible = sharedBlock > 0;
    }

    private static void HideRosterHealthBar(NMultiplayerPlayerState __instance)
    {
        if (RosterHealthBar.GetValue(__instance) is NHealthBar healthBar)
            healthBar.Visible = false;
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

        List<Creature> targets = targetEnumerable.Where(SharedVitals.IsSharedPlayer).ToList();
        if (targets.Count == 0)
            return;

        var breakdown = new List<(Creature Target, int Damage)>();
        foreach (Creature target in targets)
        {
            int damage = attack.GetTotalDamage(new[] { target }, owner);
            breakdown.Add((target, damage));
        }
        long total = breakdown.Sum(item => (long)item.Damage);

        if (IntentValueLabelField.GetValue(__instance) is MegaRichTextLabel label)
            label.Text = FormatIntentLabel(label.Text, attack.Repeats, total);

        string details = string.Join("\n", breakdown.Select(item => $"{item.Target.Name}: {item.Damage}"));
        string hpShareTooltip = IsJapanese()
            ? $"共有HPへの合計予定ダメージ: {total}\n{details}"
            : $"Total projected damage to shared HP: {total}\n{details}";
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

    internal static string FormatIntentLabel(string vanillaLabel, int repeats, long sharedTotal)
        => repeats > 1 ? $"{vanillaLabel} ({sharedTotal})" : sharedTotal.ToString();

    private sealed class IntentTooltipState
    {
        public string LastAddedText { get; set; } = string.Empty;
    }
}
