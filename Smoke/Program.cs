using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using HPShare;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

try
{
    HPShareMod.Initialize();
    Console.WriteLine("PATCH_APPLICATION_OK");

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

    Type uiPatches = typeof(HPShareMod).Assembly.GetType("HPShare.UiPatches", throwOnError: true)!;
    MethodInfo formatIntent = uiPatches.GetMethod("FormatIntentLabel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    string multiHit = (string)formatIntent.Invoke(null, ["2×4", 4, 8L])!;
    string singleHit = (string)formatIntent.Invoke(null, ["28", 1, 28L])!;
    if (multiHit != "2×4 (8)" || singleHit != "28" || multiHit.Contains('Σ') || singleHit.Contains('Σ'))
        throw new InvalidOperationException($"Intent formatting mismatch: multi={multiHit}, single={singleHit}");
    Console.WriteLine("INTENT_FORMAT_OK");

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
    Console.WriteLine("ALLOCATION_TEST_OK");

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
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
