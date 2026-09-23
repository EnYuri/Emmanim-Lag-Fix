using EmmanimLagFix.Code;
using Halfling.Scene2D;
using HarmonyLib;
using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

var gameAssembly = Assembly.Load("Cosmoteer");
var halflingAssembly = Assembly.Load("HalflingCore");
// Exact heat-context guard and numerical equivalence with populated/empty
// context. No real ship is necessary because this rule never reads context.
{
    var patch = typeof(EntryPoint).Assembly.GetType("EmmanimLagFix.Code.HeatModulationContextSkipPatch", true)!;
    var guard = AccessTools.Method(patch, "CanSkip")!;
    var type = gameAssembly.GetType("Cosmoteer.Ships.Statuses.StatusType", true)!;
    var heat = RuntimeHelpers.GetUninitializedObject(type);
    var idField = AccessTools.Field(type, "ID")!;
    idField.SetValue(heat, Activator.CreateInstance(idField.FieldType, new object[] { "cosmoteer.heat" }));
    var layerField = AccessTools.Field(type, "Layer")!;
    layerField.SetValue(heat, Enum.Parse(layerField.FieldType, "Tile"));
    var provider = Activator.CreateInstance(gameAssembly.GetType("Cosmoteer.Ships.Statuses.TileStatusEffectDataProvider", true)!)!;
    var constantType = gameAssembly.GetType("Cosmoteer.Ships.Statuses.ConstantValueModulatorRules", true)!;
    var constant = Activator.CreateInstance(constantType)!;
    AccessTools.Field(constantType, "Value").SetValue(constant, 1f);
    var mode = AccessTools.Field(constantType, "ModificationMode")!;
    mode.SetValue(constant, Enum.Parse(mode.FieldType, "Subtract"));
    var rangeProperty = AccessTools.Property(constantType, "AffectedValueRange")!;
    var range = Activator.CreateInstance(rangeProperty.PropertyType, new object[] { 0f, float.PositiveInfinity })!;
    rangeProperty.SetValue(constant, range);
    var multiType = gameAssembly.GetType("Cosmoteer.Ships.Statuses.MultiValueModulatorRules", true)!;
    var multi = Activator.CreateInstance(multiType)!;
    var modulatorsField = AccessTools.Field(multiType, "Modulators")!;
    var modulators = Array.CreateInstance(modulatorsField.FieldType.GetElementType()!, 1);
    modulators.SetValue(constant, 0);
    modulatorsField.SetValue(multi, modulators);
    AccessTools.Field(type, "ValueModulators").SetValue(heat, multi);
    bool Allows(object? source = null, int count = 0) =>
        (bool)guard.Invoke(null, new[] { heat, source ?? provider, count })!;
    if (!Allows() || Allows(source: new object()) || Allows(count: 1))
        throw new InvalidOperationException("Heat-context provider/empty-dictionary guards failed.");
    var filterField = AccessTools.Field(constantType, "StatusFilter")!;
    filterField.SetValue(constant, RuntimeHelpers.GetUninitializedObject(filterField.FieldType));
    if (Allows()) throw new InvalidOperationException("Filtered heat incorrectly skipped context.");
    filterField.SetValue(constant, null);
    AccessTools.Field(constantType, "Value").SetValue(constant, 2f);
    if (Allows()) throw new InvalidOperationException("Modified heat rule incorrectly passed exact guard.");
    AccessTools.Field(constantType, "Value").SetValue(constant, 1f);
    idField.SetValue(heat, Activator.CreateInstance(idField.FieldType, new object[] { "cosmoteer.fire" }));
    if (Allows()) throw new InvalidOperationException("Non-heat status incorrectly skipped context.");
    idField.SetValue(heat, Activator.CreateInstance(idField.FieldType, new object[] { "cosmoteer.heat" }));

    var dataType = gameAssembly.GetType("Cosmoteer.Ships.Statuses.ValueModulationData", true)!;
    var constructor = dataType.GetConstructors().Single();
    var timeType = constructor.GetParameters()[3].ParameterType;
    var time = Activator.CreateInstance(timeType, new object[] { 1d / 30d })!;
    var infoType = gameAssembly.GetType("Cosmoteer.Ships.Statuses.IStatusLocationInfo", true)!;
    var dictionaryType = typeof(Dictionary<,>).MakeGenericType(type, infoType);
    var empty = (IDictionary)Activator.CreateInstance(dictionaryType)!;
    var populated = (IDictionary)Activator.CreateInstance(dictionaryType)!;
    populated.Add(heat, null);
    var populateCore = AccessTools.Method(patch, "PopulateCore")!;
    // Eligible skip must not enter the provider (null ship/status would fail
    // there). Non-heat status must enter vanilla, proving fallback is real.
    populateCore.Invoke(null, new object?[] { heat, provider, null, null, empty });
    if (empty.Count != 0) throw new InvalidOperationException("Skipped context was mutated.");
    idField.SetValue(heat, Activator.CreateInstance(idField.FieldType, new object[] { "cosmoteer.fire" }));
    try
    {
        populateCore.Invoke(null, new object?[] { heat, provider, null, null, empty });
        throw new InvalidOperationException("Non-heat context did not call vanilla provider.");
    }
    catch (TargetInvocationException e) when (e.InnerException is NullReferenceException) { }
    idField.SetValue(heat, Activator.CreateInstance(idField.FieldType, new object[] { "cosmoteer.heat" }));

    // The GetBuffs skip shares the exact same guard: eligible heat must return
    // null without entering the provider, non-heat must call vanilla.
    var buffsCore = AccessTools.Method(patch, "BuffsCore")!;
    if (buffsCore.Invoke(null, new object?[] { heat, provider, null, null }) != null)
        throw new InvalidOperationException("Eligible heat buffs skip did not return null.");
    idField.SetValue(heat, Activator.CreateInstance(idField.FieldType, new object[] { "cosmoteer.fire" }));
    try
    {
        buffsCore.Invoke(null, new object?[] { heat, provider, null, null });
        throw new InvalidOperationException("Non-heat buffs did not call vanilla provider.");
    }
    catch (TargetInvocationException e) when (e.InnerException is NullReferenceException) { }
    idField.SetValue(heat, Activator.CreateInstance(idField.FieldType, new object[] { "cosmoteer.heat" }));

    var modulate = AccessTools.Method(multiType, "ModulateValue")!;
    foreach (var value in new[] { -1f, 0f, 0.01f, 1f, 100f, 100000f })
    foreach (var resistance in new[] { 0f, 0.5f, 1f })
    {
        object Data(IDictionary context) => constructor.Invoke(new object?[] { value, resistance, range, time, null, context });
        var vanilla = (float)modulate.Invoke(multi, new[] { Data(populated) })!;
        var optimized = (float)modulate.Invoke(multi, new[] { Data(empty) })!;
        if (BitConverter.SingleToInt32Bits(vanilla) != BitConverter.SingleToInt32Bits(optimized))
            throw new InvalidOperationException("Heat-context skip changed a modulation result.");
    }

    // The specialized loop's per-status arithmetic must be bit-identical to
    // the real modulator chain plus the handler's outer ValueClampRange clamp,
    // across the input space including out-of-range and non-finite values.
    var modulateCore = AccessTools.Method(patch, "ModulateCore")!;
    var clamp = AccessTools.Method(typeof(Halfling.Mathx), "Clamp", new[] { typeof(float), range.GetType() })!;
    var clampRange = Activator.CreateInstance(range.GetType(), new object[] { 0f, float.PositiveInfinity })!;
    foreach (var value in new[] { -1f, 0f, 0.01f, 1f, 350f, 4600f, 100000f, float.PositiveInfinity, float.NaN })
    foreach (var resistance in new[] { 0f, 0.3f, 0.5f, 1f })
    {
        var raw = (float)modulate.Invoke(multi, new object?[]
            { constructor.Invoke(new object?[] { value, resistance, range, time, null, empty }) })!;
        var expected = (float)clamp.Invoke(null, new[] { raw, clampRange })!;
        var actual = (float)modulateCore.Invoke(null, new object?[] { value, resistance, 1f / 30f, -1f, range, clampRange })!;
        if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual))
            throw new InvalidOperationException(
                $"Specialized heat modulation diverged: value={value} resistance={resistance} expected={expected} actual={actual}");
    }

    // End-to-end check of the specialized loop on a real StatusStore: with an
    // unranged resistance range the part lookup is provably unreachable, so a
    // ship-less uninitialized handler exercises enumeration, value updates and
    // the changed-status event path exactly as in-game. A status that does not
    // change must leave its list clean; a changed one must dirty it via the
    // same OnStatusValueModified overload vanilla would call.
    var iv2 = halflingAssembly.GetType("Halfling.Geometry.IntVector2", true)!;
    var storeType = gameAssembly.GetType("Cosmoteer.Ships.Statuses.Subhandlers.StatusStore`1", true)!
        .MakeGenericType(iv2);
    var store = Activator.CreateInstance(storeType, heat)!;
    var getOrCreate = AccessTools.Method(storeType, "GetOrCreateStatusList")!;
    var statusType = gameAssembly.GetType("Cosmoteer.Ships.Statuses.Status`1", true)!.MakeGenericType(iv2);
    var locationField = AccessTools.Field(statusType, "<Location>k__BackingField")!;
    var statusValueField = AccessTools.Field(statusType, "<Value>k__BackingField")!;
    var addStatus = AccessTools.Method(getOrCreate.ReturnType, "Add", new[] { statusType, typeof(bool) })!;
    var inputs = new[] { 1f, 350f, 4600f, 100000f, 0f, float.PositiveInfinity };
    var lists = new List<object>();
    var statuses = new List<object>();
    for (var i = 0; i < inputs.Length; i++)
    {
        var status = RuntimeHelpers.GetUninitializedObject(statusType);
        locationField.SetValue(status, Activator.CreateInstance(iv2, new object[] { i, 0 }));
        statusValueField.SetValue(status, inputs[i]);
        var list = getOrCreate.Invoke(store, new object?[] { locationField.GetValue(status) })!;
        addStatus.Invoke(list, new[] { status, true });
        lists.Add(list);
        statuses.Add(status);
    }
    // The store's flat enumerator and list enumeration must visit the same
    // statuses in the same order - the premise the loop's determinism relies on.
    var flat = ((IEnumerable)store).Cast<object>().ToList();
    var nested = ((IEnumerable)AccessTools.Method(storeType, "GetAllStatusLists")!.Invoke(store, null)!)
        .Cast<object>().SelectMany(list => ((IEnumerable)list).Cast<object>()).ToList();
    if (!flat.SequenceEqual(nested))
        throw new InvalidOperationException("StatusStore list enumeration changed status order.");

    AccessTools.Field(type, "ValueClampRange")!.SetValue(heat, clampRange);
    AccessTools.Field(type, "ValueModulationResistanceRange")!
        .SetValue(heat, Activator.CreateInstance(range.GetType(), new object[] { 0f, 0f }));
    var handlerType = gameAssembly.GetType("Cosmoteer.Ships.Statuses.TileStatusHandler", true)!;
    var handlerBase = gameAssembly.GetType("Cosmoteer.Ships.Statuses.StatusHandler`1", true)!
        .MakeGenericType(iv2);
    var handler = RuntimeHelpers.GetUninitializedObject(handlerType);
    AccessTools.Field(handlerBase, "<StatusType>k__BackingField")!.SetValue(handler, heat);
    AccessTools.Field(handlerBase, "<Store>k__BackingField")!.SetValue(handler, store);
    AccessTools.Field(handlerBase, "<StatusCount>k__BackingField")!.SetValue(handler, statuses.Count);
    var updaterType = halflingAssembly.GetType("Halfling.Timing.FixedUpdater", true)!;
    var updater = RuntimeHelpers.GetUninitializedObject(updaterType);
    AccessTools.Field(updaterType, "<Interval>k__BackingField")!.SetValue(updater, time);

    // StatusList.Add already marks each list dirty; reset before the loop so a
    // set flag below can only come from the changed-status event path.
    var dirtyField = AccessTools.Field(getOrCreate.ReturnType, "_isDirty")!;
    foreach (var list in lists) dirtyField.SetValue(list, false);

    AccessTools.Method(patch, "SpecializedLoop")!.Invoke(null, new[] { handler, updater });

    for (var i = 0; i < inputs.Length; i++)
    {
        var raw = (float)modulate.Invoke(multi, new object?[]
            { constructor.Invoke(new object?[] { inputs[i], 0f, range, time, null, empty }) })!;
        var expected = (float)clamp.Invoke(null, new[] { raw, clampRange })!;
        var actual = (float)statusValueField.GetValue(statuses[i])!;
        if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual))
            throw new InvalidOperationException(
                $"Specialized loop diverged at status {i}: expected={expected} actual={actual}");
        var expectDirty = !inputs[i].Equals(expected);
        if ((bool)dirtyField.GetValue(lists[i])! != expectDirty)
            throw new InvalidOperationException(
                $"Changed-status event path diverged at status {i}: dirty expected {expectDirty}.");
    }
}
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
{
    var type = typeof(EntryPoint).Assembly.GetType("EmmanimLagFix.Code.HeatModulationContextSkipPatch", true)!;
    if (!(bool)AccessTools.Property(type, "LoopInstalled")!.GetValue(null)!)
        throw new InvalidOperationException("Heat modulation loop specialization was not installed.");
}
CrewAssignmentRateTests.Run(gameAssembly, smokeId);
ParallelBatchPolicyTests.Run(gameAssembly);
ResourceTraversalTests.Run(typeof(EntryPoint).Assembly);
ManualTransferExpiryTests.Run(gameAssembly, typeof(EntryPoint).Assembly);
CrewOxygenValidityTests.Run(gameAssembly, typeof(EntryPoint).Assembly);
ThrusterFirstPassTests.Run(gameAssembly, typeof(EntryPoint).Assembly);

// Avoidance tag comparisons must retain HashSet.Overlaps receiver-comparer
// semantics, including different comparers on the two input sets. The concrete
// path must allocate nothing once JIT compilation has warmed up.
{
    var type = typeof(EntryPoint).Assembly.GetType("EmmanimLagFix.Code.DoodadAvoidanceTagAllocationPatch", true)!;
    var tagTargets = ((IEnumerable<MethodBase>)AccessTools.Method(type, "TargetMethods").Invoke(null, null)!).ToArray();
    if (tagTargets.Length < 3)
        throw new InvalidOperationException($"Expected every IAvoidableDoodad.MatchesTags implementation; found {tagTargets.Length}.");
    if ((int)AccessTools.Field(type, "AppliedCount").GetValue(null)! != tagTargets.Length)
        throw new InvalidOperationException("Not every avoidance tag transpiler applied.");
    foreach (var target in tagTargets)
    {
        if (Harmony.GetPatchInfo(target)?.Transpilers.All(p => p.owner != smokeId) != false)
            throw new InvalidOperationException($"Avoidance tag transpiler is missing on {target.DeclaringType?.Name}.");
        RuntimeHelpers.PrepareMethod(target.MethodHandle);
    }
    var overlaps = AccessTools.Method(type, "Overlaps").MakeGenericMethod(typeof(string))
        .CreateDelegate<Func<HashSet<string>, IEnumerable<string>, bool>>();
    var tagSets = new[]
    {
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(new[] { "a", "b" }, StringComparer.Ordinal),
        new HashSet<string>(new[] { "A", "C" }, StringComparer.Ordinal),
        new HashSet<string>(new[] { "A", "C" }, StringComparer.OrdinalIgnoreCase)
    };
    foreach (var left in tagSets)
    foreach (var right in tagSets)
    {
        if (overlaps(left, right) != left.Overlaps(right)
            || overlaps(left, right.ToArray()) != left.Overlaps(right.ToArray()))
            throw new InvalidOperationException("Avoidance overlap changed membership/comparer semantics.");
    }
    for (var i = 0; i < 10000; i++) _ = overlaps(tagSets[1], tagSets[2]);
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 10000; i++) _ = overlaps(tagSets[1], tagSets[2]);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    if (allocated != 0)
        throw new InvalidOperationException($"Concrete avoidance tag overlap still allocates: {allocated} bytes.");
    var routePatch = typeof(EntryPoint).Assembly.GetType("EmmanimLagFix.Code.ResourceTransferAvoidanceAllocationPatch", true)!;
    if ((int)AccessTools.Field(routePatch, "ReplacedSites").GetValue(null)! != 4)
        throw new InvalidOperationException("Expected all four transfer avoidance call sites to be replaced.");
    var managerType = gameAssembly.GetType("Cosmoteer.Simulation.Doodads.SimDoodadsManager", true)!;
    var manager = RuntimeHelpers.GetUninitializedObject(managerType);
    var avoidersField = AccessTools.Field(managerType, "_avoidableDoodads");
    var avoiders = (System.Collections.IList)Activator.CreateInstance(avoidersField.FieldType)!;
    avoidersField.SetValue(manager, avoiders);
    var planetType = gameAssembly.GetType("Cosmoteer.Simulation.Doodads.PlanetDoodad", true)!;
    var planet = RuntimeHelpers.GetUninitializedObject(planetType);
    var tagsProperty = AccessTools.Property(planetType, "Tags");
    var tags = Activator.CreateInstance(tagsProperty.PropertyType)!;
    var idType = tagsProperty.PropertyType.GetGenericArguments()[0];
    tagsProperty.PropertyType.GetMethod("Add")!.Invoke(tags, new[] { Activator.CreateInstance(idType) });
    tagsProperty.SetValue(planet, tags);
    var avoiderType = planetType.GetNestedType("DamageAvoider", BindingFlags.Public | BindingFlags.NonPublic)!;
    var planetAvoider = Activator.CreateInstance(avoiderType, new object[] { planet, 5f })!;
    avoiders.Add(planetAvoider);
    var setType = tagsProperty.PropertyType;
    var setAdd = setType.GetMethod("Add")!;
    var idFromInt = idType.GetMethods(BindingFlags.Static | BindingFlags.Public)
        .Single(m => m.Name == "op_Explicit" && m.ReturnType == idType);
    object NewSet(params int[] ids)
    {
        var set = Activator.CreateInstance(setType)!;
        foreach (var id in ids)
            setAdd.Invoke(set, new[] { idFromInt.Invoke(null, new object[] { id })! });
        return set;
    }
    // SpaceStation and StasisSpaceStation are the other IAvoidableDoodad
    // implementations; both read a HashSet<T> reachable without a live sim.
    var stationShipType = gameAssembly.GetType("Cosmoteer.Ships.Ship", true)!;
    var shipTagsProperty = AccessTools.Property(stationShipType, "Tags")!;
    var shipField = AccessTools.Field(
        gameAssembly.GetType("Cosmoteer.Ships.ShipComponent", true)!, "<Ship>k__BackingField")!;
    var station = RuntimeHelpers.GetUninitializedObject(
        gameAssembly.GetType("Cosmoteer.Ships.Special.SpaceStation", true)!);
    var stationShip = RuntimeHelpers.GetUninitializedObject(stationShipType);
    var stationTags = NewSet(1000003);
    shipTagsProperty.SetValue(stationShip, stationTags);
    shipField.SetValue(station, stationShip);
    avoiders.Add(station);
    var stasisSpawnerField = AccessTools.Field(
        gameAssembly.GetType("Cosmoteer.Ships.Special.StasisLandmark", true)!, "<StasisSpawner>k__BackingField")!;
    var stasisStationType = gameAssembly.GetType("Cosmoteer.Ships.Special.StasisSpaceStation", true)!;
    var stasisAvoiders = new List<(object avoider, object tags)>();
    foreach (var spawnerTypeName in new[]
    {
        "Cosmoteer.Simulation.Stasis.StasisShipSpawner",
        "Cosmoteer.Simulation.Stasis.SimStasisManager+SerializedStasisShip"
    })
    {
        var spawnerType = gameAssembly.GetType(spawnerTypeName, true)!;
        var spawner = RuntimeHelpers.GetUninitializedObject(spawnerType);
        var spawnerTags = NewSet(1000004);
        AccessTools.Property(spawnerType, "Tags")!.SetValue(spawner, spawnerTags);
        var stasisStation = RuntimeHelpers.GetUninitializedObject(stasisStationType);
        stasisSpawnerField.SetValue(stasisStation, spawner);
        avoiders.Add(stasisStation);
        stasisAvoiders.Add((stasisStation, spawnerTags));
    }
    // Every MatchesTags implementation must agree with the vanilla
    // tags != null && set != null && tags.Overlaps(set) shape.
    var hashSetOverlaps = setType.GetMethod("Overlaps")!;
    var emptySet = NewSet();
    var matchCandidates = new object?[] { tags, NewSet(1000003), NewSet(2000001), emptySet, null };
    void CheckMatchesTags(object avoider, object? theirTags, string name)
    {
        var matchesTags = avoider.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(m => m.Name.EndsWith(".MatchesTags", StringComparison.Ordinal));
        foreach (var candidate in matchCandidates)
        {
            var expected = candidate != null && theirTags != null
                && (bool)hashSetOverlaps.Invoke(candidate, new[] { theirTags })!;
            var actual = (bool)matchesTags.Invoke(avoider, new[] { candidate })!;
            if (actual != expected)
                throw new InvalidOperationException($"Avoidance tag comparison diverged on {name}.");
        }
    }
    CheckMatchesTags(planetAvoider, tags, "DamageAvoider");
    CheckMatchesTags(station, stationTags, "SpaceStation");
    foreach (var (stasisAvoider, spawnerTags) in stasisAvoiders)
        CheckMatchesTags(stasisAvoider, spawnerTags, stasisAvoider.GetType().Name);
    // The concrete enumerator path must not allocate for the live value type
    // (ID<SimObjectSpawner>) either.
    var overlapsGeneric = AccessTools.Method(type, "Overlaps")!.MakeGenericMethod(idType);
    var pReceiver = System.Linq.Expressions.Expression.Parameter(typeof(object));
    var pOther = System.Linq.Expressions.Expression.Parameter(typeof(object));
    var overlapsObj = System.Linq.Expressions.Expression
        .Lambda<Func<object, object, bool>>(System.Linq.Expressions.Expression.Call(
            overlapsGeneric,
            System.Linq.Expressions.Expression.Convert(pReceiver, setType),
            System.Linq.Expressions.Expression.Convert(pOther, typeof(IEnumerable<>).MakeGenericType(idType))),
            pReceiver, pOther).Compile();
    var leftIds = NewSet(1, 2, 3);
    var rightIds = NewSet(2, 4);
    for (var i = 0; i < 10000; i++) _ = overlapsObj(leftIds, rightIds);
    var beforeIds = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 10000; i++) _ = overlapsObj(leftIds, rightIds);
    var allocatedIds = GC.GetAllocatedBytesForCurrentThread() - beforeIds;
    if (allocatedIds != 0)
        throw new InvalidOperationException($"Value-type avoidance tag overlap still allocates: {allocatedIds} bytes.");
    var shapeType = typeof(Halfling.Geometry.Circle);
    var replacement = AccessTools.Method(routePatch, "ShouldAvoidLocation").MakeGenericMethod(shapeType);
    var original = managerType.GetMethods().Single(m => m.Name == "ShouldAvoidLocation" && m.GetParameters().Length == 3).MakeGenericMethod(shapeType);
    foreach (var distance in new[] { 0f, 4f, 7f, 100f })
    foreach (var buffer in new[] { 0f, 2f })
    foreach (var candidateTags in new[] { tags, Activator.CreateInstance(tagsProperty.PropertyType), null })
    {
        var shape = new Halfling.Geometry.Circle(new Halfling.Geometry.Vector2(distance, 0f), 1f);
        var expected = (bool)original.Invoke(manager, new object?[] { shape, candidateTags, buffer })!;
        var actual = (bool)replacement.Invoke(null, new object?[] { manager, shape, candidateTags, buffer })!;
        if (actual != expected) throw new InvalidOperationException("Transfer avoidance changed geometry/tag behaviour.");
    }
}

// The tile-line overlay RefreshData replacement must traverse rules and
// blueprint components in exactly vanilla order and must not allocate once
// warmed up. Fixtures use uninitialized objects; only the fields the
// traversal reads are populated.
{
    var overlayPatchType = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.TileLineOverlayAllocationPatch", true)!;
    var overlayRendererType = gameAssembly.GetType(
        "Cosmoteer.Ships.Blueprints.Graphics.TileLineBlueprintOverlayRenderer", true)!;
    var overlayRefreshTarget = AccessTools.Method(overlayRendererType, "RefreshData")!;
    if (Harmony.GetPatchInfo(overlayRefreshTarget)?.Prefixes.Any(p => p.owner == smokeId) != true)
        throw new InvalidOperationException("Tile-line overlay RefreshData prefix is missing.");

    var partComponentRulesType = gameAssembly.GetType("Cosmoteer.Ships.Parts.PartComponentRules", true)!;
    var lineRulesType = gameAssembly.GetType("Cosmoteer.Ships.Parts.Logic.PartTileLineScoreValueRules", true)!;
    var toggledRulesType = gameAssembly.GetType("Cosmoteer.Ships.Parts.Logic.PartToggledComponentsRules", true)!;
    var plainRulesType = gameAssembly.GetType("Cosmoteer.Ships.Parts.Logic.JunkToggleRules", true)!;
    var partRulesType = gameAssembly.GetType("Cosmoteer.Ships.Parts.PartRules", true)!;
    var rulesListType = typeof(List<>).MakeGenericType(partComponentRulesType);
    var toggledComponentsField = AccessTools.Field(toggledRulesType, "Components")!;

    object NewRulesList(params object?[] items)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(rulesListType)!;
        foreach (var item in items) list.Add(item);
        return list;
    }
    object NewToggled(params object?[] items)
    {
        var toggled = RuntimeHelpers.GetUninitializedObject(toggledRulesType);
        toggledComponentsField.SetValue(toggled, NewRulesList(items));
        return toggled;
    }
    var lineA = RuntimeHelpers.GetUninitializedObject(lineRulesType);
    var lineB = RuntimeHelpers.GetUninitializedObject(lineRulesType);
    var lineC = RuntimeHelpers.GetUninitializedObject(lineRulesType);
    var lineD = RuntimeHelpers.GetUninitializedObject(lineRulesType);
    var partRules = RuntimeHelpers.GetUninitializedObject(partRulesType);
    AccessTools.Field(partRulesType, "Components")!.SetValue(partRules, NewRulesList(
        RuntimeHelpers.GetUninitializedObject(plainRulesType),
        lineA,
        NewToggled(lineB, RuntimeHelpers.GetUninitializedObject(plainRulesType), NewToggled(lineC), null),
        lineD,
        null));

    var expectedOrder = new List<object>();
    var vanillaRecursive = (System.Collections.IEnumerable)AccessTools
        .Method(partRulesType, "GetComponentsRecursive")!.Invoke(partRules, null)!;
    foreach (var component in vanillaRecursive)
        if (lineRulesType.IsInstanceOfType(component))
            expectedOrder.Add(component);
    if (expectedOrder.Count != 4)
        throw new InvalidOperationException("Vanilla recursive component traversal changed shape.");

    var lineRulesListType = typeof(List<>).MakeGenericType(lineRulesType);
    var lineRulesActionType = typeof(Action<,>).MakeGenericType(lineRulesListType, lineRulesType);
    var lineRulesVisit = lineRulesListType.GetMethod("Add")!.CreateDelegate(lineRulesActionType);
    var visitMethod = AccessTools.Method(overlayPatchType, "VisitLineRules")!
        .MakeGenericMethod(lineRulesListType);
    var actualOrder = (System.Collections.IList)Activator.CreateInstance(lineRulesListType)!;
    visitMethod.Invoke(null, new object?[] {
        AccessTools.Field(partRulesType, "Components")!.GetValue(partRules), actualOrder, lineRulesVisit });
    if (!expectedOrder.SequenceEqual(actualOrder.Cast<object>()))
        throw new InvalidOperationException("Tile-line overlay rules traversal order diverged from vanilla.");

    var overlayShipType = gameAssembly.GetType("Cosmoteer.Ships.Ship", true)!;
    var blueprintPartType = gameAssembly.GetType("Cosmoteer.Ships.Blueprints.BlueprintPart", true)!;
    var lineValueType = gameAssembly.GetType(
        "Cosmoteer.Ships.Blueprints.Logic.Values.BlueprintPartTileLineScoreValue", true)!;
    var bpComponentsField = AccessTools.Field(blueprintPartType, "_components")!;
    var bpComponentsType = bpComponentsField.FieldType;
    var blockingProperty = AccessTools.Property(lineValueType, "BlockingPart")!;
    var componentsProperty = AccessTools.Property(blueprintPartType, "Components")!;
    var bpmType = gameAssembly.GetType("Cosmoteer.Ships.Blueprints.BlueprintPartsManager", true)!;
    var orderedPartsField = bpmType.BaseType!.GetField("_orderedParts",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    var blockerPart = RuntimeHelpers.GetUninitializedObject(blueprintPartType);
    var otherPart = RuntimeHelpers.GetUninitializedObject(blueprintPartType);
    object NewLineValue(object? blocking)
    {
        var value = RuntimeHelpers.GetUninitializedObject(lineValueType);
        AccessTools.Field(lineValueType, "<BlockingPart>k__BackingField")!.SetValue(value, blocking);
        return value;
    }
    object NewBlueprintPart(params object?[] comps)
    {
        var part = RuntimeHelpers.GetUninitializedObject(blueprintPartType);
        var list = (System.Collections.IList)Activator.CreateInstance(bpComponentsType)!;
        foreach (var comp in comps) list.Add(comp);
        bpComponentsField.SetValue(part, list);
        return part;
    }
    var lineV1 = NewLineValue(blockerPart);
    var lineV2 = NewLineValue(null);
    var lineV3 = NewLineValue(otherPart);
    var lineV4 = NewLineValue(blockerPart);
    var bpA = NewBlueprintPart(lineV1, lineV2, lineV3);
    var bpB = NewBlueprintPart(lineV4);

    var overlayShip = RuntimeHelpers.GetUninitializedObject(overlayShipType);
    var bpm = RuntimeHelpers.GetUninitializedObject(bpmType);
    var orderedParts = (System.Collections.IList)Activator.CreateInstance(
        typeof(List<>).MakeGenericType(blueprintPartType))!;
    orderedParts.Add(bpA);
    orderedParts.Add(bpB);
    orderedPartsField.SetValue(bpm, orderedParts);
    AccessTools.Field(overlayShipType, "<BlueprintParts>k__BackingField")!.SetValue(overlayShip, bpm);

    var bpSetType = typeof(HashSet<>).MakeGenericType(blueprintPartType);
    var primaryParts = Activator.CreateInstance(bpSetType)!;
    bpSetType.GetMethod("Add")!.Invoke(primaryParts, new[] { blockerPart });
    var setContains = bpSetType.GetMethod("Contains")!;

    var expectedSecondary = new List<object>();
    foreach (var bp in orderedParts)
    {
        foreach (var comp in (System.Collections.IEnumerable)componentsProperty.GetValue(bp)!)
        {
            if (!lineValueType.IsInstanceOfType(comp)) continue;
            var blocking = blockingProperty.GetValue(comp);
            if (blocking != null && (bool)setContains.Invoke(primaryParts, new[] { blocking })!)
                expectedSecondary.Add(comp);
        }
    }
    if (expectedSecondary.Count != 2)
        throw new InvalidOperationException("Secondary-line fixture shape changed.");

    var lineValueListType = typeof(List<>).MakeGenericType(lineValueType);
    var lineValueActionType = typeof(Action<,>).MakeGenericType(lineValueListType, lineValueType);
    var lineValueVisit = lineValueListType.GetMethod("Add")!.CreateDelegate(lineValueActionType);
    var collectMethod = AccessTools.Method(overlayPatchType, "CollectObstructedSecondaryLines")!
        .MakeGenericMethod(lineValueListType);
    var actualSecondary = (System.Collections.IList)Activator.CreateInstance(lineValueListType)!;
    collectMethod.Invoke(null, new[] { overlayShip, primaryParts, actualSecondary, lineValueVisit });
    if (!expectedSecondary.SequenceEqual(actualSecondary.Cast<object>()))
        throw new InvalidOperationException("Tile-line overlay secondary traversal diverged from vanilla.");

    // Both helpers must be allocation-free once warmed up; invoke them through
    // compiled expressions so reflection invocation overhead does not pollute
    // the measurement. A no-op visitor keeps the collected list from growing.
    var noopLineVisit = System.Linq.Expressions.Expression.Lambda(lineRulesActionType,
        System.Linq.Expressions.Expression.Empty(),
        System.Linq.Expressions.Expression.Parameter(lineRulesListType),
        System.Linq.Expressions.Expression.Parameter(lineRulesType)).Compile();
    var noopValueVisit = System.Linq.Expressions.Expression.Lambda(lineValueActionType,
        System.Linq.Expressions.Expression.Empty(),
        System.Linq.Expressions.Expression.Parameter(lineValueListType),
        System.Linq.Expressions.Expression.Parameter(lineValueType)).Compile();
    var vp1 = System.Linq.Expressions.Expression.Parameter(typeof(object));
    var vp2 = System.Linq.Expressions.Expression.Parameter(typeof(object));
    var vp3 = System.Linq.Expressions.Expression.Parameter(typeof(object));
    var visitInvoker = System.Linq.Expressions.Expression.Lambda<Func<object, object, object, int>>(
        System.Linq.Expressions.Expression.Block(
            System.Linq.Expressions.Expression.Call(visitMethod,
                System.Linq.Expressions.Expression.Convert(vp1, rulesListType),
                System.Linq.Expressions.Expression.Convert(vp2, lineRulesListType),
                System.Linq.Expressions.Expression.Convert(vp3, lineRulesActionType)),
            System.Linq.Expressions.Expression.Constant(0)),
        vp1, vp2, vp3).Compile();
    var cp1 = System.Linq.Expressions.Expression.Parameter(typeof(object));
    var cp2 = System.Linq.Expressions.Expression.Parameter(typeof(object));
    var cp3 = System.Linq.Expressions.Expression.Parameter(typeof(object));
    var cp4 = System.Linq.Expressions.Expression.Parameter(typeof(object));
    var collectInvoker = System.Linq.Expressions.Expression.Lambda<Func<object, object, object, object, int>>(
        System.Linq.Expressions.Expression.Block(
            System.Linq.Expressions.Expression.Call(collectMethod,
                System.Linq.Expressions.Expression.Convert(cp1, overlayShipType),
                System.Linq.Expressions.Expression.Convert(cp2, bpSetType),
                System.Linq.Expressions.Expression.Convert(cp3, lineValueListType),
                System.Linq.Expressions.Expression.Convert(cp4, lineValueActionType)),
            System.Linq.Expressions.Expression.Constant(0)),
        cp1, cp2, cp3, cp4).Compile();
    var componentsList = AccessTools.Field(partRulesType, "Components")!.GetValue(partRules)!;
    for (var i = 0; i < 20000; i++)
    {
        _ = visitInvoker(componentsList, actualOrder, noopLineVisit);
        _ = collectInvoker(overlayShip, primaryParts, actualSecondary, noopValueVisit);
    }
    var overlayBefore = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 20000; i++)
    {
        _ = visitInvoker(componentsList, actualOrder, noopLineVisit);
        _ = collectInvoker(overlayShip, primaryParts, actualSecondary, noopValueVisit);
    }
    var overlayAllocated = GC.GetAllocatedBytesForCurrentThread() - overlayBefore;
    if (overlayAllocated != 0)
        throw new InvalidOperationException(
            $"Tile-line overlay traversal still allocates: {overlayAllocated} bytes.");
}

// The item-cost text cache must return the exact vanilla-produced string on a
// hit and must recompute when any fingerprinted input changes. BuildToolbox's
// static constructor needs a live game, so the extracted cache type and the
// fingerprint function are exercised directly instead.
{
    var costPatchType = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.BuildItemCostTextCachePatch", true)!;
    var toolboxType = gameAssembly.GetType("Cosmoteer.Game.Gui.Build.BuildToolbox", true)!;
    var costTextTarget = AccessTools.Method(toolboxType, "GetItemCostText")!;
    var costPatchInfo = Harmony.GetPatchInfo(costTextTarget);
    if (costPatchInfo?.Prefixes.Any(p => p.owner == smokeId) != true
        || costPatchInfo.Postfixes.Any(p => p.owner == smokeId) != true)
        throw new InvalidOperationException("Item-cost text cache prefix/postfix is missing.");

    // Cache mechanics: miss -> store -> hit -> fingerprint change -> miss.
    var cacheType = costPatchType.GetNestedType("CostTextCache", BindingFlags.NonPublic)!;
    var costCache = Activator.CreateInstance(cacheType)!;
    var keyType = costPatchType.GetNestedType("CostTextKey", BindingFlags.NonPublic)!;
    var resourceArray = new object();
    var costKeyCtor = keyType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(c => c.GetParameters().Length == 2);
    var costKey = costKeyCtor.Invoke(new[] { 42, resourceArray });
    var tryGet = AccessTools.Method(cacheType, "TryGet")!;
    var store = AccessTools.Method(cacheType, "Store")!;
    var tryArgs = new object?[] { costKey, 7L, null, null };
    if ((bool)tryGet.Invoke(costCache, tryArgs)!)
        throw new InvalidOperationException("Item-cost cache hit on an empty cache.");
    store.Invoke(costCache, new object?[] { costKey, 7L, "cached-text", true });
    tryArgs = new object?[] { costKey, 7L, null, null };
    if (!(bool)tryGet.Invoke(costCache, tryArgs)!
        || !Equals(tryArgs[2], "cached-text") || !(bool)tryArgs[3]!)
        throw new InvalidOperationException("Item-cost cache failed to return the stored result.");
    tryArgs = new object?[] { costKey, 8L, null, null };
    if ((bool)tryGet.Invoke(costCache, tryArgs)!)
        throw new InvalidOperationException("Item-cost cache hit despite a fingerprint change.");
    var otherKey = costKeyCtor.Invoke(new object?[] { 42, new object() });
    tryArgs = new object?[] { otherKey, 7L, null, null };
    if ((bool)tryGet.Invoke(costCache, tryArgs)!)
        throw new InvalidOperationException("Item-cost cache hit for a different resource array.");
    var otherCostKey = costKeyCtor.Invoke(new object?[] { 43, resourceArray });
    tryArgs = new object?[] { otherCostKey, 7L, null, null };
    if ((bool)tryGet.Invoke(costCache, tryArgs)!)
        throw new InvalidOperationException("Item-cost cache hit for a different credit cost.");

    // Fingerprint coverage: identical inputs must produce identical hashes and
    // every input the vanilla method reads must affect the fingerprint.
    var fingerprintMethod = costPatchType
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(m => m.Name == "ComputeFingerprint" && m.GetParameters().Length == 10);
    var resourceRulesType = gameAssembly.GetType("Cosmoteer.Resources.ResourceRules", true)!;
    var costResourceIdType = gameAssembly.GetType("Cosmoteer.Data.ID`1", true)!
        .MakeGenericType(resourceRulesType);
    var costedQuantitiesType = gameAssembly.GetType("Cosmoteer.Ships.Resources.CostedQuantities", true)!;
    var costedDictType = typeof(Dictionary<,>).MakeGenericType(costResourceIdType, costedQuantitiesType);
    var countDictType = typeof(Dictionary<,>).MakeGenericType(costResourceIdType, typeof(int));
    var costResourceTupleType = typeof(ValueTuple<,>).MakeGenericType(costResourceIdType, typeof(int));
    var costIdFromInt = costResourceIdType.GetMethods(BindingFlags.Static | BindingFlags.Public)
        .Single(m => m.Name == "op_Explicit" && m.ReturnType == costResourceIdType
            && m.GetParameters()[0].ParameterType == typeof(int));
    object NewId(int raw) => costIdFromInt.Invoke(null, new object[] { raw })!;
    object NewResourceArray(params object[] tuples)
    {
        var arr = Array.CreateInstance(costResourceTupleType, tuples.Length);
        for (var i = 0; i < tuples.Length; i++) arr.SetValue(tuples[i], i);
        return arr;
    }
    object NewResourceTuple(object id, int qty) =>
        Activator.CreateInstance(costResourceTupleType, id, qty)!;
    var resId1 = NewId(1);
    var resId2 = NewId(2);
    var shipObj = new object();
    var simObj = new object();
    var avail = (System.Collections.IDictionary)Activator.CreateInstance(countDictType)!;
    avail[resId1] = 5;
    var buyable = (System.Collections.IDictionary)Activator.CreateInstance(costedDictType)!;
    var costedDataField = AccessTools.Field(costedQuantitiesType, "_data")!;
    var costedAdd = AccessTools.Method(costedQuantitiesType, "Add", new[] { typeof(int), typeof(int) })!;
    object NewQuantities(int quantity, int price)
    {
        var q = RuntimeHelpers.GetUninitializedObject(costedQuantitiesType);
        costedDataField.SetValue(q, Activator.CreateInstance(costedDataField.FieldType)!);
        costedAdd.Invoke(q, new object[] { quantity, price });
        return q;
    }
    var quantities = NewQuantities(3, 10);
    buyable[resId2] = quantities;
    var refundable = (System.Collections.IDictionary)Activator.CreateInstance(costedDictType)!;
    refundable[resId1] = quantities;

    var resArray1 = NewResourceArray(NewResourceTuple(resId1, 2), NewResourceTuple(resId2, 4));
    long Fingerprint(object? resArr = null, int credits = 100, bool editing = false,
        object? shipRef = null, object? simRef = null, int money = 50, int mode = 2,
        object? availD = null, object? buyD = null, object? refD = null) =>
        (long)fingerprintMethod.Invoke(null, new object?[] {
            credits, resArr ?? resArray1, editing, shipRef ?? shipObj, simRef ?? simObj,
            money, mode, availD ?? avail, buyD ?? buyable, refD ?? refundable })!;
    var baseFingerprint = Fingerprint();
    if (Fingerprint() != baseFingerprint)
        throw new InvalidOperationException("Item-cost fingerprint is not deterministic.");
    if (Fingerprint(credits: 101) == baseFingerprint
        || Fingerprint(editing: true) == baseFingerprint
        || Fingerprint(money: 51) == baseFingerprint
        || Fingerprint(mode: 3) == baseFingerprint
        || Fingerprint(shipRef: new object()) == baseFingerprint
        || Fingerprint(simRef: new object()) == baseFingerprint
        || Fingerprint(resArr: Array.CreateInstance(costResourceTupleType, 0)) == baseFingerprint
        || Fingerprint(resArr: NewResourceArray(NewResourceTuple(resId1, 3), NewResourceTuple(resId2, 4)))
            == baseFingerprint
        || Fingerprint(resArr: NewResourceArray(NewResourceTuple(resId2, 2), NewResourceTuple(resId1, 4)))
            == baseFingerprint)
        throw new InvalidOperationException("Item-cost fingerprint ignores a vanilla input.");
    var availMutated = (System.Collections.IDictionary)Activator.CreateInstance(countDictType)!;
    availMutated[resId1] = 6;
    if (Fingerprint(availD: availMutated) == baseFingerprint)
        throw new InvalidOperationException("Item-cost fingerprint ignores available-resource counts.");
    var availExtra = (System.Collections.IDictionary)Activator.CreateInstance(countDictType)!;
    availExtra[resId1] = 5;
    availExtra[resId2] = 1;
    if (Fingerprint(availD: availExtra) == baseFingerprint)
        throw new InvalidOperationException("Item-cost fingerprint ignores available-resource keys.");
    var buyableMutated = (System.Collections.IDictionary)Activator.CreateInstance(costedDictType)!;
    buyableMutated[resId2] = NewQuantities(4, 10);
    if (Fingerprint(buyD: buyableMutated) == baseFingerprint
        || Fingerprint(refD: buyableMutated) == baseFingerprint)
        throw new InvalidOperationException("Item-cost fingerprint ignores buyable/refundable contents.");
}

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

// A raised input-tick delay is the lead by which the client stamps its own
// outgoing inputs, and IsReadyForTick stalls the world on an under-stamped
// lead, so it must not wait up to 167 ms for the next scheduled HostUpdate.
// The postfix is what records what actually went out; Harmony runs a postfix
// even when a prefix skipped the original, so its absence would latch the
// last-sent value and send on every tick forever.
if (!hostOnTickInfo.Postfixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException(
        "Expected multiplayer HostUpdate delay-tracking postfix was not installed.");
}
var isUrgentDelayChange = hostUpdatePatchType.GetMethod(
    "IsUrgentDelayChange",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(hostUpdatePatchType.FullName, "IsUrgentDelayChange");
var unknownDelay = (int)(hostUpdatePatchType.GetField(
    "UnknownDelay",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingFieldException(hostUpdatePatchType.FullName, "UnknownDelay"))
    .GetValue(null)!;
foreach (var (delay, lastSent, expected, why) in new[]
{
    (2, unknownDelay, true, "the first delay of a session has never been sent"),
    (13, 12, true, "a rise leaves peers under-stamping their lead"),
    (12, 12, false, "an unchanged delay is what the 6 Hz schedule is for"),
    (11, 12, false, "a fall only costs peers a little surplus lead"),
})
{
    if ((bool)isUrgentDelayChange.Invoke(null, new object[] { delay, lastSent })! != expected)
    {
        throw new InvalidOperationException(
            $"Urgent-HostUpdate decision for delay {delay} after {lastSent} should be {expected}: {why}.");
    }
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

// The deterministic action and hit-effect queues are drained through the
// contention-free replacement only when the shape check matched this build's
// SimRoot. The prefix pair on EnqueueDeterministic, the drain prefix on
// ExecuteQueued and the release postfix on Dispose must all be installed.
var detQueuePatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.DeterministicQueueContentionPatch",
    throwOnError: true)!;
if (AccessTools.Field(detQueuePatchType, "Applied").GetValue(null) is not true)
{
    throw new InvalidOperationException(
        "The deterministic queue contention patch fell back to vanilla behaviour, "
        + "so producers still convoy on the queue locks.");
}
var objectIdType = AccessTools.TypeByName("Cosmoteer.Game.ObjectID")
    ?? throw new InvalidOperationException("Cosmoteer.Game.ObjectID was not found.");
var hitFxRulesType = AccessTools.TypeByName("Cosmoteer.Simulation.HitEffects.MultiHitEffectRules")
    ?? throw new InvalidOperationException("MultiHitEffectRules was not found.");
var hitFxParamsType = AccessTools.TypeByName("Cosmoteer.Simulation.HitEffects.HitEffectParams")
    ?? throw new InvalidOperationException("HitEffectParams was not found.");
var enqActionTarget = AccessTools.DeclaredMethod(simRootType, "EnqueueDeterministic",
    new[] { objectIdType, typeof(object), typeof(Action<object>) })
    ?? throw new MissingMethodException(simRootType.FullName, "EnqueueDeterministic(action)");
var enqHitFxTarget = AccessTools.DeclaredMethod(simRootType, "EnqueueDeterministic",
    new[] { objectIdType, hitFxRulesType, hitFxParamsType })
    ?? throw new MissingMethodException(simRootType.FullName, "EnqueueDeterministic(hitEffects)");
bool HasDetQueuePrefix(MethodBase target) =>
    Harmony.GetPatchInfo(target)?.Prefixes.Any(
        patch => patch.PatchMethod.DeclaringType?.DeclaringType == detQueuePatchType) == true;
if (!HasDetQueuePrefix(enqActionTarget) || !HasDetQueuePrefix(enqHitFxTarget)
    || !HasDetQueuePrefix(executeQueuedTarget))
{
    throw new InvalidOperationException(
        "Expected Emmanim prefixes were not installed on SimRoot's deterministic "
        + "queue methods, so enqueue traffic still serializes on the vanilla locks.");
}
if (Harmony.GetPatchInfo(simDisposeTarget)?.Postfixes.Any(
        patch => patch.PatchMethod.DeclaringType?.DeclaringType == detQueuePatchType) != true)
{
    throw new InvalidOperationException(
        "Expected Emmanim postfix was not installed on SimRoot.Dispose, so a resync "
        + "could leave the old simulation in the deterministic queue hot cache.");
}

// Drained actions must run grouped by ship id and, within one ship, in the
// order they were posted - exactly the (shipID, actionID) sort vanilla applies.
var oidAdd = objectIdType.GetMethods(BindingFlags.Static | BindingFlags.Public)
    .First(method => method.Name == "op_Addition"
        && method.GetParameters() is [{ ParameterType: var left }, { ParameterType: var right }]
        && left == objectIdType && right == typeof(uint));
object MakeObjectId(uint id) =>
    oidAdd.Invoke(null, new object?[] { Activator.CreateInstance(objectIdType), id })!;
var detEnqueue = AccessTools.DeclaredMethod(detQueuePatchType, "EnqueueAction")
    ?? throw new MissingMethodException(detQueuePatchType.FullName, "EnqueueAction");
var detHitFxEnqueue = AccessTools.DeclaredMethod(detQueuePatchType, "EnqueueHitEffects")
    ?? throw new MissingMethodException(detQueuePatchType.FullName, "EnqueueHitEffects");
var detDrain = AccessTools.DeclaredMethod(detQueuePatchType, "Drain")
    ?? throw new MissingMethodException(detQueuePatchType.FullName, "Drain");
var detRelease = AccessTools.DeclaredMethod(detQueuePatchType, "Release")
    ?? throw new MissingMethodException(detQueuePatchType.FullName, "Release");
var detStateFor = AccessTools.DeclaredMethod(detQueuePatchType, "StateFor")
    ?? throw new MissingMethodException(detQueuePatchType.FullName, "StateFor");

var detSim = new object();
var detOrder = new List<uint>();
void PostAction(uint ship, string tag) =>
    detEnqueue.Invoke(null, new object?[]
    {
        detSim, MakeObjectId(ship), tag,
        new Action<object?>(_ => detOrder.Add(ship)),
    });
PostAction(7, "a");
PostAction(3, "b");
PostAction(7, "c");
detDrain.Invoke(null, new object?[] { detSim });
if (!detOrder.SequenceEqual(new uint[] { 3, 7, 7 }))
{
    throw new InvalidOperationException(
        $"Deterministic queue drained [{string.Join(", ", detOrder)}], expected [3, 7, 7]: "
        + "the (shipID, arrival) ordering changed versus vanilla.");
}

// An action posted by a running callback must still run in the same drain
// pass, appended after the sorted prefix just like vanilla's live-Count loop.
var reentrySim = new object();
var reentryOrder = new List<string>();
detEnqueue.Invoke(null, new object?[]
{
    reentrySim, MakeObjectId(5), null,
    new Action<object?>(_ =>
    {
        reentryOrder.Add("outer");
        detEnqueue.Invoke(null, new object?[]
        {
            reentrySim, MakeObjectId(1), null,
            new Action<object?>(_ => reentryOrder.Add("inner")),
        });
    }),
});
detDrain.Invoke(null, new object?[] { reentrySim });
if (!reentryOrder.SequenceEqual(new[] { "outer", "inner" }))
{
    throw new InvalidOperationException(
        "A callback posted mid-drain was not executed in the same pass, "
        + "which vanilla's live-Count loop guarantees.");
}

// Concurrent producers must all land, and each thread's own posts to one ship
// must stay in that thread's posting order - the only cross-thread ordering
// vanilla's lock ever established.
var concSim = new object();
var perShipThread = new System.Collections.Concurrent.ConcurrentDictionary<
    (uint Ship, int Worker), System.Collections.Concurrent.ConcurrentQueue<int>>();
var concRan = 0;
Parallel.For(0, 8, worker =>
{
    for (var i = 0; i < 200; i++)
    {
        var ship = (uint)(worker % 3);
        var ordinal = i;
        detEnqueue.Invoke(null, new object?[]
        {
            concSim, MakeObjectId(ship), null,
            new Action<object?>(_ =>
            {
                Interlocked.Increment(ref concRan);
                perShipThread.GetOrAdd((ship, worker), static _ => new()).Enqueue(ordinal);
            }),
        });
    }
});
detDrain.Invoke(null, new object?[] { concSim });
if (concRan != 8 * 200)
{
    throw new InvalidOperationException(
        $"Deterministic queue ran {concRan} callbacks, expected {8 * 200}.");
}
foreach (var posted in perShipThread.Values)
{
    var ordinals = posted.ToArray();
    if (!ordinals.SequenceEqual(ordinals.OrderBy(ordinal => ordinal)))
    {
        throw new InvalidOperationException(
            "Deterministic queue reordered one thread's own posts to a ship, "
            + "which vanilla's arrival-order actionID does not do.");
    }
}

// The hit-effect enqueue must land on the replacement queue with its params
// untouched until the drain; DoEffect is not callable here, so the queued
// entry itself is the assertion. Releasing the sim must drop the state.
var fxSim = new object();
var fxParams = RuntimeHelpers.GetUninitializedObject(hitFxParamsType);
detHitFxEnqueue.Invoke(null, new object?[] { fxSim, MakeObjectId(2), null, fxParams });
var fxState = detStateFor.Invoke(null, new object?[] { fxSim, false })
    ?? throw new InvalidOperationException("Hit-effect enqueue created no queue state.");
var fxQueue = AccessTools.Field(fxState.GetType(), "HitEffects").GetValue(fxState)
    ?? throw new InvalidOperationException("Queue state has no hit-effect queue.");
if ((int)fxQueue.GetType().GetProperty("Count")!.GetValue(fxQueue)! != 1)
{
    throw new InvalidOperationException(
        "A hit-effect enqueue did not land on the replacement queue.");
}
detRelease.Invoke(null, new object?[] { fxSim });
if (detStateFor.Invoke(null, new object?[] { fxSim, false }) != null)
{
    throw new InvalidOperationException(
        "Deterministic queue state survived release for a disposed simulation.");
}
var hotAfterDetRelease = AccessTools.Field(detQueuePatchType, "_hot").GetValue(null);
if (hotAfterDetRelease != null
    && ReferenceEquals(
        AccessTools.Field(hotAfterDetRelease.GetType(), "Sim").GetValue(hotAfterDetRelease), fxSim))
{
    throw new InvalidOperationException(
        "The deterministic queue hot cache still strongly references the released simulation.");
}

// The FTL efficiency overlay's gate must compare pending drives without LINQ
// and its prefix must be installed on DrawFtlEfficiencyOverlay.
var ftlOverlayPatchType = typeof(EntryPoint).Assembly.GetType(
    "EmmanimLagFix.Code.FtlEfficiencyOverlayAllocationPatch",
    throwOnError: true)!;
var drawFtlTarget = AccessTools.DeclaredMethod(
    AccessTools.TypeByName("Cosmoteer.Game.Gui.Build.BuildToolbox")!, "DrawFtlEfficiencyOverlay")
    ?? throw new MissingMethodException("BuildToolbox", "DrawFtlEfficiencyOverlay");
if (Harmony.GetPatchInfo(drawFtlTarget)?.Prefixes.Any(
        patch => patch.PatchMethod.DeclaringType == ftlOverlayPatchType) != true)
{
    throw new InvalidOperationException(
        "Expected Emmanim prefix was not installed on BuildToolbox.DrawFtlEfficiencyOverlay.");
}

// The gate must reproduce vanilla's SequenceEqual semantics exactly: null
// cached equals only null; a non-null cached list sequence-equals the current
// list, with null counting as empty.
var pendingDriveType = AccessTools.TypeByName("Cosmoteer.Game.Gui.Build.PendingFtlDrive")
    ?? throw new InvalidOperationException("PendingFtlDrive was not found.");
var ftlRulesType = AccessTools.TypeByName("Cosmoteer.Ships.Parts.Ftl.FtlDriveRules")
    ?? throw new InvalidOperationException("FtlDriveRules was not found.");
var pendingListType = typeof(List<>).MakeGenericType(pendingDriveType);
var ftlVector2Type = AccessTools.TypeByName("Halfling.Geometry.Vector2")!;
var ftlIntRectType = AccessTools.TypeByName("Halfling.Geometry.IntRect")!;
var ftlIntVector2Type = AccessTools.TypeByName("Halfling.Geometry.IntVector2")!;
object MakePendingDrive(object rules, float x, float y, int rx, int ry, int rw, int rh) =>
    Activator.CreateInstance(pendingDriveType, rules,
        Activator.CreateInstance(ftlVector2Type, x, y),
        Activator.CreateInstance(ftlIntRectType, rx, ry, rw, rh))!;
object MakePendingList(params object[] drives)
{
    var list = (IList)Activator.CreateInstance(pendingListType)!;
    foreach (var drive in drives)
        list.Add(drive);
    return list;
}
var pendingEqual = AccessTools.DeclaredMethod(ftlOverlayPatchType, "PendingDrivesEqual")
    ?? throw new MissingMethodException(ftlOverlayPatchType.FullName, "PendingDrivesEqual");
var ftlRulesA = RuntimeHelpers.GetUninitializedObject(ftlRulesType);
var driveA = MakePendingDrive(ftlRulesA, 1f, 2f, 0, 0, 2, 2);
var driveA2 = MakePendingDrive(ftlRulesA, 1f, 2f, 0, 0, 2, 2);
var driveB = MakePendingDrive(ftlRulesA, 9f, 9f, 4, 4, 2, 2);
bool GateEqual(object? cached, object? current) =>
    (bool)pendingEqual.Invoke(null, new[] { cached, current })!;
if (!GateEqual(null, null)) throw new InvalidOperationException("FTL gate: null != null.");
if (GateEqual(null, MakePendingList(driveA))) throw new InvalidOperationException("FTL gate: null == list.");
if (!GateEqual(MakePendingList(), null)) throw new InvalidOperationException("FTL gate: empty cached != null current.");
if (!GateEqual(MakePendingList(driveA), MakePendingList(driveA2)))
    throw new InvalidOperationException("FTL gate: identical drives reported different.");
if (GateEqual(MakePendingList(driveA), MakePendingList(driveA2, driveB)))
    throw new InvalidOperationException("FTL gate: length difference ignored.");
if (GateEqual(MakePendingList(driveA), MakePendingList(driveB)))
    throw new InvalidOperationException("FTL gate: content difference ignored.");

// Bitwise-compare the re-implemented efficiency math against vanilla's
// BlueprintPartsManager.CalculateJumpEfficiency on a fabricated ship: one
// plain part plus one FTL drive part, no pending drives.
var ftlManagerType = AccessTools.TypeByName("Cosmoteer.Ships.Blueprints.BlueprintPartsManager")!;
var ftlBpPartType = AccessTools.TypeByName("Cosmoteer.Ships.Blueprints.BlueprintPart")!;
var ftlPartRulesType = AccessTools.TypeByName("Cosmoteer.Ships.Parts.PartRules")!;
var ftlComponentRulesType = AccessTools.TypeByName("Cosmoteer.Ships.Parts.PartComponentRules")!;
var ftlRangeFloatType = AccessTools.TypeByName("Halfling.Range`1")!.MakeGenericType(typeof(float));
object MakeRules(float density, int w, int h, params object[] components)
{
    var rules = RuntimeHelpers.GetUninitializedObject(ftlPartRulesType);
    AccessTools.Field(ftlPartRulesType, "Size").SetValue(rules, Activator.CreateInstance(ftlIntVector2Type, w, h));
    AccessTools.Field(ftlPartRulesType, "Density").SetValue(rules, density);
    var componentsList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ftlComponentRulesType))!;
    foreach (var component in components)
        componentsList.Add(component);
    AccessTools.Field(ftlPartRulesType, "Components").SetValue(rules, componentsList);
    return rules;
}
object MakePart(object rules, int x, int y)
{
    var part = RuntimeHelpers.GetUninitializedObject(ftlBpPartType);
    AccessTools.Field(ftlBpPartType, "<Rules>k__BackingField").SetValue(part, rules);
    AccessTools.Field(ftlBpPartType, "<Location>k__BackingField")
        .SetValue(part, Activator.CreateInstance(ftlIntVector2Type, x, y));
    AccessTools.Field(ftlBpPartType, "_rotFlip").SetValue(part, 0);
    return part;
}
var ftlDrive = RuntimeHelpers.GetUninitializedObject(ftlRulesType);
AccessTools.Field(ftlRulesType, "JumpEfficiency").SetValue(ftlDrive, 0.75f);
var driveRange = Activator.CreateInstance(ftlRangeFloatType)!;
AccessTools.Field(ftlRangeFloatType, "Min").SetValue(driveRange, 1f);
AccessTools.Field(ftlRangeFloatType, "Max").SetValue(driveRange, 8f);
AccessTools.Field(ftlRulesType, "JumpEfficiencyDistanceRange").SetValue(ftlDrive, driveRange);

var ftlManager = RuntimeHelpers.GetUninitializedObject(ftlManagerType);
var ftlOrderedParts = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ftlBpPartType))!;
var plainPart = MakePart(MakeRules(1.5f, 2, 2), 0, 0);
var drivePartObj = MakePart(MakeRules(2f, 2, 2, ftlDrive), 2, 0);
ftlOrderedParts.Add(plainPart);
ftlOrderedParts.Add(drivePartObj);
AccessTools.Field(ftlManagerType, "_orderedParts").SetValue(ftlManager, ftlOrderedParts);
var byCategory = (IDictionary)Activator.CreateInstance(AccessTools
    .Field(ftlManagerType, "_partsByCategory").FieldType)!;
var ftlSetType = typeof(HashSet<>).MakeGenericType(ftlBpPartType);
var ftlSet = Activator.CreateInstance(ftlSetType)!;
ftlSetType.GetMethod("Add")!.Invoke(ftlSet, new[] { drivePartObj });
byCategory.Add(AccessTools.Field(AccessTools.TypeByName("Cosmoteer.PartCategories")!, "Ftl")
    .GetValue(null)!, ftlSet);
AccessTools.Field(ftlManagerType, "_partsByCategory").SetValue(ftlManager, byCategory);

var vanillaCalc = AccessTools.DeclaredMethod(ftlManagerType, "CalculateJumpEfficiency")
    ?? throw new MissingMethodException(ftlManagerType.FullName, "CalculateJumpEfficiency");
var patchedCalc = AccessTools.DeclaredMethod(ftlOverlayPatchType, "CalculateJumpEfficiency")
    ?? throw new MissingMethodException(ftlOverlayPatchType.FullName, "CalculateJumpEfficiency");
var vanillaEff = (float)vanillaCalc.Invoke(ftlManager, new object?[] { null, null })!;

// The replacement writes overlay pixels directly; hand it a pinned buffer
// standing in for the upload texture and compare both the efficiency and the
// written cells against the vanilla callback path.
var bounds = Activator.CreateInstance(ftlIntRectType, 0, 0, 4, 2)!;
var pixelSize = Marshal.SizeOf(AccessTools.TypeByName("Halfling.Graphics.IntColor")!);
var pixels = new byte[4 * 2 * pixelSize];
var pixelHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
try
{
    var buildGuiRulesType = AccessTools.TypeByName("Cosmoteer.Game.Gui.Build.BuildGuiRules")!;
    var guiRules = RuntimeHelpers.GetUninitializedObject(buildGuiRulesType);
    var hueRange = Activator.CreateInstance(ftlRangeFloatType)!;
    AccessTools.Field(ftlRangeFloatType, "Min").SetValue(hueRange, 100f);
    AccessTools.Field(ftlRangeFloatType, "Max").SetValue(hueRange, 200f);
    AccessTools.Field(buildGuiRulesType, "FtlOverlayHueRange").SetValue(guiRules, hueRange);
    AccessTools.Field(buildGuiRulesType, "FtlOverlayAlpha").SetValue(guiRules, 0.5f);
    var patchedEff = (float)patchedCalc.Invoke(null, new object?[]
    {
        ftlManager, null, pixelHandle.AddrOfPinnedObject(), 4 * pixelSize, bounds, guiRules,
    })!;
    if (BitConverter.SingleToUInt32Bits(vanillaEff) != BitConverter.SingleToUInt32Bits(patchedEff))
    {
        throw new InvalidOperationException(
            $"FTL efficiency diverged: vanilla={vanillaEff} patched={patchedEff}.");
    }
    // The drive part's own cells must stay untouched (TransparentWhite == 0
    // bytes aside, vanilla skipped the callback there); the plain part's two
    // cells must have been written with non-zero bytes.
    var transparentBytes = new byte[pixelSize];
    bool CellIsTransparent(int x, int y) =>
        pixels.Skip((y * 4 + x) * pixelSize).Take(pixelSize).SequenceEqual(transparentBytes);
    if (CellIsTransparent(0, 0) || CellIsTransparent(1, 0))
        throw new InvalidOperationException("FTL overlay skipped the plain part's cells.");
    if (!CellIsTransparent(2, 0) || !CellIsTransparent(3, 0))
        throw new InvalidOperationException("FTL overlay wrote into the drive part's own cells.");
}
finally
{
    pixelHandle.Free();
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
// one, so prove generation retirement leaves both dense and sparse rounds empty
// and that a recycled scratch treats every previously seen set as unseen. The
// table probes on object identity, which is all the game's sets use, so
// uninitialized instances are valid keys - and the dense round crosses the
// half-full boundary of the initial 256 slots, which exercises Grow's rehash.
if (AccessTools.Property(searchSetsPatchType, "UsesReferenceEquality")!.GetValue(null) is not true)
{
    throw new InvalidOperationException(
        "ContiguousPathSet no longer answers equality by reference, so an identity-probed "
        + "visited table would accept a different first occurrence than vanilla did.");
}

var scratchType = searchSetsPatchType.GetNestedType("SearchScratch", BindingFlags.NonPublic)!;
var scratchRent = AccessTools.Method(scratchType, "Rent")!;
var scratchAdd = AccessTools.Method(scratchType, "Add")!;
var scratchRelease = AccessTools.Method(scratchType, "Release")!;
var scratchCountField = AccessTools.Field(scratchType, "_count")!;

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
    var remaining = (int)scratchCountField.GetValue(scratch)!;
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

    // Every set from the retired generation, not just the first: a stamp left
    // current anywhere would silently prune that branch of the next walk.
    for (var i = 0; i < visitCount; i++)
    {
        if (scratchAdd.Invoke(reused, new[] { sets[i] }) is not true)
        {
            throw new InvalidOperationException(
                $"A recycled search scratch still considered set {i} of {visitCount} as visited.");
        }
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


// The new search-local context must keep nested rounds on the vanilla set,
// isolate simultaneous searches, and release the outer generation on failure.
var allocLocal = AccessTools.Method(sourceVisitedPatchType, "AllocLocal")!;
var localAdd = AccessTools.Method(sourceVisitedPatchType, "LocalAdd")!;
void CheckLocalRound()
{
    object?[] outerArgs = { null };
    var outer = allocLocal.Invoke(null, outerArgs)!;
    try
    {
        if (outerArgs[0] is null || localAdd.Invoke(null, new[] { outer, sources[0], outerArgs[0] }) is not true)
            throw new InvalidOperationException("Local outer generation was not captured.");
        object?[] innerArgs = { null };
        var inner = allocLocal.Invoke(null, innerArgs)!;
        try
        {
            if (innerArgs[0] is not null
                || localAdd.Invoke(null, new[] { inner, sources[0], innerArgs[0] }) is not true
                || localAdd.Invoke(null, new[] { inner, sources[0], innerArgs[0] }) is not false
                || (int)setCountProperty.GetValue(inner)! != 1)
                throw new InvalidOperationException("Nested local search lost vanilla fallback.");
        }
        finally { ((IDisposable)inner).Dispose(); }
        if (localAdd.Invoke(null, new[] { outer, sources[0], outerArgs[0] }) is not false
            || localAdd.Invoke(null, new[] { outer, sources[1], outerArgs[0] }) is not true
            || (int)setCountProperty.GetValue(outer)! != 0)
            throw new InvalidOperationException("Nested search corrupted the outer generation.");
        // Simulate a failure inside the game's try/finally.
        throw new ApplicationException("Expected local-search cleanup fixture");
    }
    catch (ApplicationException) { }
    finally { ((IDisposable)outer).Dispose(); }
}
CheckLocalRound();
CheckLocalRound();
Parallel.For(0, 32, _ => CheckLocalRound());

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

    // Longest-first ordering binds SimRoot.ParallelFixedUpdate and rewrites
    // each dispatch slice descending by part count; both the binding and the
    // ordering itself are verified here.
    {
        var lptType = typeof(EntryPoint).Assembly.GetType(
            "EmmanimLagFix.Code.ParallelBucketLptPatch", throwOnError: true)!;
        var lptTarget = (MethodBase?)AccessTools
            .DeclaredMethod(lptType, "TargetMethod")!
            .Invoke(null, null);
        if (lptTarget?.Name != "ParallelFixedUpdate")
        {
            throw new InvalidOperationException(
                $"Bucket ordering bound {lptTarget?.Name ?? "nothing"} instead of ParallelFixedUpdate.");
        }

        var sort = AccessTools.DeclaredMethod(lptType, "SortSlice")!
            .MakeGenericMethod(typeof(int));
        var slice = new[] { 1, 9, 4, 7, 2, 8 };
        sort.Invoke(null, new object[] { slice, 1, 4, (Func<int, int>)(x => x) });
        if (!slice.SequenceEqual(new[] { 1, 9, 7, 4, 2, 8 }))
        {
            throw new InvalidOperationException(
                "Bucket ordering produced [" + string.Join(",", slice)
                + "]; expected [1,9,7,4,2,8].");
        }
    }

    // The convoy timer binds the compiler-emitted local function inside
    // FastParallel.For; resolving it by hand proves the name substring still
    // matches on this game build.
    {
        var waitPatchType = typeof(EntryPoint).Assembly.GetType(
            "EmmanimLagFix.Code.FastParallelWaitDiagnosticsPatch", throwOnError: true)!;
        var waitTarget = (MethodBase?)AccessTools
            .DeclaredMethod(waitPatchType, "TargetMethod")!
            .Invoke(null, null);
        if (waitTarget?.DeclaringType
                != halflingAssembly.GetType("Halfling.Performance.FastParallel", throwOnError: true)!
            || !waitTarget.Name.Contains("WaitUntilFinished", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FastParallel wait timing bound {waitTarget?.Name ?? "nothing"} "
                + "instead of For's _WaitUntilFinished local function.");
        }
    }

    // The worker-priority override binds FastParallel.RunThread by name; the
    // binding must resolve even though the patch is inert without its flag.
    {
        var fastParallel = halflingAssembly.GetType(
            "Halfling.Performance.FastParallel", throwOnError: true)!;
        var runThread = fastParallel
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "RunThread");
        if (runThread == null)
        {
            throw new InvalidOperationException(
                "FastParallel.RunThread not found; the worker-priority override cannot bind.");
        }
    }

    // Constructor deserialization methods contain exception filters and cannot
    // be wrapped by Harmony. Verify that MonoMod.Core native detours bind every
    // closed target, actually redirect an invocation through our dispatcher,
    // and that the compiled constructor invoker handles classes, structs and
    // callee exceptions.
    {
        var invokePatchType = typeof(EntryPoint).Assembly.GetType(
            "EmmanimLagFix.Code.DeserializationInvokeCompilePatch", throwOnError: true)!;
        var invokeTargets = ((IEnumerable<MethodBase>)AccessTools
            .DeclaredMethod(invokePatchType, "ScanTargets")!
            .Invoke(null, null)!).ToList();
        if (invokeTargets.Any(t => t.ContainsGenericParameters))
        {
            throw new InvalidOperationException(
                "Deserialization invoke compile resolved an open-generic target: "
                + invokeTargets.First(t => t.ContainsGenericParameters).DeclaringType);
        }
        if (invokeTargets.Count < 2)
        {
            throw new InvalidOperationException(
                $"Deserialization detour resolved {invokeTargets.Count} targets; expected at least 2.");
        }
        if (!invokeTargets.Any(t => t.GetMethodBody()?.ExceptionHandlingClauses
                .Any(c => c.Flags == ExceptionHandlingClauseOptions.Filter) == true))
        {
            throw new InvalidOperationException(
                "Deserialization detour fixture no longer contains a filter region; "
                + "re-evaluate whether the lower-level detour is still necessary.");
        }

        var compile = AccessTools.DeclaredMethod(invokePatchType, "Compile")!;
        var classCtor = typeof(Version).GetConstructor(new[] { typeof(int), typeof(int) })!;
        var structCtor = typeof(KeyValuePair<int, string>).GetConstructors()[0];
        var compiledClass = (Func<object?[]?, object?>)compile
            .Invoke(null, new object[] { classCtor })!;
        var compiledStruct = (Func<object?[]?, object?>)compile
            .Invoke(null, new object[] { structCtor })!;
        if (compiledClass(new object?[] { 1, 2 }) is not Version { Major: 1, Minor: 2 }
            || compiledStruct(new object?[] { 7, "x" }) is not KeyValuePair<int, string> pair
            || pair.Key != 7 || pair.Value != "x")
        {
            throw new InvalidOperationException(
                "Compiled deserialization invoker produced a wrong object.");
        }

        // A callee exception must escape the compiled delegate raw - the
        // prefix's InvokeCompiled reproduces reflection's
        // TargetInvocationException wrap and the DeserializeAsNullException
        // outcome on top of that raw throw.
        var throwingMethod = typeof(Convert).GetMethod(
            nameof(Convert.FromBase64String), new[] { typeof(string) })!;
        var compiledThrower = (Func<object?[]?, object?>)compile
            .Invoke(null, new object[] { throwingMethod })!;
        try
        {
            compiledThrower(new object?[] { null });
            throw new InvalidOperationException(
                "Compiled deserialization invoker swallowed a callee exception.");
        }
        catch (ArgumentNullException)
        {
        }

        AccessTools.DeclaredMethod(invokePatchType, "Apply")!.Invoke(null, null);
        var applied = (int)AccessTools.Property(invokePatchType, "Applied")!.GetValue(null)!;
        var resolved = (int)AccessTools.Property(invokePatchType, "Resolved")!.GetValue(null)!;
        if (applied != invokeTargets.Count || resolved != invokeTargets.Count)
        {
            var failure = AccessTools.Field(invokePatchType, "FailureReason")!.GetValue(null);
            throw new InvalidOperationException(
                $"Deserialization native detours applied {applied}/{resolved}; "
                + $"scan found {invokeTargets.Count}. Failure: {failure}");
        }

        // Invoke a detoured target on an uninitialized method object with the
        // skip flag. Neither vanilla nor the replacement touches serializer or
        // source in this branch, making it a safe ABI/redirection probe across
        // the private closed-generic declaring type.
        var probeTarget = (MethodInfo)invokeTargets[0];
        var probeSelf = RuntimeHelpers.GetUninitializedObject(probeTarget.DeclaringType!);
        var probeParams = probeTarget.GetParameters();
        var call = new object?[probeParams.Length];
        for (var i = 0; i < probeParams.Length; i++)
        {
            var parameterType = probeParams[i].ParameterType;
            if (parameterType.IsByRef)
            {
                call[i] = null;
            }
            else if (parameterType.IsValueType)
            {
                call[i] = Activator.CreateInstance(parameterType);
            }
        }
        call[3] = typeof(object);
        call[4] = Enum.Parse(probeParams[4].ParameterType, "SkipConstructorDeserializer");
        var entries = AccessTools.Field(invokePatchType, "Entries")!;
        var before = (long)entries.GetValue(null)!;
        var result = probeTarget.Invoke(probeSelf, call);
        var after = (long)entries.GetValue(null)!;
        if (result is not false || call[^1] != null || after != before + 1)
        {
            throw new InvalidOperationException(
                $"Deserialization native detour ABI probe failed: result={result}, "
                + $"out={call[^1]}, entries={before}->{after}.");
        }

        // Exercise the complete hot path without needing serialized input:
        // instantiate a SpecificConstructorDeserializationMethod around
        // object..ctor (zero arguments), then let the detoured method compile
        // and invoke it. The serializer and source are intentionally
        // uninitialized because the zero-argument path never reads them.
        var specificTarget = (MethodInfo)invokeTargets.First(t =>
            t.DeclaringType!.Name == "SpecificConstructorDeserializationMethod");
        var specificType = specificTarget.DeclaringType!;
        var specificParams = specificTarget.GetParameters();
        var serializer = RuntimeHelpers.GetUninitializedObject(specificParams[0].ParameterType);
        var objectCtor = typeof(object).GetConstructor(Type.EmptyTypes)!;
        var methodCtor = specificType.GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 2);
        var methodObject = methodCtor.Invoke(new object[] { serializer, objectCtor });
        var hotCall = new object?[specificParams.Length];
        for (var i = 0; i < specificParams.Length; i++)
        {
            var parameterType = specificParams[i].ParameterType;
            if (!parameterType.IsByRef && parameterType.IsValueType)
            {
                hotCall[i] = Activator.CreateInstance(parameterType);
            }
        }
        hotCall[0] = serializer;
        hotCall[3] = typeof(object);
        var invocations = AccessTools.Field(invokePatchType, "Invocations")!;
        var compiled = AccessTools.Field(invokePatchType, "Compiled")!;
        var invocationsBefore = (long)invocations.GetValue(null)!;
        var compiledBefore = (long)compiled.GetValue(null)!;
        var hotResult = specificTarget.Invoke(methodObject, hotCall);
        var invocationsAfter = (long)invocations.GetValue(null)!;
        var compiledAfter = (long)compiled.GetValue(null)!;
        if (hotResult is not true
            || hotCall[^1] == null
            || hotCall[^1]!.GetType() != typeof(object)
            || invocationsAfter != invocationsBefore + 1
            || compiledAfter != compiledBefore + 1)
        {
            throw new InvalidOperationException(
                $"Deserialization compiled-invoke probe failed: result={hotResult}, "
                + $"out={hotCall[^1]?.GetType()}, calls={invocationsBefore}->{invocationsAfter}, "
                + $"compiled={compiledBefore}->{compiledAfter}.");
        }

        // The original reflection path treats a constructor/factory that
        // throws DeserializeAsNullException as a successful null result and
        // wraps every other callee exception in TargetInvocationException.
        object CreateMethodObject(string methodName) => methodCtor.Invoke(
            new object[]
            {
                serializer,
                typeof(DeserializationDetourFixture).GetMethod(
                    methodName, BindingFlags.Static | BindingFlags.NonPublic)!,
            });

        var nullCall = (object?[])hotCall.Clone();
        nullCall[^1] = new object();
        var nullResult = specificTarget.Invoke(
            CreateMethodObject(nameof(DeserializationDetourFixture.DeserializeAsNull)),
            nullCall);
        if (nullResult is not true || nullCall[^1] != null)
        {
            throw new InvalidOperationException(
                "Deserialization detour did not preserve DeserializeAsNullException semantics.");
        }

        var failureCall = (object?[])hotCall.Clone();
        failureCall[^1] = null;
        try
        {
            specificTarget.Invoke(
                CreateMethodObject(nameof(DeserializationDetourFixture.Fail)),
                failureCall);
            throw new InvalidOperationException(
                "Deserialization detour swallowed a constructor exception.");
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException is TargetInvocationException
                { InnerException: InvalidOperationException fixture }
                && fixture.Message == "detour exception fixture")
        {
            // One wrapper comes from Dispatch (matching MethodBase.Invoke)
            // and one from this smoke test's reflective call to TryDeserialize.
        }
    }

    // ThreadedTaskQueue accepts only Action<ThreadedTaskQueue> and
    // Func<ThreadedTaskQueue,T>; its vanilla WorkerThread nevertheless uses
    // Delegate.DynamicInvoke for every operation. Verify the transpiler and
    // the cached direct-call bridge, including void/value returns, exception
    // wrapping and steady-state allocation behavior.
    {
        var queuePatchType = typeof(EntryPoint).Assembly.GetType(
            "EmmanimLagFix.Code.ThreadedTaskQueueDynamicInvokePatch",
            throwOnError: true)!;
        if ((bool)AccessTools.Field(queuePatchType, "Applied")!.GetValue(null)! != true)
        {
            throw new InvalidOperationException(
                "ThreadedTaskQueue DynamicInvoke replacement did not apply.");
        }

        var invokeMethod = AccessTools.DeclaredMethod(queuePatchType, "Invoke")!;
        var invoke = (Func<Delegate, object?[]?, object?>)invokeMethod.CreateDelegate(
            typeof(Func<Delegate, object?[]?, object?>));
        var queueType = halflingAssembly.GetType(
            "Halfling.Performance.ThreadedTaskQueue", throwOnError: true)!;
        var queue = RuntimeHelpers.GetUninitializedObject(queueType);
        var queueArgs = new[] { queue };

        var funcType = typeof(Func<,>).MakeGenericType(queueType, typeof(int));
        var funcMethod = typeof(ThreadedTaskQueueFixture).GetMethod(
            nameof(ThreadedTaskQueueFixture.Return42),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var func = funcMethod.CreateDelegate(funcType);
        if (invoke(func, queueArgs) is not 42)
        {
            throw new InvalidOperationException(
                "ThreadedTaskQueue direct invoker returned the wrong value.");
        }

        var actionType = typeof(Action<>).MakeGenericType(queueType);
        var actionMethod = typeof(ThreadedTaskQueueFixture).GetMethod(
            nameof(ThreadedTaskQueueFixture.MarkCalled),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var action = actionMethod.CreateDelegate(actionType);
        ThreadedTaskQueueFixture.Called = false;
        if (invoke(action, queueArgs) != null || !ThreadedTaskQueueFixture.Called)
        {
            throw new InvalidOperationException(
                "ThreadedTaskQueue direct action invoker did not run.");
        }

        var failMethod = typeof(ThreadedTaskQueueFixture).GetMethod(
            nameof(ThreadedTaskQueueFixture.Fail),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var fail = failMethod.CreateDelegate(funcType);
        try
        {
            invoke(fail, queueArgs);
            throw new InvalidOperationException(
                "ThreadedTaskQueue direct invoker swallowed a callback exception.");
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException is InvalidOperationException fixture
                && fixture.Message == "queue callback fixture")
        {
        }

        // Warmed direct invocations use the cached generated delegate and do
        // not allocate. This excludes the caller-owned one-element args array,
        // which the follow-up transpiler can remove separately if worthwhile.
        var objectFuncType = typeof(Func<,>).MakeGenericType(queueType, typeof(object));
        var objectFunc = typeof(ThreadedTaskQueueFixture).GetMethod(
                nameof(ThreadedTaskQueueFixture.ReturnObject),
                BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate(objectFuncType);
        for (var i = 0; i < 100; i++)
        {
            _ = invoke(objectFunc, queueArgs);
        }
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            _ = invoke(objectFunc, queueArgs);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (allocated != 0)
        {
            throw new InvalidOperationException(
                $"ThreadedTaskQueue direct invoker allocated {allocated} bytes / 10000 warmed calls.");
        }
    }

    // Update bucket 7 runs on the pool through vanilla's own SimRoot.ParallelUpdate.
    // Everything about it is resolved by reflection - an internal type, a private
    // method and an inherited setter - so a rename would leave the bucket silently
    // serial again rather than failing the build.
    {
        var parallelBucketType = typeof(EntryPoint).Assembly.GetType(
            "EmmanimLagFix.Code.SmoothedValueBucketParallelPatch", throwOnError: true)!;
        string Reason() =>
            AccessTools.PropertyGetter(parallelBucketType, "FailureReason")!
                .Invoke(null, null) as string ?? "no reason recorded";

        var bucketSimRootType = AccessTools.PropertyGetter(parallelBucketType, "SimRootType")!
            .Invoke(null, null) as Type
            ?? throw new InvalidOperationException(
                "SmoothedValueBucketParallelPatch could not resolve SimRoot: " + Reason());

        var parallelUpdate = AccessTools.PropertyGetter(parallelBucketType, "ParallelUpdate")!
            .Invoke(null, null) as MethodInfo
            ?? throw new InvalidOperationException(
                "SimRoot.ParallelUpdate was not found: " + Reason());
        var setter = AccessTools.PropertyGetter(parallelBucketType, "Setter")!
            .Invoke(null, null) as MethodInfo
            ?? throw new InvalidOperationException(
                "SceneRoot.SetCustomUpdateBucketHandler was not found: " + Reason());

        // The postfix builds the handler from the setter's own second parameter,
        // so the two shapes have to agree or the CreateDelegate throws at runtime
        // on a scene that is already half-constructed.
        var handlerType = setter.GetParameters() is { Length: 2 } setterParameters
            ? setterParameters[1].ParameterType
            : throw new InvalidOperationException(
                "SetCustomUpdateBucketHandler no longer takes (bucket, handler).");
        var invoke = handlerType.GetMethod("Invoke")
            ?? throw new InvalidOperationException(
                $"{handlerType.Name} is not a delegate type.");
        if (!invoke.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual(parallelUpdate.GetParameters().Select(p => p.ParameterType)))
        {
            throw new InvalidOperationException(
                "SimRoot.ParallelUpdate no longer matches the custom bucket handler signature, "
                + "so update bucket 7 cannot be bound to it.");
        }

        var targets = AccessTools.DeclaredMethod(parallelBucketType, "TargetMethods")!;
        var bucketTargets = ((IEnumerable<MethodBase>)targets.Invoke(null, null)!).ToArray();
        if (bucketTargets.Length != 1 || bucketTargets[0].Name != "StartInit")
        {
            throw new InvalidOperationException(
                "Expected SimRoot.StartInit as the single place to append the bucket "
                + "registration; got ["
                + string.Join(", ", bucketTargets.Select(m => m.Name)) + "].");
        }

        // Bucket 7 must still be one vanilla does not already run in parallel, or
        // this patch is redundant and its overwrite hides a vanilla change.
        var buckets = AccessTools.TypeByName("Cosmoteer.UpdateBuckets")
            ?? throw new TypeLoadException("Cosmoteer.UpdateBuckets was not found.");
        if (buckets.GetFields(BindingFlags.Static | BindingFlags.Public)
                .Where(field => field.IsLiteral)
                .All(field => (int)field.GetRawConstantValue()! != 7))
        {
            throw new InvalidOperationException("Update bucket 7 no longer exists.");
        }

        Console.WriteLine(
            "PASS: smoothed-value update bucket 7 binds vanilla's SimRoot.ParallelUpdate "
            + $"({bucketSimRootType.FullName}).");
    }

    var relayType = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.PeerDiagnosticsRelayPatch", throwOnError: true)!;
    var breakdownType = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.SceneUpdateBreakdown", throwOnError: true)!;
    var formatFocused = AccessTools.Method(breakdownType, "FormatFocused")!;
    var focused = (string)formatFocused.Invoke(null, new object[]
    {
        new[] { new KeyValuePair<int, long>(1, Stopwatch.Frequency), new KeyValuePair<int, long>(2, Stopwatch.Frequency * 2) },
        new[] { new KeyValuePair<int, long>(1, 20), new KeyValuePair<int, long>(2, 40) },
        new Dictionary<int, string> { [1] = "Statuses", [2] = "Resources" }, 100L
    })!;
    if (focused != "frames=100 st=10.000/20 res=20.000/40")
        throw new InvalidOperationException($"Focused bucket report mismatch: {focused}");
    var unknown = (string)formatFocused.Invoke(null, new object[]
    {
        Array.Empty<KeyValuePair<int, long>>(), Array.Empty<KeyValuePair<int, long>>(),
        new Dictionary<int, string> { [1] = "Statuses" }, 100L
    })!;
    if (unknown != "frames=100 st=0.000/0 res=-/-")
        throw new InvalidOperationException($"Missing bucket was confused with zero work: {unknown}");
    var focusedPayload = "#ELFDIAG#kind=status-resource t=2147483647 frames=999999 st=9999.999/99999999 res=9999.999/99999999"
        + " cap=1/1 ctx=on sk=9999999999 vf=9999999999 bs=9999999999/9999999999";
    if (focusedPayload.Length > 195)
        throw new InvalidOperationException("Focused diagnostics exceed the chat budget.");

    // The wide bucket list is the 2.2.6 addition, and the only list built to a
    // character budget: a relayed line is truncated before sending rather than cut
    // mid-field, so Format has to stop at the last whole entry that fits. Twelve
    // equally expensive buckets with names longer than the clip is the worst case.
    var formatMethod = AccessTools.Method(breakdownType, "Format")
        ?? throw new MissingMethodException(breakdownType.FullName, "Format");
    var manyBuckets = Enumerable.Range(1, 12)
        .Select(i => new KeyValuePair<int, long>(i, Stopwatch.Frequency * (13 - i)))
        .ToArray();
    var longNames = Enumerable.Range(1, 12)
        .ToDictionary(i => i, i => "BucketNameNumber" + i);
    foreach (var budget in new[] { 130, 74, 12, 7 })
    {
        var list = (string)formatMethod.Invoke(null, new object[]
        {
            manyBuckets.ToArray(), longNames, 1d / Stopwatch.Frequency, 12, 6, budget
        })!;
        if (list.Length > budget)
        {
            throw new InvalidOperationException(
                $"A bucket list built to a {budget}-character budget came out {list.Length} long: {list}");
        }
        foreach (var row in list.Split(','))
        {
            // Every surviving row must still be a whole "name:0.0" pair; a cut one
            // is what the budget exists to prevent.
            var colon = row.IndexOf(':');
            if (row == "-")
            {
                continue;
            }
            if (colon <= 0 || !double.TryParse(
                    row[(colon + 1)..],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            {
                throw new InvalidOperationException(
                    $"A bucket list was cut mid-field at budget {budget}: {list}");
            }
        }
    }

    // Unbudgeted, the local line must actually list more than the old five.
    var wideList = (string)formatMethod.Invoke(null, new object[]
    {
        manyBuckets.ToArray(), longNames, 1d / Stopwatch.Frequency, 12, 0, 0
    })!;
    if (wideList.Split(',').Length != 12)
    {
        throw new InvalidOperationException(
            $"Expected all twelve buckets in the local line, got: {wideList}");
    }

    var widePayload = "#ELFDIAG#kind=buckets t=2147483647 "
        + "fb=[" + new string('x', 130) + "] cores=999/999";
    if (widePayload.Length > 195)
    {
        throw new InvalidOperationException(
            $"Wide bucket diagnostics exceed the chat budget at {widePayload.Length} characters.");
    }
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
    // Include more producers than the original capacity: helper-thread turnover
    // must not wrap a new producer onto a still-live worker's slot.
    var producerCount = configuredShardCount * 2 + 1;
    var assignedIndexes = new int[producerCount];
    var assignmentReal = new List<int>();
    var assignmentShard = AccessTools.Method(shardingType, "Shard")!.MakeGenericMethod(typeof(int));
    var assignmentDrain = AccessTools.Method(shardingType, "DrainInto")!.MakeGenericMethod(typeof(int));
    var producerLists = new List<int>[producerCount];
    using (var startAssignments = new ManualResetEventSlim(false))
    {
        var assignmentThreads = Enumerable.Range(0, producerCount)
            .Select(i => new Thread(() =>
            {
                startAssignments.Wait();
                assignedIndexes[i] = (int)currentShardIndex.Invoke(null, null)!;
                var mine = (List<int>)assignmentShard.Invoke(null, [assignmentReal])!;
                producerLists[i] = mine;
                lock (mine) mine.Add(i);
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
    if (assignedIndexes.Distinct().Count() != producerCount)
    {
        throw new InvalidOperationException(
            "Stable sink-job producers collided despite having enough shard slots: "
            + string.Join(", ", assignedIndexes));
    }
    if (producerLists.Distinct(ReferenceEqualityComparer.Instance).Count() != producerCount)
        throw new InvalidOperationException("Extra sink-job producers still share a shard list.");
    assignmentDrain.Invoke(null, [assignmentReal]);
    assignmentReal.Sort();
    if (!assignmentReal.SequenceEqual(Enumerable.Range(0, producerCount)))
        throw new InvalidOperationException("Shard growth lost or duplicated registered producer entries.");

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

    // The other side of the same pool. For ends in a local function whose whole
    // body is "while (task.PendingBatches > 0) spinWait.SpinOnce(-1)", and on the
    // 2026-09-20 host trace that was 12.33 s - 6.9% of all process CPU - with
    // 9.34 s of it in SpinOnceCore and 7.07 s of GC rendezvous underneath. It now
    // parks past a budget and is released by the completion of the task's last
    // batch. This does IL surgery on a method whose whole body is that loop, so
    // prove the rewrite compiles and that the handshake actually fires.
    {
        var waitPark = typeof(EntryPoint).Assembly
            .GetType("EmmanimLagFix.Code.FastParallelWaitParkPatch", throwOnError: true)!;

        var waitTarget = fastParallelType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(method => method.Name.Contains("WaitUntilFinished"))
            ?? throw new MissingMethodException(
                fastParallelType.FullName, "For._WaitUntilFinished (local function)");

        if (Harmony.GetPatchInfo(waitTarget)?.Transpilers.Any(patch => patch.owner == smokeId) != true)
        {
            throw new InvalidOperationException(
                "The bounded-wait transpiler was not installed on For._WaitUntilFinished.");
        }
        if (AccessTools.Field(waitPark, "Applied").GetValue(null) is not true)
        {
            throw new InvalidOperationException(
                "For._WaitUntilFinished's SpinOnce(-1) was not rewritten, so every dispatcher "
                + "would keep spinning: "
                + (AccessTools.Field(waitPark, "FailureReason").GetValue(null) as string
                   ?? "no reason recorded")
                + ".");
        }

        // The replacement threads a second argument onto an existing ldloca, so a
        // stack mistake here is invalid IL on the hottest join in the game.
        RuntimeHelpers.PrepareMethod(waitTarget.MethodHandle);

        var runParallelBatch = AccessTools.DeclaredMethod(fastParallelType, "RunParallelBatch")
            ?? throw new MissingMethodException(fastParallelType.FullName, "RunParallelBatch");
        if (Harmony.GetPatchInfo(runParallelBatch)?.Postfixes.Any(patch => patch.owner == smokeId) != true)
        {
            throw new InvalidOperationException(
                "The completion postfix was not installed on FastParallel.RunParallelBatch, so a "
                + "parked dispatcher would only ever be released by the backstop timeout.");
        }

        var waitBudget = (int)AccessTools.Field(waitPark, "SpinBudget").GetValue(null)!;
        var waitParkMs = (int)AccessTools.Field(waitPark, "ParkMilliseconds").GetValue(null)!;
        if (waitBudget <= 0)
        {
            throw new InvalidOperationException(
                "The dispatcher spin budget is not positive, so a bucket join would park instead "
                + "of spinning through the sub-millisecond wait it is normally in.");
        }
        if (waitParkMs <= 11)
        {
            throw new InvalidOperationException(
                $"A {waitParkMs} ms backstop is at or below this process's ~11 ms timer "
                + "granularity, which is what put 2.1.3's workers on a wake/probe/re-park "
                + "treadmill.");
        }

        var waitCounters = (string)AccessTools.DeclaredMethod(waitPark, "Counters")!
            .Invoke(null, null)!;
        if (!waitCounters.EndsWith("@" + waitBudget, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Dispatcher wait diagnostics '{waitCounters}' do not report spin budget {waitBudget}.");
        }

        // A synthetic task stands in for a dispatch: PendingBatches is the only
        // field either side of the handshake reads.
        var parallelTaskType = fastParallelType.GetNestedType("ParallelTask", BindingFlags.NonPublic)
            ?? throw new TypeLoadException(fastParallelType.FullName + ".ParallelTask");
        var pendingBatches = AccessTools.Field(parallelTaskType, "PendingBatches")
            ?? throw new MissingFieldException(parallelTaskType.FullName, "PendingBatches");
        var syntheticTask = RuntimeHelpers.GetUninitializedObject(parallelTaskType);
        pendingBatches.SetValue(syntheticTask, 1);

        var waitSpin = AccessTools.DeclaredMethod(waitPark, "WaitSpin")
            ?? throw new MissingMethodException(waitPark.FullName, "WaitSpin");
        var complete = AccessTools.DeclaredMethod(waitPark, "Complete")
            ?? throw new MissingMethodException(waitPark.FullName, "Complete");
        var waitParkCount = AccessTools.Field(waitPark, "ParkCount");
        var waitWakeCount = AccessTools.Field(waitPark, "WakeCount");
        var waitTimeoutCount = AccessTools.Field(waitPark, "TimeoutCount");
        long WaitParks() => (long)waitParkCount.GetValue(null)!;
        long WaitWakes() => (long)waitWakeCount.GetValue(null)!;
        long WaitTimeouts() => (long)waitTimeoutCount.GetValue(null)!;

        void WaitOnce(ref SpinWait spin)
        {
            object[] args = { spin, syntheticTask };
            waitSpin.Invoke(null, args);
            spin = (SpinWait)args[0];
        }

        // Below the budget the dispatcher must spin exactly as vanilla does, or
        // every one of roughly twenty joins a frame pays a kernel round trip.
        var waitParksBefore = WaitParks();
        var coldWait = new SpinWait();
        WaitOnce(ref coldWait);
        if (coldWait.Count != 1 || WaitParks() != waitParksBefore)
        {
            throw new InvalidOperationException(
                "A dispatcher below the spin budget parked instead of spinning.");
        }

        // Past it, it parks - and the completion of the last batch must release it
        // rather than the backstop. Counting timeouts separately is what makes
        // this a handshake test and not merely a liveness test.
        var waitTimeoutsBefore = WaitTimeouts();
        var waitWakesBefore = WaitWakes();
        Exception? waiterFailure = null;
        var reachedBudget = new ManualResetEventSlim(false);
        var waiterThread = new Thread(() =>
        {
            try
            {
                var spin = new SpinWait();
                while (spin.Count < waitBudget)
                {
                    spin.SpinOnce(-1);
                }
                reachedBudget.Set();
                // Vanilla's own loop is the authority, so mirror it here.
                while ((int)pendingBatches.GetValue(syntheticTask)! > 0)
                {
                    WaitOnce(ref spin);
                }
            }
            catch (Exception ex)
            {
                waiterFailure = ex;
            }
        })
        { IsBackground = true, Name = "FastParallel wait park smoke" };
        waiterThread.Start();

        if (!reachedBudget.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException(
                "The dispatcher smoke thread never reached its spin budget.");
        }

        // Let it actually enter the park before completing the task, so the wake
        // path is what ends the wait.
        var waitParkDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < waitParkDeadline && waiterFailure == null && WaitParks() == waitParksBefore)
        {
            Thread.Sleep(1);
        }
        if (WaitParks() == waitParksBefore)
        {
            throw new InvalidOperationException(
                "A dispatcher past the spin budget never parked, so the spin this patch exists "
                + "to stop would still run.");
        }

        pendingBatches.SetValue(syntheticTask, 0);
        complete.Invoke(null, new[] { syntheticTask });
        var joinedWaiter = waiterThread.Join(TimeSpan.FromSeconds(5));

        if (waiterFailure != null)
        {
            throw new InvalidOperationException(
                "The parked-dispatcher smoke thread threw, so the bounded wait is not safe to run "
                + "on a real dispatch.", waiterFailure);
        }
        if (!joinedWaiter)
        {
            throw new InvalidOperationException(
                "A parked dispatcher did not return after its task completed, so the bounded wait "
                + "can hang a frame.");
        }
        if (WaitWakes() == waitWakesBefore)
        {
            throw new InvalidOperationException(
                "The completion postfix never observed the waiting dispatcher, so every join "
                + "would wait out the backstop timeout instead.");
        }
        if (WaitTimeouts() != waitTimeoutsBefore)
        {
            throw new InvalidOperationException(
                "A dispatcher park ended on the backstop rather than on the task's completion, "
                + "so the Dekker handshake is not exact.");
        }

        reachedBudget.Dispose();
    }

    // Nested dispatch. Ten fixed-update buckets already hand every worker a batch
    // of ships, and the bucket members dispatch again - ResourceManager.FixedUpdate
    // alone twice per ship per world tick. A small nested range now runs inline, so
    // prove the prefix landed on the seven-argument For and that the boundary is
    // exactly ThreadCount * 8, since an over-wide limit would serialize a
    // megaship's genuinely large sink-job range.
    var forTarget = AccessTools.DeclaredMethod(
        fastParallelType,
        "For",
        new[]
        {
            typeof(int), typeof(int),
            halflingAssembly.GetType("Halfling.Performance.FastParallelAction", throwOnError: true)!,
            typeof(object), typeof(bool), typeof(int?), typeof(string),
        })
        ?? throw new MissingMethodException(fastParallelType.FullName, "For(int, int, FastParallelAction, object, bool, int?, string)");
    if (forTarget.IsGenericMethod || forTarget.DeclaringType!.IsGenericType)
    {
        throw new InvalidOperationException(
            "FastParallel.For became generic; a shared canonical body cannot be patched per "
            + "instantiation, the defect that made the 2.1.0 proxy sentinel inert.");
    }

    var nestedPatchType = typeof(EntryPoint).Assembly
        .GetType("EmmanimLagFix.Code.FastParallelNestedDispatchPatch", throwOnError: true)!;
    var smallRangeLimit = (int)AccessTools.Field(nestedPatchType, "SmallRangeLimit").GetValue(null)!;
    var expectedLimit = (int)AccessTools.PropertyGetter(fastParallelType, "ThreadCount")
        .Invoke(null, null)! * 8;
    if (smallRangeLimit != expectedLimit || smallRangeLimit <= 0)
    {
        throw new InvalidOperationException(
            $"Expected a nested-inline limit of {expectedLimit}, got {smallRangeLimit}: "
            + (AccessTools.Field(nestedPatchType, "FailureReason").GetValue(null) as string
               ?? "no reason recorded")
            + ".");
    }
    if (Harmony.GetPatchInfo(forTarget)?.Prefixes.Count(patch => patch.owner == smokeId) != 1)
    {
        throw new InvalidOperationException(
            "Expected exactly one Emmanim nested-dispatch prefix on FastParallel.For.");
    }

    var isInlinableRange = AccessTools.DeclaredMethod(nestedPatchType, "IsInlinableRange")
        ?? throw new MissingMethodException(nestedPatchType.FullName, "IsInlinableRange");
    bool Inlinable(int from, int to, bool copyStackData, int? batchSize) =>
        (bool)isInlinableRange.Invoke(null, new object?[] { from, to, copyStackData, batchSize })!;
    foreach (var (from, to, copy, batch, expected, why) in new (int, int, bool, int?, bool, string)[]
    {
        (0, smallRangeLimit, false, null, true, "a range exactly at the limit is inlinable"),
        (0, smallRangeLimit + 1, false, null, false, "a range past the limit must keep its dispatch"),
        (0, 4, false, 1, false, "an explicit batch size is the caller's partitioning"),
        (0, 4, true, null, false, "copyStackData captures the dispatcher's stack for other threads"),
        (0, 0, false, null, false, "vanilla owns the empty-range early-out"),
        (7, 7 + smallRangeLimit, false, null, true, "the limit is a length, not an upper bound"),
    })
    {
        if (Inlinable(from, to, copy, batch) != expected)
        {
            throw new InvalidOperationException(
                $"Nested-inline decision for [{from}, {to}) copyStackData={copy} batchSize={batch} "
                + $"should be {expected}: {why}.");
        }
    }

    // Overflow guard: the length test must not wrap for a caller passing extremes.
    if (Inlinable(int.MinValue, int.MaxValue, false, null))
    {
        throw new InvalidOperationException(
            "A full-int32 range was reported inlinable, so the length test overflowed.");
    }

    // The idle gate. A range longer than one is inlined only while no worker is
    // parked, so the whole repair rests on one property on another patch class:
    // renamed or made private, ShouldRunInline stops compiling rather than
    // silently reverting, but a *type* rename would leave a stale reflection
    // path elsewhere, so assert the member exists and reads as an int here too.
    var idleGateType = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.FastParallelIdleParkPatch", throwOnError: true)!;
    var parkedWorkers = AccessTools.PropertyGetter(idleGateType, "ParkedWorkers")
        ?? throw new MissingMemberException(idleGateType.FullName, "ParkedWorkers");
    if (parkedWorkers.Invoke(null, null) is not int parked || parked != 0)
    {
        throw new InvalidOperationException(
            "FastParallelIdleParkPatch.ParkedWorkers did not read as zero outside a running "
            + "pool, so the nested-dispatch idle gate cannot be trusted to mean what it says.");
    }

    // The prefix now also refines the batch size of a top-level dispatch, and it
    // must take batchSize by ref or the write is invisible to the original method.
    var forPrefix = AccessTools.DeclaredMethod(nestedPatchType, "Prefix")
        ?? throw new MissingMethodException(nestedPatchType.FullName, "Prefix");
    var batchSizeParam = forPrefix.GetParameters()
        .SingleOrDefault(parameter => parameter.Name == "batchSize")
        ?? throw new InvalidOperationException(
            "The FastParallel.For prefix no longer names a batchSize parameter, so Harmony "
            + "cannot bind it and the batch-size refinement is silently inert.");
    if (!batchSizeParam.ParameterType.IsByRef)
    {
        throw new InvalidOperationException(
            "The prefix's batchSize parameter is by value; a Harmony prefix only changes an "
            + "argument the original method sees when it takes it by ref.");
    }

    // Batch sizing. FastParallel claims batches rather than pre-assigning them
    // (RunParallelTask: Interlocked.Increment(ref task.CurBatch)), so a pass
    // finishes when its most expensive batch does. Vanilla's eight-batches-per-
    // worker default bundles a megaship with nine fighters; the refinement only
    // ever lowers the batch size, and never touches a caller's explicit one.
    var batchType = typeof(EntryPoint).Assembly
        .GetType("EmmanimLagFix.Code.FastParallelBatchSize", throwOnError: true)!;
    var batchesPerParticipant =
        (int)AccessTools.Field(batchType, "BatchesPerParticipant").GetValue(null)!;
    if (batchesPerParticipant <= 8)
    {
        throw new InvalidOperationException(
            $"BatchesPerParticipant is {batchesPerParticipant}, at or below vanilla's implied 8: "
            + "the refinement can then only ever coarsen or do nothing, which is not its point. "
            + (AccessTools.PropertyGetter(batchType, "FailureReason").Invoke(null, null) as string
               ?? "no reason recorded")
            + ".");
    }

    var refine = AccessTools.DeclaredMethod(batchType, "Refine")
        ?? throw new MissingMethodException(batchType.FullName, "Refine");
    int? Refine(int from, int to, bool copy, int? batch, int threads, int perParticipant) =>
        (int?)refine.Invoke(
            null, new object?[] { from, to, copy, batch, threads, perParticipant });

    // Vanilla's own formula, restated so the expectations below are not circular.
    static int VanillaBatch(int count, int threads) => Math.Max(count / (threads * 8), 1);

    foreach (var (count, threads, per, expected, why) in new (int, int, int, int?, string)[]
    {
        // 607 ships on the 2026-09-14 client at 2.2.6's seven workers: vanilla
        // batches 10, eight participants x 32 batches asks for 2.
        (607, 7, 32, 2, "the measured session's shape must actually be refined"),
        // Vanilla is already at 1 below ThreadCount * 16, and 1 is the floor.
        (16, 7, 32, null, "a range vanilla already batches at 1 has nothing finer to ask for"),
        // A coarser target than vanilla's must be ignored, not installed.
        (607, 7, 2, null, "the refinement must only ever lower the batch size"),
        (607, 7, 0, null, "a zero target disables the refinement"),
        (607, 0, 32, null, "a pool with no workers runs the range inline in vanilla"),
    })
    {
        var got = Refine(0, count, false, null, threads, per);
        if (got != expected)
        {
            throw new InvalidOperationException(
                $"Refine({count} items, {threads} workers, {per}/participant) should be "
                + $"{(expected?.ToString() ?? "vanilla")}, got {(got?.ToString() ?? "vanilla")}: {why}.");
        }
        if (got.HasValue && got.Value >= VanillaBatch(count, threads))
        {
            throw new InvalidOperationException(
                $"Refine({count}, {threads}) returned {got} which does not beat vanilla's "
                + $"{VanillaBatch(count, threads)}.");
        }
    }

    if (Refine(0, 607, false, 4, 7, 32) != null || Refine(0, 607, true, null, 7, 32) != null
        || Refine(0, 0, false, null, 7, 32) != null)
    {
        throw new InvalidOperationException(
            "An explicit batchSize, copyStackData, or an empty range must all be left to vanilla, "
            + "exactly as the inline path leaves them.");
    }

    // Overflow guard, same shape as the inline one: a full-int32 range must not wrap.
    if (Refine(int.MinValue, int.MaxValue, false, null, 7, 32) is { } wrapped && wrapped <= 0)
    {
        throw new InvalidOperationException(
            $"A full-int32 range produced a batch size of {wrapped}, so the length test overflowed.");
    }

    // Pool size. Vanilla sizes the pool at physical cores minus one, which leaves
    // half the hardware threads of an SMT machine unreachable; that was the right
    // call while idle workers spun forever, and stopped being right in 2.1.3 when
    // they started parking. The resize is not a Harmony patch, because the spin
    // budget and the inline limit above are derived from the worker count in
    // static constructors whose order is undefined - so assert that it has already
    // settled by the time anything read the count, and that both derived values
    // agree with the pool actually installed.
    var poolType = typeof(EntryPoint).Assembly
        .GetType("EmmanimLagFix.Code.FastParallelPoolSize", throwOnError: true)!;
    var settled = AccessTools.PropertyGetter(poolType, "Settled")
        ?? throw new MissingMemberException(poolType.FullName, "Settled");
    if (settled.Invoke(null, null) is not true)
    {
        throw new InvalidOperationException(
            "The FastParallel pool size had not settled even though the spin budget and "
            + "inline limit were already derived from a worker count.");
    }

    var defaultWorkerCount = AccessTools.DeclaredMethod(poolType, "DefaultWorkerCount")
        ?? throw new MissingMethodException(poolType.FullName, "DefaultWorkerCount");
    foreach (var (processors, expected) in new[] { (1, 0), (2, 1), (8, 7), (16, 15) })
    {
        var actual = (int)defaultWorkerCount.Invoke(null, new object[] { processors })!;
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Expected {processors} logical processors to request {expected} workers, got {actual}.");
        }
    }

    var requested = (int)AccessTools.Field(poolType, "RequestedWorkerCount").GetValue(null)!;
    var installed = (int)AccessTools.PropertyGetter(fastParallelType, "ThreadCount").Invoke(null, null)!;
    if (requested > 0 && installed != requested)
    {
        throw new InvalidOperationException(
            $"Requested a {requested}-worker pool but FastParallel reports {installed}: "
            + (AccessTools.Field(poolType, "FailureReason").GetValue(null) as string
               ?? "no reason recorded")
            + ".");
    }
    var parkWorkerCount = (int)AccessTools.Field(parkPatchType, "WorkerCount").GetValue(null)!;
    if (parkWorkerCount != installed)
    {
        throw new InvalidOperationException(
            $"The park patch derived its budget from {parkWorkerCount} workers but the pool runs "
            + $"{installed}; the resize landed after the budget was computed.");
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

// The sim phase is in turn split into the deterministic world tick and the
// per-frame visual pass, then attributed per scene bucket. This is what makes
// sim= readable: a 2026-09-11 client spent 243.7 of a 281 ms frame inside
// SimRoot.Update and the subsystem behind it was not recorded.
{
    var breakdownSceneRoot = HarmonyLib.AccessTools.TypeByName("Halfling.Scene2D.SceneRoot")
        ?? throw new InvalidOperationException(
            "Halfling.Scene2D.SceneRoot was not found, so fixed-update timing cannot be installed.");

    if (HarmonyLib.AccessTools.DeclaredMethod(breakdownSceneRoot, "DoFixedUpdates", Type.EmptyTypes) == null)
    {
        throw new InvalidOperationException(
            "SceneRoot.DoFixedUpdates() was not found, so fixed= would report dashes.");
    }

    var breakdownSimRoot = HarmonyLib.AccessTools.TypeByName("Cosmoteer.Simulation.SimRoot")
        ?? throw new InvalidOperationException(
            "Cosmoteer.Simulation.SimRoot was not found, so bucket timing cannot be installed.");

    // Declared, not inherited: SimRoot overrides both, and patching the base
    // would miss the virtual dispatch entirely while still resolving.
    foreach (var bucketMethod in new[] { "FixedUpdateForBucket", "UpdateForBucket" })
    {
        if (HarmonyLib.AccessTools.DeclaredMethod(breakdownSimRoot, bucketMethod, new[] { typeof(int) }) == null)
        {
            throw new InvalidOperationException(
                $"SimRoot.{bucketMethod}(int) was not declared, so bucket attribution would be lost "
                + "or would silently measure another scene.");
        }
    }

    // The bucket numbers are meaningless without names, and vanilla's own
    // GetBucketName walks every field per call, so the patch builds its own map.
    foreach (var bucketConstants in new[] { "Cosmoteer.FixedUpdateBuckets", "Cosmoteer.UpdateBuckets" })
    {
        var constantsType = HarmonyLib.AccessTools.TypeByName(bucketConstants)
            ?? throw new InvalidOperationException(
                $"{bucketConstants} was not found, so buckets would be reported as bare numbers.");

        var named = constantsType
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Count(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(int));

        if (named == 0)
        {
            throw new InvalidOperationException(
                $"{bucketConstants} exposed no public const int buckets, so the name map would be empty.");
        }
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

    // The postfix narrows to multiplayer with an `is BaseMPManager` test, so
    // singleplayer keeps vanilla pacing. If that type stopped deriving from
    // NetManager the test would never match and the patch would go inert.
    var mpManagerType = HarmonyLib.AccessTools.TypeByName(
            "Cosmoteer.Game.Multiplayer.BaseMPManager")
        ?? throw new InvalidOperationException(
            "Cosmoteer.Game.Multiplayer.BaseMPManager was not found, so the catch-up patch "
            + "could not tell multiplayer from singleplayer.");

    if (!netManagerType.IsAssignableFrom(mpManagerType))
    {
        throw new InvalidOperationException(
            "BaseMPManager no longer derives from NetManager, so the catch-up patch's "
            + "multiplayer test can never match.");
    }

    // The credit is only useful because AdvanceNetworkTime consumes an
    // accumulator in a loop rather than one tick per call, and because
    // IsReadyForTick still gates each tick. Both are load-bearing for the
    // "can never run ahead of received inputs" claim in the patch's comment.
    if (HarmonyLib.AccessTools.DeclaredMethod(mpManagerType, "AdvanceNetworkTime") is null
        || HarmonyLib.AccessTools.DeclaredMethod(mpManagerType, "IsReadyForTick") is null)
    {
        throw new InvalidOperationException(
            "BaseMPManager.AdvanceNetworkTime/IsReadyForTick were not found, so extra "
            + "per-frame credit is no longer known to be gated by received inputs.");
    }

    // The patch is on by default now. A Prepare() that returned false with no
    // override file present would silently restore the old opt-in behaviour.
    var catchUpType = typeof(EmmanimLagFix.Code.EntryPoint).Assembly
            .GetType("EmmanimLagFix.Code.NetworkTimeCatchUpPatch")
        ?? throw new InvalidOperationException("NetworkTimeCatchUpPatch was not found.");

    var prepare = HarmonyLib.AccessTools.DeclaredMethod(catchUpType, "Prepare")
        ?? throw new InvalidOperationException("NetworkTimeCatchUpPatch.Prepare was not found.");

    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "ticks-per-frame.txt"))
        && prepare.Invoke(null, null) is not true)
    {
        throw new InvalidOperationException(
            "NetworkTimeCatchUpPatch.Prepare() returned false with no ticks-per-frame.txt "
            + "override present, so the catch-up patch would not be applied at all.");
    }

    // 2.2.0's ceiling of 3 pinned a slow peer at 4 world ticks per frame and
    // collapsed it to 2 fps while *lowering* its tick rate. The safe default is
    // one tick - vanilla's own cap - so only the quadratic penalty is removed.
    // A future edit that raises this constant again must be deliberate.
    var ceiling = catchUpType
            .GetField("MaxTicksPerFrame",
                System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "NetworkTimeCatchUpPatch.MaxTicksPerFrame was not found.");

    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "ticks-per-frame.txt"))
        && ceiling.GetValue(null) is not 1)
    {
        throw new InvalidOperationException(
            "NetworkTimeCatchUpPatch defaults to more than one input tick per frame. "
            + "That is the 2.2.0 behaviour measured to collapse a slow peer to 2 fps; "
            + $"found {ceiling.GetValue(null)}.");
    }
}

// Status value modulation builds a temporary list of changed statuses. The
// capacity patch must resolve both concrete handler bodies and rewrite exactly
// one allocation in each; merely seeing a registered transpiler is insufficient
// because a guarded transpiler can still fall back without changing the IL.
{
    var capacityPatchType = typeof(EmmanimLagFix.Code.EntryPoint).Assembly.GetType(
            "EmmanimLagFix.Code.StatusModulationListCapacityPatch", throwOnError: true)!
        ?? throw new InvalidOperationException(
            "StatusModulationListCapacityPatch was not found.");
    var findTarget = HarmonyLib.AccessTools.DeclaredMethod(capacityPatchType, "FindTarget")
        ?? throw new InvalidOperationException(
            "StatusModulationListCapacityPatch.FindTarget was not found.");
    var partType = gameAssembly.GetType("Cosmoteer.Ships.Parts.Part", throwOnError: true)!;
    var tileTarget = (System.Reflection.MethodInfo?)findTarget.Invoke(
            null, new object[] { typeof(Halfling.Geometry.IntVector2) })
        ?? throw new InvalidOperationException("The tile modulation target did not resolve.");
    var partTarget = (System.Reflection.MethodInfo?)findTarget.Invoke(
            null, new object[] { partType })
        ?? throw new InvalidOperationException("The part modulation target did not resolve.");

    var capacityProbe = new HarmonyLib.Harmony(smokeId + ".statuscapacity");
    capacityProbe.PatchAll(typeof(EmmanimLagFix.Code.EntryPoint).Assembly);
    try
    {
        foreach (var (target, label) in new[] { (tileTarget, "IntVector2"), (partTarget, "Part") })
        {
            var mine = HarmonyLib.Harmony.GetPatchInfo(target)?.Transpilers
                .Count(patch => patch.owner == smokeId + ".statuscapacity"
                    && patch.PatchMethod.DeclaringType == capacityPatchType) ?? 0;
            if (mine != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one capacity transpiler on StatusHandler<{label}> "
                    + $"modulation, found {mine}.");
            }

            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(target.MethodHandle);
            var contextPatchType = typeof(EntryPoint).Assembly.GetType(
                "EmmanimLagFix.Code.HeatModulationContextSkipPatch", true)!;
            var contextCount = Harmony.GetPatchInfo(target)?.Transpilers.Count(patch =>
                patch.owner == smokeId + ".statuscapacity" && patch.PatchMethod.DeclaringType == contextPatchType) ?? 0;
            if (contextCount != (label == "IntVector2" ? 1 : 0))
                throw new InvalidOperationException("Heat context rewrite covered an unexpected handler.");
        }

        foreach (var flagName in new[] { "AppliedToTiles", "AppliedToParts" })
        {
            var getter = HarmonyLib.AccessTools.DeclaredPropertyGetter(capacityPatchType, flagName)
                ?? throw new InvalidOperationException(
                    $"StatusModulationListCapacityPatch.{flagName} was not found.");
            if (getter.Invoke(null, null) is not true)
            {
                throw new InvalidOperationException(
                    $"StatusModulationListCapacityPatch.{flagName} is false, so the "
                    + "guarded transpiler did not rewrite its target.");
            }
        }

        // Exercise the helper without constructing any game state: it reads
        // only StatusCount and allocates the same pooled TempList as vanilla.
        // This catches a wrong generic element type or an accidentally inert
        // EnsureCapacity call independently of the IL shape assertions above.
        var handlerDefinition = gameAssembly.GetType(
                "Cosmoteer.Ships.Statuses.StatusHandler`1", throwOnError: true)!;
        var tileHandlerType = gameAssembly.GetType(
                "Cosmoteer.Ships.Statuses.TileStatusHandler", throwOnError: true)!;
        var tileHandlerBase = handlerDefinition.MakeGenericType(
            typeof(Halfling.Geometry.IntVector2));
        var statusCountField = HarmonyLib.AccessTools.Field(
                tileHandlerBase, "<StatusCount>k__BackingField")
            ?? throw new InvalidOperationException(
                "StatusHandler<T>.StatusCount backing field was not found.");
        var uninitializedHandler = System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(tileHandlerType);
        statusCountField.SetValue(uninitializedHandler, 37);

        var allocator = HarmonyLib.AccessTools.DeclaredMethod(
                capacityPatchType, "AllocWithCapacity")!
            .MakeGenericMethod(typeof(Halfling.Geometry.IntVector2));
        var allocatedList = allocator.Invoke(null, new[] { uninitializedHandler })
            ?? throw new InvalidOperationException(
                "StatusModulationListCapacityPatch allocator returned null.");
        try
        {
            var capacity = (int)(allocatedList.GetType().GetProperty("Capacity")!
                .GetValue(allocatedList) ?? -1);
            if (capacity < 37)
            {
                throw new InvalidOperationException(
                    $"Status modulation list capacity is {capacity}, expected at least 37.");
            }
        }
        finally
        {
            ((IDisposable)allocatedList).Dispose();
        }
    }
    finally
    {
        capacityProbe.UnpatchAll(smokeId + ".statuscapacity");
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

// Network resource transfer patches: the positional ResourceDistributor port
// is exercised end-to-end against the vanilla algorithm - every mode, both
// delta signs, random capacities - comparing remainder, per-container amounts,
// and write order (vanilla's dictionary insertion order vs the scratch's
// first-touch order, which is what the push/pull loops emit).
{
    var transferPatch = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.NetworkResourceTransferPatch", true)!;
    var cachePatch = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.SubnetworkCacheOperationPatch", true)!;
    var transferTargets = ((IEnumerable<MethodBase>)AccessTools
        .Method(transferPatch, "TargetMethods")!.Invoke(null, null)!).ToArray();
    var cacheTargets = ((IEnumerable<MethodBase>)AccessTools
        .Method(cachePatch, "TargetMethods")!.Invoke(null, null)!).ToArray();
    if (transferTargets.Length != 4 || cacheTargets.Length != 4)
    {
        throw new InvalidOperationException(
            "Network resource transfer/cache patches resolved "
            + $"{transferTargets.Length}+{cacheTargets.Length} targets, expected 4+4.");
    }

    var distType = gameAssembly.GetType(
        "Cosmoteer.Ships.Parts.Resources.ResourceDistributor", true)!;
    var modeType = gameAssembly.GetType(
        "Cosmoteer.Ships.Parts.Resources.MultiResourceStorageMode", true)!;
    var cvType = distType.GetNestedType("ContainerValue",
        BindingFlags.Public | BindingFlags.NonPublic)!;
    var vanillaDistribute = distType
        .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(m => m.Name == "Distribute" && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 6)
        .MakeGenericMethod(typeof(int));

    var transferScratchType = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.TransferScratch", true)!;
    var distPort = typeof(EntryPoint).Assembly.GetType(
        "EmmanimLagFix.Code.SinkDistribution", true)!;
    var ourDistribute = AccessTools.Method(distPort, "Distribute")!;
    var orderField = transferScratchType.GetField("Order")!;
    var amountsField = transferScratchType.GetField("Amounts")!;
    var indicesField = transferScratchType.GetField("Indices")!;
    var beginMethod = transferScratchType.GetMethod("Begin")!;

    var funcType = typeof(Func<,,>).MakeGenericType(typeof(int), cvType, typeof(int));
    var rng = new Random(0x5EED);
    int cases = 0;
    for (int mode = 0; mode < 10; mode++)
    {
        var modeObj = Enum.ToObject(modeType, mode);
        for (int trial = 0; trial < 400; trial++)
        {
            int n = rng.Next(0, 8);
            var values = new int[n * 3];
            for (int i = 0; i < n; i++)
            {
                // Zero values are deliberate: they exercise the eligible-list
                // filtering and zero-quantity touches that still record a
                // dictionary entry in vanilla.
                values[i * 3 + 0] = rng.Next(3) == 0 ? 0 : rng.Next(1, 60);
                values[i * 3 + 1] = rng.Next(3) == 0 ? 0 : rng.Next(60);
                values[i * 3 + 2] = rng.Next(3) == 0 ? 0 : rng.Next(60);
            }
            int delta = rng.Next(-150, 151);
            Func<int, int, int> gv = (i, k) => values[i * 3 + k];

            var pi = Expression.Parameter(typeof(int));
            var pcv = Expression.Parameter(cvType);
            var gvDel = Expression.Lambda(funcType,
                Expression.Invoke(Expression.Constant(gv), pi,
                    Expression.Convert(pcv, typeof(int))), pi, pcv).Compile();

            var containers = new List<int>(n);
            for (int i = 0; i < n; i++) containers.Add(i);
            var dict = new Dictionary<int, int>();
            var randA = new SmokeRand((ulong)(trial * 7919 + mode));
            var randB = new SmokeRand((ulong)(trial * 7919 + mode));
            int vanillaLeft = (int)vanillaDistribute.Invoke(null,
                new object?[] { containers, delta, modeObj, dict, gvDel, randA })!;

            var scratch = Activator.CreateInstance(transferScratchType, nonPublic: true)!;
            beginMethod.Invoke(scratch, new object?[] { n });
            var indices = (List<int>)indicesField.GetValue(scratch)!;
            int ourLeft = (int)ourDistribute.Invoke(null,
                new object?[] { indices, delta, modeObj, gv, randB, scratch })!;

            if (ourLeft != vanillaLeft)
            {
                throw new InvalidOperationException(
                    $"Distribution port mismatch on remainder: mode={mode} delta={delta} "
                    + $"n={n} vanilla={vanillaLeft} ported={ourLeft}.");
            }
            var order = (List<int>)orderField.GetValue(scratch)!;
            var amounts = (int[])amountsField.GetValue(scratch)!;
            if (order.Count != dict.Count)
            {
                throw new InvalidOperationException(
                    $"Distribution port mismatch on touched count: mode={mode} "
                    + $"delta={delta} n={n} vanilla={dict.Count} ported={order.Count}.");
            }
            int j = 0;
            foreach (var kv in dict)
            {
                if (kv.Key != order[j] || kv.Value != amounts[order[j]])
                {
                    throw new InvalidOperationException(
                        $"Distribution port mismatch at position {j}: mode={mode} "
                        + $"delta={delta} n={n} vanilla=({kv.Key},{kv.Value}) "
                        + $"ported=({order[j]},{amounts[order[j]]}).");
                }
                j++;
            }
            cases++;
        }
    }
    if (cases != 4000)
        throw new InvalidOperationException("Distribution equivalence ran " + cases + " cases.");
}

Console.WriteLine("PASS: resource traversal/desired-priority snapshot/path-contiguity hashing and visited-set search, generation-stamped resource source visited set, lock-free resource counts, transfer, trade, technology-purchase, pickup-overlay, blueprint network/stat refresh, redundant AtlasQuad write suppression, build-stats, sparse heat diffusion, visual smoothed-value throttle, opt-in resource/single-player memory diagnostics, role-priority, multiplayer initialization/session-timeout/buffer/InputTick forwarding, lazy paint-toolbox pickers/groups, toggle-mode delegate cache, allocation-free resource-ID comparison, hoisted thruster-cache guard, allocation-free shader-constant updates, plain-text layout, subscription-stable part colour updates, status-regulator affected-cell cache, pre-sized status-modulation change lists, streaming-sound start guard, sharded non-deterministic callback queue, throttled codex show-conditions, pooled status-dictionary enumeration, peer diagnostics relay, client-side desync bucket reporting, sharded resource sink-job collection, throttled minimap membership scanning, parked FastParallel idle workers, bounded FastParallel dispatcher waits, inlined small nested FastParallel dispatches, finer top-level batch sizing, a logical-processor-sized worker pool, urgent input-tick-delay HostUpdates, roof-decal target skipped for stages whose shaders never sample it, frame-phase timing, per-bucket sim breakdown, real-time multiplayer tick catch-up, and main-thread lost-ship saving patches resolved and compiled on this game build.");

internal static class DeserializationDetourFixture
{
    internal static object DeserializeAsNull() =>
        throw new Halfling.Serialization.DeserializeAsNullException();

    internal static object Fail() =>
        throw new InvalidOperationException("detour exception fixture");
}

/// <summary>
/// Deterministic <see cref="Halfling.Random.Rand"/> for the distribution
/// equivalence check - two instances on one seed must produce identical draw
/// streams so a ported shuffle can be compared against vanilla's.
/// </summary>
internal sealed class SmokeRand : Halfling.Random.Rand
{
    private ulong _state;

    public SmokeRand(ulong seed)
    {
        _state = seed | 1;
    }

    public override ulong UInt64()
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return _state;
    }
}

internal static class ThreadedTaskQueueFixture
{
    internal static bool Called;
    private static readonly object Result = new();

    internal static int Return42(Halfling.Performance.ThreadedTaskQueue _) => 42;

    internal static object ReturnObject(Halfling.Performance.ThreadedTaskQueue _) =>
        Result;

    internal static void MarkCalled(Halfling.Performance.ThreadedTaskQueue _) =>
        Called = true;

    internal static int Fail(Halfling.Performance.ThreadedTaskQueue _) =>
        throw new InvalidOperationException("queue callback fixture");
}
