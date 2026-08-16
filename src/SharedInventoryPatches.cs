using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Runs;

namespace HPShare;

/// <summary>Synchronizes persistent decks, relic inventories, and potion belts.</summary>
internal static class SharedInventoryPatches
{
    private static readonly ConditionalWeakTable<CardModel, SharedGroup<CardModel>> CardGroups = new();
    private static readonly ConditionalWeakTable<RelicModel, SharedGroup<RelicModel>> RelicGroups = new();
    private static readonly ConditionalWeakTable<PotionModel, SharedGroup<PotionModel>> PotionGroups = new();
    private static readonly ConditionalWeakTable<object, ConsumedSharedResourceMarker> ConsumedSharedResources = new();
    private static readonly ConditionalWeakTable<Player, PotionSlotContribution> PotionContributions = new();
    private static readonly ConditionalWeakTable<object, InventoryReadyMarker> ReadyInventories = new();
    private static readonly ConditionalWeakTable<NCardGrid, SelectionRefreshState> SelectionRefreshStates = new();
    private static readonly FieldInfo RelicsField = AccessTools.Field(typeof(Player), "_relics");
    private static readonly FieldInfo PotionSlotsField = AccessTools.Field(typeof(Player), "_potionSlots");
    private static readonly FieldInfo CardOwnerField = AccessTools.Field(typeof(CardModel), "_owner");
    private static readonly FieldInfo EventOnChosenField = AccessTools.Field(
        typeof(MegaCrit.Sts2.Core.Events.EventOption), "<OnChosen>k__BackingField");
    private static readonly MethodInfo EventSetFinishedMethod = AccessTools.Method(
        typeof(MegaCrit.Sts2.Core.Models.EventModel), "SetEventFinished");
    private static readonly MethodInfo SetMaxPotionCountMethod = AccessTools.Method(typeof(Player), "SetMaxPotionCountInternal");
    private static readonly FieldInfo TopBarDeckPileField = AccessTools.Field(typeof(NTopBarDeckButton), "_pile");
    private static readonly FieldInfo TopBarDeckCountField = AccessTools.Field(typeof(NTopBarDeckButton), "_count");
    private static readonly MethodInfo TopBarDeckRefreshMethod = AccessTools.Method(typeof(NTopBarDeckButton), "OnPileContentsChanged");
    private static readonly MethodInfo RelicHolderRefreshStatusMethod = AccessTools.Method(typeof(NRelicInventoryHolder), "RefreshStatus");
    private static readonly FieldInfo SelectionCardsField = AccessTools.Field(typeof(NCardGridSelectionScreen), "_cards");
    private static readonly FieldInfo DeckSelectionSelectedCardsField = AccessTools.Field(typeof(NDeckCardSelectScreen), "_selectedCards");
    private static readonly FieldInfo UpgradeSelectionSelectedCardsField = AccessTools.Field(typeof(NDeckUpgradeSelectScreen), "_selectedCards");
    private static readonly MethodInfo DeckSelectionCancelMethod = AccessTools.Method(typeof(NDeckCardSelectScreen), "CancelSelection");
    private static readonly MethodInfo UpgradeSelectionCancelMethod = AccessTools.Method(typeof(NDeckUpgradeSelectScreen), "CancelSelection");
    private static readonly AsyncLocal<int> MirrorDepth = new();
    private static int _mirrorDepth
    {
        get => MirrorDepth.Value;
        set => MirrorDepth.Value = value;
    }

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(RunManager), "InitializeNewRun"),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(NewRunInitializedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(RunManager), "InitializeSavedRun"),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(SavedRunInitializedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.SyncWithSerializedPlayer)),
            prefix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(PlayerSyncPrefix)),
            finalizer: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(PlayerSyncFinalizer)));

        harmony.Patch(AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(DeckCardAddedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(CardPile), nameof(CardPile.RemoveInternal)),
            prefix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(DeckCardRemovingPrefix)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(DeckCardRemovedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(CardModel), nameof(CardModel.UpgradeInternal)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(CardUpgradedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(CardModel), nameof(CardModel.DowngradeInternal)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(CardDowngradedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(CardModel), nameof(CardModel.EnchantInternal)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(CardEnchantedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(CardModel), nameof(CardModel.AfflictInternal)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(CardAfflictedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(CardModel), nameof(CardModel.ClearEnchantmentInternal)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(CardEnchantmentClearedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(CardModel), nameof(CardModel.ClearAfflictionInternal)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(CardAfflictionClearedPostfix)));

        MethodInfo relicObtain = AccessTools.GetDeclaredMethods(typeof(RelicCmd)).Single(method =>
            method.Name == nameof(RelicCmd.Obtain) && !method.IsGenericMethod);
        harmony.Patch(relicObtain,
            prefix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(RelicObtainPrefix)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(RelicObtainedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(RelicCmd), nameof(RelicCmd.Remove)),
            prefix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(RelicRemovePrefix)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(RelicRemovedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(RelicCmd), nameof(RelicCmd.Melt)),
            prefix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(RelicRemovePrefix)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(RelicRemovedPostfix)));

        harmony.Patch(SetMaxPotionCountMethod,
            prefix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(MaxPotionCountPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.AddPotionInternal)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(PotionAddedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.DiscardPotionInternal)),
            prefix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(PotionDiscardingPrefix)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(PotionRemovedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.RemoveUsedPotionInternal)),
            prefix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(UsedPotionRemovingPrefix)),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(PotionRemovedPostfix)));

        harmony.Patch(AccessTools.Method(typeof(NEventOptionButton), "OnRelease"),
            prefix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(EventOptionButtonReleasePrefix)));
        harmony.Patch(AccessTools.Method(typeof(NTopBarDeckButton), "_Process"),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(TopBarDeckProcessPostfix)));
        harmony.Patch(AccessTools.Method(typeof(NRelicInventory), "OnRelicObtained"),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(RelicInventoryObtainedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(NCardGrid), "_Process"),
            postfix: new HarmonyMethod(typeof(SharedInventoryPatches), nameof(CardGridProcessPostfix)));
    }

    private static void NewRunInitializedPostfix(RunManager __instance)
    {
        if (__instance.DebugOnlyGetState() is RunState state)
            Initialize(state, isNewRun: true);
    }

    private static void SavedRunInitializedPostfix(RunManager __instance)
    {
        if (__instance.DebugOnlyGetState() is RunState state)
            Initialize(state, isNewRun: false);
    }

    private static void PlayerSyncPrefix()
        => _mirrorDepth++;

    private static Exception? PlayerSyncFinalizer(Player __instance, Exception? __exception)
    {
        _mirrorDepth = Math.Max(0, _mirrorDepth - 1);
        if (_mirrorDepth == 0 && __exception is null)
            RebindAfterPlayerSync(__instance);
        return __exception;
    }

    private static void RebindAfterPlayerSync(Player player)
    {
        if (player.RunState is not { } runState || !ReadyInventories.TryGetValue(runState, out _)
            || !TryGetParty(player, out IReadOnlyList<Player> players))
            return;

        if (ModConfig.ShareDeck)
            RunInventoryStep("rebind shared decks after player sync", () => RebindMatchingDecks(players));
        if (ModConfig.ShareRelics)
            RunInventoryStep("rebind shared relics after player sync", () => RebindMatchingRelics(players));
        if (ModConfig.SharePotions)
        {
            RunInventoryStep("restore shared potion slots after player sync", () =>
            {
                RestoreSharedPotionSlotCount(players);
                RebindMatchingPotions(players);
            });
        }
    }

    private static void RebindMatchingDecks(IReadOnlyList<Player> players)
    {
        IReadOnlyList<CardModel> first = players[0].Deck.Cards;
        if (!players.Skip(1).All(player => DecksMatch(first, player.Deck.Cards)))
        {
            ReconcileDecksFromAuthority(players);
            return;
        }
        for (int index = 0; index < first.Count; index++)
            RegisterGroup(CardGroups, players.Select(player => player.Deck.Cards[index]));
    }

    private static void RebindMatchingRelics(IReadOnlyList<Player> players)
    {
        Player authority = players[0];
        if (!players.Skip(1).All(player => RelicInventoriesMatch(authority, player)))
        {
            ReconcileRelicsFromAuthority(players);
            return;
        }
        RegisterRelicGroupsByOccurrence(players, authority);
    }

    private static void RebindMatchingPotions(IReadOnlyList<Player> players)
    {
        Player first = players[0];
        if (!players.Skip(1).All(player => PotionBeltsMatch(first, player)))
        {
            ReconcilePotionsFromAuthority(players);
            return;
        }
        for (int slot = 0; slot < first.MaxPotionCount; slot++)
        {
            List<PotionModel> group = players.Select(player => player.GetPotionAtSlotIndex(slot))
                .Where(potion => potion is not null)
                .Cast<PotionModel>()
                .ToList();
            if (group.Count == players.Count)
                RegisterGroup(PotionGroups, group);
        }
    }

    private static void ReconcileRelicsFromAuthority(
        IReadOnlyList<Player> players,
        Player? requestedAuthority = null,
        bool logRepair = true)
    {
        Player authority = requestedAuthority is not null && players.Contains(requestedAuthority)
            ? requestedAuthority
            : players[0];
        SynchronizeRelicCounts(players, authority, removeExtras: true);
        if (logRepair)
            Godot.GD.Print("[ShareEverything] Repaired a mismatched shared relic inventory from one authoritative player.");
    }

    private static bool RelicInventoriesMatch(Player left, Player right)
    {
        if (left.Relics.Count != right.Relics.Count)
            return false;
        var unused = right.Relics.ToList();
        foreach (RelicModel relic in left.Relics)
        {
            RelicModel? match = unused.FirstOrDefault(candidate => candidate.Id.Equals(relic.Id));
            if (match is null)
                return false;
            unused.Remove(match);
        }
        return unused.Count == 0;
    }

    private static void SynchronizeRelicCounts(
        IReadOnlyList<Player> players,
        Player authority,
        bool removeExtras)
    {
        IReadOnlyList<RelicModel> templates = authority.Relics.ToList();
        _mirrorDepth++;
        try
        {
            foreach (Player target in players.Where(player => !ReferenceEquals(player, authority)))
            {
                var unused = target.Relics.ToList();
                foreach (RelicModel template in templates)
                {
                    RelicModel? existing = unused.FirstOrDefault(candidate => candidate.Id.Equals(template.Id));
                    if (existing is not null)
                    {
                        unused.Remove(existing);
                        continue;
                    }

                    RelicModel clone = CloneRelic(template);
                    // Runtime relic mutations must update NRelicInventory as well
                    // as the Player model. A silent direct insertion leaves the UI
                    // one item shorter and makes the next vanilla Obtain index invalid.
                    target.AddRelicInternal(clone, -1, false);
                }

                if (!removeExtras)
                    continue;
                foreach (RelicModel extra in unused)
                {
                    target.RemoveRelicInternal(extra, false);
                    MarkConsumedSharedResource(extra);
                }
            }
        }
        finally
        {
            _mirrorDepth--;
        }

        RegisterRelicGroupsByOccurrence(players, authority);
    }

    private static void RegisterRelicGroupsByOccurrence(
        IReadOnlyList<Player> players,
        Player authority)
    {
        var seenById = new Dictionary<ModelId, int>();
        foreach (RelicModel template in authority.Relics)
        {
            int occurrence = seenById.TryGetValue(template.Id, out int seen) ? seen : 0;
            seenById[template.Id] = occurrence + 1;
            List<RelicModel> group = players.Select(player => player.Relics
                    .Where(relic => relic.Id.Equals(template.Id))
                    .ElementAtOrDefault(occurrence))
                .Where(relic => relic is not null)
                .Cast<RelicModel>()
                .ToList();
            if (group.Count == players.Count)
                RegisterGroup(RelicGroups, group);
        }
    }

    private static void ReconcileDecksFromAuthority(IReadOnlyList<Player> players)
    {
        Player authority = players[0];
        if (authority.RunState is not RunState state)
            return;
        RebuildDeck(players, state, authority.Deck.Cards.ToList());
        Godot.GD.Print("[ShareEverything] Repaired mismatched shared decks from the stable party authority.");
    }

    private static void ReconcilePotionsFromAuthority(IReadOnlyList<Player> players)
    {
        Player authority = players[0];
        int slotCount = authority.MaxPotionCount;
        List<PotionModel?> templates = authority.PotionSlots.ToList();
        var groupsBySlot = new Dictionary<int, List<PotionModel>>();
        for (int slot = 0; slot < slotCount; slot++)
        {
            PotionModel? potion = authority.GetPotionAtSlotIndex(slot);
            if (potion is not null)
                groupsBySlot[slot] = [potion];
        }

        _mirrorDepth++;
        try
        {
            foreach (Player target in players.Skip(1))
            {
                foreach (PotionModel potion in target.PotionSlots.OfType<PotionModel>().ToList())
                    target.DiscardPotionInternal(potion, true);
                SetMaxPotionCountMethod.Invoke(target, [slotCount]);
                for (int slot = 0; slot < templates.Count; slot++)
                {
                    PotionModel? template = templates[slot];
                    if (template is null)
                        continue;
                    PotionModel clone = ClonePotion(template);
                    PotionProcureResult result = target.AddPotionInternal(clone, slot, true);
                    if (result.success)
                        groupsBySlot[slot].Add(clone);
                }
            }
        }
        finally
        {
            _mirrorDepth--;
        }

        foreach (List<PotionModel> group in groupsBySlot.Values)
            RegisterGroup(PotionGroups, group);
        Godot.GD.Print("[ShareEverything] Repaired a mismatched shared potion belt from the stable party authority.");
    }

    private static void RestoreSharedPotionSlotCount(IReadOnlyList<Player> players)
    {
        if (!players.All(player => PotionContributions.TryGetValue(player, out _)))
            return;
        int sharedCount = players.Sum(player => PotionContributions.TryGetValue(player, out var contribution)
            ? contribution.Value : 0);
        _mirrorDepth++;
        try
        {
            foreach (Player player in players)
                if (player.MaxPotionCount != sharedCount)
                    SetMaxPotionCountMethod.Invoke(player, [sharedCount]);
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static void Initialize(RunState state, bool isNewRun)
    {
        IReadOnlyList<Player> players = state.Players.OrderBy(player => player.NetId).ToList();
        if (players.Count <= 1)
            return;

        _mirrorDepth++;
        try
        {
            if (ModConfig.ShareDeck)
                RunInventoryStep("initialize shared decks", () =>
                {
                    if (isNewRun)
                        InitializeNewRunDeck(players, state);
                    else
                        InitializeLoadedDeck(players, state);
                });
            if (ModConfig.ShareRelics)
                RunInventoryStep("initialize shared relics", () => InitializeRelics(players));
            if (ModConfig.SharePotions)
                RunInventoryStep("initialize shared potions", () => InitializePotions(players, isNewRun));
        }
        finally
        {
            _mirrorDepth--;
            ReadyInventories.Remove(state);
            ReadyInventories.Add(state, new InventoryReadyMarker());
        }
    }

    private static void RunInventoryStep(string description, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[ShareEverything] Failed to {description}: {ex}");
        }
    }

    private static void InitializeNewRunDeck(IReadOnlyList<Player> players, RunState state)
    {
        List<CardModel> templates = SelectSharedStarterCards(
            players.Select(player => (IReadOnlyList<CardModel>)player.Deck.Cards).ToList(),
            static card => !card.IsBasicStrikeOrDefend
                ? 2
                : card.Type == CardType.Attack ? 0 : 1);
        RebuildDeck(players, state, templates);
    }

    /// <summary>
    /// Selects three Strikes and three Defends from the first participant, followed
    /// by every participant's non-basic starter cards in participant order.
    /// Roles are 0=Strike, 1=Defend, 2=character-specific starter.
    /// </summary>
    internal static List<T> SelectSharedStarterCards<T>(
        IReadOnlyList<IReadOnlyList<T>> decks,
        Func<T, int> getRole)
    {
        if (decks.Count == 0)
            return [];

        List<T> allCards = decks.SelectMany(deck => deck).ToList();
        List<T> strikes = decks[0].Where(card => getRole(card) == 0).Take(3).ToList();
        List<T> defends = decks[0].Where(card => getRole(card) == 1).Take(3).ToList();
        if (strikes.Count < 3)
            strikes.AddRange(allCards.Where(card => getRole(card) == 0).Take(3 - strikes.Count));
        if (defends.Count < 3)
            defends.AddRange(allCards.Where(card => getRole(card) == 1).Take(3 - defends.Count));

        var result = new List<T>(6 + allCards.Count(card => getRole(card) == 2));
        result.AddRange(strikes);
        result.AddRange(defends);
        result.AddRange(allCards.Where(card => getRole(card) == 2));
        return result;
    }

    private static void InitializeLoadedDeck(IReadOnlyList<Player> players, RunState state)
    {
        IReadOnlyList<CardModel> first = players[0].Deck.Cards;
        bool alreadyShared = players.Skip(1).All(player => DecksMatch(first, player.Deck.Cards));
        if (alreadyShared)
        {
            for (int index = 0; index < first.Count; index++)
                RegisterGroup(CardGroups, players.Select(player => player.Deck.Cards[index]));
            return;
        }

        // Never concatenate multiple player decks here. A serialized shared deck
        // may temporarily differ while players are being synchronized; merging
        // those copies would multiply every card by the party size.
        RebuildDeck(players, state, first.ToList());
    }

    private static void RebuildDeck(
        IReadOnlyList<Player> players,
        RunState state,
        IReadOnlyList<CardModel> templates)
    {
        var clonedDecks = players.ToDictionary(
            player => player,
            player => templates.Select(CloneOwnerlessPersistentCard).ToList());

        foreach (Player player in players)
            foreach (CardModel card in player.Deck.Cards.ToList())
                card.RemoveFromState();

        for (int templateIndex = 0; templateIndex < templates.Count; templateIndex++)
        {
            var group = new List<CardModel>(players.Count);
            foreach (Player player in players)
            {
                CardModel clone = clonedDecks[player][templateIndex];
                state.AddCard(clone, player);
                player.Deck.AddInternal(clone, -1, true);
                group.Add(clone);
            }
            RegisterGroup(CardGroups, group);
        }
    }

    private static bool DecksMatch(IReadOnlyList<CardModel> left, IReadOnlyList<CardModel> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            CardModel a = left[index];
            CardModel b = right[index];
            if (!CardsMatch(a, b))
                return false;
        }
        return true;
    }

    private static bool NullableIdEquals(ModelId? left, ModelId? right)
        => Equals(left, right);

    private static bool CardsMatch(CardModel left, CardModel right)
        => left.Id.Equals(right.Id)
           && left.CurrentUpgradeLevel == right.CurrentUpgradeLevel
           && NullableIdEquals(left.Enchantment?.Id, right.Enchantment?.Id)
           && NullableIdEquals(left.Affliction?.Id, right.Affliction?.Id);

    private static void DeckCardAddedPostfix(CardPile __instance, CardModel __0, int __1, bool __2)
    {
        CardModel card = __0;
        bool silent = __2;
        if (_mirrorDepth > 0 || !ModConfig.ShareDeck || __instance.Type != PileType.Deck)
            return;
        Player? owner = card.Owner;
        if (!TryGetReadyParty(owner, out IReadOnlyList<Player> players))
            return;

        var group = new List<CardModel> { card };
        _mirrorDepth++;
        try
        {
            foreach (Player target in players)
            {
                if (ReferenceEquals(target, owner))
                    continue;
                CardModel? clone = null;
                try
                {
                    clone = CloneOwnerlessPersistentCard(card);
                    if (target.RunState is not RunState state)
                        continue;
                    state.AddCard(clone, target);
                    // AddInternal's non-negative index is a serialized pile index,
                    // not a generally safe List insertion point during multiplayer
                    // reward resolution. Shared decks already have identical order,
                    // so appending the same mutation to every deck is deterministic.
                    target.Deck.AddInternal(clone, -1, silent);
                    group.Add(clone);
                }
                catch (Exception ex)
                {
                    if (clone is not null && !clone.HasBeenRemovedFromState)
                    {
                        try
                        {
                            clone.RemoveFromState();
                        }
                        catch
                        {
                            // Preserve the original reward task even when cleanup
                            // of a partially inserted mirror also fails.
                        }
                    }
                    Godot.GD.PrintErr(
                        $"[ShareEverything] Failed to mirror deck card {card.Id} to {target.NetId}: {ex}");
                }
            }
            RegisterGroup(CardGroups, group);
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static bool DeckCardRemovingPrefix(CardPile __instance, CardModel __0)
    {
        CardModel card = __0;
        if (_mirrorDepth > 0 || !ModConfig.ShareDeck || __instance.Type != PileType.Deck)
            return true;
        if (!TryGetReadyParty(card.Owner, out _))
            return true;

        // A different player may already have paid this shared card cost. Treat
        // the stale repeat as an idempotent no-op instead of indexing a removed
        // card and faulting the event task.
        if (IndexOfReference(__instance.Cards, card) < 0 || card.HasBeenRemovedFromState)
            return false;

        IReadOnlyList<CardModel> counterparts = TryGetGroup(CardGroups, card, out SharedGroup<CardModel>? group)
            ? group!.Items
            : FindCardCounterparts(card, __instance);

        _mirrorDepth++;
        try
        {
            foreach (CardModel counterpart in counterparts)
            {
                if (ReferenceEquals(counterpart, card) || counterpart.Pile?.Type != PileType.Deck)
                    continue;
                counterpart.RemoveFromState();
            }
        }
        finally
        {
            _mirrorDepth--;
        }
        return true;
    }

    private static void DeckCardRemovedPostfix(CardPile __instance, CardModel __0)
    {
        if (ModConfig.ShareDeck && __instance.Type == PileType.Deck)
            MarkConsumedSharedResource(__0);
    }

    private static IReadOnlyList<CardModel> FindCardCounterparts(CardModel card, CardPile sourcePile)
    {
        Player? owner = card.Owner;
        if (!TryGetParty(owner, out IReadOnlyList<Player> players))
            return [card];
        int sourceIndex = IndexOfReference(sourcePile.Cards, card);
        if (sourceIndex < 0)
            return [card];

        var result = new List<CardModel> { card };
        foreach (Player player in players)
        {
            if (ReferenceEquals(player, owner) || sourceIndex >= player.Deck.Cards.Count)
                continue;
            CardModel candidate = player.Deck.Cards[sourceIndex];
            if (CardsMatch(card, candidate))
                result.Add(candidate);
        }
        return result;
    }

    /// <summary>
    /// Copies a permanent-deck card without CardModel.CreateCloneForPlayer,
    /// which is intentionally restricted to cards in combat piles.
    /// </summary>
    private static CardModel CloneOwnerlessPersistentCard(CardModel source)
    {
        CardModel clone = source.IsMutable
            ? (CardModel)source.MutableClone()
            : source.ToMutable();
        // MutableClone preserves the old owner. RunState.AddCard is the sole
        // authority that assigns the new owner, so clear the copied reference
        // before passing the card to it.
        CardOwnerField.SetValue(clone, null);
        return clone;
    }

    private static void CardUpgradedPostfix(CardModel __instance)
    {
        if (_mirrorDepth > 0 || !ModConfig.ShareDeck || __instance.Pile?.Type != PileType.Deck
            || !TryGetGroup(CardGroups, __instance, out SharedGroup<CardModel>? group))
            return;
        _mirrorDepth++;
        try
        {
            foreach (CardModel counterpart in group!.Items)
            {
                if (ReferenceEquals(counterpart, __instance)
                    || counterpart.CurrentUpgradeLevel >= __instance.CurrentUpgradeLevel)
                    continue;
                counterpart.UpgradeInternal();
                counterpart.FinalizeUpgradeInternal();
            }
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static void CardDowngradedPostfix(CardModel __instance)
    {
        if (_mirrorDepth > 0 || !ModConfig.ShareDeck || __instance.Pile?.Type != PileType.Deck
            || !TryGetGroup(CardGroups, __instance, out SharedGroup<CardModel>? group))
            return;
        _mirrorDepth++;
        try
        {
            foreach (CardModel counterpart in group!.Items)
                if (!ReferenceEquals(counterpart, __instance) && counterpart.CurrentUpgradeLevel != 0)
                    counterpart.DowngradeInternal();
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static void CardEnchantedPostfix(CardModel __instance)
    {
        if (!TryBeginCardMutation(__instance, out SharedGroup<CardModel>? group)
            || __instance.Enchantment is not EnchantmentModel source)
            return;
        try
        {
            foreach (CardModel counterpart in group!.Items)
            {
                if (ReferenceEquals(counterpart, __instance))
                    continue;
                counterpart.ClearEnchantmentInternal();
                EnchantmentModel canonical = source.CanonicalInstance
                                             ?? ModelDb.GetById<EnchantmentModel>(source.Id);
                counterpart.EnchantInternal(canonical.ToMutable(), source.Amount);
            }
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static void CardAfflictedPostfix(CardModel __instance)
    {
        if (!TryBeginCardMutation(__instance, out SharedGroup<CardModel>? group)
            || __instance.Affliction is not AfflictionModel source)
            return;
        try
        {
            foreach (CardModel counterpart in group!.Items)
            {
                if (ReferenceEquals(counterpart, __instance))
                    continue;
                counterpart.ClearAfflictionInternal();
                AfflictionModel canonical = source.CanonicalInstance
                                            ?? ModelDb.GetById<AfflictionModel>(source.Id);
                counterpart.AfflictInternal(canonical.ToMutable(), source.Amount);
            }
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static void CardEnchantmentClearedPostfix(CardModel __instance)
        => MirrorCardClear(__instance, static card => card.ClearEnchantmentInternal());

    private static void CardAfflictionClearedPostfix(CardModel __instance)
        => MirrorCardClear(__instance, static card => card.ClearAfflictionInternal());

    private static bool TryBeginCardMutation(
        CardModel card,
        out SharedGroup<CardModel>? group)
    {
        group = null;
        if (_mirrorDepth > 0 || !ModConfig.ShareDeck || card.Pile?.Type != PileType.Deck
            || !TryGetReadyParty(card.Owner, out _)
            || !TryGetGroup(CardGroups, card, out group))
            return false;
        _mirrorDepth++;
        return true;
    }

    private static void MirrorCardClear(CardModel card, Action<CardModel> clear)
    {
        if (!TryBeginCardMutation(card, out SharedGroup<CardModel>? group))
            return;
        try
        {
            foreach (CardModel counterpart in group!.Items)
                if (!ReferenceEquals(counterpart, card))
                    clear(counterpart);
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static void RelicObtainPrefix(Player __1, out RelicMutationState __state)
        => __state = CaptureRelicMutation(__1);

    private static bool RelicRemovePrefix(
        RelicModel __0,
        ref Task __result,
        out RelicMutationState __state)
    {
        RelicModel relic = __0;
        Player? owner = relic.Owner;
        __state = CaptureRelicMutation(owner);
        if (!__state.Enabled)
            return true;

        bool isPresent = !relic.HasBeenRemovedFromState
                         && owner is not null
                         && owner.Relics.Any(candidate => ReferenceEquals(candidate, relic));
        if (!isPresent)
        {
            __state = RelicMutationState.Disabled;
            __result = Task.CompletedTask;
            Godot.GD.Print($"[ShareEverything] Ignored a repeated removal of shared relic {relic.Id}.");
            return false;
        }
        return true;
    }

    private static RelicMutationState CaptureRelicMutation(Player? owner)
    {
        if (!ModConfig.ShareRelics || !TryGetReadyParty(owner, out _))
            return RelicMutationState.Disabled;
        return new RelicMutationState(owner, owner!.Relics.ToList(), true);
    }

    private static void RelicObtainedPostfix(
        ref Task<RelicModel> __result,
        RelicMutationState __state)
    {
        if (!__state.Enabled)
            return;
        __result = MirrorRelicObtainAsync(__result, __state);
    }

    private static async Task<RelicModel> MirrorRelicObtainAsync(
        Task<RelicModel> original,
        RelicMutationState state)
    {
        RelicModel relic = await original;
        try
        {
            if (state.Owner is not Player owner
                || !TryGetReadyParty(owner, out IReadOnlyList<Player> players))
                return relic;

            MirrorRelicDelta(players, state, owner.Relics.ToList());
        }
        catch (Exception ex)
        {
            // A mirror failure must never fault the vanilla reward/event task.
            Godot.GD.PrintErr($"[ShareEverything] Failed to mirror obtained relic {relic.Id}: {ex}");
        }
        return relic;
    }

    private static void RelicRemovedPostfix(
        RelicModel __0,
        ref Task __result,
        RelicMutationState __state)
    {
        if (!__state.Enabled)
            return;
        __result = MirrorRelicRemoveAsync(__result, __0, __state);
    }

    private static async Task MirrorRelicRemoveAsync(
        Task original,
        RelicModel relic,
        RelicMutationState state)
    {
        await original;
        MarkConsumedSharedResource(relic);
        try
        {
            if (state.Owner is Player owner
                && TryGetReadyParty(owner, out IReadOnlyList<Player> players))
                MirrorRelicDelta(players, state, owner.Relics.ToList());
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[ShareEverything] Failed to mirror relic removal {relic.Id}: {ex}");
        }
    }

    private static void MirrorRelicDelta(
        IReadOnlyList<Player> players,
        RelicMutationState state,
        IReadOnlyList<RelicModel> after)
    {
        if (state.Owner is not Player owner)
            return;

        List<RelicModel> removed = state.Before
            .Where(before => !after.Any(current => ReferenceEquals(before, current)))
            .ToList();
        _mirrorDepth++;
        try
        {
            foreach (RelicModel removedRelic in removed)
            {
                IReadOnlyList<RelicModel> counterparts = TryGetGroup(
                        RelicGroups, removedRelic, out SharedGroup<RelicModel>? group)
                    ? group!.Items
                    : FindRelicCounterpartsByOccurrence(players, owner, state.Before, removedRelic);
                foreach (RelicModel counterpart in counterparts)
                {
                    if (ReferenceEquals(counterpart, removedRelic)
                        || counterpart.HasBeenRemovedFromState
                        || !counterpart.Owner.Relics.Any(candidate => ReferenceEquals(candidate, counterpart)))
                        continue;
                    counterpart.Owner.RemoveRelicInternal(counterpart, false);
                    MarkConsumedSharedResource(counterpart);
                }
            }

            // Add-only reconciliation is deliberate. Another player's relic
            // command may be awaiting a choice at the same time; removing relics
            // absent from this command owner's snapshot would erase that in-flight
            // acquisition. Explicit removals above are the only removals mirrored.
            SynchronizeRelicCounts(players, owner, removeExtras: false);
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static IReadOnlyList<RelicModel> FindRelicCounterpartsByOccurrence(
        IReadOnlyList<Player> players,
        Player owner,
        IReadOnlyList<RelicModel> before,
        RelicModel relic)
    {
        int occurrence = before.TakeWhile(candidate => !ReferenceEquals(candidate, relic))
            .Count(candidate => candidate.Id.Equals(relic.Id));
        return players.Where(player => !ReferenceEquals(player, owner))
            .Select(player => player.Relics.Where(candidate => candidate.Id.Equals(relic.Id))
                .ElementAtOrDefault(occurrence))
            .Where(candidate => candidate is not null)
            .Cast<RelicModel>()
            .ToList();
    }

    private static void InitializeRelics(IReadOnlyList<Player> players)
    {
        List<RelicModel> templates = players
            .SelectMany(player => player.Relics)
            .GroupBy(relic => relic.Id)
            .Select(group => group.First())
            .ToList();

        var orderedByPlayer = new Dictionary<Player, List<RelicModel>>();
        foreach (Player player in players)
        {
            var ordered = new List<RelicModel>(templates.Count);
            foreach (RelicModel template in templates)
            {
                RelicModel relic = player.Relics.FirstOrDefault(candidate => candidate.Id.Equals(template.Id))
                                   ?? CloneRelic(template);
                if (!player.Relics.Contains(relic))
                    player.AddRelicInternal(relic, -1, true);
                ordered.Add(relic);
            }
            orderedByPlayer[player] = ordered;
            List<RelicModel> raw = (List<RelicModel>)RelicsField.GetValue(player)!;
            raw.Clear();
            raw.AddRange(ordered);
        }

        for (int index = 0; index < templates.Count; index++)
            RegisterGroup(RelicGroups, players.Select(player => orderedByPlayer[player][index]));
    }

    private static RelicModel CloneRelic(RelicModel source)
    {
        RelicModel canonical = source.CanonicalInstance ?? ModelDb.GetById<RelicModel>(source.Id);
        RelicModel clone = canonical.ToMutable();
        clone.FloorAddedToDeck = source.FloorAddedToDeck;
        return clone;
    }

    private static void InitializePotions(IReadOnlyList<Player> players, bool isNewRun)
    {
        bool alreadyShared = !isNewRun && players.Skip(1).All(player => PotionBeltsMatch(players[0], player));
        if (alreadyShared)
        {
            int total = players[0].MaxPotionCount;
            for (int index = 0; index < players.Count; index++)
                SetPotionContribution(players[index], total / players.Count + (index < total % players.Count ? 1 : 0));
            for (int slot = 0; slot < total; slot++)
            {
                List<PotionModel> group = players.Select(player => player.GetPotionAtSlotIndex(slot))
                    .Where(potion => potion is not null)
                    .ToList()!;
                if (group.Count == players.Count)
                    RegisterGroup(PotionGroups, group);
            }
            return;
        }

        var sourceSegments = players.Select(player => player.PotionSlots.ToList()).ToList();
        int sharedCount = sourceSegments.Sum(segment => segment.Count);
        for (int index = 0; index < players.Count; index++)
            SetPotionContribution(players[index], sourceSegments[index].Count);

        var templates = new List<PotionModel?>();
        foreach (List<PotionModel?> segment in sourceSegments)
            templates.AddRange(segment);

        var groupsBySlot = new Dictionary<int, List<PotionModel>>();
        foreach (Player player in players)
        {
            SetMaxPotionCountMethod.Invoke(player, [sharedCount]);
            List<PotionModel?> raw = (List<PotionModel?>)PotionSlotsField.GetValue(player)!;
            for (int slot = 0; slot < raw.Count; slot++)
                raw[slot] = null;

            for (int slot = 0; slot < templates.Count; slot++)
            {
                PotionModel? template = templates[slot];
                if (template is null)
                    continue;
                PotionModel clone = ClonePotion(template);
                PotionProcureResult result = player.AddPotionInternal(clone, slot, true);
                if (!result.success)
                    continue;
                if (!groupsBySlot.TryGetValue(slot, out List<PotionModel>? group))
                {
                    group = [];
                    groupsBySlot.Add(slot, group);
                }
                group.Add(clone);
            }
        }
        foreach (List<PotionModel> group in groupsBySlot.Values)
            RegisterGroup(PotionGroups, group);
    }

    private static bool PotionBeltsMatch(Player left, Player right)
    {
        if (left.MaxPotionCount != right.MaxPotionCount)
            return false;
        for (int slot = 0; slot < left.MaxPotionCount; slot++)
        {
            PotionModel? a = left.GetPotionAtSlotIndex(slot);
            PotionModel? b = right.GetPotionAtSlotIndex(slot);
            if (a is null != (b is null) || a is not null && !a.Id.Equals(b!.Id))
                return false;
        }
        return true;
    }

    private static bool MaxPotionCountPrefix(Player __instance, int __0)
    {
        if (_mirrorDepth > 0 || !ModConfig.SharePotions
            || !PotionContributions.TryGetValue(__instance, out PotionSlotContribution? contribution)
            || !TryGetReadyParty(__instance, out IReadOnlyList<Player> players))
            return true;

        int previousContribution = contribution.Value;
        try
        {
            int currentShared = __instance.MaxPotionCount;
            contribution.Value = Math.Max(0, contribution.Value + __0 - currentShared);
            int newShared = players.Sum(player => PotionContributions.TryGetValue(player, out var item)
                ? item.Value : 0);
            _mirrorDepth++;
            foreach (Player player in players)
                SetMaxPotionCountMethod.Invoke(player, [newShared]);
            return false;
        }
        catch (Exception ex)
        {
            contribution.Value = previousContribution;
            Godot.GD.PrintErr($"[ShareEverything] Failed to mirror potion-slot count: {ex}");
            return true;
        }
        finally
        {
            _mirrorDepth = Math.Max(0, _mirrorDepth - 1);
        }
    }

    private static void PotionAddedPostfix(
        Player __instance,
        PotionModel __0,
        int __1,
        bool __2,
        PotionProcureResult __result)
    {
        PotionModel potion = __0;
        bool silent = __2;
        if (_mirrorDepth > 0 || !ModConfig.SharePotions || !__result.success
            || !TryGetReadyParty(__instance, out IReadOnlyList<Player> players))
            return;
        int actualSlot = __instance.GetPotionSlotIndex(potion);
        var group = new List<PotionModel> { potion };
        _mirrorDepth++;
        try
        {
            foreach (Player target in players)
            {
                if (ReferenceEquals(target, __instance))
                    continue;
                PotionModel clone = ClonePotion(potion);
                PotionProcureResult mirrored = target.AddPotionInternal(clone, actualSlot, silent);
                if (mirrored.success)
                    group.Add(clone);
                else
                    Godot.GD.PrintErr(
                        $"[ShareEverything] Could not mirror potion {potion.Id} into slot {actualSlot} for {target.NetId}.");
            }
            RegisterGroup(PotionGroups, group);
        }
        catch (Exception ex)
        {
            // The original potion procurement already succeeded. Never fault its
            // reward/event task because a mirrored belt needs repair.
            Godot.GD.PrintErr($"[ShareEverything] Failed to mirror obtained potion {potion.Id}: {ex}");
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static bool PotionDiscardingPrefix(Player __instance, PotionModel __0, bool __1)
    {
        if (ShouldSkipStalePotionRemoval(__instance, __0))
            return false;
        MirrorPotionRemoval(__instance, __0, (player, counterpart) => player.DiscardPotionInternal(counterpart, __1));
        return true;
    }

    private static bool UsedPotionRemovingPrefix(Player __instance, PotionModel __0)
    {
        if (ShouldSkipStalePotionRemoval(__instance, __0))
            return false;
        MirrorPotionRemoval(__instance, __0, static (player, counterpart) => player.RemoveUsedPotionInternal(counterpart));
        return true;
    }

    private static void PotionRemovedPostfix(PotionModel __0)
    {
        if (!ModConfig.SharePotions)
            return;
        MarkConsumedSharedResource(__0);
    }

    private static bool ShouldSkipStalePotionRemoval(Player owner, PotionModel potion)
    {
        if (_mirrorDepth > 0 || !ModConfig.SharePotions)
            return false;
        if (!TryGetReadyParty(owner, out _))
            return false;
        if (!potion.HasBeenRemovedFromState && owner.GetPotionSlotIndex(potion) >= 0)
            return false;

        Godot.GD.Print($"[ShareEverything] Ignored a repeated removal of shared potion {potion.Id}.");
        return true;
    }

    private static void MirrorPotionRemoval(
        Player owner,
        PotionModel potion,
        Action<Player, PotionModel> remove)
    {
        if (_mirrorDepth > 0 || !ModConfig.SharePotions
            || !TryGetReadyParty(owner, out _)
            || !TryGetGroup(PotionGroups, potion, out SharedGroup<PotionModel>? group))
            return;
        _mirrorDepth++;
        try
        {
            foreach (PotionModel counterpart in group!.Items)
            {
                if (ReferenceEquals(counterpart, potion) || counterpart.HasBeenRemovedFromState)
                    continue;
                Player target = counterpart.Owner;
                if (target.GetPotionSlotIndex(counterpart) >= 0)
                {
                    remove(target, counterpart);
                    MarkConsumedSharedResource(counterpart);
                }
            }
        }
        catch (Exception ex)
        {
            // This is a prefix: swallowing the mirror failure lets the requested
            // vanilla removal continue on its original owner.
            Godot.GD.PrintErr($"[ShareEverything] Failed to mirror potion removal {potion.Id}: {ex}");
        }
        finally
        {
            _mirrorDepth--;
        }
    }

    private static PotionModel ClonePotion(PotionModel source)
    {
        PotionModel canonical = source.CanonicalInstance ?? ModelDb.GetById<PotionModel>(source.Id);
        return canonical.ToMutable();
    }

    private static void TopBarDeckProcessPostfix(NTopBarDeckButton __instance)
    {
        if (!ModConfig.ShareDeck)
            return;
        try
        {
            if (TopBarDeckPileField.GetValue(__instance) is CardPile pile
                && TopBarDeckCountField.GetValue(__instance) is float displayed
                && Math.Abs(displayed - pile.Cards.Count) > 0.01f)
                TopBarDeckRefreshMethod.Invoke(__instance, null);
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[ShareEverything] Failed to refresh the shared deck header count: {ex}");
        }
    }

    private static void RelicInventoryObtainedPostfix(
        NRelicInventory __instance,
        RelicModel __0)
    {
        if (_mirrorDepth <= 0 || !ModConfig.ShareRelics)
            return;
        try
        {
            NRelicInventoryHolder? holder = __instance.RelicNodes.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Relic.Model, __0));
            if (holder is not null)
                RelicHolderRefreshStatusMethod.Invoke(holder, null);
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[ShareEverything] Failed to reveal mirrored relic {__0.Id}: {ex}");
        }
    }

    private static void CardGridProcessPostfix(NCardGrid __instance)
    {
        if (!ModConfig.ShareDeck || __instance.IsAnimatingOut)
            return;

        NCardGridSelectionScreen? screen = FindSelectionScreen(__instance);
        if (screen is null)
            return;
        try
        {
            if (SelectionCardsField.GetValue(screen) is not IReadOnlyList<CardModel> source)
                return;
            List<CardModel> latest = source.Where(card =>
                    !card.HasBeenRemovedFromState && card.Pile?.Type == PileType.Deck)
                .ToList();
            if (screen is NDeckUpgradeSelectScreen)
                latest.RemoveAll(card => card.CurrentUpgradeLevel >= card.MaxUpgradeLevel);

            SelectionRefreshState state = SelectionRefreshStates.GetValue(
                __instance, static _ => new SelectionRefreshState());
            if (state.Matches(latest))
                return;

            CancelInvalidSelection(screen, latest);
            SelectionCardsField.SetValue(screen, latest);
            __instance.SetCards(
                latest,
                PileType.Deck,
                [SortingOrders.Ascending],
                Task.CompletedTask);
            state.Capture(latest);
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[ShareEverything] Failed to refresh an open shared-deck selection screen: {ex}");
        }
    }

    private static NCardGridSelectionScreen? FindSelectionScreen(Node node)
    {
        Node? current = node.GetParent();
        while (current is not null)
        {
            if (current is NCardGridSelectionScreen screen)
                return screen;
            current = current.GetParent();
        }
        return null;
    }

    private static void CancelInvalidSelection(
        NCardGridSelectionScreen screen,
        IReadOnlyList<CardModel> latest)
    {
        FieldInfo selectedField;
        MethodInfo cancelMethod;
        if (screen is NDeckCardSelectScreen)
        {
            selectedField = DeckSelectionSelectedCardsField;
            cancelMethod = DeckSelectionCancelMethod;
        }
        else if (screen is NDeckUpgradeSelectScreen)
        {
            selectedField = UpgradeSelectionSelectedCardsField;
            cancelMethod = UpgradeSelectionCancelMethod;
        }
        else
        {
            return;
        }

        if (selectedField.GetValue(screen) is not HashSet<CardModel> selected
            || !selected.Any(card => !latest.Any(candidate => ReferenceEquals(candidate, card))))
            return;
        try
        {
            cancelMethod.Invoke(screen, [null]);
        }
        catch
        {
            // The preview may be closing on this same frame. Clearing the stale
            // references is sufficient for the refreshed grid to remain usable.
            selected.Clear();
        }
    }

    private static bool EventOptionButtonReleasePrefix(NEventOptionButton __instance)
    {
        if (!OptionReferencesUnavailableSharedResource(__instance.Option))
            return true;
        MegaCrit.Sts2.Core.Models.EventModel eventModel = __instance.Event;
        EventOnChosenField.SetValue(__instance.Option, (Func<Task>)(() =>
        {
            EventSetFinishedMethod.Invoke(eventModel, [eventModel.Description]);
            return Task.CompletedTask;
        }));
        Godot.GD.Print("[ShareEverything] Let an already-consumed event choice finish as a no-op.");
        return true;
    }

    private static bool OptionReferencesUnavailableSharedResource(
        MegaCrit.Sts2.Core.Events.EventOption? option)
    {
        if (option is null || EventOnChosenField.GetValue(option) is not Delegate onChosen)
            return false;
        return ReferencedCapturedResources(onChosen)
            .Any(IsUnavailableSharedResource);
    }

    private static IEnumerable<object> ReferencedCapturedResources(Delegate onChosen)
    {
        if (onChosen.Target is not object closure)
            yield break;

        var referencedFields = new HashSet<FieldInfo>();
        foreach (MethodBase implementation in DelegateImplementations(onChosen.Method))
        {
            try
            {
                foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(implementation))
                    if (instruction.operand is FieldInfo field
                        && (typeof(CardModel).IsAssignableFrom(field.FieldType)
                            || typeof(RelicModel).IsAssignableFrom(field.FieldType)
                            || typeof(PotionModel).IsAssignableFrom(field.FieldType)))
                        referencedFields.Add(field);
            }
            catch
            {
                continue;
            }
        }

        List<object> closures = ReachableCompilerClosures(closure).ToList();
        foreach (FieldInfo field in referencedFields)
        {
            object? owner = closures.FirstOrDefault(candidate => field.DeclaringType?.IsInstanceOfType(candidate) == true);
            if (owner is null)
                continue;
            object? resource = null;
            try
            {
                resource = field.GetValue(owner);
            }
            catch
            {
                // A compiler-generated closure may have been invalidated as the
                // event page changed. It cannot identify a stale current cost.
            }
            if (resource is CardModel or RelicModel or PotionModel)
                yield return resource;
        }
    }

    private static IEnumerable<MethodBase> DelegateImplementations(MethodInfo method)
    {
        yield return method;
        Type? stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        MethodInfo? moveNext = stateMachine?.GetMethod(
            "MoveNext", BindingFlags.Instance | BindingFlags.NonPublic);
        if (moveNext is not null)
            yield return moveNext;
    }

    private static IEnumerable<object> ReachableCompilerClosures(object root)
    {
        var queue = new Queue<(object Value, int Depth)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            (object value, int depth) = queue.Dequeue();
            if (!visited.Add(value))
                continue;
            yield return value;
            if (depth >= 3)
                continue;
            foreach (FieldInfo field in value.GetType().GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? nested;
                try
                {
                    nested = field.GetValue(value);
                }
                catch
                {
                    continue;
                }
                if (nested is null)
                    continue;
                Type type = nested.GetType();
                if (type.IsNestedPrivate
                    && (type.Name.Contains("DisplayClass", StringComparison.Ordinal)
                        || type.Name.StartsWith("<>", StringComparison.Ordinal)))
                    queue.Enqueue((nested, depth + 1));
            }
        }
    }

    private static bool IsUnavailableSharedResource(object resource)
        => ConsumedSharedResources.TryGetValue(resource, out _);

    private static void MarkConsumedSharedResource(object resource)
    {
        ConsumedSharedResources.Remove(resource);
        ConsumedSharedResources.Add(resource, new ConsumedSharedResourceMarker());
    }

    private static void SetPotionContribution(Player player, int value)
    {
        PotionContributions.Remove(player);
        PotionContributions.Add(player, new PotionSlotContribution(value));
    }

    private static bool TryGetParty(Player? player, out IReadOnlyList<Player> players)
    {
        // Starter cards are inserted while Player.CreateForNewRun is still
        // constructing the player. At that point CardModel.Owner (and later the
        // player's RunState) can legitimately be null. The completed RunManager
        // initialization postfix will build the shared starter deck instead.
        players = player?.RunState is { } runState
            ? runState.Players.OrderBy(candidate => candidate.NetId).ToList()
            : Array.Empty<Player>();
        return players.Count > 1;
    }

    private static bool TryGetReadyParty(Player? player, out IReadOnlyList<Player> players)
    {
        if (!TryGetParty(player, out players) || player?.RunState is not { } runState)
            return false;
        return ReadyInventories.TryGetValue(runState, out _);
    }

    private static int IndexOfReference<T>(IReadOnlyList<T> list, T value) where T : class
    {
        for (int index = 0; index < list.Count; index++)
            if (ReferenceEquals(list[index], value))
                return index;
        return -1;
    }

    private static void RegisterGroup<T>(
        ConditionalWeakTable<T, SharedGroup<T>> table,
        IEnumerable<T> items) where T : class
    {
        var distinct = new List<T>();
        foreach (T item in items)
            if (!distinct.Any(existing => ReferenceEquals(existing, item)))
                distinct.Add(item);
        var group = new SharedGroup<T>(distinct);
        foreach (T item in group.Items)
        {
            table.Remove(item);
            table.Add(item, group);
        }
    }

    private static bool TryGetGroup<T>(
        ConditionalWeakTable<T, SharedGroup<T>> table,
        T item,
        out SharedGroup<T>? group) where T : class
        => table.TryGetValue(item, out group);

    private sealed record SharedGroup<T>(IReadOnlyList<T> Items) where T : class;

    private sealed class PotionSlotContribution(int value)
    {
        public int Value { get; set; } = value;
    }

    private sealed class InventoryReadyMarker
    {
    }

    private sealed class ConsumedSharedResourceMarker
    {
    }

    private sealed class SelectionRefreshState
    {
        private List<SelectionCardState>? _cards;

        public bool Matches(IReadOnlyList<CardModel> cards)
        {
            if (_cards is null || _cards.Count != cards.Count)
                return false;
            for (int index = 0; index < cards.Count; index++)
            {
                SelectionCardState previous = _cards[index];
                CardModel current = cards[index];
                if (!ReferenceEquals(previous.Card, current)
                    || previous.UpgradeLevel != current.CurrentUpgradeLevel
                    || previous.MaxUpgradeLevel != current.MaxUpgradeLevel)
                    return false;
            }
            return true;
        }

        public void Capture(IReadOnlyList<CardModel> cards)
            => _cards = cards.Select(card => new SelectionCardState(
                card, card.CurrentUpgradeLevel, card.MaxUpgradeLevel)).ToList();
    }

    private sealed record SelectionCardState(
        CardModel Card,
        int UpgradeLevel,
        int MaxUpgradeLevel);

    private sealed record RelicMutationState(
        Player? Owner,
        IReadOnlyList<RelicModel> Before,
        bool Enabled)
    {
        public static RelicMutationState Disabled { get; } = new(null, [], false);
    }
}
