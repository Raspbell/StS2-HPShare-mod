using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace HPShare;

internal static class SharedVitals
{
    private static readonly ConditionalWeakTable<object, PartyReadyMarker> ReadyParties = new();
    private static readonly object ReadyPartiesLock = new();
    private static readonly FieldInfo CurrentHpField = AccessTools.Field(typeof(Creature), "_currentHp");
    private static readonly FieldInfo MaxHpField = AccessTools.Field(typeof(Creature), "_maxHp");
    private static readonly FieldInfo BlockField = AccessTools.Field(typeof(Creature), "_block");
    private static readonly FieldInfo CurrentHpChangedField = AccessTools.Field(typeof(Creature), "CurrentHpChanged");
    private static readonly FieldInfo MaxHpChangedField = AccessTools.Field(typeof(Creature), "MaxHpChanged");
    private static readonly FieldInfo BlockChangedField = AccessTools.Field(typeof(Creature), "BlockChanged");

    public static int RawCurrentHp(Creature creature) => (int)CurrentHpField.GetValue(creature)!;
    public static int RawMaxHp(Creature creature) => (int)MaxHpField.GetValue(creature)!;
    public static int RawBlock(Creature creature) => (int)BlockField.GetValue(creature)!;

    public static bool TryGetParty(Creature creature, out IReadOnlyList<Player> players)
    {
        players = Array.Empty<Player>();
        Player? player = creature.Player ?? creature.PetOwner;
        if (player?.RunState is null)
            return false;

        players = player.RunState.Players;
        return players.Count > 1;
    }

    public static bool IsPartyPlayer(Creature creature)
        => creature.Player is not null && TryGetParty(creature, out _);

    public static bool IsSharedPlayer(Creature creature)
        => IsPartyPlayer(creature) && IsPartyVitalsReady(creature);

    public static bool IsPartyVitalsReady(Creature creature)
    {
        Player? player = creature.Player;
        object? runState = player?.RunState;
        if (runState is null || !TryGetParty(creature, out IReadOnlyList<Player> players))
            return false;
        if (ReadyParties.TryGetValue(runState, out _))
            return true;

        bool initialized = players.All(candidate =>
            RawMaxHp(candidate.Creature) > 0 && RawCurrentHp(candidate.Creature) > 0);
        if (!initialized)
            return false;

        MarkPartyReady(runState);
        return true;
    }

    public static void MarkPartyReadyFromUi(Creature creature)
    {
        Player? player = creature.Player;
        object? runState = player?.RunState;
        if (runState is null || !TryGetParty(creature, out IReadOnlyList<Player> players))
            return;
        // A health bar is only bound after creature construction. Requiring every Max HP value
        // also avoids activating while the lobby is still initializing players one at a time.
        if (players.All(candidate => RawMaxHp(candidate.Creature) > 0))
            MarkPartyReady(runState);
    }

    public static bool IsOsty(Creature creature)
    {
        Player? owner = creature.PetOwner;
        if (owner is null)
            return false;

        try
        {
            return ReferenceEquals(owner.Osty, creature);
        }
        catch
        {
            return creature.ModelId.Entry.Equals("OSTY", StringComparison.OrdinalIgnoreCase)
                || creature.Name.Contains("Osty", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static List<Creature> PlayerCreatures(Creature creature)
    {
        if (!TryGetParty(creature, out IReadOnlyList<Player> players))
            return [];
        return players.Select(player => player.Creature).ToList();
    }

    public static List<Creature> Osties(Creature creature)
    {
        if (!TryGetParty(creature, out IReadOnlyList<Player> players))
            return [];

        var osties = new List<Creature>();
        foreach (Player player in players)
        {
            try
            {
                Creature? osty = player.Osty;
                if (osty is not null)
                    osties.Add(osty);
            }
            catch
            {
                // A non-Necrobinder has no Osty and some game states do not expose pets yet.
            }
        }
        return osties;
    }

    public static int SharedCurrentHp(Creature creature)
        => IsOsty(creature)
            ? SaturatingSum(Osties(creature), RawCurrentHp)
            : SaturatingSum(PlayerCreatures(creature), RawCurrentHp);

    public static int SharedMaxHp(Creature creature)
        => IsOsty(creature)
            ? SaturatingSum(Osties(creature), RawMaxHp)
            : SaturatingSum(PlayerCreatures(creature), RawMaxHp);

    public static int SharedBlock(Creature creature)
        => SaturatingSum(PlayerCreatures(creature), RawBlock);

    public static bool SharedOstyAlive(Creature creature)
        => IsOsty(creature) && SharedCurrentHp(creature) > 0;

    public static int LoseSharedPlayerHp(Creature receiver, int requestedLoss)
    {
        List<Creature> creatures = PlayerCreatures(receiver);
        int oldTotal = SaturatingSum(creatures, RawCurrentHp);
        int loss = Math.Clamp(requestedLoss, 0, oldTotal);
        if (loss == 0)
            return 0;

        int[] current = creatures.Select(RawCurrentHp).ToArray();
        int receiverIndex = creatures.FindIndex(candidate => ReferenceEquals(candidate, receiver));
        int[] removal = AllocateReceiverFirst(loss, current, receiverIndex);
        for (int i = 0; i < creatures.Count; i++)
            SetRawCurrentHpWithoutNotification(creatures[i], current[i] - removal[i]);
        NotifyCurrentHpChanges(creatures, current, includeUnchanged: true);
        return loss;
    }

    public static int HealSharedPlayerHp(Creature receiver, int requestedHeal)
    {
        List<Creature> creatures = PlayerCreatures(receiver);
        if (creatures.Count == 0 || requestedHeal <= 0)
            return 0;

        int[] oldValues = creatures.Select(RawCurrentHp).ToArray();
        int oldTotal = SaturatingSum(creatures, RawCurrentHp);
        int maxTotal = SaturatingSum(creatures, RawMaxHp);
        int heal = Math.Clamp(requestedHeal, 0, Math.Max(0, maxTotal - oldTotal));
        if (heal == 0)
            return 0;

        int remaining = heal;
        int receiverIndex = creatures.FindIndex(c => ReferenceEquals(c, receiver));
        if (receiverIndex >= 0)
            remaining -= FillCreature(creatures[receiverIndex], remaining);

        foreach (Creature creature in creatures.OrderBy(GetStableId))
        {
            if (remaining <= 0)
                break;
            if (ReferenceEquals(creature, receiver))
                continue;
            remaining -= FillCreature(creature, remaining);
        }

        NotifyCurrentHpChanges(creatures, oldValues, includeUnchanged: true);
        return heal - remaining;
    }

    public static int DamageSharedOstyHp(Creature osty, int requestedLoss)
    {
        List<Creature> osties = Osties(osty);
        if (osties.Count == 0 || requestedLoss <= 0)
            return 0;

        int[] oldValues = osties.Select(RawCurrentHp).ToArray();
        int[] removal = AllocateProportionally(requestedLoss, oldValues, osties);
        int actual = removal.Sum();
        if (actual == 0)
            return 0;

        // Raw Osty HP remains the owner's contribution ledger. Damage, however,
        // belongs to the one shared pool, so distribute the loss across every
        // contribution instead of exhausting only the Osty that was redirected to.
        for (int i = 0; i < osties.Count; i++)
            SetRawCurrentHpWithoutNotification(osties[i], oldValues[i] - removal[i]);
        NotifyCurrentHpChanges(osties, oldValues, includeUnchanged: true);
        return actual;
    }

    public static int DrainSharedBlock(Creature receiver, int requested)
    {
        List<Creature> creatures = PlayerCreatures(receiver);
        int total = SaturatingSum(creatures, RawBlock);
        int drain = Math.Clamp(requested, 0, total);
        if (drain == 0)
            return 0;

        int[] current = creatures.Select(RawBlock).ToArray();
        int[] removal = AllocateProportionally(drain, current, creatures);
        for (int i = 0; i < creatures.Count; i++)
            SetRawBlockWithoutNotification(creatures[i], current[i] - removal[i]);
        NotifyBlockChanges(creatures, current, includeUnchanged: true);
        return drain;
    }

    public static int[] AllocateProportionally(int total, IReadOnlyList<int> weights, IReadOnlyList<Creature>? tieOrder = null)
    {
        var result = new int[weights.Count];
        long weightSum = weights.Sum(static value => (long)Math.Max(0, value));
        if (total <= 0 || weightSum <= 0)
            return result;

        int assigned = 0;
        var remainders = new List<(int Index, decimal Remainder, ulong StableId)>(weights.Count);
        for (int i = 0; i < weights.Count; i++)
        {
            int capacity = Math.Max(0, weights[i]);
            decimal exact = (decimal)total * capacity / weightSum;
            int floor = Math.Min(capacity, decimal.ToInt32(decimal.Floor(exact)));
            result[i] = floor;
            assigned += floor;
            ulong stableId = tieOrder is null ? (ulong)i : GetStableId(tieOrder[i]);
            remainders.Add((i, exact - floor, stableId));
        }

        foreach ((int index, _, _) in remainders
                     .OrderByDescending(item => item.Remainder)
                     .ThenBy(item => item.StableId))
        {
            if (assigned >= total)
                break;
            if (result[index] >= Math.Max(0, weights[index]))
                continue;
            result[index]++;
            assigned++;
        }

        // Capacity and integer rounding can leave more than one pass in extreme values.
        while (assigned < total)
        {
            bool progressed = false;
            foreach ((int index, _, _) in remainders.OrderBy(item => item.StableId))
            {
                if (assigned >= total)
                    break;
                if (result[index] >= Math.Max(0, weights[index]))
                    continue;
                result[index]++;
                assigned++;
                progressed = true;
            }
            if (!progressed)
                break;
        }
        return result;
    }

    public static int[] AllocateReceiverFirst(int total, IReadOnlyList<int> capacities, int receiverIndex)
    {
        var result = new int[capacities.Count];
        if (total <= 0 || capacities.Count == 0)
            return result;

        long available = capacities.Sum(static value => (long)Math.Max(0, value));
        int remaining = (int)Math.Min(total, available);
        if (receiverIndex >= 0 && receiverIndex < capacities.Count)
            remaining -= TakeFrom(receiverIndex, remaining);

        for (int index = 0; index < capacities.Count && remaining > 0; index++)
        {
            if (index == receiverIndex)
                continue;
            remaining -= TakeFrom(index, remaining);
        }
        return result;

        int TakeFrom(int index, int requested)
        {
            int taken = Math.Min(requested, Math.Max(0, capacities[index]));
            result[index] = taken;
            return taken;
        }
    }

    private static void SetRawCurrentHpWithoutNotification(Creature creature, int value)
        => CurrentHpField.SetValue(creature, Math.Clamp(value, 0, 999_999_999));

    private static void SetRawBlockWithoutNotification(Creature creature, int value)
        => BlockField.SetValue(creature, Math.Clamp(value, 0, 999_999_999));

    private static int FillCreature(Creature creature, int amount)
    {
        int capacity = Math.Max(0, RawMaxHp(creature) - RawCurrentHp(creature));
        int add = Math.Min(amount, capacity);
        if (add > 0)
            SetRawCurrentHpWithoutNotification(creature, RawCurrentHp(creature) + add);
        return add;
    }

    private static ulong GetStableId(Creature creature)
        => creature.Player?.NetId ?? creature.PetOwner?.NetId ?? creature.CombatId ?? 0;

    private static int SaturatingSum(IEnumerable<Creature> creatures, Func<Creature, int> selector)
    {
        long sum = 0;
        foreach (Creature creature in creatures)
            sum = Math.Min(999_999_999, sum + Math.Max(0, selector(creature)));
        return (int)sum;
    }

    private static void NotifyCurrentHpChanges(
        IReadOnlyList<Creature> creatures,
        IReadOnlyList<int> oldValues,
        bool includeUnchanged)
    {
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            int raw = RawCurrentHp(creature);
            if (includeUnchanged || oldValues[i] != raw)
                (CurrentHpChangedField.GetValue(creature) as Action<int, int>)?.Invoke(oldValues[i], raw);
        }
    }

    private static void NotifyBlockChanges(
        IReadOnlyList<Creature> creatures,
        IReadOnlyList<int> oldValues,
        bool includeUnchanged)
    {
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            int raw = RawBlock(creature);
            if (includeUnchanged || oldValues[i] != raw)
                (BlockChangedField.GetValue(creature) as Action<int, int>)?.Invoke(oldValues[i], raw);
        }
    }

    public static void NotifyOtherBlockViews(Creature changed)
    {
        foreach (Creature creature in PlayerCreatures(changed))
        {
            if (ReferenceEquals(creature, changed))
                continue;
            int raw = RawBlock(creature);
            (BlockChangedField.GetValue(creature) as Action<int, int>)?.Invoke(raw, raw);
        }
    }

    public static void NotifyOtherOstyHpViews(Creature changed, bool maximumChanged)
    {
        foreach (Creature creature in Osties(changed))
        {
            if (ReferenceEquals(creature, changed))
                continue;
            if (maximumChanged)
            {
                int rawMaximum = RawMaxHp(creature);
                (MaxHpChangedField.GetValue(creature) as Action<int, int>)?.Invoke(rawMaximum, rawMaximum);
            }
            else
            {
                int rawCurrent = RawCurrentHp(creature);
                (CurrentHpChangedField.GetValue(creature) as Action<int, int>)?.Invoke(rawCurrent, rawCurrent);
            }
        }
    }

    public static void NotifyOtherPlayerHpViews(Creature changed, bool maximumChanged)
    {
        foreach (Creature creature in PlayerCreatures(changed))
        {
            if (ReferenceEquals(creature, changed))
                continue;
            if (maximumChanged)
            {
                int rawMaximum = RawMaxHp(creature);
                (MaxHpChangedField.GetValue(creature) as Action<int, int>)?.Invoke(rawMaximum, rawMaximum);
            }
            else
            {
                int rawCurrent = RawCurrentHp(creature);
                (CurrentHpChangedField.GetValue(creature) as Action<int, int>)?.Invoke(rawCurrent, rawCurrent);
            }
        }
    }

    private static void MarkPartyReady(object runState)
    {
        lock (ReadyPartiesLock)
        {
            if (!ReadyParties.TryGetValue(runState, out _))
                ReadyParties.Add(runState, new PartyReadyMarker());
        }
    }

    private sealed class PartyReadyMarker
    {
    }
}

internal static class SharedVitalsPatches
{
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Creature), nameof(Creature.GetHpPercentRemaining)),
            postfix: new HarmonyMethod(typeof(SharedVitalsPatches), nameof(HpPercentPostfix)));
        harmony.Patch(AccessTools.Method(typeof(Creature), nameof(Creature.HealInternal)),
            prefix: new HarmonyMethod(typeof(SharedVitalsPatches), nameof(HealInternalPrefix)));
        harmony.Patch(AccessTools.PropertySetter(typeof(Creature), nameof(Creature.Block)),
            postfix: new HarmonyMethod(typeof(SharedVitalsPatches), nameof(BlockSetterPostfix)));
        harmony.Patch(AccessTools.PropertySetter(typeof(Creature), nameof(Creature.CurrentHp)),
            postfix: new HarmonyMethod(typeof(SharedVitalsPatches), nameof(CurrentHpSetterPostfix)));
        harmony.Patch(AccessTools.PropertySetter(typeof(Creature), nameof(Creature.MaxHp)),
            postfix: new HarmonyMethod(typeof(SharedVitalsPatches), nameof(MaxHpSetterPostfix)));
    }

    private static void HpPercentPostfix(Creature __instance, ref double __result)
    {
        if (!SharedVitals.IsSharedPlayer(__instance))
            return;
        int maximum = SharedVitals.SharedMaxHp(__instance);
        __result = maximum <= 0 ? 0d : (double)SharedVitals.SharedCurrentHp(__instance) / maximum;
    }

    private static bool HealInternalPrefix(Creature __instance, decimal amount)
    {
        if (!SharedVitals.IsSharedPlayer(__instance))
            return true;
        SharedVitals.HealSharedPlayerHp(__instance, DecimalToNonNegativeInt(amount));
        return false;
    }

    private static void BlockSetterPostfix(Creature __instance)
    {
        if (SharedVitals.IsSharedPlayer(__instance))
            SharedVitals.NotifyOtherBlockViews(__instance);
    }

    private static void CurrentHpSetterPostfix(Creature __instance)
    {
        if (SharedVitals.IsSharedPlayer(__instance))
            SharedVitals.NotifyOtherPlayerHpViews(__instance, maximumChanged: false);
        else if (SharedVitals.IsOsty(__instance) && SharedVitals.TryGetParty(__instance, out _))
            SharedVitals.NotifyOtherOstyHpViews(__instance, maximumChanged: false);
    }

    private static void MaxHpSetterPostfix(Creature __instance)
    {
        if (SharedVitals.IsSharedPlayer(__instance))
            SharedVitals.NotifyOtherPlayerHpViews(__instance, maximumChanged: true);
        else if (SharedVitals.IsOsty(__instance) && SharedVitals.TryGetParty(__instance, out _))
            SharedVitals.NotifyOtherOstyHpViews(__instance, maximumChanged: true);
    }

    private static int DecimalToNonNegativeInt(decimal value)
        => decimal.ToInt32(decimal.Truncate(Math.Clamp(value, 0m, 999_999_999m)));
}
