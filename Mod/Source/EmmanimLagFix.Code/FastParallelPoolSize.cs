using System.Globalization;
using Halfling.Logging;
using Halfling.Performance;

namespace EmmanimLagFix.Code;

/// <summary>
/// Sizes the FastParallel pool by logical processors instead of physical cores.
///
/// Halfling's static constructor is explicit about the choice:
/// <code>
/// // The default is the number of physical cores (not logical/hyper-threading
/// // cores) minus one.
/// if (systemInfo != null &amp;&amp; systemInfo.PhysicalCores &gt; 0)
///     ThreadCount = systemInfo.PhysicalCores - 1;
/// else
///     ThreadCount = Environment.ProcessorCount - 1;
/// </code>
///
/// On a 4-core/8-thread machine that is three workers plus the caller - <b>four
/// of eight hardware threads</b>, with the other half structurally unreachable.
/// The 2026-09-14 session's client was exactly that shape: its relayed spin
/// budget of <c>@20</c> only occurs below eight workers, and it used 1.5 of 8
/// logical processors while holding the lockstep gate on 92% of host frames.
///
/// <b>Why the vanilla default was right and no longer is.</b> An SMT sibling
/// shares its physical core's execution ports, so a worker that spins steals
/// throughput from whatever runs on its partner - and until 2.1.3 every idle
/// Halfling worker spun forever on <c>SpinOnce(-1)</c>, which one 20-second trace
/// measured at 39.0 s of 65.9 s of process CPU. Under that behaviour filling the
/// SMT siblings would have been actively harmful, and physical-minus-one was the
/// correct conservative choice. Since 2.1.3 an idle worker parks on a kernel
/// event past a bounded spin, so an unused sibling now buys nothing and a parked
/// one costs nothing. The premise changed; the default did not. This is the same
/// re-test the mod's notes already call for on Server GC, which was rejected on
/// that same pre-2.1.3 spinning footing.
///
/// <b>Why this cannot desync.</b> Worker count already differs between peers -
/// the 2026-09-14 host ran eleven workers against that client's three - and both
/// peers' whole-game and simulation integrity hashes matched for the entire
/// session with no mismatch reported. Simulation results therefore cannot depend
/// on how many threads ran the batches, which is what makes resizing the pool a
/// scheduling change rather than a state change. Anything genuinely order-
/// sensitive already goes through <c>SimRoot.EnqueueDeterministic</c>.
///
/// <b>Pair this with the nested-dispatch inlining.</b> More workers means more
/// kernel wakes per dispatch, and <see cref="FastParallelNestedDispatchPatch"/>
/// is what removes the dispatches that were pure overhead. Raising the count
/// without it moves cost around instead of removing it. SMT siblings add perhaps
/// twenty to thirty percent each rather than a whole core, so this is not a
/// doubling even where it doubles the thread count.
///
/// <b>Not a Harmony patch.</b> <see cref="FastParallelIdleParkPatch"/> derives
/// its spin budget from the worker count and
/// <see cref="FastParallelNestedDispatchPatch"/> derives its inline limit from
/// it, both in static constructors whose relative order is not defined. So the
/// resize is an idempotent method that every reader calls first, rather than a
/// prefix on <c>FastParallel.Start</c> that would land after those reads and
/// leave the two deriving from a count that no longer holds.
/// </summary>
internal static class FastParallelPoolSize
{
    /// <summary>Pool size to install, or zero to leave vanilla's alone.</summary>
    internal static readonly int RequestedWorkerCount;

    /// <summary>What the pool was sized at before this ran, for the log line.</summary>
    internal static int VanillaWorkerCount { get; private set; }

    /// <summary>True once the decision has been made, whatever it was.</summary>
    internal static bool Settled { get; private set; }

    /// <summary>True only when the count was actually changed.</summary>
    internal static bool Resized { get; private set; }

    internal static string? FailureReason { get; private set; }

    private static readonly object Gate = new();

    static FastParallelPoolSize()
    {
        var requested = DefaultWorkerCount(Environment.ProcessorCount);

        // Optional override, one line holding the worker count, beside the mod
        // folder. Written for calibration runs; absent in a normal install.
        // "0" restores vanilla's physical-core sizing exactly.
        try
        {
            var path = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
                "..",
                "fastparallel-threads.txt"));
            if (File.Exists(path)
                && int.TryParse(
                    File.ReadAllText(path).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var configured))
            {
                requested = configured;
                if (requested <= 0)
                {
                    FailureReason = "the fastparallel-threads.txt override restores vanilla sizing";
                }
            }
        }
        catch (Exception)
        {
            // A malformed or unreadable override is not a reason to change
            // threading behaviour; fall through to the default.
        }

        RequestedWorkerCount = requested;
    }

    /// <summary>
    /// One worker per logical processor beyond the calling thread, which runs
    /// batches itself. Exposed for the smoke test.
    /// </summary>
    internal static int DefaultWorkerCount(int processorCount) =>
        processorCount > 1 ? processorCount - 1 : 0;

    /// <summary>
    /// Installs the pool size, once. Safe to call from anywhere and at any time:
    /// after the threads exist the count can no longer change, and this reports
    /// that rather than throwing.
    /// </summary>
    internal static void Apply()
    {
        if (Settled)
        {
            return;
        }

        lock (Gate)
        {
            if (Settled)
            {
                return;
            }

            try
            {
                // Reading the property runs FastParallel's own static
                // constructor, which only reads system info.
                VanillaWorkerCount = FastParallel.ThreadCount;

                if (RequestedWorkerCount <= 0)
                {
                    Log("[EmmanimLagFix] FastParallel pool left at its vanilla size of "
                        + VanillaWorkerCount.ToString(CultureInfo.InvariantCulture) + " workers: "
                        + (FailureReason ?? "no reason recorded") + ".");
                    return;
                }

                if (RequestedWorkerCount == VanillaWorkerCount)
                {
                    return;
                }

                // The setter throws once the threads exist, so say why instead.
                if (FastParallel.s_createdThreads)
                {
                    FailureReason = "the worker threads had already been created";
                    Log("[EmmanimLagFix] FastParallel pool kept its vanilla size of "
                        + VanillaWorkerCount.ToString(CultureInfo.InvariantCulture)
                        + " workers: " + FailureReason + ".");
                    return;
                }

                FastParallel.ThreadCount = RequestedWorkerCount;
                Resized = true;
                Log("[EmmanimLagFix] FastParallel pool resized from "
                    + VanillaWorkerCount.ToString(CultureInfo.InvariantCulture) + " to "
                    + RequestedWorkerCount.ToString(CultureInfo.InvariantCulture) + " workers on "
                    + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)
                    + " logical processors.");
            }
            catch (Exception e)
            {
                // A refused resize must not stop the pool from starting.
                // Vanilla's count is still in place and the game runs as before.
                FailureReason = e.GetType().Name + ": " + e.Message;
                Log("[EmmanimLagFix] FastParallel pool kept its vanilla size: "
                    + FailureReason + ".");
            }
            finally
            {
                Settled = true;
            }
        }
    }

    private static void Log(string message)
    {
        try
        {
            Logger.Log(message);
        }
        catch (Exception)
        {
            // The logger is not initialized in the standalone smoke host.
        }
    }
}
