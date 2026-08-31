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
    private const int PortRefreshIntervalTicks = 300;
    private const int StatRefreshIntervalTicks = 30;
    private static readonly ConditionalWeakTable<object, Gate> Gates = new();
    private static readonly Type CallbackType = AccessTools.TypeByName(
        "Cosmoteer.Ships.Parts.PartsManager+UpdateCallbacks")
        ?? throw new TypeLoadException("PartsManager.UpdateCallbacks was not found.");

    [ThreadStatic]
    private static bool? s_allowPortRefresh;

    [ThreadStatic]
    private static bool? s_allowStatRefresh;

    private sealed class Gate
    {
        public int NextPortTick;
        public int NextStatTick;
    }

    private static MethodBase TargetMethod() =>
        AccessTools.Method(CallbackType, "Update")
        ?? throw new MissingMethodException(CallbackType.FullName, "Update");

    private static void Prefix(
        object __instance,
        SceneRoot root,
        out (bool? Port, bool? Stat) __state)
    {
        __state = (s_allowPortRefresh, s_allowStatRefresh);
        if (root is not SimRoot sim || sim.IsPaused.Confirmed)
        {
            s_allowPortRefresh = true;
            s_allowStatRefresh = true;
            return;
        }

        var gate = Gates.GetOrCreateValue(__instance);
        var tick = sim.Tick;
        if (tick >= gate.NextPortTick || tick < gate.NextPortTick - PortRefreshIntervalTicks)
        {
            gate.NextPortTick = tick + PortRefreshIntervalTicks;
            s_allowPortRefresh = true;
        }
        else
        {
            s_allowPortRefresh = false;
        }

        if (tick >= gate.NextStatTick || tick < gate.NextStatTick - StatRefreshIntervalTicks)
        {
            gate.NextStatTick = tick + StatRefreshIntervalTicks;
            s_allowStatRefresh = true;
        }
        else
        {
            s_allowStatRefresh = false;
        }
    }

    private static void Postfix((bool? Port, bool? Stat) __state)
    {
        RestoreState(__state);
    }

    private static Exception? Finalizer(
        Exception? __exception,
        (bool? Port, bool? Stat) __state)
    {
        RestoreState(__state);
        return __exception;
    }

    private static void RestoreState((bool? Port, bool? Stat) state)
    {
        s_allowPortRefresh = state.Port;
        s_allowStatRefresh = state.Stat;
    }

    internal static bool AllowPortRefresh => s_allowPortRefresh ?? true;

    internal static bool AllowStatRefresh => s_allowStatRefresh ?? true;
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

/// <summary>
/// Blueprint stat providers only drive display/planning state, but vanilla
/// reevaluates every provider on every unpaused scene update. Use the same
/// per-ship callback gate as the network-port patch, at the more responsive
/// cadence of once per game second. Paused blueprint editing retains vanilla
/// per-frame feedback.
/// </summary>
[HarmonyPatch(typeof(Cosmoteer.Ships.Blueprints.Logic.Values.BlueprintPartStatProvider), "UpdateOperational")]
internal static class BlueprintPartStatProviderRefreshPatch
{
    private static bool Prefix() => BlueprintNetworkPortRefreshBatchPatch.AllowStatRefresh;
}
