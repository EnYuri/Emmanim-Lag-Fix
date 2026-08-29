using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Parts.Logic;
using Halfling.Scene2D;
using Halfling.Timing;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// PartSmoothedValue already separates deterministic simulation values from
/// non-deterministic presentation values. Vanilla still walks every visual
/// value on every rendered frame, which scales poorly on factory, radiator and
/// large modded thruster assemblies.
///
/// Keep deterministic FixedUpdate completely vanilla. Visual values are
/// refreshed at 20 Hz wall time and receive the full accumulated game-time
/// delta, preserving their movement rate while reducing list walks and
/// ValueChanged event fan-out. At most one manager state is retained per ship.
/// </summary>
[HarmonyPatch(typeof(PartSmoothedValue.SmoothedValueManager), "Update")]
internal static class PartSmoothedValueVisualThrottlePatch
{
    private const int RefreshesPerSecond = 20;
    private static readonly long RefreshIntervalTicks = Math.Max(1, Stopwatch.Frequency / RefreshesPerSecond);
    private static readonly ConditionalWeakTable<PartSmoothedValue.SmoothedValueManager, State> States = new();

    private sealed class State
    {
        public long NextRefreshTimestamp;
        public Time AccumulatedGameTime;
    }

    private static bool Prefix(PartSmoothedValue.SmoothedValueManager __instance, SceneRoot root)
    {
        var state = States.GetOrCreateValue(__instance);
        var deltaTime = float.IsNaN(root.Clock.DeltaTime) ? (Time)0f : root.Clock.DeltaTime;
        state.AccumulatedGameTime += deltaTime;

        var now = Stopwatch.GetTimestamp();
        if (now < state.NextRefreshTimestamp)
        {
            return false;
        }

        state.NextRefreshTimestamp = now + RefreshIntervalTicks;
        var accumulated = state.AccumulatedGameTime;
        state.AccumulatedGameTime = 0f;

        foreach (var value in __instance._nonDeterministicValues)
        {
            value.UpdateValue(accumulated);
        }

        return false;
    }
}
