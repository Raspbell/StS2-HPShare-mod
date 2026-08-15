using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.addons.mega_text;

namespace HPShare;

internal static class DescriptionPatches
{
    private static readonly ConditionalWeakTable<NCard, FontAdjustmentState> FontStates = new();
    private static readonly System.Reflection.FieldInfo DescriptionLabelField =
        AccessTools.Field(typeof(NCard), "_descriptionLabel");

    private static readonly HashSet<string> BlockReferenceCards =
    [
        "BodySlam", "DemonicShield", "Entrench", "Expose", "Mimic", "Prolong",
    ];

    private static readonly HashSet<string> BlockRetentionCards =
    [
        "Barricade", "Blur", "Prolong",
    ];

    private static readonly HashSet<string> OstyCards =
    [
        "Afterlife", "Bodyguard", "BoneShards", "Cleanse", "Dirge", "Fetch", "Flatten",
        "HighFive", "LegionOfBone", "NecroMastery", "Poke", "Protector", "PullAggro",
        "Rattle", "Reanimate", "RightHandHand", "Sacrifice", "SicEm", "Snap", "Spur",
        "Squeeze", "SweepingGaze", "Unleash",
    ];

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(CardModel), nameof(CardModel.GetDescriptionForPile),
            [typeof(PileType), typeof(Creature)]),
            postfix: new HarmonyMethod(typeof(DescriptionPatches), nameof(DescriptionPostfix)));
        harmony.Patch(AccessTools.Method(typeof(CardModel), nameof(CardModel.GetDescriptionForUpgradePreview)),
            postfix: new HarmonyMethod(typeof(DescriptionPatches), nameof(DescriptionPostfix)));
        harmony.Patch(AccessTools.Method(typeof(NCard), nameof(NCard.UpdateVisuals),
            [typeof(PileType), typeof(CardPreviewMode)]),
            postfix: new HarmonyMethod(typeof(DescriptionPatches), nameof(CardVisualsPostfix)));
    }

    private static void DescriptionPostfix(CardModel __instance, ref string __result)
    {
        string typeName = __instance.GetType().Name;
        var notes = new List<string>();
        bool japanese = UiPatches.IsJapanese();

        if (BlockReferenceCards.Contains(typeName))
        {
            notes.Add(japanese
                ? "「あなたのブロック」を参照する効果は、自分が共有ブロックへ加えた貢献量だけを参照します。"
                : "Effects that refer to your Block use only your contribution to shared Block.");
        }
        if (BlockRetentionCards.Contains(typeName))
        {
            notes.Add(japanese
                ? "ブロックを維持する効果は、自分の共有ブロックへの貢献分だけに適用されます。"
                : "Block retention applies only to your contribution to shared Block.");
        }
        if (OstyCards.Contains(typeName))
        {
            notes.Add(japanese
                ? "オスティのHP・最大HPを参照／変更する効果は、自分が共有オスティへ加えた貢献分だけを使用します。"
                : "Effects that refer to or change Osty's HP/Max HP use only your contribution to shared Osty.");
        }

        if (notes.Count > 0)
            __result = string.Concat(__result, "\n", string.Join("\n", notes.Distinct()));
    }

    private static void CardVisualsPostfix(NCard __instance)
    {
        if (DescriptionLabelField.GetValue(__instance) is not MegaRichTextLabel label)
            return;

        FontAdjustmentState state = FontStates.GetValue(__instance, static _ => new FontAdjustmentState());
        int noteCount = GetAdditionalNoteCount(__instance.Model);
        if (noteCount == 0)
        {
            if (!state.IsAdjusted)
                return;
            label.MinFontSize = state.OriginalMinFontSize;
            label.MaxFontSize = state.OriginalMaxFontSize;
            state.IsAdjusted = false;
            label.SetTextAutoSize(label.Text);
            return;
        }

        if (!state.IsAdjusted)
        {
            state.OriginalMinFontSize = label.MinFontSize;
            state.OriginalMaxFontSize = label.MaxFontSize;
            state.IsAdjusted = true;
        }

        int reduction = noteCount == 1 ? 3 : 5;
        label.MinFontSize = Math.Min(state.OriginalMinFontSize, 10);
        label.MaxFontSize = Math.Max(12, state.OriginalMaxFontSize - reduction);
        label.SetTextAutoSize(label.Text);
    }

    private static int GetAdditionalNoteCount(CardModel? card)
    {
        if (card is null)
            return 0;
        string typeName = card.GetType().Name;
        int count = 0;
        if (BlockReferenceCards.Contains(typeName))
            count++;
        if (BlockRetentionCards.Contains(typeName))
            count++;
        if (OstyCards.Contains(typeName))
            count++;
        return count;
    }

    private sealed class FontAdjustmentState
    {
        public bool IsAdjusted { get; set; }
        public int OriginalMinFontSize { get; set; }
        public int OriginalMaxFontSize { get; set; }
    }
}
