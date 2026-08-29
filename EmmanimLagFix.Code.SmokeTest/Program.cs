using EmmanimLagFix.Code;
using HarmonyLib;
using System.Reflection;
using System.Runtime.CompilerServices;

var gameAssembly = Assembly.Load("Cosmoteer");
var halflingAssembly = Assembly.Load("HalflingCore");

var prefixTargets = new[]
{
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.PlayerResourcesDisplay", throwOnError: true)!, Method: "GetResourceCounts"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Resources.CrewAndResourceTransferWindow", throwOnError: true)!, Method: "OnUpdatingUIState"),
    (Type: gameAssembly.GetType("Cosmoteer.Modes.Career.Comms.CommTradeTab", throwOnError: true)!, Method: "OnUpdatingUIState"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Resources.CrewAndResourceTransferWindow+<>c__DisplayClass21_2", throwOnError: true)!, Method: "<.ctor>b__9"),
    (Type: gameAssembly.GetType("Cosmoteer.Modes.Career.Comms.CommTradeTab+<>c__DisplayClass23_3", throwOnError: true)!, Method: "<.ctor>b__8"),
    (Type: gameAssembly.GetType("Cosmoteer.Modes.Career.Comms.CommTechsTab+<>c__DisplayClass8_3", throwOnError: true)!, Method: "<.ctor>b__2"),
    (Type: gameAssembly.GetType("Cosmoteer.Simulation.SimOverlayRenderer", throwOnError: true)!, Method: "<OnDrawCrewUnderlays>g___DrawResourceNuggetPickups|98_3"),
    (Type: gameAssembly.GetType("Cosmoteer.Ships.Parts.PartsManager+UpdateCallbacks", throwOnError: true)!, Method: "Update"),
    (Type: gameAssembly.GetType("Cosmoteer.Source.Ships.Blueprints.BaseBlueprintPartNetworkPort", throwOnError: true)!, Method: "UpdateOperational"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Build.Stats.BuildToolboxStatsGui", throwOnError: true)!, Method: "Update"),
    (Type: gameAssembly.GetType("Cosmoteer.Ships.Statuses.Subhandlers.StatusDiffuser", throwOnError: true)!, Method: "PerformDiffusion"),
    (Type: gameAssembly.GetType("Cosmoteer.Ships.Parts.Logic.PartSmoothedValue+SmoothedValueManager", throwOnError: true)!, Method: "Update"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Crew.RoleEditWindow+<>c__DisplayClass19_0", throwOnError: true)!, Method: "<CreatePrioritiesTab>g___AddPart|1"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Crew.RoleEditWindow+JobPriorityWidget", throwOnError: true)!, Method: "OnUpdatePriorityState"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Crew.RoleEditWindow+AssignmentPriorityWidget", throwOnError: true)!, Method: "OnUpdatePriorityState"),
    (Type: gameAssembly.GetType("Cosmoteer.Gui.Multiplayer.GameLaunchFlow+HostLaunchFlow+<>c__DisplayClass7_0", throwOnError: true)!, Method: "<DoHostLaunchFlow>b__1"),
    (Type: gameAssembly.GetType("Cosmoteer.Gui.Multiplayer.GameLaunchFlow+ClientLaunchFlow+<>c__DisplayClass11_0", throwOnError: true)!, Method: "<OnStreamMessageReceived>b__0"),
    (Type: gameAssembly.GetType("Cosmoteer.Modes.LoadGameInit", throwOnError: true)!, Method: "CreateGame")
};

// These two are transpiler-only (no Prefix), and their transpilers throw
// InvalidOperationException immediately at patch time if the target IL
// shape does not contain exactly one delegate-construction site to replace
// (see ToggleModeDelegateCachePatch.ReplaceDelegateConstruction). So for
// these, reaching the Transpilers-installed check below without an
// exception already proves the transpiler matched successfully.
var transpilerTargets = new[]
{
    (Type: gameAssembly.GetType("Cosmoteer.Ships.Blueprints.Logic.Values.BlueprintPartStatProvider", throwOnError: true)!, Method: "UpdateOperational"),
    (Type: gameAssembly.GetType("Cosmoteer.Source.Ships.Blueprints.BaseBlueprintPartNetworkPort", throwOnError: true)!, Method: "UpdateOperational"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Resources.CrewAndResourceTransferWindow+<>c__DisplayClass21_0", throwOnError: true)!, Method: "<.ctor>b__8"),
    (Type: gameAssembly.GetType("Cosmoteer.Modes.Career.Comms.CommTradeTab+<>c__DisplayClass23_0", throwOnError: true)!, Method: "<.ctor>b__6"),
    (Type: halflingAssembly.GetType("Halfling.Network.NetworkMessenger", throwOnError: true)!, Method: "ProcessUnresponsiveSessions"),
    (Type: halflingAssembly.GetType("Halfling.Network.NetworkMessenger", throwOnError: true)!, Method: "EnqueueOutgoingAcks"),
};

var transferConstructors = new[]
{
    gameAssembly.GetType("Cosmoteer.Game.Gui.Resources.CrewAndResourceTransferWindow", throwOnError: true)!
        .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single(),
    gameAssembly.GetType("Cosmoteer.Modes.Career.Comms.CommTradeTab", throwOnError: true)!
        .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single()
};

var techConstructor = gameAssembly.GetType("Cosmoteer.Modes.Career.Comms.CommTechsTab", throwOnError: true)!
    .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();

var resourceManagerType = gameAssembly.GetType("Cosmoteer.Ships.Resources.ResourceManager", throwOnError: true)!;
var resourceSinkInfoType = gameAssembly.GetType("Cosmoteer.Ships.Resources.ResourceManager+SinkInfo", throwOnError: true)!;
var resourceSearchTarget = AccessTools.Method(resourceManagerType, "SearchForSources", new[] { resourceSinkInfoType })
    ?? throw new MissingMethodException(resourceManagerType.FullName, "SearchForSources(SinkInfo)");
var perShipCountType = gameAssembly.GetType("Cosmoteer.Ships.Resources.ResourceManager+PerShipCount", throwOnError: true)!;
var perShipGetCountTarget = AccessTools.Method(perShipCountType, "GetCount")
    ?? throw new MissingMethodException(perShipCountType.FullName, "GetCount");
var perShipAddCountTarget = AccessTools.Method(perShipCountType, "AddCount")
    ?? throw new MissingMethodException(perShipCountType.FullName, "AddCount");
var streamCopyTarget = AccessTools.Method(typeof(Stream), nameof(Stream.CopyTo), new[] { typeof(Stream) })
    ?? throw new MissingMethodException(typeof(Stream).FullName, "CopyTo(Stream)");

const string smokeId = "nayuri.emmanim_lag_fix.smoke_test";
var harmony = new Harmony(smokeId);
harmony.PatchAll(typeof(EntryPoint).Assembly);

var timeoutPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.MultiplayerSessionTimeoutPatch",
    throwOnError: true)!;
var successfulTimeoutTranspilers = (int)(timeoutPatchType.GetField(
    "SuccessfulTranspilerCount",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingFieldException(timeoutPatchType.FullName, "SuccessfulTranspilerCount"))
    .GetValue(null)!;
if (successfulTimeoutTranspilers != 2)
{
    throw new InvalidOperationException(
        $"Expected both multiplayer timeout transpilers to match, got {successfulTimeoutTranspilers}.");
}

foreach (var targetInfo in prefixTargets)
{
    var target = AccessTools.Method(targetInfo.Type, targetInfo.Method)
        ?? throw new MissingMethodException(targetInfo.Type.FullName, targetInfo.Method);
    var info = Harmony.GetPatchInfo(target)
        ?? throw new InvalidOperationException($"Harmony did not patch {targetInfo.Type.FullName}.{targetInfo.Method}.");
    if (!info.Prefixes.Any(patch => patch.owner == smokeId))
    {
        throw new InvalidOperationException($"Expected Emmanim prefix was not installed on {targetInfo.Type.FullName}.{targetInfo.Method}.");
    }
}

var streamCopyInfo = Harmony.GetPatchInfo(streamCopyTarget)
    ?? throw new InvalidOperationException("Harmony did not patch Stream.CopyTo(Stream).");
if (!streamCopyInfo.Prefixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected multiplayer stream-copy capacity prefix was not installed.");
}

var channelStreamType = halflingAssembly.GetType("Halfling.Network.ChannelStream", throwOnError: true)!;
var channelInputBufferField = channelStreamType.GetField(
    "_inBuf",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingFieldException(channelStreamType.FullName, "_inBuf");
var testChannel = (Stream)RuntimeHelpers.GetUninitializedObject(channelStreamType);
var testPayload = Enumerable.Range(0, 65553).Select(index => (byte)(index * 31)).ToArray();
channelInputBufferField.SetValue(testChannel, new MemoryStream(testPayload, writable: false));
using (var copied = new MemoryStream())
{
    testChannel.CopyTo(copied);
    if (copied.Capacity != testPayload.Length || !copied.ToArray().SequenceEqual(testPayload))
    {
        throw new InvalidOperationException(
            $"Multiplayer stream copy mismatch: capacity={copied.Capacity}, length={copied.Length}.");
    }
}

foreach (var targetInfo in transpilerTargets)
{
    var target = AccessTools.Method(targetInfo.Type, targetInfo.Method)
        ?? throw new MissingMethodException(targetInfo.Type.FullName, targetInfo.Method);
    var info = Harmony.GetPatchInfo(target)
        ?? throw new InvalidOperationException($"Harmony did not patch {targetInfo.Type.FullName}.{targetInfo.Method}.");
    if (!info.Transpilers.Any(patch => patch.owner == smokeId))
    {
        throw new InvalidOperationException($"Expected Emmanim transpiler was not installed on {targetInfo.Type.FullName}.{targetInfo.Method}.");
    }
}

foreach (var constructor in transferConstructors)
{
    var info = Harmony.GetPatchInfo(constructor)
        ?? throw new InvalidOperationException($"Harmony did not patch {constructor.DeclaringType!.FullName} constructor.");
    if (!info.Prefixes.Any(patch => patch.owner == smokeId))
    {
        throw new InvalidOperationException($"Expected Emmanim prefix was not installed on {constructor.DeclaringType!.FullName} constructor.");
    }
}

var techConstructorInfo = Harmony.GetPatchInfo(techConstructor)
    ?? throw new InvalidOperationException("Harmony did not patch the technology-purchase constructor.");
if (!techConstructorInfo.Prefixes.Any(patch => patch.owner == smokeId) ||
    !techConstructorInfo.Transpilers.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected Emmanim technology-purchase constructor patches were not installed.");
}

var resourceSearchInfo = Harmony.GetPatchInfo(resourceSearchTarget)
    ?? throw new InvalidOperationException("Harmony did not patch per-sink resource source search diagnostics.");
if (!resourceSearchInfo.Prefixes.Any(patch => patch.owner == smokeId) ||
    !resourceSearchInfo.Postfixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected resource-search diagnostic timing patches were not installed.");
}

foreach (var target in new[] { perShipGetCountTarget, perShipAddCountTarget })
{
    var info = Harmony.GetPatchInfo(target)
        ?? throw new InvalidOperationException($"Harmony did not patch {target.DeclaringType!.FullName}.{target.Name}.");
    if (!info.Prefixes.Any(patch => patch.owner == smokeId))
    {
        throw new InvalidOperationException($"Expected lock-free PerShipCount prefix was not installed on {target.Name}.");
    }
}

var testCount = Activator.CreateInstance(perShipCountType, nonPublic: true)
    ?? throw new InvalidOperationException("Could not create a PerShipCount test instance.");
var shipType = gameAssembly.GetType("Cosmoteer.Ships.Ship", throwOnError: true)!;
var testShip = RuntimeHelpers.GetUninitializedObject(shipType);
var mpValueType = perShipAddCountTarget.GetParameters()[1].ParameterType;
var displayedField = AccessTools.Field(mpValueType, "Displayed")
    ?? throw new MissingFieldException(mpValueType.FullName, "Displayed");
var confirmedField = AccessTools.Field(mpValueType, "Confirmed")
    ?? throw new MissingFieldException(mpValueType.FullName, "Confirmed");
object CreateMPValue(int displayedValue, int confirmedValue) =>
    Activator.CreateInstance(
        mpValueType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        args: new object[] { displayedValue, confirmedValue },
        culture: null)
    ?? throw new InvalidOperationException("Could not create an MPValue<int> test value.");
var firstAdd = perShipAddCountTarget.Invoke(
    testCount,
    new[] { testShip, CreateMPValue(2, 3) })!;
var secondAdd = perShipAddCountTarget.Invoke(
    testCount,
    new[] { testShip, CreateMPValue(5, 7) })!;
var valueType = perShipGetCountTarget.GetParameters()[1].ParameterType;
var displayedKind = Enum.Parse(valueType, "Displayed");
var confirmedKind = Enum.Parse(valueType, "Confirmed");
var displayed = (int)perShipGetCountTarget.Invoke(testCount, new[] { testShip, displayedKind })!;
var confirmed = (int)perShipGetCountTarget.Invoke(testCount, new[] { testShip, confirmedKind })!;
var firstDisplayed = (int)displayedField.GetValue(firstAdd)!;
var firstConfirmed = (int)confirmedField.GetValue(firstAdd)!;
var secondDisplayed = (int)displayedField.GetValue(secondAdd)!;
var secondConfirmed = (int)confirmedField.GetValue(secondAdd)!;
if (firstDisplayed != 2 || firstConfirmed != 3 ||
    secondDisplayed != 7 || secondConfirmed != 10 ||
    displayed != 7 || confirmed != 10)
{
    throw new InvalidOperationException(
        $"Lock-free PerShipCount behavior mismatch: first={firstDisplayed}/{firstConfirmed}, " +
        $"second={secondDisplayed}/{secondConfirmed}, " +
        $"displayed={displayed}, confirmed={confirmed}.");
}

const int parallelAdds = 2000;
Parallel.For(0, parallelAdds, _ =>
{
    perShipAddCountTarget.Invoke(testCount, new[] { testShip, CreateMPValue(1, 1) });
});
var parallelConfirmed = (int)perShipGetCountTarget.Invoke(testCount, new[] { testShip, confirmedKind })!;
if (parallelConfirmed != 10 + parallelAdds)
{
    throw new InvalidOperationException(
        $"Lock-free PerShipCount lost a concurrent update: expected {10 + parallelAdds}, got {parallelConfirmed}.");
}

harmony.UnpatchAll(smokeId);
Console.WriteLine("PASS: resource, lock-free resource counts, transfer, trade, technology-purchase, pickup-overlay, blueprint-network refresh, build-stats, sparse heat diffusion, visual smoothed-value throttle, opt-in resource diagnostics, role-priority, multiplayer initialization/session-timeout buffer handling, and toggle-mode delegate cache patches resolved on this game build.");
