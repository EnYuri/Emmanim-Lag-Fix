using System.Diagnostics;
using System.Reflection;
using Halfling.Logging;
using Halfling.Performance;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Times the unbounded spin at the end of <see cref="FastParallel.For"/>,
/// split between the thread that owns the pool and the worker pool itself.
///
/// A dispatched <c>For</c> finishes by running batch zero and claiming its own
/// remaining batches on the calling thread, then spinning in
/// <c>_WaitUntilFinished</c> until every batch claimed by a worker has
/// completed. The main thread's wait is the bucket join - it is what <c>fb</c>
/// already measures. A <b>worker</b>'s wait is different: it only happens on a
/// nested dispatch, and it means a batch of that worker's inner task was
/// claimed by a pool thread that then failed to finish it promptly.
///
/// The pool runs at <see cref="ThreadPriority.Lowest"/>, so a worker that has
/// claimed a batch loses every scheduling decision against normal-priority
/// threads - the renderer, audio, the GC's own threads, anything else on the
/// machine. While it sits descheduled, the nested caller spins, and the
/// stall is billed to whatever manager call contained the dispatch - which
/// is how a 77-part ship's status pass can record a 90 ms
/// <c>ShipStatusManager.FixedUpdate</c> in <c>lg</c>.
///
/// <c>fpw=[wTotal/maxWxN,mTotal]</c> reports, per frame over the report
/// window: total worker-side wait milliseconds, the longest single worker
/// wait and the wait count, and the main thread's total for comparison. A
/// large worker figure that tracks <c>lg</c> confirms the convoy; a small one
/// sends the search elsewhere. Timing only; gated on the same flag as the
/// rest of the phase diagnostics.
/// </summary>
[HarmonyPatch]
internal static class FastParallelWaitDiagnosticsPatch
{
    /// <summary>The name vanilla gives its pool threads.</summary>
    private const string WorkerThreadName = "FastParallel Pool";

    /// <summary>Total worker-side spin ticks in the window.</summary>
    internal static long WorkerWaitTicks;

    /// <summary>Worker-side waits in the window.</summary>
    internal static long WorkerWaitCount;

    /// <summary>Longest single worker-side spin, ticks.</summary>
    internal static long WorkerWaitMax;

    /// <summary>Total main/other-thread spin ticks in the window.</summary>
    internal static long MainWaitTicks;

    /// <summary>Whether the local-function target resolved, for the smoke test.</summary>
    internal static bool Resolved { get; private set; }

    private static bool Prepare() => FramePhaseDiagnosticsPatch.Enabled;

    private static MethodBase TargetMethod()
    {
        // _WaitUntilFinished is a local function inside For, so the compiler
        // emits it as a private static method whose name embeds the source
        // name. Matching on the substring survives the ordinal suffix moving
        // between compiler builds.
        var target = typeof(FastParallel)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(method => method.Name.Contains("WaitUntilFinished"));
        if (target == null)
        {
            throw new MissingMethodException(
                typeof(FastParallel).FullName,
                "For._WaitUntilFinished (local function)");
        }

        Resolved = true;
        return target;
    }

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(long __state)
    {
        var elapsed = Stopwatch.GetTimestamp() - __state;
        if (Thread.CurrentThread.Name == WorkerThreadName)
        {
            Interlocked.Add(ref WorkerWaitTicks, elapsed);
            Interlocked.Increment(ref WorkerWaitCount);
            var observed = Volatile.Read(ref WorkerWaitMax);
            while (elapsed > observed)
            {
                observed = Interlocked.CompareExchange(ref WorkerWaitMax, elapsed, observed);
            }
        }
        else
        {
            Interlocked.Add(ref MainWaitTicks, elapsed);
        }
    }

    /// <summary>
    /// Formats and resets the window's counters. Called next to
    /// <see cref="BucketInnerDiagnostics.Take"/> so it shares the frame
    /// count contract.
    /// </summary>
    internal static string Take()
    {
        var workerTicks = Interlocked.Exchange(ref WorkerWaitTicks, 0);
        var workerCount = Interlocked.Exchange(ref WorkerWaitCount, 0);
        var workerMax = Interlocked.Exchange(ref WorkerWaitMax, 0);
        var mainTicks = Interlocked.Exchange(ref MainWaitTicks, 0);

        var frames = FramePhaseDiagnosticsPatch.LastFrames;
        if (!FramePhaseDiagnosticsPatch.Enabled || frames <= 0)
        {
            return "fpw=[]";
        }

        var msPerTickPerFrame = 1000d / Stopwatch.Frequency / frames;
        var msPerTick = 1000d / Stopwatch.Frequency;
        return $"fpw=[{workerTicks * msPerTickPerFrame:F1}"
            + $"/{workerMax * msPerTick:F1}x{workerCount / (double)frames:F0}"
            + $",m{mainTicks * msPerTickPerFrame:F1}]";
    }
}
