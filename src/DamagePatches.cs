using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace HPShare;

internal static class DamagePatches
{
    private static readonly AsyncLocal<DamageBatch?> CurrentBatch = new();

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Creature), nameof(Creature.DamageBlockInternal)),
            prefix: new HarmonyMethod(typeof(DamagePatches), nameof(DamageBlockPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Creature), nameof(Creature.LoseHpInternal)),
            prefix: new HarmonyMethod(typeof(DamagePatches), nameof(LoseHpPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Hook), nameof(Hook.ModifyDamage)),
            postfix: new HarmonyMethod(typeof(DamagePatches), nameof(ModifyDamagePostfix)));

        MethodInfo terminalDamage = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
        [
            typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext),
            typeof(IEnumerable<Creature>),
            typeof(decimal),
            typeof(ValueProp),
            typeof(Creature),
            typeof(CardModel),
            typeof(CardPlay),
        ]);
        harmony.Patch(terminalDamage,
            prefix: new HarmonyMethod(typeof(DamagePatches), nameof(DamageBatchPrefix)),
            postfix: new HarmonyMethod(typeof(DamagePatches), nameof(DamageBatchPostfix)));
    }

    private static void ModifyDamagePostfix(Creature? target, Creature? dealer, ref decimal __result)
    {
        // Damage previews may legitimately have no target yet (for example while a
        // freshly drawn card is being laid out). Never let a UI preview kill the
        // multiplayer combat turn loop.
        if (target is null || dealer?.IsMonster != true)
            return;
        if (target.IsPlayer && SharedVitals.IsSharedPlayer(target))
            __result *= ModConfig.EnemyAttackCoefficient;
    }

    private static void DamageBatchPrefix(
        IEnumerable<Creature> targets,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        CardPlay? cardPlay,
        out DamageBatch? __state)
    {
        __state = CurrentBatch.Value;
        List<Creature> targetList = targets.ToList();
        List<Creature> playerTargets = targetList.Where(SharedVitals.IsSharedPlayer).ToList();
        if (playerTargets.Count == 0 || props.HasFlag(ValueProp.Unblockable))
        {
            CurrentBatch.Value = null;
            return;
        }

        try
        {
            IRunState runState = IRunState.GetFrom(targetList);
            var damages = new List<(Creature Target, int Damage)>(playerTargets.Count);
            foreach (Creature target in playerTargets)
            {
                IEnumerable<AbstractModel> modifiers;
                decimal modified = Hook.ModifyDamage(
                    runState,
                    target.CombatState,
                    target,
                    dealer,
                    amount,
                    props,
                    cardSource,
                    cardPlay,
                    ModifyDamageHookType.All,
                    CardPreviewMode.None,
                    out modifiers);
                damages.Add((target, ToDamageInt(modified)));
            }
            CurrentBatch.Value = new DamageBatch(damages);
        }
        catch (Exception ex)
        {
            CurrentBatch.Value = null;
            Godot.GD.PrintErr($"[HPShare] Could not precompute shared Block allocation; using safe sequential pooling: {ex.Message}");
        }
    }

    private static void DamageBatchPostfix(DamageBatch? __state)
        => CurrentBatch.Value = __state;

    private static bool DamageBlockPrefix(Creature __instance, decimal amount, ValueProp props, ref decimal __result)
    {
        if (!SharedVitals.IsSharedPlayer(__instance))
            return true;
        if (props.HasFlag(ValueProp.Unblockable))
        {
            __result = decimal.Zero;
            return false;
        }

        int incoming = ToDamageInt(amount);
        int requested = CurrentBatch.Value?.TakeBlockFor(__instance, incoming)
            ?? Math.Min(incoming, SharedVitals.SharedBlock(__instance));
        __result = SharedVitals.DrainSharedBlock(__instance, requested);
        return false;
    }

    private static bool LoseHpPrefix(Creature __instance, decimal amount, ValueProp props, ref DamageResult __result)
    {
        int requested = ToDamageInt(amount);
        if (SharedVitals.IsSharedPlayer(__instance))
        {
            int oldTotal = SharedVitals.SharedCurrentHp(__instance);
            int actual = SharedVitals.LoseSharedPlayerHp(__instance, requested);
            int newTotal = SharedVitals.SharedCurrentHp(__instance);
            bool killed = oldTotal > 0 && newTotal <= 0;
            __result = NewDamageResult(__instance, props, actual, killed, killed ? Math.Max(0, requested - oldTotal) : 0);
            return false;
        }

        if (SharedVitals.IsOsty(__instance) && SharedVitals.TryGetParty(__instance, out _))
        {
            int oldShared = SharedVitals.SharedCurrentHp(__instance);
            int oldContribution = SharedVitals.RawCurrentHp(__instance);
            int actual = SharedVitals.DamageOstyContribution(__instance, requested);
            bool killed = oldShared > 0 && SharedVitals.SharedCurrentHp(__instance) <= 0;
            __result = NewDamageResult(__instance, props, actual, killed, killed ? Math.Max(0, requested - oldContribution) : 0);
            return false;
        }

        return true;
    }

    private static DamageResult NewDamageResult(Creature receiver, ValueProp props, int unblocked, bool killed, int overkill)
        => new(receiver, props)
        {
            UnblockedDamage = unblocked,
            WasTargetKilled = killed,
            OverkillDamage = overkill,
        };

    private static int ToDamageInt(decimal value)
        => decimal.ToInt32(decimal.Truncate(Math.Clamp(value, 0m, 999_999_999m)));

    private sealed class DamageBatch
    {
        private readonly List<(Creature Target, int Damage)> _damage;
        private Dictionary<Creature, Queue<int>>? _blockAllocation;

        public DamageBatch(List<(Creature Target, int Damage)> damage)
            => _damage = damage;

        public int TakeBlockFor(Creature target, int actualIncoming)
        {
            if (_blockAllocation is null)
            {
                int availableBlock = SharedVitals.SharedBlock(target);
                List<(Creature Target, int Damage, int Sequence)> orderedTargets = _damage
                    .Select((item, index) => (item.Target, item.Damage, Sequence: index))
                    .OrderBy(item => item.Target.Player?.NetId ?? 0)
                    .ThenBy(item => item.Sequence)
                    .ToList();
                int[] weights = orderedTargets.Select(item => item.Damage).ToArray();
                long totalDamage = weights.Sum(static damage => (long)damage);
                int blockedTotal = (int)Math.Min(availableBlock, totalDamage);
                int[] allocation = SharedVitals.AllocateProportionally(
                    blockedTotal,
                    weights,
                    orderedTargets.Select(item => item.Target).ToList());
                _blockAllocation = new Dictionary<Creature, Queue<int>>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < orderedTargets.Count; i++)
                {
                    Creature allocatedTarget = orderedTargets[i].Target;
                    if (!_blockAllocation.TryGetValue(allocatedTarget, out Queue<int>? queue))
                    {
                        queue = new Queue<int>();
                        _blockAllocation.Add(allocatedTarget, queue);
                    }
                    queue.Enqueue(allocation[i]);
                }
            }

            if (!_blockAllocation.TryGetValue(target, out Queue<int>? allocations) || allocations.Count == 0)
                return Math.Min(actualIncoming, SharedVitals.SharedBlock(target));

            int allocated = allocations.Dequeue();
            return Math.Min(actualIncoming, allocated);
        }
    }
}
