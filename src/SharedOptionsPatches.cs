using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace HPShare;

/// <summary>Optional party-wide Power and gold sharing.</summary>
internal static class SharedOptionsPatches
{
    private static readonly FieldInfo RawGoldField = AccessTools.Field(typeof(Player), "_gold");
    private static readonly FieldInfo GoldChangedField = AccessTools.Field(typeof(Player), "GoldChanged");
    private static readonly PropertyInfo CanonicalInstanceProperty = AccessTools.Property(typeof(PowerModel), "CanonicalInstance");
    private static readonly PropertyInfo AmountProperty = AccessTools.Property(typeof(PowerModel), nameof(PowerModel.Amount));
    private static readonly MethodInfo MutableCloneMethod = AccessTools.Method(typeof(AbstractModel), "MutableClone");
    private static readonly AsyncLocal<int> MirrorDepth = new();
    private static readonly ConditionalWeakTable<PlayerChoiceContext, PendingApplications> PendingMirrors = new();
    [ThreadStatic] private static int _playerSyncDepth;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.PropertyGetter(typeof(Player), nameof(Player.Gold)),
            prefix: new HarmonyMethod(typeof(SharedOptionsPatches), nameof(GoldGetterPrefix)));
        harmony.Patch(
            AccessTools.PropertySetter(typeof(Player), nameof(Player.Gold)),
            prefix: new HarmonyMethod(typeof(SharedOptionsPatches), nameof(GoldSetterPrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(Player), nameof(Player.SyncWithSerializedPlayer)),
            prefix: new HarmonyMethod(typeof(SharedOptionsPatches), nameof(PlayerSyncPrefix)),
            finalizer: new HarmonyMethod(typeof(SharedOptionsPatches), nameof(PlayerSyncFinalizer)));
        harmony.Patch(
            AccessTools.Method(typeof(Player), "ToSerializable"),
            postfix: new HarmonyMethod(typeof(SharedOptionsPatches), nameof(PlayerToSerializablePostfix)));

        MethodInfo directApply = AccessTools.GetDeclaredMethods(typeof(PowerCmd)).Single(method =>
            method.Name == nameof(PowerCmd.Apply)
            && !method.IsGenericMethod
            && method.GetParameters().Length == 7);
        harmony.Patch(
            directApply,
            prefix: new HarmonyMethod(typeof(SharedOptionsPatches), nameof(DirectApplyPrefix)),
            postfix: new HarmonyMethod(typeof(SharedOptionsPatches), nameof(DirectApplyPostfix)));

        MethodInfo modifyAmount = AccessTools.Method(typeof(PowerCmd), nameof(PowerCmd.ModifyAmount));
        harmony.Patch(
            modifyAmount,
            prefix: new HarmonyMethod(typeof(SharedOptionsPatches), nameof(ModifyAmountPrefix)),
            postfix: new HarmonyMethod(typeof(SharedOptionsPatches), nameof(ModifyAmountPostfix)));
    }

    private static bool DirectApplyPrefix(
        PlayerChoiceContext __0,
        PowerModel __1,
        Creature __2,
        decimal __3,
        Creature __4,
        CardModel __5,
        ref Task __result,
        out bool __state)
    {
        __state = ConsumePendingMirror(__0, __1, __2, __3, __4, __5);
        if (!__state)
            return true;
        __result = Task.CompletedTask;
        return false;
    }

    private static void PlayerSyncPrefix()
        => _playerSyncDepth++;

    private static Exception? PlayerSyncFinalizer(Player __instance, Exception? __exception)
    {
        _playerSyncDepth = Math.Max(0, _playerSyncDepth - 1);
        if (_playerSyncDepth == 0 && __exception is null
            && TryGetGoldParty(__instance, out IReadOnlyList<Player> players))
            NotifyGoldViews(players);
        return __exception;
    }

    private static void PlayerToSerializablePostfix(Player __instance, SerializablePlayer __result)
    {
        if (ModConfig.ShareGold && __instance.RunState?.Players.Count > 1)
            __result.Gold = RawGold(__instance);
    }

    private static bool GoldGetterPrefix(Player __instance, ref int __result)
    {
        if (_playerSyncDepth > 0 || !TryGetGoldParty(__instance, out IReadOnlyList<Player> players))
            return true;
        __result = SaturatingGoldSum(players);
        return false;
    }

    private static bool GoldSetterPrefix(Player __instance, int __0)
    {
        if (_playerSyncDepth > 0 || !TryGetGoldParty(__instance, out IReadOnlyList<Player> players))
            return true;

        int desiredTotal = Math.Clamp(__0, 0, 999_999_999);
        int currentTotal = SaturatingGoldSum(players);
        int delta = desiredTotal - currentTotal;
        if (delta > 0)
        {
            int current = RawGold(__instance);
            RawGoldField.SetValue(__instance, Math.Min(999_999_999, current + delta));
        }
        else if (delta < 0)
        {
            int receiverIndex = IndexOf(players, __instance);
            int[] current = players.Select(RawGold).ToArray();
            int[] removal = SharedVitals.AllocateReceiverFirst(-delta, current, receiverIndex);
            for (int index = 0; index < players.Count; index++)
                RawGoldField.SetValue(players[index], current[index] - removal[index]);
        }
        NotifyGoldViews(players);
        return false;
    }

    private static bool TryGetGoldParty(Player player, out IReadOnlyList<Player> players)
    {
        players = Array.Empty<Player>();
        if (!ModConfig.ShareGold || player.RunState is null)
            return false;
        players = player.RunState.Players.OrderBy(candidate => candidate.NetId).ToList();
        return players.Count > 1;
    }

    private static int RawGold(Player player)
        => Math.Max(0, (int)RawGoldField.GetValue(player)!);

    private static int SaturatingGoldSum(IEnumerable<Player> players)
    {
        long total = 0;
        foreach (Player player in players)
            total = Math.Min(999_999_999L, total + RawGold(player));
        return (int)total;
    }

    private static int IndexOf(IReadOnlyList<Player> players, Player player)
    {
        for (int index = 0; index < players.Count; index++)
            if (ReferenceEquals(players[index], player))
                return index;
        return -1;
    }

    private static void NotifyGoldViews(IEnumerable<Player> players)
    {
        foreach (Player player in players)
            (GoldChangedField.GetValue(player) as Action)?.Invoke();
    }

    private static void DirectApplyPostfix(
        PlayerChoiceContext __0,
        PowerModel __1,
        Creature __2,
        decimal __3,
        Creature __4,
        CardModel __5,
        bool __6,
        ref Task __result,
        bool __state)
    {
        if (__state || !ShouldMirror(__1, __2))
            return;
        __result = MirrorNewPowerAsync(__result, __0, __1, __2, __3, __4, __5, __6);
    }

    private static void ModifyAmountPostfix(
        PlayerChoiceContext __0,
        PowerModel __1,
        decimal __2,
        Creature __3,
        CardModel __4,
        bool __5,
        ref Task<int> __result,
        ModifyState __state)
    {
        Creature target = __1.Owner;
        if (__state.IsDuplicate || !ShouldMirror(__1, target))
            return;
        __result = MirrorExistingPowerAsync(
            __result, __0, __1, target, __2, __3, __4, __5, __state.PreviousAmount);
    }

    private static bool ModifyAmountPrefix(
        PlayerChoiceContext __0,
        PowerModel __1,
        decimal __2,
        Creature __3,
        CardModel __4,
        ref Task<int> __result,
        out ModifyState __state)
    {
        bool isDuplicate = ConsumePendingMirror(__0, __1, __1.Owner, __2, __3, __4);
        __state = new ModifyState(isDuplicate, __1.Amount);
        if (!isDuplicate)
            return true;
        __result = Task.FromResult(__1.Amount);
        return false;
    }

    private static bool ShouldMirror(PowerModel power, Creature target)
        => ModConfig.ShareBuffsAndDebuffs
           && MirrorDepth.Value == 0
           && power.Type is PowerType.Buff or PowerType.Debuff
           && SharedVitals.IsPartyPlayer(target);

    private static bool ConsumePendingMirror(
        PlayerChoiceContext context,
        PowerModel power,
        Creature target,
        decimal amount,
        Creature applier,
        CardModel cardSource)
    {
        if (MirrorDepth.Value > 0 || !ModConfig.ShareBuffsAndDebuffs)
            return false;
        return PendingMirrors.TryGetValue(context, out PendingApplications? pending)
               && pending.Consume(power.Id, target, amount, applier, cardSource);
    }

    private static async Task MirrorNewPowerAsync(
        Task original,
        PlayerChoiceContext context,
        PowerModel power,
        Creature originalTarget,
        decimal amount,
        Creature applier,
        CardModel cardSource,
        bool silent)
    {
        await original;
        int authoritativeAmount = power.Amount;
        await ApplyToOtherPlayers(
            context,
            power,
            originalTarget,
            authoritativeAmount,
            amount,
            amount,
            applier,
            cardSource,
            silent);
    }

    private static async Task<int> MirrorExistingPowerAsync(
        Task<int> original,
        PlayerChoiceContext context,
        PowerModel power,
        Creature originalTarget,
        decimal amount,
        Creature applier,
        CardModel cardSource,
        bool silent,
        int previousAmount)
    {
        int result = await original;
        int actualDelta = CalculateActualPowerDelta(previousAmount, result);
        await ApplyToOtherPlayers(
            context,
            power,
            originalTarget,
            result,
            actualDelta,
            amount,
            applier,
            cardSource,
            silent);
        return result;
    }

    internal static int CalculateActualPowerDelta(int previousAmount, int finalAmount)
        => finalAmount - previousAmount;

    private static async Task ApplyToOtherPlayers(
        PlayerChoiceContext context,
        PowerModel power,
        Creature originalTarget,
        int authoritativeAmount,
        decimal amountToApply,
        decimal pendingMatchAmount,
        Creature applier,
        CardModel cardSource,
        bool silent)
    {
        if (!SharedVitals.TryGetParty(originalTarget, out IReadOnlyList<Player> players))
            return;

        int previousDepth = MirrorDepth.Value;
        MirrorDepth.Value = previousDepth + 1;
        try
        {
            // Powers created by turn-start hooks (Doom from Neurosurge, for example)
            // can be mutable instances whose CanonicalInstance is null. Always fall
            // back to the model database so those generated effects remain shareable.
            PowerModel canonical = (PowerModel?)CanonicalInstanceProperty.GetValue(power)
                                   ?? ModelDb.GetById<PowerModel>(power.Id);
            foreach (Player player in players)
            {
                Creature target = player.Creature;
                if (ReferenceEquals(target, originalTarget))
                    continue;

                Dictionary<PowerModel, int> amountsBefore = target.Powers
                    .Where(candidate => candidate.Id.Equals(power.Id))
                    .ToDictionary(candidate => candidate, candidate => candidate.Amount);
                var clone = (PowerModel)MutableCloneMethod.Invoke(canonical, null)!;
                PendingMirrors.GetOrCreateValue(context)
                    .Add(power.Id, target, pendingMatchAmount, applier, cardSource);
                await PowerCmd.Apply(context, clone, target, amountToApply, applier, cardSource, silent);

                PowerModel? mirroredPower = target.Powers
                    .Where(candidate => candidate.Id.Equals(power.Id))
                    .FirstOrDefault(candidate => !amountsBefore.ContainsKey(candidate))
                    ?? target.Powers
                        .Where(candidate => candidate.Id.Equals(power.Id))
                        .FirstOrDefault(candidate =>
                            amountsBefore.TryGetValue(candidate, out int oldAmount)
                            && candidate.Amount != oldAmount)
                    ?? target.Powers.FirstOrDefault(candidate => candidate.Id.Equals(power.Id));

                // PowerModel.Amount raises the vanilla modification notification, so both
                // gameplay checks (including Doom) and its UI see the same authoritative value.
                if (mirroredPower is not null && mirroredPower.Amount != authoritativeAmount)
                    AmountProperty.SetValue(mirroredPower, authoritativeAmount);
            }
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[ShareEverything] Failed to mirror {power.Id}: {ex}");
        }
        finally
        {
            MirrorDepth.Value = previousDepth;
        }
    }

    private sealed class PendingApplications
    {
        private readonly object _lock = new();
        private readonly List<PendingApplication> _items = [];

        public void Add(ModelId powerId, Creature target, decimal amount, Creature applier, CardModel cardSource)
        {
            lock (_lock)
                _items.Add(new PendingApplication(powerId, target, amount, applier, cardSource));
        }

        public bool Consume(ModelId powerId, Creature target, decimal amount, Creature applier, CardModel cardSource)
        {
            lock (_lock)
            {
                int index = _items.FindIndex(item =>
                    item.PowerId.Equals(powerId)
                    && ReferenceEquals(item.Target, target)
                    && item.Amount == amount
                    && ReferenceEquals(item.Applier, applier)
                    && ReferenceEquals(item.CardSource, cardSource));
                if (index < 0)
                    return false;
                _items.RemoveAt(index);
                return true;
            }
        }
    }

    private sealed record PendingApplication(
        ModelId PowerId,
        Creature Target,
        decimal Amount,
        Creature Applier,
        CardModel CardSource);

    private readonly record struct ModifyState(bool IsDuplicate, int PreviousAmount);
}
