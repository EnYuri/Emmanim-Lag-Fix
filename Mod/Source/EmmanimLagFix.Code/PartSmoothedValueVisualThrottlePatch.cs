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
/// refreshed once per 1/20 second of game time and receive the full accumulated
/// delta, preserving their movement rate while reducing list walks and
/// ValueChanged event fan-out. At most one manager state is retained per ship.
///
/// The gate is game time, not wall time. Versions 2.0.x through 2.1.8 compared
/// <c>Stopwatch.GetTimestamp()</c> against a per-manager deadline, which cost a
/// QueryPerformanceCounter read per manager per rendered frame - a 20-second CPU
/// trace put this prefix at 91.1 ms of self time, 1.7% of everything under
/// GameRoot.Update, on a 346-ship host.
///
/// A wall clock also disagrees with game time whenever the simulation runs
/// slower than real time, which is the ordinary state of a client that has
/// fallen behind: the gate opens while there is less than a refresh interval of
/// game time to hand over. Accumulating first and comparing the accumulator
/// removes both - no clock read at all, and a frame that advanced no game time
/// cannot pass the gate. Total delivered time is unchanged, because the
/// accumulator is drained rather than discarded, so smoothing rates are
/// identical.
/// </summary>
[HarmonyPatch(typeof(PartSmoothedValue.SmoothedValueManager), "Update")]
internal static class PartSmoothedValueVisualThrottlePatch
{
    private const int RefreshesPerSecond = 20;

    /// <summary>
    /// Seconds of game time between visual refreshes.
    /// </summary>
    private const float RefreshInterval = 1f / RefreshesPerSecond;

    private static readonly ConditionalWeakTable<PartSmoothedValue.SmoothedValueManager, State> States = new();

    private sealed class State
    {
        public Time AccumulatedGameTime;
    }

    private static bool Prefix(PartSmoothedValue.SmoothedValueManager __instance, SceneRoot root)
    {
        var state = States.GetOrCreateValue(__instance);
        var deltaTime = float.IsNaN(root.Clock.DeltaTime) ? (Time)0f : root.Clock.DeltaTime;
        state.AccumulatedGameTime += deltaTime;

        var accumulated = state.AccumulatedGameTime;
        if ((float)accumulated < RefreshInterval)
        {
            return false;
        }

        state.AccumulatedGameTime = 0f;

        foreach (var value in __instance._nonDeterministicValues)
        {
            value.UpdateValue(accumulated);
        }

        return false;
    }
}
