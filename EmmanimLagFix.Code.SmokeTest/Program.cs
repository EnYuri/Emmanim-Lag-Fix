using EmmanimLagFix.Code;
using HarmonyLib;
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

// Rewritten IL only fails when the method is compiled, which would otherwise be
// on a moving ship mid-game. Force it here so a malformed branch or an
// unbalanced stack is an immediate InvalidProgramException instead.
foreach (var rewritten in new MethodBase[] { thrusterCacheTarget, compareTarget, buildLinesTarget, updateColorTarget }
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

harmony.UnpatchAll(smokeId);
Console.WriteLine("PASS: resource traversal/desired-priority snapshot/path-contiguity hashing, lock-free resource counts, transfer, trade, technology-purchase, pickup-overlay, blueprint network/stat refresh, redundant AtlasQuad write suppression, build-stats, sparse heat diffusion, visual smoothed-value throttle, opt-in resource/single-player memory diagnostics, role-priority, multiplayer initialization/session-timeout/buffer/InputTick forwarding, lazy paint-toolbox pickers/groups, toggle-mode delegate cache, allocation-free resource-ID comparison, hoisted thruster-cache guard, allocation-free shader-constant updates, plain-text layout, subscription-stable part colour updates, and status-regulator affected-cell cache patches resolved and compiled on this game build.");
