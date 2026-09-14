using System.Diagnostics;
using System.Globalization;
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
///
/// The gate is inert below its own refresh rate, which is the state a large
/// fleet is measured in: at 17-21 rendered frames per second, one refresh per
/// frame is already at or under 20 Hz, so every frame passes and the walk is
/// paid in full. Update bucket 7 was 4.6-6.7 ms per frame on a 2026-09-14
/// single-player save, the largest item on the update side of the sim. It is
/// also serial - <c>SimRoot</c> marks only buckets 8, 10, 12 and 15 as
/// <c>ParallelUpdate</c>. The opt-in counters below say whether that time is the
/// number of values or the cost of reading one, which decides whether the answer
/// is to parallelise the bucket or to stop re-reading a provider chain.
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

    /// <summary>
    /// Rides the existing memory-diagnostics flags, like every other probe here,
    /// so a normal install pays neither the timestamps nor the counters.
    /// </summary>
    private static readonly bool Measure = FramePhaseDiagnosticsPatch.Enabled;

    private static long _ticks;
    private static long _refreshes;
    private static long _skips;
    private static long _values;

    private sealed class State
    {
        public Time AccumulatedGameTime;
    }

    private static bool Prefix(PartSmoothedValue.SmoothedValueManager __instance, SceneRoot root)
    {
        var started = Measure ? Stopwatch.GetTimestamp() : 0L;
        var state = States.GetOrCreateValue(__instance);
        var deltaTime = float.IsNaN(root.Clock.DeltaTime) ? (Time)0f : root.Clock.DeltaTime;
        state.AccumulatedGameTime += deltaTime;

        var accumulated = state.AccumulatedGameTime;
        if ((float)accumulated < RefreshInterval)
        {
            if (Measure)
            {
                Interlocked.Increment(ref _skips);
                Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - started);
            }

            return false;
        }

        state.AccumulatedGameTime = 0f;

        foreach (var value in __instance._nonDeterministicValues)
        {
            value.UpdateValue(accumulated);
        }

        if (Measure)
        {
            Interlocked.Increment(ref _refreshes);
            Interlocked.Add(ref _values, __instance._nonDeterministicValues.Count);
            Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - started);
        }

        return false;
    }

    /// <summary>
    /// Aggregate milliseconds per rendered frame, the refresh/skip split of the
    /// throttle, and the mean values walked per refresh. A skip share near zero
    /// means the throttle is inert at this frame rate and is not the lever.
    /// </summary>
    internal static string Snapshot()
    {
        var ticks = Interlocked.Exchange(ref _ticks, 0);
        var refreshes = Interlocked.Exchange(ref _refreshes, 0);
        var skips = Interlocked.Exchange(ref _skips, 0);
        var values = Interlocked.Exchange(ref _values, 0);

        var frames = FramePhaseDiagnosticsPatch.LastFrames;
        if (!Measure || frames <= 0 || refreshes + skips == 0)
        {
            return "smv=-";
        }

        var ms = ticks * 1000d / Stopwatch.Frequency / frames;
        return "smv=" + ms.ToString("F2", CultureInfo.InvariantCulture)
            + "/" + refreshes.ToString(CultureInfo.InvariantCulture)
            + "+" + skips.ToString(CultureInfo.InvariantCulture)
            + "x" + (refreshes > 0
                ? ((double)values / refreshes).ToString("F1", CultureInfo.InvariantCulture)
                : "0");
    }
}
