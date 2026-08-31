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

harmony.UnpatchAll(smokeId);
Console.WriteLine("PASS: resource traversal/desired-priority snapshot/path-contiguity hashing, lock-free resource counts, transfer, trade, technology-purchase, pickup-overlay, blueprint network/stat refresh, redundant AtlasQuad write suppression, build-stats, sparse heat diffusion, visual smoothed-value throttle, opt-in resource/single-player memory diagnostics, role-priority, multiplayer initialization/session-timeout/buffer/InputTick forwarding, lazy paint-toolbox pickers/groups, and toggle-mode delegate cache patches resolved on this game build.");
