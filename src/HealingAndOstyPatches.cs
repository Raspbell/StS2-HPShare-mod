using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HPShare;

internal static class HealingPatches
{
    private static readonly MethodInfo VanillaHeal = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Heal));
    private static readonly MethodInfo ScaledHeal = AccessTools.Method(typeof(HealingPatches), nameof(HealPercentage));

    public static void Apply(Harmony harmony)
    {
        // These are every v0.111.0 player-heal path that computes the amount as a percentage.
        PatchPercentageHealStateMachine(harmony, typeof(BloodPotion), "OnUse");
        PatchPercentageHealStateMachine(harmony, typeof(FairyInABottle), "OnUse");
        PatchPercentageHealStateMachine(harmony, typeof(Ambergris), "OnUse");
        PatchPercentageHealStateMachine(harmony, typeof(FakeLeesWaffle), "AfterObtained");
        PatchPercentageHealStateMachine(harmony, typeof(LizardTail), "AfterPreventingDeath");
        PatchPercentageHealStateMachine(harmony, typeof(AncientEventModel), "BeforeEventStarted");

        harmony.Patch(AccessTools.Method(typeof(HealRestSiteOption), nameof(HealRestSiteOption.GetBaseHealAmount)),
            postfix: new HarmonyMethod(typeof(HealingPatches), nameof(RestSiteBaseHealPostfix)));
    }

    public static Task HealPercentage(Creature creature, decimal amount, bool playAnim)
    {
        if (SharedVitals.IsSharedPlayer(creature))
        {
            int personalMaximum = SharedVitals.RawMaxHp(creature);
            if (personalMaximum > 0)
                amount *= (decimal)SharedVitals.SharedMaxHp(creature) / personalMaximum;
        }
        return CreatureCmd.Heal(creature, amount, playAnim);
    }

    private static void RestSiteBaseHealPostfix(Creature creature, ref decimal __result)
    {
        if (SharedVitals.IsSharedPlayer(creature))
        {
            int personalMaximum = SharedVitals.RawMaxHp(creature);
            if (personalMaximum > 0)
                __result *= (decimal)SharedVitals.SharedMaxHp(creature) / personalMaximum;
        }
    }

    private static void PatchPercentageHealStateMachine(Harmony harmony, Type declaringType, string methodName)
    {
        Type? stateMachine = declaringType.GetNestedTypes(BindingFlags.NonPublic)
            .FirstOrDefault(type => type.Name.StartsWith($"<{methodName}>", StringComparison.Ordinal));
        MethodInfo? moveNext = stateMachine is null ? null : AccessTools.Method(stateMachine, "MoveNext");
        if (moveNext is null)
        {
            Godot.GD.PrintErr($"[HPShare] Percentage-heal patch target not found: {declaringType.FullName}.{methodName}");
            return;
        }

        harmony.Patch(moveNext,
            transpiler: new HarmonyMethod(typeof(HealingPatches), nameof(PercentageHealTranspiler)));
    }

    private static IEnumerable<CodeInstruction> PercentageHealTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && Equals(instruction.operand, VanillaHeal))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = ScaledHeal;
            }
            yield return instruction;
        }
    }
}

internal static class DeathPreventionPatches
{
    public static void Apply(Harmony harmony)
    {
        PatchCreatureArgument(harmony, typeof(FairyInABottle), nameof(FairyInABottle.ShouldDie));
        PatchCreatureArgument(harmony, typeof(FairyInABottle), nameof(FairyInABottle.AfterPreventingDeath));
        PatchCreatureArgument(harmony, typeof(LizardTail), nameof(LizardTail.ShouldDieLate));
        PatchCreatureArgument(harmony, typeof(LizardTail), nameof(LizardTail.AfterPreventingDeath));
    }

    private static void PatchCreatureArgument(Harmony harmony, Type type, string method)
    {
        harmony.Patch(AccessTools.Method(type, method),
            prefix: new HarmonyMethod(typeof(DeathPreventionPatches), nameof(UsePreventerOwnersCreature)));
    }

    private static void UsePreventerOwnersCreature(AbstractModel __instance, ref Creature creature)
    {
        if (!SharedVitals.IsSharedPlayer(creature))
            return;

        Player? owner = __instance switch
        {
            RelicModel relic => relic.Owner,
            PotionModel potion => potion.Owner,
            _ => null,
        };
        if (owner is not null)
            creature = owner.Creature;
    }
}

internal static class OstyPatches
{
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)),
            postfix: new HarmonyMethod(typeof(OstyPatches), nameof(IsAlivePostfix)));
        harmony.Patch(AccessTools.Method(typeof(DieForYouPower), nameof(DieForYouPower.ModifyUnblockedDamageTarget)),
            postfix: new HarmonyMethod(typeof(OstyPatches), nameof(DieForYouPostfix)));
    }

    private static void IsAlivePostfix(Creature __instance, ref bool __result)
    {
        if (SharedVitals.IsSharedPlayer(__instance))
            __result = SharedVitals.SharedCurrentHp(__instance) > 0;
        else if (SharedVitals.IsOsty(__instance) && SharedVitals.TryGetParty(__instance, out _))
            __result = SharedVitals.SharedCurrentHp(__instance) > 0;
    }

    private static void DieForYouPostfix(DieForYouPower __instance, Creature target, ref Creature __result)
    {
        Creature osty = __instance.Owner;
        if (!SharedVitals.TryGetParty(osty, out _))
            return;

        // A zero personal contribution does not mean that this physical Osty is
        // dead. Keep redirecting until the shared Osty pool as a whole is empty.
        if (ReferenceEquals(__result, osty) && !SharedVitals.SharedOstyAlive(osty))
            __result = target;
    }
}
