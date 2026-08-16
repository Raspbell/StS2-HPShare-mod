using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using HPShare;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Vfx;

try
{
    HPShareMod.Initialize();
    Console.WriteLine("PATCH_APPLICATION_OK");

    MethodInfo goldGetter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.Gold));
    MethodInfo goldSetter = AccessTools.PropertySetter(typeof(Player), nameof(Player.Gold));
    MethodInfo directPowerApply = AccessTools.GetDeclaredMethods(typeof(PowerCmd)).Single(method =>
        method.Name == nameof(PowerCmd.Apply)
        && !method.IsGenericMethod
        && method.GetParameters().Length == 7);
    MethodInfo modifyPowerAmount = AccessTools.Method(typeof(PowerCmd), nameof(PowerCmd.ModifyAmount));
    MethodInfo playerToSerializable = AccessTools.Method(typeof(Player), "ToSerializable");
    foreach (MethodInfo method in new[]
             { goldGetter, goldSetter, playerToSerializable, directPowerApply, modifyPowerAmount })
    {
        if (Harmony.GetPatchInfo(method)?.Owners.Contains(HPShareMod.HarmonyId) != true)
            throw new InvalidOperationException($"Optional sharing patch is missing: {method}");
    }
    Console.WriteLine("OPTIONAL_SHARING_PATCHES_OK");

    Type playerCombatState = typeof(Player).Assembly.GetType(
        "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState", throwOnError: true)!;
    Type cardPile = typeof(Player).Assembly.GetType(
        "MegaCrit.Sts2.Core.Entities.Cards.CardPile", throwOnError: true)!;
    Type runManager = typeof(Player).Assembly.GetType(
        "MegaCrit.Sts2.Core.Runs.RunManager", throwOnError: true)!;
    Type relicCmd = typeof(Player).Assembly.GetType(
        "MegaCrit.Sts2.Core.Commands.RelicCmd", throwOnError: true)!;
    Type potionModel = typeof(Player).Assembly.GetType(
        "MegaCrit.Sts2.Core.Models.PotionModel", throwOnError: true)!;
    MethodBase[] expandedSharingMethods =
    [
        AccessTools.PropertyGetter(playerCombatState, "Energy"),
        AccessTools.PropertySetter(playerCombatState, "Energy"),
        AccessTools.PropertyGetter(playerCombatState, "MaxEnergy"),
        AccessTools.PropertyGetter(typeof(Player), nameof(Player.MaxEnergy)),
        AccessTools.PropertySetter(typeof(Player), nameof(Player.MaxEnergy)),
        AccessTools.Method(cardPile, "AddInternal"),
        AccessTools.Method(cardPile, "RemoveInternal"),
        AccessTools.Method(runManager, "InitializeNewRun"),
        AccessTools.Method(runManager, "InitializeSavedRun"),
        AccessTools.Method(typeof(Player), nameof(Player.SyncWithSerializedPlayer)),
        AccessTools.GetDeclaredMethods(relicCmd).Single(method => method.Name == "Obtain" && !method.IsGenericMethod),
        AccessTools.Method(relicCmd, "Remove"),
        AccessTools.Method(relicCmd, "Melt"),
        AccessTools.Method(typeof(Player), "SetMaxPotionCountInternal"),
        AccessTools.Method(typeof(Player), nameof(Player.AddPotionInternal)),
        AccessTools.Method(typeof(Player), nameof(Player.DiscardPotionInternal)),
        AccessTools.Method(typeof(Player), nameof(Player.RemoveUsedPotionInternal)),
    ];
    foreach (MethodBase method in expandedSharingMethods)
    {
        if (Harmony.GetPatchInfo(method)?.Owners.Contains(HPShareMod.HarmonyId) != true)
            throw new InvalidOperationException($"Expanded sharing patch is missing: {method}");
    }
    Console.WriteLine("EXPANDED_SHARING_PATCHES_OK");

    static List<CodeInstruction> ImplementationInstructions(MethodInfo method)
    {
        MethodInfo implementation = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                                        .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)
                                    ?? method;
        return PatchProcessor.GetOriginalInstructions(implementation);
    }

    bool potionProcurementReachesPatchedEndpoint = AccessTools.GetDeclaredMethods(typeof(PotionCmd))
        .Where(method => method.Name == nameof(PotionCmd.TryToProcure) && method.GetMethodBody() is not null)
        .SelectMany(ImplementationInstructions)
        .Any(instruction => instruction.operand is MethodInfo called
                            && called.Name == nameof(Player.AddPotionInternal));
    bool deckAdditionReachesPatchedEndpoint = AccessTools.GetDeclaredMethods(typeof(CardPileCmd))
        .Where(method => method.Name == nameof(CardPileCmd.Add) && method.GetMethodBody() is not null)
        .SelectMany(ImplementationInstructions)
        .Any(instruction => instruction.operand is MethodInfo called
                            && called.Name == nameof(CardPile.AddInternal));
    bool deckRemovalReachesPatchedEndpoint = AccessTools.GetDeclaredMethods(typeof(CardPileCmd))
        .Where(method => method.Name == nameof(CardPileCmd.RemoveFromDeck) && method.GetMethodBody() is not null)
        .SelectMany(ImplementationInstructions)
        .Any(instruction => instruction.operand is MethodInfo called
                            && called.Name is nameof(CardModel.RemoveFromState) or nameof(CardPile.RemoveInternal));
    if (!potionProcurementReachesPatchedEndpoint
        || !deckAdditionReachesPatchedEndpoint
        || !deckRemovalReachesPatchedEndpoint)
    {
        throw new InvalidOperationException(
            $"A chained inventory endpoint bypasses sharing: potion={potionProcurementReachesPatchedEndpoint}, "
            + $"deckAdd={deckAdditionReachesPatchedEndpoint}, deckRemove={deckRemovalReachesPatchedEndpoint}");
    }
    Console.WriteLine("CHAINED_INVENTORY_ENDPOINTS_OK");

    Assembly chainAssembly = typeof(CardModel).Assembly;
    bool ModelMethodCalls(string typeName, string methodName, Type commandType, string commandName)
    {
        Type modelType = chainAssembly.GetTypes().Single(type => type.Name == typeName);
        MethodInfo method = modelType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly)!;
        return ImplementationInstructions(method).Any(instruction =>
            instruction.operand is MethodInfo called
            && called.DeclaringType == commandType
            && called.Name == commandName);
    }

    if (!ModelMethodCalls("EntropicBrew", "OnUse", typeof(PotionCmd), nameof(PotionCmd.TryToProcure))
        || !ModelMethodCalls("AlchemicalCoffer", "AfterObtained", typeof(PotionCmd), nameof(PotionCmd.TryToProcure))
        || !ModelMethodCalls("LargeCapsule", "AfterObtained", typeof(RelicCmd), nameof(RelicCmd.Obtain))
        || !ModelMethodCalls("PrecariousShears", "AfterObtained", typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromDeck)))
    {
        throw new InvalidOperationException("A known chained inventory effect changed its command path.");
    }
    Console.WriteLine("KNOWN_CHAINED_EFFECT_PATHS_OK");

    Type sharedInventory = typeof(HPShareMod).Assembly.GetType(
        "HPShare.SharedInventoryPatches", throwOnError: true)!;
    FieldInfo inventoryMirrorDepth = sharedInventory.GetField(
        "MirrorDepth", BindingFlags.Static | BindingFlags.NonPublic)!;
    if (inventoryMirrorDepth.FieldType != typeof(AsyncLocal<int>))
        throw new InvalidOperationException("Inventory mirror depth is not isolated per async flow.");

    MethodInfo relicObtainMethod = AccessTools.GetDeclaredMethods(relicCmd)
        .Single(method => method.Name == "Obtain" && !method.IsGenericMethod);
    Patches relicPatches = Harmony.GetPatchInfo(relicObtainMethod)!;
    if (!relicPatches.Prefixes.Any(patch => patch.owner == HPShareMod.HarmonyId)
        || !relicPatches.Postfixes.Any(patch => patch.owner == HPShareMod.HarmonyId)
        || relicPatches.Finalizers.Any(patch => patch.owner == HPShareMod.HarmonyId))
    {
        throw new InvalidOperationException("Relic delta synchronization patch is incomplete.");
    }
    Console.WriteLine("RELIC_DELTA_PATCH_OK");

    MethodInfo mirrorRelicObtain = sharedInventory.GetMethod(
        "MirrorRelicObtainAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
    MethodInfo mirrorRelicMoveNext = mirrorRelicObtain
        .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType
        .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
    if (PatchProcessor.GetOriginalInstructions(mirrorRelicMoveNext)
        .Any(instruction => instruction.operand is MethodInfo called
                            && called.DeclaringType == relicCmd
                            && called.Name == "Obtain"))
    {
        throw new InvalidOperationException(
            "Mirrored relics still re-run acquisition effects on every player.");
    }
    Console.WriteLine("DERIVED_RELIC_SINGLE_RESOLUTION_GUARD_OK");

    MethodInfo synchronizeRelicCounts = sharedInventory.GetMethod(
        "SynchronizeRelicCounts", BindingFlags.Static | BindingFlags.NonPublic)!;
    List<CodeInstruction> relicSyncInstructions = PatchProcessor.GetOriginalInstructions(synchronizeRelicCounts);
    if (!relicSyncInstructions.Any(instruction => instruction.operand is MethodInfo called
            && called.Name == nameof(Player.AddRelicInternal))
        || relicSyncInstructions.Any(instruction => instruction.operand is FieldInfo field
            && field.Name == "RelicsField"))
    {
        throw new InvalidOperationException(
            "Runtime relic synchronization does not update the relic UI safely.");
    }
    MethodInfo mirrorRelicDelta = sharedInventory.GetMethod(
        "MirrorRelicDelta", BindingFlags.Static | BindingFlags.NonPublic)!;
    if (PatchProcessor.GetOriginalInstructions(mirrorRelicDelta)
        .Any(instruction => instruction.operand is MethodInfo called
                            && called.DeclaringType == relicCmd
                            && called.Name is "Obtain" or "Remove" or "Melt"))
    {
        throw new InvalidOperationException(
            "Mirrored relic deltas still re-run chained relic command effects.");
    }
    Console.WriteLine("RELIC_UI_AND_CHAIN_DELTA_GUARD_OK");

    MethodInfo isUnavailableSharedResource = sharedInventory.GetMethod(
        "IsUnavailableSharedResource", BindingFlags.Static | BindingFlags.NonPublic)!;
    MethodInfo markConsumedSharedResource = sharedInventory.GetMethod(
        "MarkConsumedSharedResource", BindingFlags.Static | BindingFlags.NonPublic)!;
    var pendingReward = new object();
    if ((bool)isUnavailableSharedResource.Invoke(null, [pendingReward])!)
        throw new InvalidOperationException("An unconsumed event reward was incorrectly grayed out.");
    markConsumedSharedResource.Invoke(null, [pendingReward]);
    if (!(bool)isUnavailableSharedResource.Invoke(null, [pendingReward])!)
        throw new InvalidOperationException("A consumed shared event cost was not recorded.");
    Console.WriteLine("EVENT_COST_EXPLICIT_LEDGER_GUARD_OK");

    Type concreteRelicType = chainAssembly.GetTypes().First(type =>
        typeof(RelicModel).IsAssignableFrom(type) && !type.IsAbstract);
    var referencedRelic = (RelicModel)RuntimeHelpers.GetUninitializedObject(concreteRelicType);
    var unrelatedRelic = (RelicModel)RuntimeHelpers.GetUninitializedObject(concreteRelicType);
    Func<Task> referencesOnlyOneCost = async () =>
    {
        await Task.Yield();
        GC.KeepAlive(referencedRelic);
    };
    GC.KeepAlive(unrelatedRelic);
    MethodInfo referencedCapturedResources = sharedInventory.GetMethod(
        "ReferencedCapturedResources", BindingFlags.Static | BindingFlags.NonPublic)!;
    var detectedCosts = ((IEnumerable<object>)referencedCapturedResources.Invoke(
        null, [referencesOnlyOneCost])!).ToList();
    if (!detectedCosts.Any(item => ReferenceEquals(item, referencedRelic))
        || detectedCosts.Any(item => ReferenceEquals(item, unrelatedRelic)))
    {
        throw new InvalidOperationException(
            "Event stale-cost detection cannot distinguish two options sharing one compiler closure.");
    }
    Console.WriteLine("EVENT_COST_FIELD_REFERENCE_GUARD_OK");

    Type topBarDeckButton = chainAssembly.GetTypes().Single(type => type.Name == "NTopBarDeckButton");
    Type relicInventory = chainAssembly.GetTypes().Single(type => type.Name == "NRelicInventory");
    Type cardGrid = chainAssembly.GetTypes().Single(type => type.Name == "NCardGrid");
    foreach (MethodInfo method in new[]
             {
                 AccessTools.Method(topBarDeckButton, "_Process"),
                 AccessTools.Method(relicInventory, "OnRelicObtained"),
                 AccessTools.Method(cardGrid, "_Process")
             })
    {
        if (Harmony.GetPatchInfo(method)?.Owners.Contains(HPShareMod.HarmonyId) != true)
            throw new InvalidOperationException($"Inventory UI refresh patch is missing: {method}");
    }

    MethodInfo topBarRefresh = sharedInventory.GetMethod(
        "TopBarDeckProcessPostfix", BindingFlags.Static | BindingFlags.NonPublic)!;
    if (!PatchProcessor.GetOriginalInstructions(topBarRefresh)
            .Any(instruction => instruction.operand is FieldInfo field && field.Name == "TopBarDeckRefreshMethod"))
        throw new InvalidOperationException("Shared deck header count is not actively refreshed.");
    MethodInfo relicReveal = sharedInventory.GetMethod(
        "RelicInventoryObtainedPostfix", BindingFlags.Static | BindingFlags.NonPublic)!;
    if (!PatchProcessor.GetOriginalInstructions(relicReveal)
            .Any(instruction => instruction.operand is FieldInfo field && field.Name == "RelicHolderRefreshStatusMethod"))
        throw new InvalidOperationException("Mirrored relic icons are not restored to their visible status color.");
    MethodInfo selectionRefresh = sharedInventory.GetMethod(
        "CardGridProcessPostfix", BindingFlags.Static | BindingFlags.NonPublic)!;
    List<CodeInstruction> selectionRefreshInstructions = PatchProcessor.GetOriginalInstructions(selectionRefresh);
    if (!selectionRefreshInstructions.Any(instruction => instruction.operand is MethodInfo called
            && called.Name == "SetCards")
        || !selectionRefreshInstructions.Any(instruction => instruction.operand is FieldInfo field
            && field.Name == "SelectionCardsField"))
    {
        throw new InvalidOperationException(
            "Open smith/removal card grids do not continuously reconcile with the shared deck.");
    }
    Console.WriteLine("INVENTORY_UI_LIVE_REFRESH_GUARD_OK");

    MethodInfo eventReleasePrefix = sharedInventory.GetMethod(
        "EventOptionButtonReleasePrefix", BindingFlags.Static | BindingFlags.NonPublic)!;
    List<CodeInstruction> eventReleaseInstructions = PatchProcessor.GetOriginalInstructions(eventReleasePrefix);
    if (eventReleaseInstructions.Any(instruction => instruction.operand is MethodInfo called
            && called.Name == "GrayOut")
        || !eventReleaseInstructions.Any(instruction => instruction.operand is FieldInfo field
            && field.Name == "EventOnChosenField"))
    {
        throw new InvalidOperationException(
            "Stale shared event costs still gray/block choices instead of replacing only their effect with a no-op.");
    }
    bool staleChoiceFinishesEvent = sharedInventory.GetNestedTypes(BindingFlags.NonPublic)
        .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
        .Where(method => method.Name.Contains("EventOptionButtonReleasePrefix", StringComparison.Ordinal)
                         && method.GetMethodBody() is not null)
        .SelectMany(method => PatchProcessor.GetOriginalInstructions(method))
        .Any(instruction => instruction.operand is FieldInfo field && field.Name == "EventSetFinishedMethod");
    Type eventOptionButton = chainAssembly.GetTypes().Single(type => type.Name == "NEventOptionButton");
    MethodInfo eventOptionCreate = AccessTools.Method(eventOptionButton, "Create");
    if (!staleChoiceFinishesEvent
        || Harmony.GetPatchInfo(eventOptionCreate)?.Owners.Contains(HPShareMod.HarmonyId) == true)
    {
        throw new InvalidOperationException(
            "A stale-cost event choice does not finish cleanly, or event creation still grays options.");
    }
    Console.WriteLine("EVENT_STALE_COST_NO_OP_GUARD_OK");

    Type modConfig = typeof(HPShareMod).Assembly.GetType("HPShare.ModConfig", throwOnError: true)!;
    MethodInfo setShareDeck = modConfig.GetMethod("SetShareDeck", BindingFlags.Static | BindingFlags.Public)!;
    setShareDeck.Invoke(null, [true, false]);
    if (!(bool)modConfig.GetProperty("ShareDeck", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!)
        throw new InvalidOperationException("Deck sharing cannot be enabled.");
    Type networkConfigSync = typeof(HPShareMod).Assembly.GetType(
        "HPShare.NetworkConfigSync", throwOnError: true)!;
    object configMessage = networkConfigSync.GetMethod(
        "CreateMessage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
    if (!(bool)configMessage.GetType().GetProperty("ShareDeck")!.GetValue(configMessage)!)
        throw new InvalidOperationException("Host config messages still force deck sharing off.");
    Console.WriteLine("DECK_SHARING_TOGGLE_OK");

    Type sharedEnergy = typeof(HPShareMod).Assembly.GetType("HPShare.SharedEnergyPatches", throwOnError: true)!;
    MethodInfo combatMaxEnergyPrefix = sharedEnergy.GetMethod(
        "CombatMaxEnergyGetterPrefix", BindingFlags.Static | BindingFlags.NonPublic)!;
    if (!PatchProcessor.GetOriginalInstructions(combatMaxEnergyPrefix)
            .Any(instruction => instruction.operand is MethodInfo called
                                && called.Name == "ModifyMaxEnergy"))
    {
        throw new InvalidOperationException("Shared maximum energy does not sum hook-modified contributions.");
    }
    MethodInfo serializeEnergy = sharedEnergy.GetMethod(
        "PlayerToSerializablePostfix", BindingFlags.Static | BindingFlags.NonPublic)!;
    if (!PatchProcessor.GetOriginalInstructions(serializeEnergy)
            .Any(instruction => instruction.operand is MethodInfo called
                                && called.Name == "RawMaxEnergy"))
    {
        throw new InvalidOperationException("Maximum-energy serialization does not preserve raw contributions.");
    }
    Console.WriteLine("MAX_ENERGY_SYNC_GUARD_OK");

    MethodInfo inventoryTryGetParty = sharedInventory.GetMethod(
        "TryGetParty", BindingFlags.Static | BindingFlags.NonPublic)!;
    object?[] noOwnerArguments = [null, null];
    bool foundPartyWithoutOwner = (bool)inventoryTryGetParty.Invoke(null, noOwnerArguments)!;
    if (foundPartyWithoutOwner)
        throw new InvalidOperationException("An ownerless starter card unexpectedly resolved a party.");
    Console.WriteLine("OWNERLESS_STARTER_CARD_GUARD_OK");

    MethodInfo deckAddedPostfix = sharedInventory.GetMethod(
        "DeckCardAddedPostfix", BindingFlags.Static | BindingFlags.NonPublic)!;
    if (!PatchProcessor.GetOriginalInstructions(deckAddedPostfix)
        .Any(instruction => instruction.operand is MethodInfo called
                            && called.Name == "TryGetReadyParty"))
    {
        throw new InvalidOperationException(
            "Deck additions can still mirror before new-run initialization completes.");
    }
    Console.WriteLine("STARTER_DECK_INITIALIZATION_BARRIER_OK");

    MethodInfo persistentCardClone = sharedInventory.GetMethod(
        "CloneOwnerlessPersistentCard", BindingFlags.Static | BindingFlags.NonPublic)!;
    List<CodeInstruction> persistentCloneInstructions = PatchProcessor.GetOriginalInstructions(persistentCardClone);
    if (persistentCloneInstructions.Any(instruction => instruction.operand is MethodInfo called
            && called.Name == nameof(CardModel.CreateCloneForPlayer)))
    {
        throw new InvalidOperationException(
            "Permanent-deck cloning still calls the combat-only CreateCloneForPlayer API.");
    }
    if (!persistentCloneInstructions.Any(instruction => instruction.operand is MethodInfo called
            && called.Name is nameof(AbstractModel.MutableClone) or nameof(CardModel.ToMutable)))
    {
        throw new InvalidOperationException("Permanent-deck cloning does not create a mutable copy.");
    }
    if (persistentCloneInstructions.Any(instruction => instruction.operand is MethodInfo called
            && called.Name == "set_Owner"))
    {
        throw new InvalidOperationException(
            "Permanent-deck cloning still uses the owner property, which rejects reassignment.");
    }
    if (!persistentCloneInstructions.Any(instruction => instruction.operand is FieldInfo field
            && field.Name == "CardOwnerField"))
    {
        throw new InvalidOperationException("Permanent-deck cloning does not clear the copied owner field.");
    }
    if (!persistentCloneInstructions.Any(instruction => instruction.opcode == OpCodes.Ldnull))
    {
        throw new InvalidOperationException("Permanent-deck cloning does not assign an ownerless value.");
    }
    Console.WriteLine("PERSISTENT_DECK_CLONE_GUARD_OK");

    MethodInfo starterSelector = sharedInventory.GetMethod(
        "SelectSharedStarterCards", BindingFlags.Static | BindingFlags.NonPublic)!
        .MakeGenericMethod(typeof(string));
    IReadOnlyList<IReadOnlyList<string>> starterDecks =
    [
        ["S1", "S2", "S3", "S4", "S5", "D1", "D2", "D3", "D4", "BashA"],
        ["S6", "S7", "S8", "S9", "S10", "D5", "D6", "D7", "D8", "BashB"],
        ["S11", "S12", "S13", "S14", "S15", "D9", "D10", "D11", "D12", "Neutralize", "Survivor"],
    ];
    static int StarterRole(string card)
        => card.StartsWith('S') && card != "Survivor" ? 0
            : card.Length > 1 && card[0] == 'D' && char.IsDigit(card[1]) ? 1 : 2;
    var selectedStarters = (List<string>)starterSelector.Invoke(null, [starterDecks, (Func<string, int>)StarterRole])!;
    string[] expectedStarters = ["S1", "S2", "S3", "D1", "D2", "D3", "BashA", "BashB", "Neutralize", "Survivor"];
    if (!selectedStarters.SequenceEqual(expectedStarters))
        throw new InvalidOperationException($"Shared starter deck mismatch: {string.Join(',', selectedStarters)}");

    IReadOnlyList<IReadOnlyList<string>> necrobinderDefectDecks =
    [
        ["S1", "S2", "S3", "S4", "D1", "D2", "D3", "D4", "Unleash", "Bodyguard"],
        ["S5", "S6", "S7", "S8", "D5", "D6", "D7", "D8", "Zap", "Dualcast"],
    ];
    var selectedNecrobinderDefect = (List<string>)starterSelector.Invoke(
        null, [necrobinderDefectDecks, (Func<string, int>)StarterRole])!;
    string[] expectedNecrobinderDefect =
        ["S1", "S2", "S3", "D1", "D2", "D3", "Unleash", "Bodyguard", "Zap", "Dualcast"];
    if (!selectedNecrobinderDefect.SequenceEqual(expectedNecrobinderDefect))
        throw new InvalidOperationException(
            $"Necrobinder/Defect starter deck mismatch: {string.Join(',', selectedNecrobinderDefect)}");
    Console.WriteLine("SHARED_STARTER_DECK_RULE_OK");

    MethodInfo initializeLoadedDeck = sharedInventory.GetMethod(
        "InitializeLoadedDeck", BindingFlags.Static | BindingFlags.NonPublic)!;
    if (PatchProcessor.GetOriginalInstructions(initializeLoadedDeck)
        .Any(instruction => instruction.operand is MethodInfo called
                            && called.DeclaringType == typeof(Enumerable)
                            && called.Name == nameof(Enumerable.SelectMany)))
    {
        throw new InvalidOperationException(
            "Loaded shared decks still concatenate one serialized copy per player.");
    }
    Console.WriteLine("SHARED_DECK_NO_MULTIPLICATION_GUARD_OK");

    Type sharedOptions = typeof(HPShareMod).Assembly.GetType("HPShare.SharedOptionsPatches", throwOnError: true)!;
    MethodInfo actualPowerDelta = sharedOptions.GetMethod(
        "CalculateActualPowerDelta",
        BindingFlags.Static | BindingFlags.NonPublic)!;
    int sharedDoom = 0;
    for (int playerIndex = 0; playerIndex < 4; playerIndex++)
    {
        int finalAmount = sharedDoom + 3;
        int delta = (int)actualPowerDelta.Invoke(null, [sharedDoom, finalAmount])!;
        sharedDoom += delta;
    }
    if (sharedDoom != 12)
        throw new InvalidOperationException($"Four-player Neurosurge Doom should total 12, got {sharedDoom}.");
    Console.WriteLine("NEUROSURGE_DOOM_AGGREGATION_OK");

    MethodBase[] initializationCriticalMethods =
    [
        AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp)),
        AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp)),
        AccessTools.Method(typeof(Creature), nameof(Creature.SetCurrentHpInternal)),
        AccessTools.Method(typeof(Creature), nameof(Creature.SetMaxHpInternal)),
    ];
    foreach (MethodBase method in initializationCriticalMethods)
    {
        Patches? patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo?.Owners.Contains(HPShareMod.HarmonyId) == true)
            throw new InvalidOperationException($"Initialization-critical method is still patched: {method}");
    }
    Console.WriteLine("INITIALIZATION_PATCH_GUARD_OK");

    Assembly gameAssembly = typeof(CardModel).Assembly;
    MethodInfo topBarUpdate = gameAssembly.GetTypes().Single(type => type.Name == "NTopBarHp")
        .GetMethod("UpdateHealth", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
    if (Harmony.GetPatchInfo(topBarUpdate)?.Owners.Contains(HPShareMod.HarmonyId) != true)
        throw new InvalidOperationException("Top-bar shared HP display patch is missing.");
    List<CodeInstruction> topBarInstructions = PatchProcessor.GetCurrentInstructions(topBarUpdate);
    if (!topBarInstructions.Any(instruction =>
            instruction.operand is MethodInfo called
            && called.DeclaringType?.FullName == "HPShare.UiPatches"
            && called.Name is "GetDisplayedCurrentHp" or "GetDisplayedMaxHp")
        || topBarInstructions.Any(instruction =>
            instruction.Calls(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp)))
            || instruction.Calls(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp)))))
    {
        throw new InvalidOperationException("Top-bar HP getters were not isolated to the display-only path.");
    }
    Console.WriteLine("TOP_BAR_DISPLAY_PATCH_OK");

    Type damagePatches = typeof(HPShareMod).Assembly.GetType("HPShare.DamagePatches", throwOnError: true)!;
    MethodInfo previewPatch = damagePatches.GetMethod("ModifyDamagePostfix", BindingFlags.Static | BindingFlags.NonPublic)!;
    Creature uninitializedDealer = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));
    object?[] previewArgs = [null, uninitializedDealer, 7m];
    previewPatch.Invoke(null, previewArgs);
    if ((decimal)previewArgs[2]! != 7m)
        throw new InvalidOperationException("Targetless damage preview unexpectedly changed damage.");
    Console.WriteLine("TARGETLESS_PREVIEW_GUARD_OK");

    MethodInfo damageNumberCreate = AccessTools.Method(typeof(NDamageNumVfx), nameof(NDamageNumVfx.Create),
        [typeof(Creature), typeof(DamageResult)]);
    if (Harmony.GetPatchInfo(damageNumberCreate)?.Owners.Contains(HPShareMod.HarmonyId) != true)
        throw new InvalidOperationException("Combined shared damage-number patch is missing.");
    Console.WriteLine("COMBINED_DAMAGE_NUMBER_PATCH_OK");

    Type uiPatches = typeof(HPShareMod).Assembly.GetType("HPShare.UiPatches", throwOnError: true)!;
    MethodInfo formatIntent = uiPatches.GetMethod("FormatIntentLabel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    string multiHit = (string)formatIntent.Invoke(null, [3, 6L])!;
    string singleHit = (string)formatIntent.Invoke(null, [1, 18L])!;
    if (multiHit != "6x3 (18)" || singleHit != "18" || multiHit.Contains('Σ') || singleHit.Contains('Σ'))
        throw new InvalidOperationException($"Intent formatting mismatch: multi={multiHit}, single={singleHit}");
    Console.WriteLine("INTENT_FORMAT_OK");
    MethodInfo saturatingSum = uiPatches.GetMethod("SaturatingSum", BindingFlags.Static | BindingFlags.NonPublic)!;
    long summedPreview = (long)saturatingSum.Invoke(null, [new long[] { 6, 12 }])!;
    if (summedPreview != 18L)
        throw new InvalidOperationException($"Party intent sum mismatch: {summedPreview}");
    Console.WriteLine("PARTY_INTENT_SUM_OK");

    Type sharedVitals = typeof(HPShareMod).Assembly.GetType("HPShare.SharedVitals", throwOnError: true)!;
    MethodInfo allocate = sharedVitals.GetMethod("AllocateProportionally", BindingFlags.Public | BindingFlags.Static)!;
    int[] result = (int[])allocate.Invoke(null, new object?[] { 5, new[] { 1, 2, 3 }, null })!;
    if (!result.SequenceEqual(new[] { 1, 2, 2 }))
        throw new InvalidOperationException($"Allocation mismatch: [{string.Join(",", result)}]");
    if (result.Sum() != 5)
        throw new InvalidOperationException("Allocation does not conserve the total.");
    int[] capped = (int[])allocate.Invoke(null, new object?[] { 7, new[] { 1, 2, 3 }, null })!;
    if (!capped.SequenceEqual(new[] { 1, 2, 3 }))
        throw new InvalidOperationException("Allocation exceeded contribution capacity.");
    int[] ostyPartial = (int[])allocate.Invoke(null, new object?[] { 4, new[] { 2, 7 }, null })!;
    int[] ostyLethal = (int[])allocate.Invoke(null, new object?[] { 12, new[] { 2, 7 }, null })!;
    if (!ostyPartial.SequenceEqual(new[] { 1, 3 })
        || !ostyLethal.SequenceEqual(new[] { 2, 7 })
        || ostyLethal.Sum() != 9
        || 12 - ostyLethal.Sum() != 3)
    {
        throw new InvalidOperationException(
            $"Shared Osty damage mismatch: partial=[{string.Join(',', ostyPartial)}], lethal=[{string.Join(',', ostyLethal)}]");
    }
    Console.WriteLine("ALLOCATION_TEST_OK");

    MethodInfo allocateReceiverFirst = sharedVitals.GetMethod("AllocateReceiverFirst", BindingFlags.Public | BindingFlags.Static)!;
    int[] firstReceiver = (int[])allocateReceiverFirst.Invoke(null, [9, new[] { 66, 66 }, 0])!;
    int[] secondReceiver = (int[])allocateReceiverFirst.Invoke(null, [9, new[] { 57, 66 }, 1])!;
    int[] overflow = (int[])allocateReceiverFirst.Invoke(null, [5, new[] { 3, 10 }, 0])!;
    if (!firstReceiver.SequenceEqual(new[] { 9, 0 })
        || !secondReceiver.SequenceEqual(new[] { 0, 9 })
        || !overflow.SequenceEqual(new[] { 3, 2 }))
    {
        throw new InvalidOperationException(
            $"Receiver-first allocation mismatch: first=[{string.Join(',', firstReceiver)}], second=[{string.Join(',', secondReceiver)}], overflow=[{string.Join(',', overflow)}]");
    }
    Console.WriteLine("RECEIVER_FIRST_DAMAGE_TEST_OK");

    var random = new Random(0x485053);
    for (int iteration = 0; iteration < 10_000; iteration++)
    {
        int[] weights = Enumerable.Range(0, random.Next(1, 7))
            .Select(_ => random.Next(0, 10_000))
            .ToArray();
        int requested = random.Next(0, 60_000);
        int[] allocation = (int[])allocate.Invoke(null, new object?[] { requested, weights, null })!;
        int expected = Math.Min(requested, weights.Sum());
        if (allocation.Sum() != expected
            || allocation.Where((value, index) => value < 0 || value > weights[index]).Any())
        {
            throw new InvalidOperationException(
                $"Allocation invariant failed: requested={requested}, weights=[{string.Join(',', weights)}], result=[{string.Join(',', allocation)}]");
        }
    }
    Console.WriteLine("ALLOCATION_FUZZ_OK");

    for (int iteration = 0; iteration < 10_000; iteration++)
    {
        int[] capacities = Enumerable.Range(0, random.Next(1, 7))
            .Select(_ => random.Next(0, 10_000))
            .ToArray();
        int receiverIndex = random.Next(capacities.Length);
        int requested = random.Next(0, 60_000);
        int[] allocation = (int[])allocateReceiverFirst.Invoke(null, [requested, capacities, receiverIndex])!;
        int expected = Math.Min(requested, capacities.Sum());
        int expectedReceiver = Math.Min(expected, capacities[receiverIndex]);
        if (allocation.Sum() != expected
            || allocation[receiverIndex] != expectedReceiver
            || allocation.Where((value, index) => value < 0 || value > capacities[index]).Any())
        {
            throw new InvalidOperationException(
                $"Receiver-first invariant failed: requested={requested}, receiver={receiverIndex}, capacities=[{string.Join(',', capacities)}], result=[{string.Join(',', allocation)}]");
        }
    }
    Console.WriteLine("RECEIVER_FIRST_DAMAGE_FUZZ_OK");

    string[] describedCards =
    [
        "BodySlam", "DemonicShield", "Entrench", "Expose", "Mimic", "Prolong",
        "Barricade", "Blur", "Afterlife", "Bodyguard", "BoneShards", "Cleanse", "Dirge",
        "Fetch", "Flatten", "HighFive", "LegionOfBone", "NecroMastery", "Poke", "Protector",
        "PullAggro", "Rattle", "Reanimate", "RightHandHand", "Sacrifice", "SicEm", "Snap",
        "Spur", "Squeeze", "SweepingGaze", "Unleash",
    ];
    HashSet<string> gameCardNames = typeof(CardModel).Assembly.GetTypes()
        .Where(type => typeof(CardModel).IsAssignableFrom(type))
        .Select(type => type.Name)
        .ToHashSet(StringComparer.Ordinal);
    string[] missingCards = describedCards.Where(name => !gameCardNames.Contains(name)).ToArray();
    if (missingCards.Length > 0)
        throw new InvalidOperationException($"Description patch card types not found: {string.Join(", ", missingCards)}");
    Console.WriteLine("DESCRIPTION_TARGETS_OK");

    MethodInfo unblockedTargetHook = AccessTools.Method(
        typeof(MegaCrit.Sts2.Core.Hooks.Hook),
        nameof(MegaCrit.Sts2.Core.Hooks.Hook.ModifyUnblockedDamageTarget));
    if (Harmony.GetPatchInfo(unblockedTargetHook)?.Owners.Contains(HPShareMod.HarmonyId) != true)
        throw new InvalidOperationException("Party-wide shared Osty redirect patch is missing.");
    Console.WriteLine("PARTY_WIDE_OSTY_REDIRECT_PATCH_OK");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
