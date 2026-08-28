using EmmanimLagFix.Code;
using HarmonyLib;
using System.Reflection;

var gameAssembly = Assembly.Load("Cosmoteer");

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
var resourceFixedUpdateTarget = AccessTools.DeclaredMethod(resourceManagerType, "FixedUpdate")
    ?? throw new MissingMethodException(resourceManagerType.FullName, "FixedUpdate");
var perShipCountType = gameAssembly.GetType("Cosmoteer.Ships.Resources.ResourceManager+PerShipCount", throwOnError: true)!;
var perShipGetCountTarget = AccessTools.Method(perShipCountType, "GetCount")
    ?? throw new MissingMethodException(perShipCountType.FullName, "GetCount");
var perShipAddCountTarget = AccessTools.Method(perShipCountType, "AddCount")
    ?? throw new MissingMethodException(perShipCountType.FullName, "AddCount");

const string smokeId = "nayuri.emmanim_lag_fix.smoke_test";
var harmony = new Harmony(smokeId);
harmony.PatchAll(typeof(EntryPoint).Assembly);

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

var resourceFixedUpdateInfo = Harmony.GetPatchInfo(resourceFixedUpdateTarget)
    ?? throw new InvalidOperationException("Harmony did not patch ResourceManager.FixedUpdate cache scope.");
if (!resourceFixedUpdateInfo.Prefixes.Any(patch => patch.owner == smokeId) ||
    !resourceFixedUpdateInfo.Finalizers.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected ResourceManager.FixedUpdate cache-scope patches were not installed.");
}

var perShipGetCountInfo = Harmony.GetPatchInfo(perShipGetCountTarget)
    ?? throw new InvalidOperationException("Harmony did not patch PerShipCount.GetCount.");
if (!perShipGetCountInfo.Prefixes.Any(patch => patch.owner == smokeId) ||
    !perShipGetCountInfo.Postfixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected PerShipCount.GetCount cache patches were not installed.");
}

var perShipAddCountInfo = Harmony.GetPatchInfo(perShipAddCountTarget)
    ?? throw new InvalidOperationException("Harmony did not patch PerShipCount.AddCount.");
if (!perShipAddCountInfo.Prefixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected PerShipCount.AddCount invalidation patch was not installed.");
}

harmony.UnpatchAll(smokeId);
Console.WriteLine("PASS: resource, fixed-update resource-count cache, transfer, trade, technology-purchase, pickup-overlay, blueprint-network refresh, build-stats, sparse heat diffusion, opt-in resource diagnostics, role-priority, multiplayer initialization, and toggle-mode delegate cache patches resolved on this game build.");
