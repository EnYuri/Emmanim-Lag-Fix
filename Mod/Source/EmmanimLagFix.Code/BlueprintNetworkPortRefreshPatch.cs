using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Simulation;
using Halfling.Scene2D;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Blueprint network ports remain attached to every live and preloaded ship so
/// repair/build comparisons are immediately available even outside blueprint
/// mode. Vanilla reevaluates their rules-based operational toggles every scene
/// update. On very large saves that means tens of thousands of identical
/// metadata lookups per rendered frame.
///
/// Give each ship's update-callback container one deterministic ten-game-second
/// gate. Paused games retain vanilla per-frame updates so blueprint editing and
/// toggle feedback remain immediate. The gate is per callback container (about
/// one per ship), never per blueprint component, avoiding the handle growth of
/// the rejected component-level cache prototype.
/// </summary>
[HarmonyPatch]
internal static class BlueprintNetworkPortRefreshBatchPatch
{
    private const int RefreshIntervalTicks = 300;
    private static readonly ConditionalWeakTable<object, Gate> Gates = new();
    private static readonly Type CallbackType = AccessTools.TypeByName(
        "Cosmoteer.Ships.Parts.PartsManager+UpdateCallbacks")
        ?? throw new TypeLoadException("PartsManager.UpdateCallbacks was not found.");

    [ThreadStatic]
    private static bool? s_allowPortRefresh;

    private sealed class Gate
    {
        public int NextTick;
    }

    private static MethodBase TargetMethod() =>
        AccessTools.Method(CallbackType, "Update")
        ?? throw new MissingMethodException(CallbackType.FullName, "Update");

    private static void Prefix(object __instance, SceneRoot root, out bool? __state)
    {
        __state = s_allowPortRefresh;
        if (root is not SimRoot sim || sim.IsPaused.Confirmed)
        {
            s_allowPortRefresh = true;
            return;
        }

        var gate = Gates.GetOrCreateValue(__instance);
        var tick = sim.Tick;
        if (tick >= gate.NextTick || tick < gate.NextTick - RefreshIntervalTicks)
        {
            gate.NextTick = tick + RefreshIntervalTicks;
            s_allowPortRefresh = true;
        }
        else
        {
            s_allowPortRefresh = false;
        }
    }

    private static void Postfix(bool? __state)
    {
        s_allowPortRefresh = __state;
    }

    private static Exception? Finalizer(Exception? __exception, bool? __state)
    {
        s_allowPortRefresh = __state;
        return __exception;
    }

    internal static bool AllowPortRefresh => s_allowPortRefresh ?? true;
}

[HarmonyPatch]
internal static class BlueprintNetworkPortRefreshPatch
{
    private static readonly Type PortType = AccessTools.TypeByName(
        "Cosmoteer.Source.Ships.Blueprints.BaseBlueprintPartNetworkPort")
        ?? throw new TypeLoadException("BaseBlueprintPartNetworkPort was not found.");

    private static MethodBase TargetMethod() =>
        AccessTools.Method(PortType, "UpdateOperational")
        ?? throw new MissingMethodException(PortType.FullName, "UpdateOperational");

    private static bool Prefix() => BlueprintNetworkPortRefreshBatchPatch.AllowPortRefresh;
}
