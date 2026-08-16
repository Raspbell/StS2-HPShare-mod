using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace HPShare;

/// <summary>Turns the party's per-player energy fields into one deterministic shared pool.</summary>
internal static class SharedEnergyPatches
{
    private static readonly FieldInfo CombatPlayerField = AccessTools.Field(typeof(PlayerCombatState), "_player");
    private static readonly FieldInfo EnergyField = AccessTools.Field(typeof(PlayerCombatState), "_energy");
    private static readonly FieldInfo EnergyChangedField = AccessTools.Field(typeof(PlayerCombatState), "EnergyChanged");
    private static readonly FieldInfo MaxEnergyField = AccessTools.Field(typeof(Player), "<MaxEnergy>k__BackingField");
    [ThreadStatic] private static bool _synchronizing;
    [ThreadStatic] private static int _playerSyncDepth;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.Energy)),
            prefix: new HarmonyMethod(typeof(SharedEnergyPatches), nameof(EnergyGetterPrefix)));
        harmony.Patch(
            AccessTools.PropertySetter(typeof(PlayerCombatState), nameof(PlayerCombatState.Energy)),
            prefix: new HarmonyMethod(typeof(SharedEnergyPatches), nameof(EnergySetterPrefix)));
        harmony.Patch(
            AccessTools.PropertyGetter(typeof(Player), nameof(Player.MaxEnergy)),
            prefix: new HarmonyMethod(typeof(SharedEnergyPatches), nameof(MaxEnergyGetterPrefix)));
        harmony.Patch(
            AccessTools.PropertySetter(typeof(Player), nameof(Player.MaxEnergy)),
            prefix: new HarmonyMethod(typeof(SharedEnergyPatches), nameof(MaxEnergySetterPrefix)));
        harmony.Patch(
            AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.MaxEnergy)),
            prefix: new HarmonyMethod(typeof(SharedEnergyPatches), nameof(CombatMaxEnergyGetterPrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(Player), nameof(Player.SyncWithSerializedPlayer)),
            prefix: new HarmonyMethod(typeof(SharedEnergyPatches), nameof(PlayerSyncPrefix)),
            finalizer: new HarmonyMethod(typeof(SharedEnergyPatches), nameof(PlayerSyncFinalizer)));
        harmony.Patch(
            AccessTools.Method(typeof(Player), "ToSerializable"),
            postfix: new HarmonyMethod(typeof(SharedEnergyPatches), nameof(PlayerToSerializablePostfix)));
    }

    private static bool EnergyGetterPrefix(PlayerCombatState __instance, ref int __result)
    {
        if (!TryGetParty(__instance, out IReadOnlyList<Player> players))
            return true;
        __result = SaturatingSum(players.Select(player => RawEnergy(player.PlayerCombatState!)));
        return false;
    }

    private static bool EnergySetterPrefix(PlayerCombatState __instance, int __0)
    {
        if (_synchronizing || !TryGetParty(__instance, out IReadOnlyList<Player> players))
            return true;
        SetSharedEnergy(players, __instance, Math.Clamp(__0, 0, 999_999_999));
        return false;
    }

    private static bool CombatMaxEnergyGetterPrefix(PlayerCombatState __instance, ref int __result)
    {
        if (!TryGetParty(__instance, out IReadOnlyList<Player> players))
            return true;

        long total = 0L;
        foreach (Player player in players)
        {
            decimal modified = Hook.ModifyMaxEnergy(
                player.Creature.CombatState!, player, RawMaxEnergy(player));
            int contribution = decimal.ToInt32(decimal.Truncate(Math.Clamp(modified, 0m, 999_999_999m)));
            total = Math.Min(999_999_999L, total + contribution);
        }
        __result = (int)total;
        return false;
    }

    private static void PlayerSyncPrefix()
        => _playerSyncDepth++;

    private static Exception? PlayerSyncFinalizer(Player __instance, Exception? __exception)
    {
        _playerSyncDepth = Math.Max(0, _playerSyncDepth - 1);
        if (_playerSyncDepth == 0 && __exception is null
            && TryGetParty(__instance, out IReadOnlyList<Player> players))
            NotifyEnergyViews(players);
        return __exception;
    }

    private static void PlayerToSerializablePostfix(Player __instance, SerializablePlayer __result)
    {
        if (ModConfig.ShareEnergy && __instance.RunState?.Players.Count > 1)
            __result.MaxEnergy = RawMaxEnergy(__instance);
    }

    private static bool MaxEnergyGetterPrefix(Player __instance, ref int __result)
    {
        if (_playerSyncDepth > 0 || !TryGetMaxEnergyParty(__instance, out IReadOnlyList<Player> players))
            return true;
        __result = SaturatingSum(players.Select(RawMaxEnergy));
        return false;
    }

    private static bool MaxEnergySetterPrefix(Player __instance, int __0)
    {
        if (_synchronizing || _playerSyncDepth > 0
            || !TryGetMaxEnergyParty(__instance, out IReadOnlyList<Player> players))
            return true;

        int desiredTotal = Math.Clamp(__0, 0, 999_999_999);
        int currentTotal = SaturatingSum(players.Select(RawMaxEnergy));
        int delta = desiredTotal - currentTotal;
        _synchronizing = true;
        try
        {
            if (delta >= 0)
            {
                MaxEnergyField.SetValue(__instance, Math.Min(999_999_999, RawMaxEnergy(__instance) + delta));
            }
            else
            {
                int receiverIndex = IndexOf(players, __instance);
                int[] current = players.Select(RawMaxEnergy).ToArray();
                int[] removal = SharedVitals.AllocateReceiverFirst(-delta, current, receiverIndex);
                for (int index = 0; index < players.Count; index++)
                    MaxEnergyField.SetValue(players[index], current[index] - removal[index]);
            }
            NotifyEnergyViews(players);
        }
        finally
        {
            _synchronizing = false;
        }
        return false;
    }

    private static void SetSharedEnergy(
        IReadOnlyList<Player> players,
        PlayerCombatState receiver,
        int desiredTotal)
    {
        int[] current = players.Select(player => RawEnergy(player.PlayerCombatState!)).ToArray();
        int currentTotal = SaturatingSum(current);
        int delta = desiredTotal - currentTotal;
        if (delta == 0)
        {
            NotifyEnergyViews(players);
            return;
        }

        int receiverIndex = IndexOf(players, (Player)CombatPlayerField.GetValue(receiver)!);
        int[] updated = (int[])current.Clone();
        if (delta > 0 && receiverIndex >= 0)
        {
            updated[receiverIndex] = Math.Min(999_999_999, updated[receiverIndex] + delta);
        }
        else if (delta < 0)
        {
            int[] removal = SharedVitals.AllocateReceiverFirst(-delta, current, receiverIndex);
            for (int index = 0; index < updated.Length; index++)
                updated[index] -= removal[index];
        }

        _synchronizing = true;
        try
        {
            for (int index = 0; index < players.Count; index++)
                EnergyField.SetValue(players[index].PlayerCombatState!, updated[index]);
            NotifyEnergyViews(players, current, updated);
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private static bool TryGetParty(PlayerCombatState state, out IReadOnlyList<Player> players)
        => TryGetParty((Player)CombatPlayerField.GetValue(state)!, out players);

    private static bool TryGetParty(Player player, out IReadOnlyList<Player> players)
    {
        players = Array.Empty<Player>();
        if (!ModConfig.ShareEnergy || player.RunState is null)
            return false;
        players = player.RunState.Players.OrderBy(candidate => candidate.NetId).ToList();
        return players.Count > 1 && players.All(candidate => candidate.PlayerCombatState is not null);
    }

    private static bool TryGetMaxEnergyParty(Player player, out IReadOnlyList<Player> players)
    {
        players = Array.Empty<Player>();
        if (!ModConfig.ShareEnergy || player.RunState is null)
            return false;
        players = player.RunState.Players.OrderBy(candidate => candidate.NetId).ToList();
        return players.Count > 1;
    }

    private static int RawEnergy(PlayerCombatState state)
        => (int)EnergyField.GetValue(state)!;

    private static int RawMaxEnergy(Player player)
        => (int)MaxEnergyField.GetValue(player)!;

    private static int SaturatingSum(IEnumerable<int> values)
    {
        long result = 0;
        foreach (int value in values)
            result = Math.Min(999_999_999L, result + Math.Max(0, value));
        return (int)result;
    }

    private static int IndexOf(IReadOnlyList<Player> players, Player player)
    {
        for (int index = 0; index < players.Count; index++)
            if (ReferenceEquals(players[index], player))
                return index;
        return -1;
    }

    private static void NotifyEnergyViews(IReadOnlyList<Player> players)
    {
        int[] raw = players.Select(player => RawEnergy(player.PlayerCombatState!)).ToArray();
        NotifyEnergyViews(players, raw, raw);
    }

    private static void NotifyEnergyViews(
        IReadOnlyList<Player> players,
        IReadOnlyList<int> oldValues,
        IReadOnlyList<int> newValues)
    {
        for (int index = 0; index < players.Count; index++)
        {
            PlayerCombatState state = players[index].PlayerCombatState!;
            (EnergyChangedField.GetValue(state) as Action<int, int>)?.Invoke(oldValues[index], newValues[index]);
        }
    }
}
