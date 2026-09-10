using EmmanimLagFix.Code;
using Halfling.Scene2D;
using HarmonyLib;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

var gameAssembly = Assembly.Load("Cosmoteer");
var halflingAssembly = Assembly.Load("HalflingCore");
// Korean IME integration is implemented by the Windows platform assembly,
// which the game has loaded before input is initialized. Load it explicitly in
// this standalone smoke host so the same Harmony target types are resolvable.
var halflingPlatformAssembly = Assembly.LoadFrom(Path.Combine(
    Path.GetDirectoryName(gameAssembly.Location)!,
    "HalflingPlatformWDX.dll"));

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
    (Type: gameAssembly.GetType("Cosmoteer.Ships.Parts.PartsManager+FixedUpdateCallbacks", throwOnError: true)!, Method: "FixedUpdate"),
    (Type: gameAssembly.GetType("Cosmoteer.Source.Ships.Blueprints.BaseBlueprintPartNetworkPort", throwOnError: true)!, Method: "UpdateOperational"),
    (Type: gameAssembly.GetType("Cosmoteer.Ships.Blueprints.Logic.Values.BlueprintPartStatProvider", throwOnError: true)!, Method: "UpdateOperational"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Build.Stats.BuildToolboxStatsGui", throwOnError: true)!, Method: "Update"),
    (Type: gameAssembly.GetType("Cosmoteer.Ships.Statuses.Subhandlers.StatusDiffuser", throwOnError: true)!, Method: "PerformDiffusion"),
    (Type: gameAssembly.GetType("Cosmoteer.Ships.Parts.Logic.PartSmoothedValue+SmoothedValueManager", throwOnError: true)!, Method: "Update"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Crew.RoleEditWindow+<>c__DisplayClass19_0", throwOnError: true)!, Method: "<CreatePrioritiesTab>g___AddPart|1"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Crew.RoleEditWindow+JobPriorityWidget", throwOnError: true)!, Method: "OnUpdatePriorityState"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Crew.RoleEditWindow+AssignmentPriorityWidget", throwOnError: true)!, Method: "OnUpdatePriorityState"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Paint.PaintToolbox", throwOnError: true)!, Method: "AddDecalsGroup"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Paint.PaintToolbox", throwOnError: true)!, Method: "SelectDecalType"),
    (Type: gameAssembly.GetType("Cosmoteer.Gui.Multiplayer.GameLaunchFlow+HostLaunchFlow+<>c__DisplayClass7_0", throwOnError: true)!, Method: "<DoHostLaunchFlow>b__1"),
    (Type: gameAssembly.GetType("Cosmoteer.Gui.Multiplayer.GameLaunchFlow+ClientLaunchFlow+<>c__DisplayClass11_0", throwOnError: true)!, Method: "<OnStreamMessageReceived>b__0"),
    (Type: gameAssembly.GetType("Cosmoteer.Modes.LoadGameInit", throwOnError: true)!, Method: "CreateGame"),
    (Type: gameAssembly.GetType("Cosmoteer.Ships.Statuses.StatusValueRegulator", throwOnError: true)!, Method: "GetAffectedCells")
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
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Paint.PaintToolbox", throwOnError: true)!, Method: "AddDecalsLayers"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Paint.PaintToolbox", throwOnError: true)!, Method: "AddBasePaintLayer"),
    (Type: gameAssembly.GetType("Cosmoteer.Ships.Rendering.AtlasQuadManager+InternalManagedAtlasQuad", throwOnError: true)!, Method: "set_Data"),
    (Type: gameAssembly.GetType("Cosmoteer.Ships.Crew.Pathing.PathContiguityManager+<SearchSetsFrom>d__21", throwOnError: true)!, Method: "MoveNext"),
};

var paintToolboxType = gameAssembly.GetType("Cosmoteer.Game.Gui.Paint.PaintToolbox", throwOnError: true)!;
var onSelfActivatedTarget = AccessTools.Method(paintToolboxType, "OnSelfActivated")
    ?? throw new MissingMethodException(paintToolboxType.FullName, "OnSelfActivated");
var addDecalsGroupTarget = AccessTools.Method(paintToolboxType, "AddDecalsGroup")
    ?? throw new MissingMethodException(paintToolboxType.FullName, "AddDecalsGroup");

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
var updateSinkJobsTarget = AccessTools.Method(
    resourceManagerType,
    "UpdateSinkJobs",
    new[] { halflingAssembly.GetType("Halfling.Timing.Time", throwOnError: true)! })
    ?? throw new MissingMethodException(resourceManagerType.FullName, "UpdateSinkJobs(Time)");
var baseResourceStorageType = gameAssembly.GetType(
    "Cosmoteer.Ships.Parts.Resources.BaseResourceStorage",
    throwOnError: true)!;
var unmetDesiredTarget = AccessTools.Method(
    baseResourceStorageType,
    "<GetSortPriority>g___HasUnmetDesired|181_0")
    ?? throw new MissingMethodException(
        baseResourceStorageType.FullName,
        "<GetSortPriority>g___HasUnmetDesired|181_0");
var perShipCountType = gameAssembly.GetType("Cosmoteer.Ships.Resources.ResourceManager+PerShipCount", throwOnError: true)!;
var perShipGetCountTarget = AccessTools.Method(perShipCountType, "GetCount")
    ?? throw new MissingMethodException(perShipCountType.FullName, "GetCount");
var perShipAddCountTarget = AccessTools.Method(perShipCountType, "AddCount")
    ?? throw new MissingMethodException(perShipCountType.FullName, "AddCount");
var baseMpManagerType = gameAssembly.GetType("Cosmoteer.Game.Multiplayer.BaseMPManager", throwOnError: true)!;
var advanceNetworkTimeTarget = AccessTools.Method(baseMpManagerType, "AdvanceNetworkTime")
    ?? throw new MissingMethodException(baseMpManagerType.FullName, "AdvanceNetworkTime");
var multiplayerUpdateTarget = AccessTools.Method(baseMpManagerType, "Update")
    ?? throw new MissingMethodException(baseMpManagerType.FullName, "Update");
var gameRootType = gameAssembly.GetType("Cosmoteer.Game.GameRoot", throwOnError: true)!;
var gameRootUpdateTarget = AccessTools.Method(gameRootType, "Update", new[] { typeof(Action) })
    ?? throw new MissingMethodException(gameRootType.FullName, "Update(Action)");
var stasisNuggetType = gameAssembly.GetType(
    "Cosmoteer.Simulation.Stasis.SimStasisManager+StasisNugget",
    throwOnError: true)!;
var mpHostManagerType = gameAssembly.GetType("Cosmoteer.Game.Multiplayer.MPHostManager", throwOnError: true)!;
var hostOnTickTarget = AccessTools.Method(mpHostManagerType, "OnTick")
    ?? throw new MissingMethodException(mpHostManagerType.FullName, "OnTick");
var inputTickType = gameAssembly.GetType(
    "Cosmoteer.Game.Multiplayer.BaseMPManager+InputTick",
    throwOnError: true)!;
var serializedInputTickChannelType = typeof(Halfling.Network.SerializedChannel<>).MakeGenericType(inputTickType);
var forwardInputTickTarget = AccessTools.Method(
    mpHostManagerType,
    "ForwardInputTick",
    new[]
    {
        inputTickType,
        typeof(Halfling.Network.MessengerID),
        serializedInputTickChannelType
    }) ?? throw new MissingMethodException(mpHostManagerType.FullName, "ForwardInputTick");
var streamCopyTarget = AccessTools.Method(typeof(Stream), nameof(Stream.CopyTo), new[] { typeof(Stream) })
    ?? throw new MissingMethodException(typeof(Stream).FullName, "CopyTo(Stream)");
var clientLaunchFlowType = gameAssembly.GetType(
    "Cosmoteer.Gui.Multiplayer.GameLaunchFlow+ClientLaunchFlow",
    throwOnError: true)!;
var startDataStreamTarget = AccessTools.Method(clientLaunchFlowType, "StartDataStreamRpc", new[] { typeof(long) })
    ?? throw new MissingMethodException(clientLaunchFlowType.FullName, "StartDataStreamRpc(long)");
var clientResyncFlowType = gameAssembly.GetType(
    "Cosmoteer.Game.Multiplayer.GameResyncFlow+ClientResyncFlow",
    throwOnError: true)!;
var startResyncDataStreamTarget = AccessTools.Method(
    clientResyncFlowType,
    "StartDataStreamRpc",
    new[] { typeof(long) })
    ?? throw new MissingMethodException(clientResyncFlowType.FullName, "StartDataStreamRpc(long)");
var hostResyncWorkerType = gameAssembly.GetType(
    "Cosmoteer.Game.Multiplayer.GameResyncFlow+HostResyncFlow+<>c__DisplayClass7_0",
    throwOnError: true)!;
var clientResyncWorkerType = gameAssembly.GetType(
    "Cosmoteer.Game.Multiplayer.GameResyncFlow+ClientResyncFlow+<>c__DisplayClass9_0",
    throwOnError: true)!;
var resyncTimingTargets = new[]
{
    AccessTools.Method(hostResyncWorkerType, "<DoHostResyncFlow>b__0")
        ?? throw new MissingMethodException(hostResyncWorkerType.FullName, "host save worker"),
    AccessTools.Method(hostResyncWorkerType, "<DoHostResyncFlow>b__2")
        ?? throw new MissingMethodException(hostResyncWorkerType.FullName, "host load worker"),
    AccessTools.Method(clientResyncWorkerType, "<OnStreamMessageReceived>b__0")
        ?? throw new MissingMethodException(clientResyncWorkerType.FullName, "client load worker")
};

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

var hashThrottlePatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.MultiplayerIntegrityHashThrottlePatch",
    throwOnError: true)!;
var successfulHashTranspilers = (int)(hashThrottlePatchType.GetField(
    "SuccessfulTranspilerCount",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingFieldException(hashThrottlePatchType.FullName, "SuccessfulTranspilerCount"))
    .GetValue(null)!;
if (successfulHashTranspilers != 1)
{
    throw new InvalidOperationException(
        $"Expected multiplayer integrity-hash transpiler to match once, got {successfulHashTranspilers}.");
}
var shouldComputeHash = hashThrottlePatchType.GetMethod(
    "ShouldComputeHash",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(hashThrottlePatchType.FullName, "ShouldComputeHash");
var selectedHashTicks = Enumerable.Range(1, 30)
    .Where(tick => (bool)shouldComputeHash.Invoke(null, new object[] { tick, 30 })!)
    .ToArray();
var expectedHashTicks = new[] { 1, 6, 11, 16, 21, 26 };
if (!selectedHashTicks.SequenceEqual(expectedHashTicks))
{
    throw new InvalidOperationException(
        $"Expected 6 Hz integrity hashes at [{string.Join(", ", expectedHashTicks)}], " +
        $"got [{string.Join(", ", selectedHashTicks)}].");
}
var advanceNetworkTimeInfo = Harmony.GetPatchInfo(advanceNetworkTimeTarget)
    ?? throw new InvalidOperationException("Harmony did not patch BaseMPManager.AdvanceNetworkTime.");
if (!advanceNetworkTimeInfo.Transpilers.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected multiplayer integrity-hash transpiler was not installed.");
}
var multiplayerUpdateInfo = Harmony.GetPatchInfo(multiplayerUpdateTarget)
    ?? throw new InvalidOperationException("Harmony did not patch BaseMPManager.Update.");
if (!multiplayerUpdateInfo.Postfixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected multiplayer memory-diagnostics postfix was not installed.");
}
var gameRootUpdateInfo = Harmony.GetPatchInfo(gameRootUpdateTarget)
    ?? throw new InvalidOperationException("Harmony did not patch GameRoot.Update(Action).");
if (!gameRootUpdateInfo.Postfixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected single-player memory-diagnostics postfix was not installed.");
}
var memoryDiagnosticsCommonType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.MemoryDiagnosticsCommon",
    throwOnError: true)!;
var isSpawnerPreloaded = memoryDiagnosticsCommonType.GetMethod(
    "IsSpawnerPreloaded",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(memoryDiagnosticsCommonType.FullName, "IsSpawnerPreloaded");
var nonPreloadableSpawner = RuntimeHelpers.GetUninitializedObject(stasisNuggetType);
if ((bool)isSpawnerPreloaded.Invoke(null, new[] { nonPreloadableSpawner })!)
{
    throw new InvalidOperationException("A non-preloadable stasis spawner was reported as preloaded.");
}
var hostUpdatePatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.MultiplayerHostUpdateThrottlePatch",
    throwOnError: true)!;
var shouldSendHostUpdate = hostUpdatePatchType.GetMethod(
    "ShouldSendHostUpdate",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(hostUpdatePatchType.FullName, "ShouldSendHostUpdate");
var selectedHostUpdateTicks = Enumerable.Range(1, 30)
    .Where(tick => (bool)shouldSendHostUpdate.Invoke(null, new object[] { tick, 30, false })!)
    .ToArray();
if (!selectedHostUpdateTicks.SequenceEqual(expectedHashTicks)
    || !(bool)shouldSendHostUpdate.Invoke(null, new object[] { 2, 30, true })!)
{
    throw new InvalidOperationException(
        $"HostUpdate throttle mismatch: selected [{string.Join(", ", selectedHostUpdateTicks)}].");
}
var hostOnTickInfo = Harmony.GetPatchInfo(hostOnTickTarget)
    ?? throw new InvalidOperationException("Harmony did not patch MPHostManager.OnTick.");
if (!hostOnTickInfo.Prefixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected multiplayer HostUpdate throttle prefix was not installed.");
}
var forwardInputTickInfo = Harmony.GetPatchInfo(forwardInputTickTarget)
    ?? throw new InvalidOperationException("Harmony did not patch MPHostManager.ForwardInputTick.");
if (!forwardInputTickInfo.Prefixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected multiplayer InputTick allocation prefix was not installed.");
}
var inputTickAllocationPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.MultiplayerInputTickAllocationPatch",
    throwOnError: true)!;
var getOrCreateInputTickFilter = inputTickAllocationPatchType.GetMethod(
    "GetOrCreateFilter",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(inputTickAllocationPatchType.FullName, "GetOrCreateFilter");
var testHost = RuntimeHelpers.GetUninitializedObject(mpHostManagerType);
var messengerIdType = halflingAssembly.GetType("Halfling.Network.MessengerID", throwOnError: true)!;
var parseMessengerId = AccessTools.Method(messengerIdType, "Parse", new[] { typeof(string) })
    ?? throw new MissingMethodException(messengerIdType.FullName, "Parse(string)");
var senderId = parseMessengerId.Invoke(null, new object[] { "1" })!;
var otherId = parseMessengerId.Invoke(null, new object[] { "2" })!;
var firstForwardFilter = (Delegate)getOrCreateInputTickFilter.Invoke(null, new[] { testHost, senderId })!;
var secondForwardFilter = (Delegate)getOrCreateInputTickFilter.Invoke(null, new[] { testHost, senderId })!;
if (!ReferenceEquals(firstForwardFilter, secondForwardFilter)
    || (bool)firstForwardFilter.DynamicInvoke(senderId)!
    || !(bool)firstForwardFilter.DynamicInvoke(otherId)!)
{
    throw new InvalidOperationException("InputTick forwarding filter cache behavior mismatch.");
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

// PartsManager normally copies each bucket's callback list through TempList on
// every invocation. The replacement must reuse an unchanged snapshot while
// retaining vanilla's rule that mutations during a callback take effect only
// on the following invocation.
var updateCallbacksType = gameAssembly.GetType(
    "Cosmoteer.Ships.Parts.PartsManager+UpdateCallbacks",
    throwOnError: true)!;
var updateCallbacksInstance = Activator.CreateInstance(
    updateCallbacksType,
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    args: new object[] { 0 },
    culture: null)!;
var updateCallbacks = (IList)(updateCallbacksType.GetProperty("Callbacks")
    ?? throw new MissingMemberException(updateCallbacksType.FullName, "Callbacks"))
    .GetValue(updateCallbacksInstance)!;
var updateSnapshotPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.PartUpdateCallbackSnapshotPatch",
    throwOnError: true)!;
var getUpdateSnapshot = updateSnapshotPatchType.GetMethod(
    "GetSnapshot",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(updateSnapshotPatchType.FullName, "GetSnapshot");
var firstUpdateCount = 0;
var secondUpdateCount = 0;
var lateUpdateCount = 0;
SceneComponent.UpdateCallback? firstUpdate = null;
SceneComponent.UpdateCallback lateUpdate = _ => lateUpdateCount++;
firstUpdate = _ =>
{
    firstUpdateCount++;
    updateCallbacks.Remove(firstUpdate);
    updateCallbacks.Add(lateUpdate);
};
SceneComponent.UpdateCallback secondUpdate = _ => secondUpdateCount++;
updateCallbacks.Add(firstUpdate);
updateCallbacks.Add(secondUpdate);
var firstUpdateSnapshot = getUpdateSnapshot.Invoke(null, new[] { updateCallbacksInstance });
var reusedUpdateSnapshot = getUpdateSnapshot.Invoke(null, new[] { updateCallbacksInstance });
if (!ReferenceEquals(firstUpdateSnapshot, reusedUpdateSnapshot))
{
    throw new InvalidOperationException("An unchanged update-callback list did not reuse its snapshot.");
}
var updateCallbacksTarget = AccessTools.Method(updateCallbacksType, "Update")
    ?? throw new MissingMethodException(updateCallbacksType.FullName, "Update");
updateCallbacksTarget.Invoke(updateCallbacksInstance, new object?[] { null });
if (firstUpdateCount != 1 || secondUpdateCount != 1 || lateUpdateCount != 0)
{
    throw new InvalidOperationException(
        "Update-callback mutations affected the active snapshot instead of the next invocation.");
}
var rebuiltUpdateSnapshot = getUpdateSnapshot.Invoke(null, new[] { updateCallbacksInstance });
if (ReferenceEquals(firstUpdateSnapshot, rebuiltUpdateSnapshot))
{
    throw new InvalidOperationException("A mutated update-callback list reused its stale snapshot.");
}
updateCallbacksTarget.Invoke(updateCallbacksInstance, new object?[] { null });
if (firstUpdateCount != 1 || secondUpdateCount != 2 || lateUpdateCount != 1)
{
    throw new InvalidOperationException("The rebuilt update-callback snapshot has incorrect contents.");
}

var fixedCallbacksType = gameAssembly.GetType(
    "Cosmoteer.Ships.Parts.PartsManager+FixedUpdateCallbacks",
    throwOnError: true)!;
var fixedCallbacksInstance = Activator.CreateInstance(
    fixedCallbacksType,
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    args: new object[] { 0 },
    culture: null)!;
var fixedCallbacks = (IList)(fixedCallbacksType.GetProperty("Callbacks")
    ?? throw new MissingMemberException(fixedCallbacksType.FullName, "Callbacks"))
    .GetValue(fixedCallbacksInstance)!;
var fixedSnapshotPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.PartFixedUpdateCallbackSnapshotPatch",
    throwOnError: true)!;
var getFixedSnapshot = fixedSnapshotPatchType.GetMethod(
    "GetSnapshot",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(fixedSnapshotPatchType.FullName, "GetSnapshot");
SceneComponent.FixedUpdateCallback fixedCallback = (_, _) => { };
fixedCallbacks.Add(fixedCallback);
var firstFixedSnapshot = getFixedSnapshot.Invoke(null, new[] { fixedCallbacksInstance });
var reusedFixedSnapshot = getFixedSnapshot.Invoke(null, new[] { fixedCallbacksInstance });
if (!ReferenceEquals(firstFixedSnapshot, reusedFixedSnapshot))
{
    throw new InvalidOperationException("An unchanged fixed-update callback list did not reuse its snapshot.");
}
fixedCallbacks.Remove(fixedCallback);
var rebuiltFixedSnapshot = getFixedSnapshot.Invoke(null, new[] { fixedCallbacksInstance });
if (ReferenceEquals(firstFixedSnapshot, rebuiltFixedSnapshot)
    || ((Array)rebuiltFixedSnapshot!).Length != 0)
{
    throw new InvalidOperationException("A changed fixed-update callback list retained its stale snapshot.");
}
var fixedInvocationCount = 0;
fixedCallbacks.Add((SceneComponent.FixedUpdateCallback)((_, _) => fixedInvocationCount++));
var fixedCallbacksTarget = AccessTools.Method(fixedCallbacksType, "FixedUpdate")
    ?? throw new MissingMethodException(fixedCallbacksType.FullName, "FixedUpdate");
fixedCallbacksTarget.Invoke(fixedCallbacksInstance, new object?[] { null, null });
if (fixedInvocationCount != 1)
{
    throw new InvalidOperationException("The fixed-update callback snapshot was not invoked exactly once.");
}

var streamCopyInfo = Harmony.GetPatchInfo(streamCopyTarget)
    ?? throw new InvalidOperationException("Harmony did not patch Stream.CopyTo(Stream).");
if (!streamCopyInfo.Prefixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected multiplayer stream-copy capacity prefix was not installed.");
}
var receiveCapacityInfo = Harmony.GetPatchInfo(startDataStreamTarget)
    ?? throw new InvalidOperationException("Harmony did not patch ClientLaunchFlow.StartDataStreamRpc(long).");
if (!receiveCapacityInfo.Postfixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected multiplayer receive-buffer capacity postfix was not installed.");
}
var resyncReceiveCapacityInfo = Harmony.GetPatchInfo(startResyncDataStreamTarget)
    ?? throw new InvalidOperationException("Harmony did not patch ClientResyncFlow.StartDataStreamRpc(long).");
if (!resyncReceiveCapacityInfo.Postfixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected multiplayer resync receive-buffer capacity postfix was not installed.");
}
foreach (var target in resyncTimingTargets)
{
    var info = Harmony.GetPatchInfo(target)
        ?? throw new InvalidOperationException($"Harmony did not patch resync worker {target.Name}.");
    if (!info.Prefixes.Any(patch => patch.owner == smokeId)
        || !info.Finalizers.Any(patch => patch.owner == smokeId))
    {
        throw new InvalidOperationException($"Expected resync timing patches were not installed on {target.Name}.");
    }
}

var channelStreamType = halflingAssembly.GetType("Halfling.Network.ChannelStream", throwOnError: true)!;
var channelInputBufferField = channelStreamType.GetField(
    "_inBuf",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingFieldException(channelStreamType.FullName, "_inBuf");
var channelOutputBufferField = channelStreamType.GetField(
    "_outBuf",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingFieldException(channelStreamType.FullName, "_outBuf");
var testChannel = (Stream)RuntimeHelpers.GetUninitializedObject(channelStreamType);
var testPayload = Enumerable.Range(0, 65553).Select(index => (byte)(index * 31)).ToArray();
channelInputBufferField.SetValue(testChannel, new MemoryStream(testPayload, writable: false));
var streamCapacityPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.MultiplayerStreamCopyCapacityPatch",
    throwOnError: true)!;
var preallocateIncoming = streamCapacityPatchType.GetMethod(
    "PreallocateIncoming",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(streamCapacityPatchType.FullName, "PreallocateIncoming");
var streamCopyPrefix = streamCapacityPatchType.GetMethod(
    "Prefix",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(streamCapacityPatchType.FullName, "Prefix");
var expandableInput = new MemoryStream();
expandableInput.Write(testPayload);
expandableInput.Position = 0;
channelInputBufferField.SetValue(testChannel, expandableInput);
preallocateIncoming.Invoke(null, new object[] { testChannel, 131071L });
if (expandableInput.Capacity != 131071 || !expandableInput.ToArray().SequenceEqual(testPayload))
{
    throw new InvalidOperationException(
        $"Multiplayer input preallocation mismatch: capacity={expandableInput.Capacity}, length={expandableInput.Length}.");
}
var outgoingChannel = (Stream)RuntimeHelpers.GetUninitializedObject(channelStreamType);
var outgoingBuffer = new MemoryStream();
outgoingBuffer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
channelOutputBufferField.SetValue(outgoingChannel, outgoingBuffer);
using var serializedGame = new MemoryStream(testPayload, writable: false);
serializedGame.Position = 17;
streamCopyPrefix.Invoke(null, new object[] { serializedGame, outgoingChannel });
var expectedOutgoingCapacity = outgoingBuffer.Length + serializedGame.Length - serializedGame.Position;
if (outgoingBuffer.Capacity != expectedOutgoingCapacity
    || !outgoingBuffer.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7 }))
{
    throw new InvalidOperationException(
        $"Multiplayer output preallocation mismatch: capacity={outgoingBuffer.Capacity}, " +
        $"expected={expectedOutgoingCapacity}, length={outgoingBuffer.Length}.");
}
using (var copied = new MemoryStream())
{
    var sourceArray = expandableInput.GetBuffer();
    testChannel.CopyTo(copied);
    expandableInput.Dispose();
    if (copied.Capacity != testPayload.Length
        || copied.CanWrite
        || !ReferenceEquals(sourceArray, copied.GetBuffer())
        || !copied.ToArray().SequenceEqual(testPayload))
    {
        throw new InvalidOperationException(
            $"Multiplayer zero-copy receive mismatch: capacity={copied.Capacity}, " +
            $"length={copied.Length}, writable={copied.CanWrite}.");
    }
}
var unmarkedChannel = (Stream)RuntimeHelpers.GetUninitializedObject(channelStreamType);
var unmarkedInput = new MemoryStream();
unmarkedInput.Write(testPayload);
unmarkedInput.Position = 0;
channelInputBufferField.SetValue(unmarkedChannel, unmarkedInput);
using (var ordinaryCopy = new MemoryStream())
{
    unmarkedChannel.CopyTo(ordinaryCopy);
    if (!ordinaryCopy.CanWrite
        || ReferenceEquals(unmarkedInput.GetBuffer(), ordinaryCopy.GetBuffer())
        || !ordinaryCopy.ToArray().SequenceEqual(testPayload))
    {
        throw new InvalidOperationException("An unmarked ChannelStream did not preserve ordinary copy semantics.");
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

var resourceSearchPatchInfo = Harmony.GetPatchInfo(resourceSearchTarget)
    ?? throw new InvalidOperationException("Harmony did not patch ResourceManager.SearchForSources(SinkInfo).");
if (!resourceSearchPatchInfo.Transpilers.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException(
        "Expected Emmanim traversal transpiler was not installed on ResourceManager.SearchForSources(SinkInfo).");
}

var updateSinkJobsPatchInfo = Harmony.GetPatchInfo(updateSinkJobsTarget)
    ?? throw new InvalidOperationException("Harmony did not patch ResourceManager.UpdateSinkJobs(Time).");
if (!updateSinkJobsPatchInfo.Prefixes.Any(patch => patch.owner == smokeId)
    || !updateSinkJobsPatchInfo.Finalizers.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException(
        "Expected Emmanim snapshot prefix/finalizer was not installed on ResourceManager.UpdateSinkJobs(Time).");
}
var unmetDesiredPatchInfo = Harmony.GetPatchInfo(unmetDesiredTarget)
    ?? throw new InvalidOperationException("Harmony did not patch BaseResourceStorage unmet-desired helper.");
if (!unmetDesiredPatchInfo.Prefixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException(
        "Expected Emmanim snapshot prefix was not installed on BaseResourceStorage unmet-desired helper.");
}

var atlasPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.AtlasQuadRedundantWritePatch",
    throwOnError: true)!;
var atlasIdentical = atlasPatchType.GetMethod(
    "AreIdentical",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(atlasPatchType.FullName, "AreIdentical");
var atlasQuadType = gameAssembly.GetType(
    "Cosmoteer.Ships.Rendering.AtlasQuad",
    throwOnError: true)!;
var leftAtlasQuad = Activator.CreateInstance(atlasQuadType)!;
var rightAtlasQuad = Activator.CreateInstance(atlasQuadType)!;
if (!(bool)atlasIdentical.Invoke(null, new[] { leftAtlasQuad, rightAtlasQuad })!)
{
    throw new InvalidOperationException("Equal AtlasQuad values were not recognized as identical.");
}
var v1Field = AccessTools.Field(atlasQuadType, "V1")
    ?? throw new MissingFieldException(atlasQuadType.FullName, "V1");
var alteredVertex = v1Field.GetValue(rightAtlasQuad)!;
var animClampField = AccessTools.Field(alteredVertex.GetType(), "AnimClamp")
    ?? throw new MissingFieldException(alteredVertex.GetType().FullName, "AnimClamp");
animClampField.SetValue(alteredVertex, 1);
v1Field.SetValue(rightAtlasQuad, alteredVertex);
if ((bool)atlasIdentical.Invoke(null, new[] { leftAtlasQuad, rightAtlasQuad })!)
{
    throw new InvalidOperationException("Different AtlasQuad values were incorrectly treated as identical.");
}

var onSelfActivatedInfo = Harmony.GetPatchInfo(onSelfActivatedTarget)
    ?? throw new InvalidOperationException("Harmony did not patch PaintToolbox.OnSelfActivated.");
if (!onSelfActivatedInfo.Postfixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected lazy paint-picker postfix was not installed on PaintToolbox.OnSelfActivated.");
}

var addDecalsGroupInfo = Harmony.GetPatchInfo(addDecalsGroupTarget)
    ?? throw new InvalidOperationException("Harmony did not patch PaintToolbox.AddDecalsGroup.");
if (!addDecalsGroupInfo.Prefixes.Any(patch => patch.owner == smokeId)
    || !addDecalsGroupInfo.Postfixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected lazy decal-group prefix/postfix was not installed on PaintToolbox.AddDecalsGroup.");
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

// ResourceIDComparer.Compare must be transpiled, and the transpiler must have
// matched the real shape rather than silently falling back: _vanillaGetIndex is
// only assigned once every guard passed and the index local function resolved.
var comparerType = AccessTools.TypeByName("Cosmoteer.Resources.ResourceIDComparer")
    ?? throw new InvalidOperationException("Cosmoteer.Resources.ResourceIDComparer was not found.");
var compareTarget = AccessTools.DeclaredMethod(comparerType, "Compare")
    ?? throw new InvalidOperationException("ResourceIDComparer.Compare was not found.");
var compareInfo = Harmony.GetPatchInfo(compareTarget)
    ?? throw new InvalidOperationException("ResourceIDComparer.Compare was not patched.");
if (!compareInfo.Transpilers.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("ResourceIDComparer.Compare transpiler was not installed.");
}

var comparerPatchType = typeof(EntryPoint).Assembly
    .GetType("EmmanimLagFix.Code.ResourceIdComparerAllocationPatch", throwOnError: true)!;
if (AccessTools.Field(comparerPatchType, "_vanillaGetIndex").GetValue(null) == null)
{
    throw new InvalidOperationException(
        "ResourceIDComparer.Compare fell back to vanilla: the method shape or its index "
        + "local function did not match on this game build.");
}

// Seed the cache so the miss path, which needs a loaded GameApp.Rules, is never
// taken, then confirm the replaced body still orders purely by cached index.
var resourceIdType = compareTarget.GetParameters()[0].ParameterType;
var comparerCacheType = comparerPatchType
    .GetNestedType("Cache`1", BindingFlags.NonPublic)!
    .MakeGenericType(resourceIdType);
var comparerIndexes = comparerCacheType
    .GetField("Indexes", BindingFlags.NonPublic | BindingFlags.Static)!
    .GetValue(null)!;
var resourceIdCtor = resourceIdType.GetConstructor(new[] { typeof(string) })!;
var smokeIdA = resourceIdCtor.Invoke(new object[] { "emmanim_smoke_resource_a" });
var smokeIdB = resourceIdCtor.Invoke(new object[] { "emmanim_smoke_resource_b" });
var comparerTryAdd = comparerIndexes.GetType().GetMethod("TryAdd")!;
comparerTryAdd.Invoke(comparerIndexes, new[] { smokeIdA, (object)5 });
comparerTryAdd.Invoke(comparerIndexes, new[] { smokeIdB, (object)2 });

var comparerInstance = AccessTools.Field(comparerType, "Instance").GetValue(null);
var compareAB = (int)compareTarget.Invoke(comparerInstance, new[] { smokeIdA, smokeIdB })!;
var compareBA = (int)compareTarget.Invoke(comparerInstance, new[] { smokeIdB, smokeIdA })!;
var compareAA = (int)compareTarget.Invoke(comparerInstance, new[] { smokeIdA, smokeIdA })!;
if (compareAB <= 0 || compareBA >= 0 || compareAA != 0)
{
    throw new InvalidOperationException(
        $"ResourceIDComparer ordering changed: a-b={compareAB}, b-a={compareBA}, a-a={compareAA}.");
}

// ThrusterManager's acceleration cache must have its guard hoisted in front of
// the activation-snapshot construction. Applied is only set once every shape
// check passed, so it distinguishes a real rewrite from the silent fallback.
var thrusterManagerType = AccessTools.TypeByName("Cosmoteer.Ships.Parts.Thrusters.ThrusterManager")
    ?? throw new InvalidOperationException("Cosmoteer.Ships.Parts.Thrusters.ThrusterManager was not found.");
var thrusterCacheTarget = AccessTools.DeclaredMethod(
        thrusterManagerType, "CalculateMaximumAccelerationAndRampTimeCached")
    ?? throw new InvalidOperationException(
        "ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached was not found.");
var thrusterCacheInfo = Harmony.GetPatchInfo(thrusterCacheTarget)
    ?? throw new InvalidOperationException(
        "ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached was not patched.");
if (!thrusterCacheInfo.Transpilers.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException(
        "ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached transpiler was not installed.");
}

var thrusterPatchType = typeof(EntryPoint).Assembly
    .GetType("EmmanimLagFix.Code.ThrusterAccelerationCacheAllocationPatch", throwOnError: true)!;
if (AccessTools.Field(thrusterPatchType, "Applied").GetValue(null) is not true)
{
    throw new InvalidOperationException(
        "ThrusterManager's cache guard was not hoisted: the method shape did not match "
        + "on this game build, so the throwaway activation snapshot is still built.");
}

// Every D3D11 shader-constant Update overload must have its boxing dirty check
// replaced by the typed one. PatchedCount is only incremented once every shape
// check passed, so it distinguishes a real rewrite from the silent fallback.
var shaderConstantType = halflingPlatformAssembly.GetType(
        "Halfling.Graphics.D3D11.D3D11Shader+D3D11BufferConstant", throwOnError: true)!;
var shaderConstantUpdates = shaderConstantType
    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.DeclaredOnly)
    .Where(m => m.Name == "Update" && m.ReturnType == typeof(void)
        && m.GetParameters().Length == 2)
    .ToArray();
if (shaderConstantUpdates.Length != 8)
{
    throw new InvalidOperationException(
        $"Expected 8 shader-constant Update overloads, found {shaderConstantUpdates.Length}.");
}

foreach (var update in shaderConstantUpdates)
{
    var info = Harmony.GetPatchInfo(update)
        ?? throw new InvalidOperationException(
            $"Shader constant Update({update.GetParameters()[1].ParameterType.Name}) was not patched.");
    if (!info.Transpilers.Any(patch => patch.owner == smokeId))
    {
        throw new InvalidOperationException(
            $"Shader constant Update({update.GetParameters()[1].ParameterType.Name}) "
            + "transpiler was not installed.");
    }
}

var shaderPatchType = typeof(EntryPoint).Assembly
    .GetType("EmmanimLagFix.Code.ShaderConstantBoxingPatch", throwOnError: true)!;
var shaderPatchedCount = (int)AccessTools.Field(shaderPatchType, "PatchedCount").GetValue(null)!;
if (shaderPatchedCount != 8)
{
    throw new InvalidOperationException(
        $"Only {shaderPatchedCount} of 8 shader-constant dirty checks were rewritten: the "
        + "method shape did not match on this game build, so the per-update box remains.");
}

// The substitution's one assumption is that each constant type's boxing
// Equals(object) agrees with its typed IEquatable<T>.Equals. Check it on real
// values of the actual types this build uses, rather than trusting the shape.
var shaderPatchedTypes = (List<Type>)AccessTools.Field(shaderPatchType, "PatchedValueTypes").GetValue(null)!;
foreach (var constantValueType in shaderPatchedTypes)
{
    var equatable = typeof(IEquatable<>).MakeGenericType(constantValueType);
    if (!equatable.IsAssignableFrom(constantValueType))
    {
        throw new InvalidOperationException(
            $"{constantValueType.FullName} does not implement IEquatable<T>; the typed "
            + "comparison substituted for the boxing one does not exist.");
    }

    var typedEquals = AccessTools.Method(constantValueType, "Equals", new[] { constantValueType })
        ?? constantValueType.GetInterfaceMap(equatable).TargetMethods
            .Single(m => m.GetParameters()[0].ParameterType == constantValueType);

    var size = System.Runtime.InteropServices.Marshal.SizeOf(constantValueType);
    var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
    try
    {
        for (var offset = 0; offset < size; offset++)
        {
            System.Runtime.InteropServices.Marshal.WriteByte(buffer, offset, 0);
        }

        var zero = System.Runtime.InteropServices.Marshal.PtrToStructure(buffer, constantValueType)!;
        var sameAsZero = System.Runtime.InteropServices.Marshal.PtrToStructure(buffer, constantValueType)!;
        for (var offset = 0; offset < size; offset++)
        {
            System.Runtime.InteropServices.Marshal.WriteByte(buffer, offset, 0x3F);
        }

        var other = System.Runtime.InteropServices.Marshal.PtrToStructure(buffer, constantValueType)!;
        foreach (var (left, right) in new[]
        {
            (zero, sameAsZero), (zero, other), (other, zero), (other, other)
        })
        {
            var boxed = left.Equals(right);
            var typed = (bool)typedEquals.Invoke(left, new[] { right })!;
            if (boxed != typed)
            {
                throw new InvalidOperationException(
                    $"{constantValueType.FullName}: boxing Equals(object) returned {boxed} but "
                    + $"the typed Equals returned {typed}; the shader-constant substitution "
                    + "would change which updates are considered dirty.");
            }
        }
    }
    finally
    {
        System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
    }
}

// TextBuilder.BuildLines must take the plain-text branch for text with no
// markup. Applied is only set once the branch condition was really rewritten.
var textBuilderType = halflingAssembly.GetType("Halfling.Graphics.Text.TextBuilder", throwOnError: true)!;
var buildLinesTarget = AccessTools.DeclaredMethod(textBuilderType, "BuildLines")
    ?? throw new InvalidOperationException("TextBuilder.BuildLines was not found.");
var buildLinesInfo = Harmony.GetPatchInfo(buildLinesTarget)
    ?? throw new InvalidOperationException("TextBuilder.BuildLines was not patched.");
if (!buildLinesInfo.Transpilers.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("TextBuilder.BuildLines transpiler was not installed.");
}

var textPatchType = typeof(EntryPoint).Assembly
    .GetType("EmmanimLagFix.Code.TextBuilderPlainTextPatch", throwOnError: true)!;
if (AccessTools.Field(textPatchType, "Applied").GetValue(null) is not true)
{
    throw new InvalidOperationException(
        "TextBuilder.BuildLines was not rewritten: the branch shape did not match on this "
        + "game build, so plain text still builds an XmlReader.");
}

// The substitution's assumption is that for the strings it accepts, the XML
// reader hands back one text node holding the identical string. Check that
// against a real XmlReader with the game's own reader settings, rather than
// trusting the character test.
var isPlainText = AccessTools.DeclaredMethod(textPatchType, "IsPlainText")
    ?? throw new InvalidOperationException("TextBuilderPlainTextPatch.IsPlainText was not found.");
var xmlReaderSettings = (System.Xml.XmlReaderSettings)AccessTools
    .Field(textBuilderType, "XML_READER_SETTINGS").GetValue(null)!;

const char tab = (char)9;
const char lf = (char)10;
const char cr = (char)13;
const char quote = (char)34;
const char soh = (char)1;

// Strings the plain-text branch must reproduce exactly.
var plainSamples = new[]
{
    string.Empty,
    " ",
    "Hello",
    "Hello, world!",
    "12,345 / 67,890",
    "자원 전송 중",
    "line one" + lf + "line two",
    "tab" + tab + "here",
    "a > b",
    "100%",
    "'quoted'",
    quote + "quoted" + quote
};

// Strings that must keep vanilla's XML path.
var xmlSamples = new[]
{
    "<b>bold</b>",
    "a &amp; b",
    "crlf" + cr + lf + "here",
    soh + "control",
    char.ConvertFromUtf32(0x1F680) + " rocket",
    new string('x', 4096)
};

foreach (var sample in plainSamples)
{
    if (isPlainText.Invoke(null, new object?[] { sample }) is not true)
    {
        throw new InvalidOperationException(
            "IsPlainText rejected a string with no markup, so it still builds an XmlReader: "
            + System.Text.Json.JsonSerializer.Serialize(sample));
    }

    var nodes = new List<string>();
    using (var reader = System.Xml.XmlReader.Create(new StringReader(sample), xmlReaderSettings))
    {
        while (reader.Read())
        {
            if (reader.NodeType is System.Xml.XmlNodeType.Text
                or System.Xml.XmlNodeType.Whitespace)
            {
                nodes.Add(reader.Value);
            }
            else
            {
                throw new InvalidOperationException(
                    "IsPlainText accepted a string the XML reader turns into a "
                    + reader.NodeType + " node, so the plain branch would drop formatting: "
                    + System.Text.Json.JsonSerializer.Serialize(sample));
            }
        }
    }

    var parsed = string.Concat(nodes);
    if (parsed != sample && sample.Length != 0)
    {
        throw new InvalidOperationException(
            "IsPlainText accepted "
            + System.Text.Json.JsonSerializer.Serialize(sample)
            + " but the XML reader returns "
            + System.Text.Json.JsonSerializer.Serialize(parsed)
            + "; the plain branch would render different text.");
    }
}

foreach (var sample in xmlSamples)
{
    if (isPlainText.Invoke(null, new object?[] { sample }) is true)
    {
        throw new InvalidOperationException(
            "IsPlainText accepted a string that must keep vanilla's XML path: "
            + System.Text.Json.JsonSerializer.Serialize(sample));
    }
}

// PartGraphics.UpdateColor must lose both its boxing dirty test and its
// self-unsubscribe. Applied is set only when both rewrites really happened.
var partGraphicsType = gameAssembly.GetType(
    "Cosmoteer.Ships.Parts.Graphics.PartGraphics", throwOnError: true)!;
var updateColorTarget = AccessTools.DeclaredMethod(partGraphicsType, "UpdateColor")
    ?? throw new InvalidOperationException("PartGraphics.UpdateColor was not found.");
var updateColorInfo = Harmony.GetPatchInfo(updateColorTarget)
    ?? throw new InvalidOperationException("PartGraphics.UpdateColor was not patched.");
if (!updateColorInfo.Transpilers.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("PartGraphics.UpdateColor transpiler was not installed.");
}

var colorPatchType = typeof(EntryPoint).Assembly
    .GetType("EmmanimLagFix.Code.PartGraphicsColorEventPatch", throwOnError: true)!;
if (AccessTools.Field(colorPatchType, "Applied").GetValue(null) is not true)
{
    throw new InvalidOperationException(
        "PartGraphics.UpdateColor was not rewritten, so every settling part still scans the "
        + "whole BeforeDraw invocation list: "
        + (AccessTools.Field(colorPatchType, "FailureReason").GetValue(null) ?? "no reason recorded"));
}

// The rewrite rests on two claims about the flags enum that the IL does not
// state: that it is backed by int32, and that Dirty is the single bit 1 so a
// mask is exactly HasFlag. Check them against the real type.
var colorUpdateFlags = partGraphicsType.GetNestedType(
        "ColorUpdateFlags", BindingFlags.NonPublic | BindingFlags.Public)
    ?? throw new InvalidOperationException("PartGraphics.ColorUpdateFlags was not found.");
if (Enum.GetUnderlyingType(colorUpdateFlags) != typeof(int))
{
    throw new InvalidOperationException(
        "PartGraphics.ColorUpdateFlags is backed by "
        + Enum.GetUnderlyingType(colorUpdateFlags).Name
        + ", not int32; the substituted `and` would read the wrong width.");
}

var dirtyValue = Convert.ToInt32(Enum.Parse(colorUpdateFlags, "Dirty"));
var registeredValue = Convert.ToInt32(Enum.Parse(colorUpdateFlags, "Registered"));
if (dirtyValue != 1 || registeredValue != 2)
{
    throw new InvalidOperationException(
        $"PartGraphics.ColorUpdateFlags has Dirty={dirtyValue}, Registered={registeredValue}; "
        + "the rewrite assumes Dirty=1 (a single bit, so `and` equals HasFlag) and "
        + "Registered=2 (so vanilla's clear mask is -3).");
}

// Leaving handlers subscribed is only safe because detaching still removes
// them, which it does by testing the Registered flag this patch preserves.
var detaching = AccessTools.DeclaredMethod(partGraphicsType, "OnPartDetaching")
    ?? throw new InvalidOperationException(
        "PartGraphics.OnPartDetaching was not found, so nothing would ever unsubscribe a "
        + "settled colour handler.");
if (Harmony.GetPatchInfo(detaching)?.Transpilers.Any(patch => patch.owner == smokeId) == true)
{
    throw new InvalidOperationException(
        "PartGraphics.OnPartDetaching was rewritten; it must keep vanilla's unsubscribe.");
}

// XA2StreamingSound.ReadSamples throws on a negative start sample, which kills
// the whole process from the audio thread. Applied is set only once every shape
// guard passed, so an installed-but-fallen-back patch still fails here.
var streamingPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.StreamingSoundSampleStartPatch",
    throwOnError: true)!;
if (AccessTools.Field(streamingPatchType, "Applied").GetValue(null) is not true)
{
    throw new InvalidOperationException(
        "The streaming-sound start guard fell back to vanilla behaviour, so a starved "
        + "audio thread would still crash the game.");
}
var streamingSoundType = AccessTools.TypeByName("Halfling.Audio.XA2.XA2StreamingSound")
    ?? throw new InvalidOperationException("Halfling.Audio.XA2.XA2StreamingSound was not found.");
var readSamplesTarget = AccessTools.Method(streamingSoundType, "ReadSamples")
    ?? throw new MissingMethodException(streamingSoundType.FullName, "ReadSamples");
if (Harmony.GetPatchInfo(readSamplesTarget)?.Prefixes.Any(patch => patch.owner == smokeId) != true)
{
    throw new InvalidOperationException(
        "Expected Emmanim prefix was not installed on XA2StreamingSound.ReadSamples.");
}
// The correction must reproduce the wrap-around UpdateBuffers intended, which is
// what C#'s sign-preserving % gets wrong, and must leave in-range starts alone
// - including the end-of-sound value vanilla itself accepts.
var streamingInRange = AccessTools.Method(streamingPatchType, "InRange")
    ?? throw new MissingMethodException(streamingPatchType.FullName, "InRange");
foreach (var (start, total, expected) in new[]
{
    (-1L, 1000L, 999L),
    (-1500L, 1000L, 500L),
    (-1000L, 1000L, 0L),
    (0L, 1000L, 0L),
    (999L, 1000L, 999L),
    (1000L, 1000L, 1000L),
    (1001L, 1000L, 1L),
    (-1L, 0L, 0L),
})
{
    var corrected = (long)streamingInRange.Invoke(null, new object?[] { start, total })!;
    if (corrected != expected)
    {
        throw new InvalidOperationException(
            $"Start sample {start} of {total} became {corrected}, expected {expected}.");
    }
}

// SimRoot posts every worker-thread scene-graph write to one ConcurrentQueue,
// which sixteen FastParallel threads contend for. Applied is set only once the
// enqueue site was really rewritten to the sharded one.
var shardingPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.NonDeterministicQueueShardingPatch",
    throwOnError: true)!;
if (AccessTools.Field(shardingPatchType, "Applied").GetValue(null) is not true)
{
    throw new InvalidOperationException(
        "The non-deterministic queue sharding fell back to vanilla behaviour, so every "
        + "worker still contends for one queue tail.");
}
var simRootType = AccessTools.TypeByName("Cosmoteer.Simulation.SimRoot")
    ?? throw new InvalidOperationException("Cosmoteer.Simulation.SimRoot was not found.");
var enqueueTarget = AccessTools.DeclaredMethod(simRootType, "EnqueueNonDeterministic")
    ?? throw new MissingMethodException(simRootType.FullName, "EnqueueNonDeterministic");
if (Harmony.GetPatchInfo(enqueueTarget)?.Transpilers.Any(patch => patch.owner == smokeId) != true)
{
    throw new InvalidOperationException(
        "Expected Emmanim transpiler was not installed on SimRoot.EnqueueNonDeterministic.");
}
var executeQueuedTarget = AccessTools.DeclaredMethod(simRootType, "ExecuteQueued")
    ?? throw new MissingMethodException(simRootType.FullName, "ExecuteQueued");
if (Harmony.GetPatchInfo(executeQueuedTarget)?.Postfixes.Any(patch => patch.owner == smokeId) != true)
{
    throw new InvalidOperationException(
        "Expected Emmanim postfix was not installed on SimRoot.ExecuteQueued, so sharded "
        + "callbacks would never run.");
}
var simDisposeTarget = AccessTools.DeclaredMethod(simRootType, nameof(IDisposable.Dispose))
    ?? throw new MissingMethodException(simRootType.FullName, nameof(IDisposable.Dispose));
if (Harmony.GetPatchInfo(simDisposeTarget)?.Postfixes.Any(patch => patch.owner == smokeId) != true)
{
    throw new InvalidOperationException(
        "Expected Emmanim postfix was not installed on SimRoot.Dispose, so a resync could "
        + "leave the old simulation in the queue-shard hot cache.");
}

// Every callback must run exactly once, and each thread's own callbacks must
// still run in the order that thread posted them - the only ordering vanilla's
// single queue actually establishes between concurrent producers.
var shardedEnqueue = AccessTools.DeclaredMethod(shardingPatchType, "ShardedEnqueue")
    ?? throw new MissingMethodException(shardingPatchType.FullName, "ShardedEnqueue");
var shardedDrain = AccessTools.DeclaredMethod(shardingPatchType, "Drain")
    ?? throw new MissingMethodException(shardingPatchType.FullName, "Drain");
var releaseShards = AccessTools.DeclaredMethod(shardingPatchType, "Release")
    ?? throw new MissingMethodException(shardingPatchType.FullName, "Release");
var fakeSim = new object();
var ranPerThread = new System.Collections.Concurrent.ConcurrentDictionary<int, List<int>>();
// Parallel.For may run several iterations on one thread, so the ordinal has to
// be globally increasing rather than restarting per iteration; otherwise a
// thread that ran two iterations records a legitimately ordered drain as
// unsorted.
var postOrdinal = 0;
Parallel.For(0, 8, _ =>
{
    for (var i = 0; i < 250; i++)
    {
        var ordinal = Interlocked.Increment(ref postOrdinal);
        var poster = Environment.CurrentManagedThreadId;
        shardedEnqueue.Invoke(null, new object?[]
        {
            fakeSim,
            new Action(() => ranPerThread.GetOrAdd(poster, static _ => new List<int>()).Add(ordinal)),
        });
    }
});
shardedDrain.Invoke(null, new object?[] { fakeSim });
var ranTotal = ranPerThread.Values.Sum(posted => posted.Count);
if (ranTotal != 8 * 250)
{
    throw new InvalidOperationException(
        $"Sharded queue ran {ranTotal} callbacks, expected {8 * 250}.");
}
foreach (var posted in ranPerThread.Values)
{
    if (!posted.SequenceEqual(posted.OrderBy(ordinal => ordinal)))
    {
        throw new InvalidOperationException(
            "Sharded queue reordered one thread's own callbacks, which vanilla does not.");
    }
}
// A second drain must find nothing left behind.
shardedDrain.Invoke(null, new object?[] { fakeSim });
if (ranPerThread.Values.Sum(posted => posted.Count) != ranTotal)
{
    throw new InvalidOperationException("Sharded queue ran a callback twice.");
}

// Disposing a simulation must sever the strong hot-cache reference and discard
// callbacks belonging to that dead scene graph. A subsequent drain proves the
// removed weak-table value cannot be rediscovered.
var disposedSim = new object();
var disposedCallbackRan = false;
shardedEnqueue.Invoke(null, new object?[]
{
    disposedSim,
    new Action(() => disposedCallbackRan = true),
});
releaseShards.Invoke(null, new object?[] { disposedSim });
shardedDrain.Invoke(null, new object?[] { disposedSim });
if (disposedCallbackRan)
{
    throw new InvalidOperationException(
        "A callback owned by a disposed simulation survived queue-shard release.");
}
var hotAfterRelease = AccessTools.Field(shardingPatchType, "_hot").GetValue(null);
if (hotAfterRelease != null)
{
    var hotSim = AccessTools.Field(hotAfterRelease.GetType(), "Sim").GetValue(hotAfterRelease);
    if (ReferenceEquals(hotSim, disposedSim))
    {
        throw new InvalidOperationException(
            "The queue-shard hot cache still strongly references the disposed simulation.");
    }
}

// The codex evaluates every unshown page's IronPython show-condition against a
// fresh script scope once per frame, and the dynamic methods that rebinding
// emits come back on the finalizer thread inside long frames.
var codexHudType = AccessTools.TypeByName("Cosmoteer.Codex.CodexHudGui")
    ?? throw new InvalidOperationException("Cosmoteer.Codex.CodexHudGui was not found.");
var codexUpdateTarget = AccessTools.Method(codexHudType, "OnUpdatingUIState")
    ?? throw new MissingMethodException(codexHudType.FullName, "OnUpdatingUIState");
if (Harmony.GetPatchInfo(codexUpdateTarget)?.Prefixes.Any(patch => patch.owner == smokeId) != true)
{
    throw new InvalidOperationException(
        "Expected Emmanim prefix was not installed on CodexHudGui.OnUpdatingUIState.");
}
// The page loop must still be reached, just not on every frame: the first call
// runs, an immediate second call is skipped, and a separate GUI keeps its own
// gate rather than inheriting another instance's.
var codexPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.CodexConditionThrottlePatch",
    throwOnError: true)!;
var codexPrefix = AccessTools.DeclaredMethod(codexPatchType, "Prefix")
    ?? throw new MissingMethodException(codexPatchType.FullName, "Prefix");
var codexGuiA = new object();
var codexGuiB = new object();
if (codexPrefix.Invoke(null, new[] { codexGuiA }) is not true)
{
    throw new InvalidOperationException("The codex throttle skipped its very first update.");
}
if (codexPrefix.Invoke(null, new[] { codexGuiA }) is not false)
{
    throw new InvalidOperationException(
        "The codex throttle ran twice in the same frame, so it throttles nothing.");
}
if (codexPrefix.Invoke(null, new[] { codexGuiB }) is not true)
{
    throw new InvalidOperationException(
        "One CodexHudGui's update suppressed another instance's; the gate is not per-instance.");
}

// Rewritten IL only fails when the method is compiled, which would otherwise be
// on a moving ship mid-game. Force it here so a malformed branch or an
// unbalanced stack is an immediate InvalidProgramException instead.
foreach (var rewritten in new MethodBase[]
{
    thrusterCacheTarget, compareTarget, buildLinesTarget, updateColorTarget, enqueueTarget,
}
    .Concat(shaderConstantUpdates))
{
    try
    {
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(rewritten.MethodHandle);
    }
    catch (Exception e)
    {
        throw new InvalidOperationException(
            $"Rewritten {rewritten.DeclaringType?.Name}.{rewritten.Name} failed to compile: {e.Message}", e);
    }
}

// The contiguous-set breadth-first search now runs an Emmanim replacement whose
// visited set is emptied in proportion to the sets actually visited, instead of
// zeroing a pooled hash set's whole bucket array on every search. Applied is set
// only when the vanilla target resolved with the expected signature and return
// type.
var searchSetsPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.PathContiguitySearchSetsPatch",
    throwOnError: true)!;
if (AccessTools.Property(searchSetsPatchType, "Applied")!.GetValue(null) is not true)
{
    throw new InvalidOperationException(
        "The contiguous-set search patch did not resolve its target, so every search still "
        + "clears the whole pooled bucket array.");
}

var pathContiguityType = gameAssembly.GetType(
    "Cosmoteer.Ships.Crew.Pathing.PathContiguityManager", throwOnError: true)!;
var contiguousSetType = gameAssembly.GetType(
    "Cosmoteer.Ships.Crew.Pathing.ContiguousPathSet", throwOnError: true)!;
var intRectType = halflingAssembly.GetType("Halfling.Geometry.IntRect", throwOnError: true)!;
var searchOriginsType = typeof(IReadOnlyList<>).MakeGenericType(
    typeof(ValueTuple<,>).MakeGenericType(contiguousSetType, intRectType));
var searchSetsTarget = pathContiguityType.GetMethod(
    "SearchSetsFrom",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    new[] { searchOriginsType, typeof(Nullable<>).MakeGenericType(intRectType) },
    modifiers: null)
    ?? throw new MissingMethodException(pathContiguityType.FullName, "SearchSetsFrom");
if (Harmony.GetPatchInfo(searchSetsTarget)?.Prefixes.Any(patch => patch.owner == smokeId) != true)
{
    throw new InvalidOperationException(
        "Expected Emmanim prefix was not installed on PathContiguityManager.SearchSetsFrom.");
}

// Dropping a visited mark would turn the breadth-first walk into an infinite
// one, so prove proportional cleanup leaves both dense and sparse rounds empty
// and that a recycled scratch treats previously seen sets as unseen. Reference
// equality is all the game's sets use, so uninitialized instances are valid keys.
var scratchType = searchSetsPatchType.GetNestedType("SearchScratch", BindingFlags.NonPublic)!;
var scratchRent = AccessTools.Method(scratchType, "Rent")!;
var scratchAdd = AccessTools.Method(scratchType, "Add")!;
var scratchRelease = AccessTools.Method(scratchType, "Release")!;
var visitedField = AccessTools.Field(scratchType, "_visited")!;

var sets = Enumerable.Range(0, 1024)
    .Select(_ => RuntimeHelpers.GetUninitializedObject(contiguousSetType))
    .ToArray();

foreach (var visitCount in new[] { sets.Length, 3 })
{
    var scratch = scratchRent.Invoke(null, null)!;
    for (var i = 0; i < visitCount; i++)
    {
        if (scratchAdd.Invoke(scratch, new[] { sets[i] }) is not true)
        {
            throw new InvalidOperationException(
                $"Visiting set {i} of {visitCount} was reported as already visited.");
        }

        if (scratchAdd.Invoke(scratch, new[] { sets[i] }) is not false)
        {
            throw new InvalidOperationException(
                $"Set {i} of {visitCount} was accepted twice, so the search would not terminate.");
        }
    }

    scratchRelease.Invoke(scratch, null);
    var visited = visitedField.GetValue(scratch)!;
    var remaining = (int)visited.GetType().GetProperty("Count")!.GetValue(visited)!;
    if (remaining != 0)
    {
        throw new InvalidOperationException(
            $"Releasing a scratch that visited {visitCount} sets left {remaining} behind.");
    }

    var reused = scratchRent.Invoke(null, null)!;
    if (!ReferenceEquals(reused, scratch))
    {
        throw new InvalidOperationException("The released search scratch was not pooled for reuse.");
    }

    if (scratchAdd.Invoke(reused, new object[] { sets[0] }) is not true)
    {
        throw new InvalidOperationException(
            "A recycled search scratch still considered a previously visited set as visited.");
    }

    scratchRelease.Invoke(reused, null);
}

// The resource source search now uses a generation-stamped identity set and
// leaves the pooled HashSet empty, instead of zeroing its retained bucket array
// once per sink per fixed update.
var sourceVisitedPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.ResourceSourceVisitedSetPatch",
    throwOnError: true)!;
if (AccessTools.Property(sourceVisitedPatchType, "Applied")!.GetValue(null) is not true)
{
    throw new InvalidOperationException(
        "The resource source visited-set patch did not match its target, so every sink still "
        + "clears the whole pooled bucket array.");
}

if (Harmony.GetPatchInfo(resourceSearchTarget)?.Transpilers.Any(patch => patch.owner == smokeId) != true)
{
    throw new InvalidOperationException(
        "Expected Emmanim transpiler was not installed on ResourceManager.SearchForSources(SinkInfo).");
}

try
{
    RuntimeHelpers.PrepareMethod(resourceSearchTarget.MethodHandle);
}
catch (Exception e)
{
    throw new InvalidOperationException(
        $"Rewritten ResourceManager.SearchForSources failed to compile: {e.Message}", e);
}

// A source left behind would be treated as already considered by the next sink,
// silently dropping it from that sink's candidates. Exercise resize plus a new
// generation and prove the real pooled set stays empty. The game's SourceInfo
// type uses reference equality, so uninitialized instances are valid keys.
var sourceInfoType = resourceManagerType.GetNestedType("SourceInfo", BindingFlags.NonPublic)
    ?? throw new TypeLoadException("ResourceManager.SourceInfo was not found.");
var allocTracked = AccessTools.Method(sourceVisitedPatchType, "AllocTracked")!;
var trackedAdd = AccessTools.Method(sourceVisitedPatchType, "TrackedAdd")!;
var setCountProperty = typeof(HashSet<>).MakeGenericType(sourceInfoType).GetProperty("Count")!;

var sources = Enumerable.Range(0, 1024)
    .Select(_ => RuntimeHelpers.GetUninitializedObject(sourceInfoType))
    .ToArray();

// Dense first to force the generation set to resize, then sparse to prove stale
// bucket heads from the previous generation are invisible.
foreach (var visitCount in new[] { sources.Length, 3, 0 })
{
    var trackedSet = allocTracked.Invoke(null, null)!;
    for (var i = 0; i < visitCount; i++)
    {
        if (trackedAdd.Invoke(null, new[] { trackedSet, sources[i] }) is not true)
        {
            throw new InvalidOperationException(
                $"Source {i} of {visitCount} was reported as already considered.");
        }

        if (trackedAdd.Invoke(null, new[] { trackedSet, sources[i] }) is not false)
        {
            throw new InvalidOperationException(
                $"Source {i} of {visitCount} was accepted twice, so the sink would double-count it.");
        }
    }

    ((IDisposable)trackedSet).Dispose();
    var leftOver = (int)setCountProperty.GetValue(trackedSet)!;
    if (leftOver != 0)
    {
        throw new InvalidOperationException(
            $"Disposing a set that considered {visitCount} sources left {leftOver} behind.");
    }
}

// A round the patch never saw allocated must still be emptied by Halfling's own
// deinitializer, which is the fallback for every shape it does not recognize.
var tempHashSetType = halflingAssembly.GetType("Halfling.Pooling.TempHashSet`1", throwOnError: true)!
    .MakeGenericType(sourceInfoType);
var untrackedSet = tempHashSetType.GetMethod("Alloc", BindingFlags.Public | BindingFlags.Static, binder: null, Type.EmptyTypes, modifiers: null)!
    .Invoke(null, null)!;
var plainAdd = tempHashSetType.GetMethod("Add", new[] { sourceInfoType })!;
for (var i = 0; i < 5; i++)
{
    plainAdd.Invoke(untrackedSet, new[] { sources[i] });
}

((IDisposable)untrackedSet).Dispose();
var untrackedLeftOver = (int)setCountProperty.GetValue(untrackedSet)!;
if (untrackedLeftOver != 0)
{
    throw new InvalidOperationException(
        $"An untracked set kept {untrackedLeftOver} sources, so the vanilla fallback was lost.");
}


// Every status dictionary the game enumerates through an interface now goes
// through a pooled wrapper around the dictionary's own struct enumerator.
// Applied is set only once every one of the thirteen target methods resolved
// and really had a call rewritten.
var statusPoolPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.StatusEnumeratorPoolPatch",
    throwOnError: true)!;
if (AccessTools.Property(statusPoolPatchType, "Applied")!.GetValue(null) is not true)
{
    throw new InvalidOperationException(
        "The status enumerator pool patch did not resolve its targets, so every status "
        + "lookup still boxes a dictionary enumerator.");
}

// The rewrite moves an interface call to a static one of the same shape inside
// a try/finally, so malformed IL has to fail here rather than on the first shot
// that lands on a part.
foreach (var statusTarget in new (Type Type, string Method)[]
{
    (gameAssembly.GetType("Cosmoteer.Ships.Parts.Part", throwOnError: true)!, "GetDamageResistance"),
    (gameAssembly.GetType("Cosmoteer.Ships.Parts.Part", throwOnError: true)!, "GetStatusResistance"),
    (gameAssembly.GetType("Cosmoteer.Ships.Parts.Part", throwOnError: true)!, "ModifyPenetrationResistance"),
    (gameAssembly.GetType("Cosmoteer.Ships.Parts.Crew.PartCrew", throwOnError: true)!, "IsBlockedByStatuses"),
})
{
    var resolved = AccessTools.Method(statusTarget.Type, statusTarget.Method)
        ?? throw new MissingMethodException(statusTarget.Type.FullName, statusTarget.Method);
    if (Harmony.GetPatchInfo(resolved)?.Transpilers.Any(patch => patch.owner == smokeId) != true)
    {
        throw new InvalidOperationException(
            $"Expected Emmanim transpiler was not installed on {statusTarget.Type.Name}.{statusTarget.Method}.");
    }

    RuntimeHelpers.PrepareMethod(resolved.MethodHandle);
}

// Both HitEffectParams.Alloc overloads carry a rewritten call, so neither can
// be addressed by name alone.
foreach (var allocOverload in AccessTools
    .GetDeclaredMethods(gameAssembly.GetType("Cosmoteer.Simulation.HitEffects.HitEffectParams", throwOnError: true)!)
    .Where(method => method.Name == "Alloc"))
{
    if (Harmony.GetPatchInfo(allocOverload)?.Transpilers.Any(patch => patch.owner == smokeId) != true)
    {
        throw new InvalidOperationException(
            "Expected Emmanim transpiler was not installed on a HitEffectParams.Alloc overload.");
    }

    RuntimeHelpers.PrepareMethod(allocOverload.MethodHandle);
}

// Patching a static method moves its body to a dynamic method on another type,
// which drops the runtime's implicit static-constructor trigger. HitEffectParams
// installs its pool's allocator from its own static constructor, and 2.0.32
// crashed on the first beam hit because that never ran. Prove it is installed
// with the patch applied - a null allocator here is that crash.
{
    var hitEffectParamsType = gameAssembly.GetType(
        "Cosmoteer.Simulation.HitEffects.HitEffectParams", throwOnError: true)!;
    var poolType = halflingAssembly.GetType("Halfling.Pooling.ObjectPool`1", throwOnError: true)!
        .MakeGenericType(hitEffectParamsType);
    var allocatorField = poolType.GetField("Allocator", BindingFlags.Public | BindingFlags.Static)
        ?? throw new MissingFieldException(poolType.FullName, "Allocator");
    if (allocatorField.GetValue(null) is null)
    {
        throw new InvalidOperationException(
            "ObjectPool<HitEffectParams>.Allocator is null after patching, so HitEffectParams.Alloc "
            + "will throw on the first weapon hit.");
    }
}

// A pooled enumerator that skipped or repeated an entry would silently corrupt
// resistance and status-effect results, and one handed out twice would make two
// loops share a cursor. StatusType only has to be a key here, so uninitialized
// instances are enough - the dictionary compares them by reference.
{
    var statusTypeType = gameAssembly.GetType("Cosmoteer.Ships.Statuses.StatusType", throwOnError: true)!;
    var dictionaryType = typeof(Dictionary<,>).MakeGenericType(statusTypeType, typeof(object));
    var dictionary = Activator.CreateInstance(dictionaryType)!;
    var add = dictionaryType.GetMethod("Add")!;
    var expected = new List<object>();
    for (var i = 0; i < 64; i++)
    {
        var value = new object();
        expected.Add(value);
        add.Invoke(dictionary, new[] { RuntimeHelpers.GetUninitializedObject(statusTypeType), value });
    }

    var values = dictionaryType.GetProperty("Values")!.GetValue(dictionary)!;
    var rentValues = AccessTools.Method(statusPoolPatchType, "RentValues")!
        .MakeGenericMethod(typeof(object));
    var rentPairs = AccessTools.Method(statusPoolPatchType, "RentPairs")!
        .MakeGenericMethod(typeof(object));

    static List<object> Drain(IEnumerator<object> enumerator)
    {
        var seen = new List<object>();
        try
        {
            while (enumerator.MoveNext())
            {
                seen.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.Dispose();
        }

        return seen;
    }

    var first = (IEnumerator<object>)rentValues.Invoke(null, new[] { values })!;
    var seenValues = Drain(first);
    if (!seenValues.SequenceEqual(expected))
    {
        throw new InvalidOperationException(
            $"The pooled value enumerator yielded {seenValues.Count} of {expected.Count} values, "
            + "or yielded them in a different order than the dictionary does.");
    }

    // Disposal must hand the instance back, or the patch allocates exactly as
    // much as the boxing it replaced.
    var second = (IEnumerator<object>)rentValues.Invoke(null, new[] { values })!;
    if (!ReferenceEquals(first, second))
    {
        throw new InvalidOperationException(
            "A disposed pooled value enumerator was not reused, so nothing is being pooled.");
    }

    // A nested enumeration must never be handed the instance the outer loop is
    // still walking.
    var nested = (IEnumerator<object>)rentValues.Invoke(null, new[] { values })!;
    if (ReferenceEquals(second, nested))
    {
        throw new InvalidOperationException(
            "Two live pooled value enumerators are the same instance, so they share a cursor.");
    }

    nested.Dispose();
    second.Dispose();
    // Disposing twice must not put the same instance on the free list twice.
    second.Dispose();
    var afterDoubleDispose = (IEnumerator<object>)rentValues.Invoke(null, new[] { values })!;
    var alsoAfterDoubleDispose = (IEnumerator<object>)rentValues.Invoke(null, new[] { values })!;
    if (ReferenceEquals(afterDoubleDispose, alsoAfterDoubleDispose))
    {
        throw new InvalidOperationException(
            "A double disposal put one pooled value enumerator on the free list twice.");
    }

    afterDoubleDispose.Dispose();
    alsoAfterDoubleDispose.Dispose();

    var pairs = (IEnumerator<KeyValuePair<object, object>>?)null;
    var pairEnumerator = rentPairs.Invoke(null, new[] { dictionary })!;
    var pairValues = new List<object>();
    var pairMoveNext = pairEnumerator.GetType().GetMethod("MoveNext")!;
    var pairCurrent = pairEnumerator.GetType().GetProperty("Current")!;
    while ((bool)pairMoveNext.Invoke(pairEnumerator, null)!)
    {
        var pair = pairCurrent.GetValue(pairEnumerator)!;
        pairValues.Add(pair.GetType().GetProperty("Value")!.GetValue(pair)!);
    }

    ((IDisposable)pairEnumerator).Dispose();
    _ = pairs;
    if (!pairValues.SequenceEqual(expected))
    {
        throw new InvalidOperationException(
            $"The pooled pair enumerator yielded {pairValues.Count} of {expected.Count} entries, "
            + "or yielded them in a different order than the dictionary does.");
    }

    // Anything that is not the expected dictionary has to fall through to
    // vanilla's own enumerator rather than being dropped.
    var fallback = (IEnumerator<object>)rentValues.Invoke(null, new object[] { expected })!;
    if (!Drain(fallback).SequenceEqual(expected))
    {
        throw new InvalidOperationException(
            "A non-dictionary source did not fall back to its own enumerator.");
    }
}
// The client relays its own diagnostics line to the host over the chat channel,
// because the host can see that a peer is holding the lockstep gate but never
// why, and asking the other player for a log file is a step that does not
// happen. Prove the marker is recognised, logged once, and kept out of chat -
// and that ordinary chat is untouched.
{
    // A resync replaces BaseMPManager. Its diagnostic samples must not mix old
    // and new players or retain the disposed game until the next minute report.
    var memoryDiagnosticsType = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.MultiplayerMemoryDiagnosticsPatch", throwOnError: true)!;
    var ensureCurrentManager = AccessTools.DeclaredMethod(memoryDiagnosticsType, "EnsureCurrentManager")
        ?? throw new MissingMethodException(memoryDiagnosticsType.FullName, "EnsureCurrentManager");
    var samples = (IDictionary)AccessTools.Field(memoryDiagnosticsType, "Samples").GetValue(null)!;
    var frameBuckets = (int[])AccessTools.Field(memoryDiagnosticsType, "FrameBuckets").GetValue(null)!;
    var managerA = RuntimeHelpers.GetUninitializedObject(mpHostManagerType);
    var managerB = RuntimeHelpers.GetUninitializedObject(mpHostManagerType);
    ensureCurrentManager.Invoke(null, new[] { managerA });
    var playerSampleType = memoryDiagnosticsType.GetNestedType("PlayerSample", BindingFlags.NonPublic)!;
    samples.Add(new object(), Activator.CreateInstance(playerSampleType)!);
    frameBuckets[7] = 1;
    AccessTools.Field(memoryDiagnosticsType, "_frameCount").SetValue(null, 1L);
    AccessTools.Field(memoryDiagnosticsType, "_nextReport").SetValue(null, 0L);
    ensureCurrentManager.Invoke(null, new[] { managerB });
    if (samples.Count != 0
        || frameBuckets.Any(count => count != 0)
        || (long)AccessTools.Field(memoryDiagnosticsType, "_frameCount").GetValue(null)! != 0
        || (long)AccessTools.Field(memoryDiagnosticsType, "_nextReport").GetValue(null)! <= Stopwatch.GetTimestamp())
    {
        throw new InvalidOperationException(
            "Multiplayer diagnostics retained samples across a manager replacement/resync.");
    }

    var relayType = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.PeerDiagnosticsRelayPatch", throwOnError: true)!;
    var tryHandle = AccessTools.Method(relayType, "TryHandleIncoming")
        ?? throw new MissingMethodException(relayType.FullName, "TryHandleIncoming");
    var receivedBefore = (int)AccessTools.Property(relayType, "Received")!.GetValue(null)!;

    var chatMessageType = gameAssembly.GetType("Cosmoteer.Multiplayer.ChatMessage", throwOnError: true)!;
    object NewChat(string text) => Activator.CreateInstance(
        chatMessageType,
        new object?[] { "peer", text, null, null })!;

    var plain = NewChat("hello");
    if ((bool)tryHandle.Invoke(null, new[] { plain })!)
    {
        throw new InvalidOperationException(
            "An ordinary chat message was swallowed as a diagnostics relay.");
    }

    var relayed = NewChat("#ELFDIAG#t=1 pv=2 hp=3");
    if (!(bool)tryHandle.Invoke(null, new[] { relayed })!)
    {
        throw new InvalidOperationException(
            "A relayed diagnostics line was not recognised, so it would show up in the chat window.");
    }

    // A second delivery of the same message must still be suppressed, but must
    // not be logged twice: every ChatBox subscribed to the provider sees it.
    if (!(bool)tryHandle.Invoke(null, new[] { relayed })!)
    {
        throw new InvalidOperationException(
            "A repeated delivery of a relayed line was not suppressed.");
    }

    var receivedAfter = (int)AccessTools.Property(relayType, "Received")!.GetValue(null)!;
    if (receivedAfter != receivedBefore + 1)
    {
        throw new InvalidOperationException(
            $"Expected exactly one relayed line to be logged, counted {receivedAfter - receivedBefore}.");
    }

    // The relay hangs off the chat receive hook, so that patch has to be live.
    var chatBoxType = gameAssembly.GetType("Cosmoteer.Gui.Multiplayer.ChatBox", throwOnError: true)!;
    var onChatReceived = AccessTools.Method(chatBoxType, "OnChatReceived")
        ?? throw new MissingMethodException(chatBoxType.FullName, "OnChatReceived");
    if (Harmony.GetPatchInfo(onChatReceived)?.Prefixes.Any(patch => patch.owner == smokeId) != true)
    {
        throw new InvalidOperationException(
            "Expected Emmanim prefix was not installed on ChatBox.OnChatReceived.");
    }
}
// ResourceManager's per-sink pass publishes through two shared lists guarded by
// one lock each, which put 637 ms of Monitor.Enter_Slowpath under UpdateSinkJobs
// in a 20-second host trace. Sharding them per thread is only correct if both
// halves rewrote: the shard hands back the real list when the drain did not.
{
    var shardingType = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.ResourceSinkJobShardingPatch", throwOnError: true)!;
    var shardCountForWorkers = AccessTools.DeclaredMethod(shardingType, "ShardCountForWorkers")
        ?? throw new MissingMethodException(shardingType.FullName, "ShardCountForWorkers");
    foreach (var (workers, expected) in new[] { (1, 8), (3, 8), (7, 8), (8, 16), (11, 16) })
    {
        var actual = (int)shardCountForWorkers.Invoke(null, new object[] { workers })!;
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Expected {workers} FastParallel workers plus the caller to use {expected} "
                + $"sink-job shards, got {actual}.");
        }
    }
    var configuredShardCount = (int)(AccessTools.PropertyGetter(
        shardingType, "ConfiguredShardCount")
        ?? throw new MissingMethodException(shardingType.FullName, "get_ConfiguredShardCount"))
        .Invoke(null, null)!;
    if (configuredShardCount < 2 || (configuredShardCount & (configuredShardCount - 1)) != 0)
    {
        throw new InvalidOperationException(
            $"Configured sink-job shard count {configuredShardCount} is not a usable power of two.");
    }
    var currentShardIndex = AccessTools.DeclaredMethod(shardingType, "CurrentShardIndex")
        ?? throw new MissingMethodException(shardingType.FullName, "CurrentShardIndex");
    var assignedIndexes = new int[configuredShardCount];
    using (var startAssignments = new ManualResetEventSlim(false))
    {
        var assignmentThreads = Enumerable.Range(0, configuredShardCount)
            .Select(i => new Thread(() =>
            {
                startAssignments.Wait();
                assignedIndexes[i] = (int)currentShardIndex.Invoke(null, null)!;
            }) { IsBackground = true, Name = $"sink-shard assignment {i}" })
            .ToArray();
        foreach (var thread in assignmentThreads)
        {
            thread.Start();
        }
        startAssignments.Set();
        foreach (var thread in assignmentThreads)
        {
            thread.Join();
        }
    }
    if (assignedIndexes.Distinct().Count() != configuredShardCount)
    {
        throw new InvalidOperationException(
            "Stable sink-job producers collided despite having enough shard slots: "
            + string.Join(", ", assignedIndexes));
    }

    foreach (var flag in new[] { "ShardApplied", "DrainApplied" })
    {
        if (AccessTools.Field(shardingType, flag)!.GetValue(null) is not true)
        {
            throw new InvalidOperationException(
                $"ResourceSinkJobShardingPatch.{flag} is false, so sink-job collection is still "
                + "taking a contended lock on every worker.");
        }
    }

    // Both rewrites insert a call inside a lock's try/finally, so malformed IL
    // has to fail here rather than on a ship that starts moving resources.
    var sinkJobsManagerType = gameAssembly.GetType(
        "Cosmoteer.Ships.Resources.ResourceManager", throwOnError: true)!;
    var timeType = halflingAssembly.GetType("Halfling.Timing.Time", throwOnError: true)!;
    foreach (var signature in new[] { new[] { typeof(int) }, new[] { timeType } })
    {
        var sinkJobs = AccessTools.Method(sinkJobsManagerType, "UpdateSinkJobs", signature)
            ?? throw new MissingMethodException(sinkJobsManagerType.FullName, "UpdateSinkJobs");
        if (Harmony.GetPatchInfo(sinkJobs)?.Transpilers.Any(patch => patch.owner == smokeId) != true)
        {
            throw new InvalidOperationException(
                "Expected Emmanim transpiler was not installed on ResourceManager.UpdateSinkJobs("
                + $"{signature[0].Name}).");
        }

        RuntimeHelpers.PrepareMethod(sinkJobs.MethodHandle);
    }

    // Determinism rests on the drain returning every shard's entries, since
    // vanilla then sorts them into a total order over distinct sink indexes.
    // Post from several threads at once and prove the sorted merge matches a
    // serial run exactly.
    var shard = AccessTools.Method(shardingType, "Shard")!.MakeGenericMethod(typeof(int));
    var drain = AccessTools.Method(shardingType, "DrainInto")!.MakeGenericMethod(typeof(int));
    var real = new List<int>();
    var expectedIndexes = Enumerable.Range(0, 4096).ToList();
    Parallel.ForEach(expectedIndexes, index =>
    {
        var mine = (List<int>)shard.Invoke(null, new object[] { real })!;
        lock (mine)
        {
            mine.Add(index);
        }
    });

    if (real.Count != 0)
    {
        throw new InvalidOperationException(
            $"{real.Count} entries reached the shared list before the drain, so the shard is not "
            + "actually redirecting the field load.");
    }

    var drained = (List<int>)drain.Invoke(null, new object[] { real })!;
    drained.Sort();
    if (!ReferenceEquals(drained, real) || !drained.SequenceEqual(expectedIndexes))
    {
        throw new InvalidOperationException(
            $"The drained sink-job list held {drained.Count} of {expectedIndexes.Count} entries, "
            + "so the parallel pass would lose or duplicate job updates.");
    }

    // Draining twice must not resurrect anything: the merge reads the field
    // several times and every read goes through the same helper.
    real.Clear();
    if (((List<int>)drain.Invoke(null, new object[] { real })!).Count != 0)
    {
        throw new InvalidOperationException("A second drain re-delivered entries it had already merged.");
    }
}

// The minimap asked every object in the sector whether it is visible on every
// drawn frame, which is 220 ms of a 20-second host trace. Membership is now
// rescanned at 10 Hz while disappearance stays immediate.
{
    var minimapType = gameAssembly.GetType("Cosmoteer.Game.Gui.Minimap", throwOnError: true)!;
    var getMinimapObjects = AccessTools.DeclaredMethod(minimapType, "GetMinimapObjects")
        ?? throw new MissingMethodException(minimapType.FullName, "GetMinimapObjects");
    var minimapPatches = Harmony.GetPatchInfo(getMinimapObjects);
    if (minimapPatches?.Prefixes.Any(patch => patch.owner == smokeId) != true
        || minimapPatches.Postfixes.Any(patch => patch.owner == smokeId) != true)
    {
        throw new InvalidOperationException(
            "Expected Emmanim prefix and postfix were not both installed on Minimap.GetMinimapObjects, "
            + "so the retained set would be filled but never used, or used but never refilled.");
    }

    // The prefix answers from the retained set only while the scene population
    // is unchanged, and drops a source the moment it leaves the scene. Both
    // members that rests on must still exist.
    _ = AccessTools.PropertyGetter(minimapType, "Sim")
        ?? throw new MissingMethodException(minimapType.FullName, "get_Sim");
    var indicatorSourceType = gameAssembly.GetType(
        "Cosmoteer.Simulation.ObjectIndicatorSource", throwOnError: true)!;
    _ = AccessTools.Method(indicatorSourceType, "GetAllInScene")
        ?? throw new MissingMethodException(indicatorSourceType.FullName, "GetAllInScene");
    _ = AccessTools.PropertyGetter(indicatorSourceType, "Sim")
        ?? throw new MissingMethodException(indicatorSourceType.FullName, "get_Sim");
}

// Only the client detects a desync, and the out-of-sync RPC carries no payload,
// so the host's log records a resync with no cause. The client now reports the
// diverging bucket over the existing diagnostics relay. Prove both halves of
// that path still resolve on this build.
{
    var integrityHashType = gameAssembly.GetType(
        "Cosmoteer.Game.Multiplayer.IntegrityHash", throwOnError: true)!;
    var equals = AccessTools.DeclaredMethod(
        integrityHashType, "Equals", new[] { integrityHashType })
        ?? throw new MissingMethodException(integrityHashType.FullName, "Equals(IntegrityHash)");
    if (Harmony.GetPatchInfo(equals)?.Postfixes.Any(patch => patch.owner == smokeId) != true)
    {
        throw new InvalidOperationException(
            "The capture postfix was not installed on IntegrityHash.Equals, so a desync would be "
            + "detected with nothing recorded about which bucket diverged.");
    }

    // The report is built from these five, and they are read by value because
    // the hashes are pooled and released immediately after the comparison.
    foreach (var name in new[] { "Tick", "InputTick", "Phase", "Bucket", "Hash" })
    {
        _ = AccessTools.PropertyGetter(integrityHashType, name)
            ?? throw new MissingMethodException(integrityHashType.FullName, "get_" + name);
    }

    var bucketsType = gameAssembly.GetType("Cosmoteer.FixedUpdateBuckets", throwOnError: true)!;
    _ = AccessTools.Method(bucketsType, "GetBucketName")
        ?? throw new MissingMethodException(bucketsType.FullName, "GetBucketName");

    var clientType = gameAssembly.GetType(
        "Cosmoteer.Game.Multiplayer.MPClientManager", throwOnError: true)!;
    var validate = AccessTools.DeclaredMethod(clientType, "ValidateIntegrityHashes")
        ?? throw new MissingMethodException(clientType.FullName, "ValidateIntegrityHashes");
    var validatePatches = Harmony.GetPatchInfo(validate);
    if (validatePatches?.Prefixes.Any(patch => patch.owner == smokeId) != true
        || validatePatches.Postfixes.Any(patch => patch.owner == smokeId) != true)
    {
        throw new InvalidOperationException(
            "Expected Emmanim prefix and postfix were not both installed on "
            + "MPClientManager.ValidateIntegrityHashes, so a captured report would either never be "
            + "sent or could be sent for a comparison made outside the validation loop.");
    }
}

// FastParallel's workers never sleep: RunThread's idle branch is SpinOnce(-1),
// which was 39.0 s of the 65.9 s of process CPU in a 20-second trace, with 17.8 s
// of GC rendezvous underneath the spin. The idle branch now parks past a spin
// budget and is signaled by AddToLive. Prove the rewrite landed and that the
// park/wake handshake actually works, since a broken one silently costs
// parallelism rather than failing.
{
    var fastParallelType = halflingAssembly.GetType("Halfling.Performance.FastParallel", throwOnError: true)!;

    var runThread = AccessTools.DeclaredMethod(fastParallelType, "RunThread")
        ?? throw new MissingMethodException(fastParallelType.FullName, "RunThread");
    var addToLive = AccessTools.DeclaredMethod(fastParallelType, "AddToLive")
        ?? throw new MissingMethodException(fastParallelType.FullName, "AddToLive");

    var parkPatchType = typeof(EntryPoint).Assembly
        .GetType("EmmanimLagFix.Code.FastParallelIdleParkPatch", throwOnError: true)!;
    var defaultSpinBudget = AccessTools.DeclaredMethod(parkPatchType, "DefaultSpinBudget")
        ?? throw new MissingMethodException(parkPatchType.FullName, "DefaultSpinBudget");
    var waiterType = parkPatchType.GetNestedType("Waiter", BindingFlags.NonPublic)
        ?? throw new TypeLoadException(parkPatchType.FullName + ".Waiter");
    var waiterEvent = AccessTools.DeclaredField(waiterType, "Event")
        ?? throw new MissingFieldException(waiterType.FullName, "Event");
    if (waiterEvent.FieldType != typeof(AutoResetEvent))
    {
        throw new InvalidOperationException(
            "FastParallel waiters must use AutoResetEvent so waking them cannot reintroduce "
            + "ManualResetEventSlim's managed Set/Reset monitor contention.");
    }
    foreach (var (workers, expected) in new[] { (1, 20), (3, 20), (7, 20), (8, 60), (11, 60) })
    {
        var actual = (int)defaultSpinBudget.Invoke(null, new object[] { workers })!;
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Expected {workers} FastParallel workers to use spin budget {expected}, got {actual}.");
        }
    }

    if (Harmony.GetPatchInfo(runThread)?.Transpilers.Any(patch => patch.owner == smokeId) != true)
    {
        throw new InvalidOperationException(
            "The idle-branch transpiler was not installed on FastParallel.RunThread.");
    }
    if (AccessTools.Field(parkPatchType, "Applied").GetValue(null) is not true)
    {
        throw new InvalidOperationException(
            "FastParallel.RunThread's idle SpinOnce(-1) was not rewritten, so every worker would "
            + "keep spinning: "
            + (AccessTools.Field(parkPatchType, "FailureReason").GetValue(null) as string
               ?? "no reason recorded")
            + ".");
    }
    if (Harmony.GetPatchInfo(addToLive)?.Postfixes.Any(patch => patch.owner == smokeId) != true)
    {
        throw new InvalidOperationException(
            "The wake postfix was not installed on FastParallel.AddToLive, so a parked worker "
            + "would only ever be released by the backstop timeout.");
    }

    // Malformed IL surfaces here rather than on a worker thread whose exception
    // nobody can catch.
    RuntimeHelpers.PrepareMethod(runThread.MethodHandle);

    // The pool the park test consults is built by the static initializer, which
    // reads App.Platform and tolerates it being null in this standalone host.
    RuntimeHelpers.RunClassConstructor(fastParallelType.TypeHandle);

    var spinBudget = (int)AccessTools.Field(parkPatchType, "SpinBudget").GetValue(null)!;
    if (spinBudget <= 0)
    {
        throw new InvalidOperationException(
            "The spin budget is not positive, so no worker would ever spin before parking.");
    }
    var compactCounters = (string)(AccessTools.DeclaredMethod(parkPatchType, "CompactCounters")
        ?? throw new MissingMethodException(parkPatchType.FullName, "CompactCounters"))
        .Invoke(null, null)!;
    if (!compactCounters.EndsWith("@" + spinBudget, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Compact FastParallel diagnostics '{compactCounters}' do not report spin budget {spinBudget}.");
    }

    var parkCountField = AccessTools.Field(parkPatchType, "ParkCount");
    var wakeCountField = AccessTools.Field(parkPatchType, "WakeCount");
    long Parks() => (long)parkCountField.GetValue(null)!;
    long Wakes() => (long)wakeCountField.GetValue(null)!;

    var idleSpin = AccessTools.DeclaredMethod(parkPatchType, "IdleSpin")
        ?? throw new MissingMethodException(parkPatchType.FullName, "IdleSpin");
    var wake = AccessTools.DeclaredMethod(parkPatchType, "Wake")
        ?? throw new MissingMethodException(parkPatchType.FullName, "Wake");

    // AutoResetEvent already retains at most one signal. Repeated dispatches
    // while a worker has not consumed that signal must therefore be coalesced
    // before entering the kernel again.
    var signalCountField = AccessTools.Field(parkPatchType, "SignalCount");
    var parkedField = AccessTools.Field(waiterType, "Parked");
    var pendingField = AccessTools.Field(waiterType, "SignalPending");
    var waitersField = AccessTools.Field(parkPatchType, "s_waiters");
    var waiterCountField = AccessTools.Field(parkPatchType, "s_waiterCount");
    var sleepersField = AccessTools.Field(parkPatchType, "s_sleepers");
    var savedWaiters = waitersField.GetValue(null);
    var savedWaiterCount = waiterCountField.GetValue(null);
    var savedSleepers = sleepersField.GetValue(null);
    var syntheticWaiter = Activator.CreateInstance(waiterType, nonPublic: true)!;
    var syntheticEvent = (AutoResetEvent)waiterEvent.GetValue(syntheticWaiter)!;
    try
    {
        var waiterArray = Array.CreateInstance(waiterType, 32);
        waiterArray.SetValue(syntheticWaiter, 0);
        waitersField.SetValue(null, waiterArray);
        waiterCountField.SetValue(null, 1);
        parkedField.SetValue(syntheticWaiter, true);
        pendingField.SetValue(syntheticWaiter, 0);
        sleepersField.SetValue(null, 1);

        var signalsBefore = (long)signalCountField.GetValue(null)!;
        wake.Invoke(null, null);
        wake.Invoke(null, null);
        var signalsAfter = (long)signalCountField.GetValue(null)!;
        if (signalsAfter - signalsBefore != 1
            || !syntheticEvent.WaitOne(0)
            || syntheticEvent.WaitOne(0))
        {
            throw new InvalidOperationException(
                "Repeated FastParallel wakes deposited more than one kernel signal for one park.");
        }
    }
    finally
    {
        parkedField.SetValue(syntheticWaiter, false);
        sleepersField.SetValue(null, savedSleepers);
        waiterCountField.SetValue(null, savedWaiterCount);
        waitersField.SetValue(null, savedWaiters);
        syntheticEvent.Dispose();
    }

    // Invoked by reference so the SpinWait's own Count is what drives the budget,
    // exactly as it does on a real worker.
    void Spin(ref SpinWait spin)
    {
        object[] args = { spin };
        idleSpin.Invoke(null, args);
        spin = (SpinWait)args[0];
    }

    // Below the budget a worker must behave exactly as vanilla did.
    var parksBefore = Parks();
    var cold = new SpinWait();
    Spin(ref cold);
    if (cold.Count != 1 || Parks() != parksBefore)
    {
        throw new InvalidOperationException(
            "A worker below the spin budget parked instead of spinning, which would give up "
            + "parallelism inside a frame rather than only between frames.");
    }

    // Past it, it must park - and AddToLive must be able to see that it has.
    using var stop = new ManualResetEventSlim(false);
    Exception? workerFailure = null;
    var worker = new Thread(() =>
    {
        try
        {
            var spin = new SpinWait();
            while (spin.Count < spinBudget)
            {
                spin.SpinOnce(-1);
            }
            while (!stop.IsSet)
            {
                Spin(ref spin);
            }
        }
        catch (Exception ex)
        {
            workerFailure = ex;
        }
    })
    { IsBackground = true, Name = "FastParallel park smoke" };
    worker.Start();

    var wakesBefore = Wakes();
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline
           && workerFailure == null
           && (Parks() == parksBefore || Wakes() == wakesBefore))
    {
        // Wake counts only when it actually observed a sleeper, so passing this loop
        // is the whole handshake: the park is published with a full fence before the
        // work test, and the signal path fences before reading it.
        wake.Invoke(null, null);
        Thread.Sleep(1);
    }

    stop.Set();
    wake.Invoke(null, null);
    var joined = worker.Join(TimeSpan.FromSeconds(5));

    if (workerFailure != null)
    {
        throw new InvalidOperationException(
            "The parked-worker smoke thread threw, so the idle branch is not safe to run on a "
            + "real FastParallel worker.", workerFailure);
    }
    if (!joined)
    {
        throw new InvalidOperationException(
            "A parked FastParallel worker did not return after a wake, so the idle branch can hang.");
    }
    if (Parks() == parksBefore)
    {
        throw new InvalidOperationException(
            "A worker past the spin budget never parked, so the spin this patch exists to stop "
            + "would still run.");
    }
    if (Wakes() == wakesBefore)
    {
        throw new InvalidOperationException(
            "AddToLive's wake never observed a parked worker, so a dispatch would leave the pool "
            + "asleep until the backstop timeout.");
    }
}

var shipRendererType = gameAssembly.GetType("Cosmoteer.Ships.Rendering.ShipRenderer", throwOnError: true)!;
var shipRenderLayerRulesType = gameAssembly.GetType("Cosmoteer.Ships.ShipRenderLayerRules", throwOnError: true)!;
var renderTargetType = Assembly.Load("HalflingCore").GetType("Halfling.Graphics.RenderTarget", throwOnError: true)!;
var layerListType = typeof(List<>).MakeGenericType(shipRenderLayerRulesType);
var roofStageTarget = AccessTools.DeclaredMethod(
    shipRendererType,
    "DrawStage",
    new[]
    {
        layerListType,
        renderTargetType,
        renderTargetType,
        renderTargetType,
        renderTargetType,
        renderTargetType,
        typeof(float),
        typeof(bool),
        typeof(Halfling.Graphics.Color?),
    })
    ?? throw new MissingMethodException(shipRendererType.FullName, "DrawStage(layers, 5 targets, float, bool, Color?)");
var roofStageInfo = Harmony.GetPatchInfo(roofStageTarget)
    ?? throw new InvalidOperationException("Harmony did not patch ShipRenderer.DrawStage(layers, ...).");
if (roofStageInfo.Prefixes.Count(patch => patch.owner == smokeId) != 1)
{
    throw new InvalidOperationException(
        "Expected exactly one Emmanim roof-decal prefix on ShipRenderer.DrawStage(layers, ...).");
}

var roofSetupMethod = AccessTools.DeclaredMethod(shipRendererType, "SetupRoofRendering")
    ?? throw new MissingMethodException(shipRendererType.FullName, "SetupRoofRendering");
if (roofSetupMethod.GetParameters().Length != 1
    || roofSetupMethod.GetParameters()[0].ParameterType != renderTargetType)
{
    throw new InvalidOperationException(
        "ShipRenderer.SetupRoofRendering no longer takes a single RenderTarget; the roof-decal skip is unverified.");
}

var roofDecalsTargetConstant = AccessTools.Field(
    gameAssembly.GetType("Cosmoteer.ShaderConstantIDs", throwOnError: true)!,
    "RoofDecalsTarget")
    ?? throw new MissingFieldException("Cosmoteer.ShaderConstantIDs", "RoofDecalsTarget");
var halflingCore = Assembly.Load("HalflingCore");
var shaderConstantIdType = halflingCore.GetType("Halfling.Graphics.ShaderConstantID", throwOnError: true)!;
var shaderConstantTypeType = halflingCore.GetType("Halfling.Graphics.ShaderConstantType", throwOnError: true)!;
if (roofDecalsTargetConstant.FieldType != shaderConstantIdType)
{
    throw new InvalidOperationException(
        "Cosmoteer.ShaderConstantIDs.RoofDecalsTarget is no longer a ShaderConstantID; the roof-decal skip is unverified.");
}

var definesConstant = AccessTools.Method(
    halflingCore.GetType("Halfling.Graphics.Shader", throwOnError: true)!,
    "DefinesConstant",
    new[] { shaderConstantIdType, shaderConstantTypeType })
    ?? throw new MissingMethodException("Halfling.Graphics.Shader", "DefinesConstant(ShaderConstantID, ShaderConstantType)");
if (definesConstant.ReturnType != typeof(bool))
{
    throw new InvalidOperationException(
        "Halfling.Graphics.Shader.DefinesConstant no longer returns bool; the roof-decal predicate is unverified.");
}

var halflingMaterialType = halflingCore.GetType("Halfling.Graphics.Material", throwOnError: true)!;
if (AccessTools.Property(halflingMaterialType, "Shader") is null)
{
    throw new MissingMemberException(halflingMaterialType.FullName, "Shader");
}

// Every material slot DrawLayer can bind must be readable, or a layer could be
// classified as inert while it is in fact drawn with a roof-paint shader.
foreach (var materialSlot in new[]
         {
             "Material", "StencilMaterial", "DiffuseMaterial",
             "NormalsMaterial", "LightMaterial", "GhostMaterial",
         })
{
    var slotField = AccessTools.Field(shipRenderLayerRulesType, materialSlot)
        ?? throw new MissingFieldException(shipRenderLayerRulesType.FullName, materialSlot);
    if (slotField.FieldType != halflingMaterialType)
    {
        throw new InvalidOperationException(
            $"ShipRenderLayerRules.{materialSlot} is no longer a Halfling Material; the roof-decal skip is unverified.");
    }
}

var roofSkipPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.RoofDecalTargetSkipPatch",
    throwOnError: true)!;
var samplesRoofDecals = roofSkipPatchType.GetMethod(
    "SamplesRoofDecalsTarget",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(roofSkipPatchType.FullName, "SamplesRoofDecalsTarget");

var addRenderLayer = layerListType.GetMethod("Add")!;
var layerMaterialField = AccessTools.Field(shipRenderLayerRulesType, "Material")!;

var emptyRoofLayers = Activator.CreateInstance(layerListType)!;
if ((bool)samplesRoofDecals.Invoke(null, new[] { emptyRoofLayers })!)
{
    throw new InvalidOperationException(
        "An empty stage was reported as needing the roof decals target.");
}

// A layer that binds no material at all cannot sample the target.
var materiallessLayers = Activator.CreateInstance(layerListType)!;
addRenderLayer.Invoke(materiallessLayers, new[] { Activator.CreateInstance(shipRenderLayerRulesType)! });
addRenderLayer.Invoke(materiallessLayers, new[] { Activator.CreateInstance(shipRenderLayerRulesType)! });
if ((bool)samplesRoofDecals.Invoke(null, new[] { materiallessLayers })!)
{
    throw new InvalidOperationException(
        "A stage whose layers bind no material was reported as needing the roof decals target.");
}

// A material carrying no shader of its own must be treated conservatively, so
// the stage keeps the target rather than being assumed inert.
var shaderlessLayer = Activator.CreateInstance(shipRenderLayerRulesType)!;
layerMaterialField.SetValue(shaderlessLayer, Activator.CreateInstance(halflingMaterialType)!);
var shaderlessLayers = Activator.CreateInstance(layerListType)!;
addRenderLayer.Invoke(shaderlessLayers, new[] { shaderlessLayer });
if (!(bool)samplesRoofDecals.Invoke(null, new[] { shaderlessLayers })!)
{
    throw new InvalidOperationException(
        "A layer whose material carries no shader was reported as not needing the roof decals target.");
}

harmony.UnpatchAll(smokeId);
// The frame-phase probe is opt-in at runtime, but the three Director methods it
// times must still exist on this build or the split silently reports dashes.
{
    var directorType = HarmonyLib.AccessTools.TypeByName("Halfling.Application.Director")
        ?? throw new InvalidOperationException(
            "Halfling.Application.Director was not found, so frame phases cannot be timed.");

    foreach (var name in new[] { "DoInput", "DoUpdate", "DoDraw" })
    {
        if (HarmonyLib.AccessTools.DeclaredMethod(directorType, name) == null)
        {
            throw new InvalidOperationException(
                $"Halfling.Application.Director.{name} was not found, so the frame-phase split "
                + "would report dashes rather than input/update/draw.");
        }
    }
}

// The update phase is split into the simulation step and the game mode so a
// large update figure can be told apart as one heavy tick or several cheap
// catch-up ticks. GameRoot.Update runs both inside a do/while, so the call
// counts are the loop's iteration counts and both targets must resolve.
{
    var simPhaseSimRootType = HarmonyLib.AccessTools.TypeByName("Cosmoteer.Simulation.SimRoot")
        ?? throw new InvalidOperationException(
            "Cosmoteer.Simulation.SimRoot was not found, so simulation steps cannot be counted.");

    if (HarmonyLib.AccessTools.DeclaredMethod(simPhaseSimRootType, "Update", Type.EmptyTypes) == null)
    {
        throw new InvalidOperationException(
            "Cosmoteer.Simulation.SimRoot.Update() was not found, so sim= would report dashes.");
    }

    // Resolved through the property's declared type, exactly as the patch does,
    // so a namespace move is caught here rather than silently disabling mode=.
    var simPhaseModeType = HarmonyLib.AccessTools.TypeByName("Cosmoteer.Game.GameRoot")
        ?.GetProperty("Mode", HarmonyLib.AccessTools.all)?.PropertyType
        ?? throw new InvalidOperationException(
            "Cosmoteer.Game.GameRoot.Mode was not found, so the game mode cannot be timed.");

    if (HarmonyLib.AccessTools.Method(simPhaseModeType, "Update", Type.EmptyTypes) == null)
    {
        throw new InvalidOperationException(
            $"{simPhaseModeType.FullName}.Update() was not found, so mode= would report dashes.");
    }
}

// The per-frame input-tick cap lives in one expression inside
// NetManager.GetTargetAdjustedDeltaTime. Resolve the method and the two members
// the postfix reads, so a rename is caught here rather than silently leaving the
// ticks-per-frame override inert.
{
    var netManagerType = HarmonyLib.AccessTools.TypeByName("Cosmoteer.Game.Multiplayer.NetManager")
        ?? throw new InvalidOperationException(
            "Cosmoteer.Game.Multiplayer.NetManager was not found, so the per-frame input-tick "
            + "cap cannot be raised.");

    if (HarmonyLib.AccessTools.DeclaredMethod(netManagerType, "GetTargetAdjustedDeltaTime") is null)
    {
        throw new InvalidOperationException(
            "NetManager.GetTargetAdjustedDeltaTime was not found, so the per-frame input-tick "
            + "cap cannot be raised.");
    }

    var simProperty = HarmonyLib.AccessTools.Property(netManagerType, "Sim")
        ?? throw new InvalidOperationException("NetManager.Sim was not found.");

    var rulesProperty = HarmonyLib.AccessTools.Property(simProperty.PropertyType, "Rules")
        ?? throw new InvalidOperationException("NetManager.Sim.Rules was not found.");

    if (HarmonyLib.AccessTools.Property(rulesProperty.PropertyType, "PhysicsUpdatesPerSecond")
            is null
        && HarmonyLib.AccessTools.Field(rulesProperty.PropertyType, "PhysicsUpdatesPerSecond")
            is null)
    {
        throw new InvalidOperationException(
            "Sim.Rules.PhysicsUpdatesPerSecond was not found, so the vanilla one-tick-per-frame "
            + "cap cannot be recomputed.");
    }
}

// The lost-ship save is moved off vanilla's background worker onto the
// Director's main-thread queue, so both halves of that hand-off must resolve.
{
    var saverType = HarmonyLib.AccessTools.TypeByName("Cosmoteer.Ships.LostShipSaver")
        ?? throw new InvalidOperationException(
            "Cosmoteer.Ships.LostShipSaver was not found, so lost-ship saving cannot be "
            + "moved onto the main thread.");

    var onLost = HarmonyLib.AccessTools.DeclaredMethod(saverType, "OnShipPotentiallyLost")
        ?? throw new InvalidOperationException(
            "LostShipSaver.OnShipPotentiallyLost was not found.");

    var shape = onLost.GetParameters();
    if (shape.Length != 4
        || shape[2].ParameterType != typeof(bool)
        || shape[3].ParameterType != typeof(bool)
        || shape[2].Name != "disposeWhenDone"
        || shape[3].Name != "asynchronous")
    {
        throw new InvalidOperationException(
            "LostShipSaver.OnShipPotentiallyLost no longer takes "
            + "(ship, mode, bool disposeWhenDone, bool asynchronous); the prefix would "
            + "not bind its parameters. Found: "
            + string.Join(", ", shape.Select(p => p.ParameterType.Name + " " + p.Name)) + ".");
    }

    var syncContext = HarmonyLib.AccessTools.DeclaredProperty(
        HarmonyLib.AccessTools.TypeByName("Halfling.Application.Director")!,
        "SynchronizationContext")
        ?? throw new InvalidOperationException(
            "Director.SynchronizationContext was not found, so there is no main-thread "
            + "queue to defer the lost-ship save onto.");

    if (HarmonyLib.AccessTools.Method(syncContext.PropertyType, "Post", new[] { typeof(Action) }) == null)
    {
        throw new InvalidOperationException(
            syncContext.PropertyType.FullName + ".Post(Action) was not found.");
    }

    // The deferred save calls the private worker directly, and GameApp.OnExiting
    // drains it through the IsReadyToExit getter. Both must still exist.
    if (HarmonyLib.AccessTools.DeclaredMethod(saverType, "SaveLostShip") == null)
    {
        throw new InvalidOperationException(
            "LostShipSaver.SaveLostShip was not found, so the deferred save has nothing to call.");
    }

    var readyGetter = HarmonyLib.AccessTools.DeclaredPropertyGetter(saverType, "IsReadyToExit")
        ?? throw new InvalidOperationException(
            "LostShipSaver.IsReadyToExit was not found, so a save still queued at exit "
            + "would not be drained.");

    // Two patch classes name targets in this area. Binding both to one method -
    // which a class-level TargetMethod combined with a method-level
    // [HarmonyPatch] would do - must fail here rather than in game.
    var probe = new HarmonyLib.Harmony(smokeId + ".lostship");
    probe.PatchAll(typeof(EmmanimLagFix.Code.EntryPoint).Assembly);
    try
    {
        foreach (var (target, label) in new[] { (onLost, "OnShipPotentiallyLost"), (readyGetter, "IsReadyToExit") })
        {
            var info = HarmonyLib.Harmony.GetPatchInfo(target);
            var mine = info == null
                ? 0
                : info.Prefixes.Concat(info.Postfixes)
                    .Count(patch => patch.owner == smokeId + ".lostship");
            if (mine != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one patch on LostShipSaver.{label}, found {mine}.");
            }
        }
    }
    finally
    {
        probe.UnpatchAll(smokeId + ".lostship");
    }
}

Console.WriteLine("PASS: resource traversal/desired-priority snapshot/path-contiguity hashing and visited-set search, generation-stamped resource source visited set, lock-free resource counts, transfer, trade, technology-purchase, pickup-overlay, blueprint network/stat refresh, redundant AtlasQuad write suppression, build-stats, sparse heat diffusion, visual smoothed-value throttle, opt-in resource/single-player memory diagnostics, role-priority, multiplayer initialization/session-timeout/buffer/InputTick forwarding, lazy paint-toolbox pickers/groups, toggle-mode delegate cache, allocation-free resource-ID comparison, hoisted thruster-cache guard, allocation-free shader-constant updates, plain-text layout, subscription-stable part colour updates, status-regulator affected-cell cache, streaming-sound start guard, sharded non-deterministic callback queue, throttled codex show-conditions, pooled status-dictionary enumeration, peer diagnostics relay, client-side desync bucket reporting, sharded resource sink-job collection, throttled minimap membership scanning, parked FastParallel idle workers, roof-decal target skipped for stages whose shaders never sample it, frame-phase timing, opt-in per-frame input-tick cap, and main-thread lost-ship saving patches resolved and compiled on this game build.");
