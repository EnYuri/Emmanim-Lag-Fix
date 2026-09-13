using System.Globalization;
using System.Reflection;
using Halfling.Logging;
using Halfling.Performance;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Runs a <b>small nested</b> <c>FastParallel.For</c> on the calling thread
/// instead of dispatching it to the worker pool.
///
/// Ten of Cosmoteer's fifty-seven fixed-update buckets are parallel:
/// <c>SimRoot</c>'s constructor registers <c>ParallelFixedUpdate</c> for
/// <c>ConvertResourcesEarly</c>, <c>ConvertResources</c>, <c>Resources</c>,
/// <c>Jobs</c>, <c>CrewPreUpdate</c>, <c>ShipCrew</c>,
/// <c>SetThrusterActivations</c>, <c>FireWeaponsAsyncStep</c>, <c>Statuses</c>
/// and <c>AimWeapons</c>. Each of those dispatches its bucket members - one
/// entry per ship - across the pool. The members then dispatch again:
/// <c>ResourceManager.FixedUpdate</c> alone calls <c>For</c> twice, once for
/// <c>SearchForSources</c> and once for <c>UpdateSinkJobs</c>.
///
/// Vanilla only avoids the dispatch when the range collapses to a single batch:
/// <code>
/// int num2 = batchSize ?? Mathx.Max(num / (ThreadCount * 8), 1);
/// int num3 = (num + num2 - 1) / num2;
/// if (num3 == 1) { body(fromInclusive, toExclusive, data); return; }
/// ...
/// AddToLive(task);          // wakes every parked worker
/// RunParallelBatch(...); RunParallelTask(...);
/// _WaitUntilFinished();     // SpinOnce(-1), unbounded
/// </code>
/// The automatic batch size is 1 for any range shorter than
/// <c>ThreadCount * 16</c>, so any ship with two or more sinks takes the full
/// path. A 2026-09-14 two-player session measured 607 ships at 11.5 world ticks
/// per second - about 14,000 dispatches per second from that one manager -
/// against a park counter of 27.0 million wakes over 84 minutes (~5,400/s).
///
/// Those inner dispatches buy no parallelism. The outer bucket dispatch has
/// already handed every worker a batch of ships, so an inner one only adds
/// <c>AddToLive</c> contention, a kernel wake per parked worker, and a nested
/// unbounded spin in <c>_WaitUntilFinished</c> on a thread that is itself
/// running an outer batch. The same 12-second sample measured 1.48 of 16 cores
/// busy with the main thread at 71% of one core, so the pool is not short of
/// work to claim - it is short of a thread that can claim it.
///
/// Only <b>small</b> ranges are inlined. A megaship whose <c>UpdateSinkJobs</c>
/// really does have thousands of sinks still dispatches and still spreads, which
/// is the one nested case where the pool genuinely helps.
///
/// This cannot change behaviour beyond what vanilla already permits. The body is
/// invoked over the identical half-open range, batch partitioning and thread
/// assignment are already arbitrary in vanilla, and <c>For</c> guarantees only
/// that the range has completed when it returns - which a synchronous call
/// guarantees too. Every argument-validation path, the empty-range early-out, an
/// explicit <c>batchSize</c>, <c>copyStackData</c> and the profiler's
/// <c>ProfilerTask</c> bookkeeping are all left to vanilla, so the inline path is
/// reached only where it is indistinguishable from vanilla's own
/// <c>num3 == 1</c> branch. That branch is also why raw exception propagation is
/// correct here rather than <c>AggregateException</c>: vanilla's inline path
/// propagates raw as well.
///
/// The same prefix also carries the top-level batch-size refinement described in
/// <see cref="FastParallelBatchSize"/>. The two share one prefix on purpose: a
/// prefix returning false suppresses every prefix after it, so two separate
/// prefixes would make the refinement depend on Harmony's undeclared ordering.
/// </summary>
[HarmonyPatch]
internal static class FastParallelNestedDispatchPatch
{
    /// <summary>Nested ranges inlined, and calls handed to vanilla.</summary>
    internal static long InlinedCount;

    internal static long VanillaCount;

    /// <summary>
    /// Longest nested range to run inline. Zero disables the patch entirely.
    ///
    /// <c>ThreadCount * 8</c> is vanilla's own scale factor: it is a length at
    /// which the automatic batch size is still 1, i.e. the regime where a
    /// dispatch buys one item per batch. Above it a batch is a real unit of work
    /// and the pool is worth paying for.
    /// </summary>
    internal static readonly int SmallRangeLimit;

    /// <summary>Why the patch is inert, for the smoke test's failure message.</summary>
    internal static string? FailureReason;

    static FastParallelNestedDispatchPatch()
    {
        // Reading the property runs FastParallel's own static constructor, which
        // only reads system info; Start() has not been called yet either way.
        int workers;
        try
        {
            FastParallelPoolSize.Apply();
            workers = FastParallel.ThreadCount;
        }
        catch (Exception)
        {
            workers = Environment.ProcessorCount - 1;
        }

        var limit = workers > 0 ? workers * 8 : 0;
        if (limit <= 0)
        {
            FailureReason = "FastParallel reported no worker threads";
        }

        // Optional override, one line holding the limit, beside the mod folder.
        // Written for calibration runs; absent in a normal install.
        try
        {
            var path = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
                "..",
                "fastparallel-inline.txt"));
            if (File.Exists(path)
                && int.TryParse(
                    File.ReadAllText(path).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var configured))
            {
                limit = configured;
                if (limit <= 0)
                {
                    FailureReason = "the fastparallel-inline.txt override disables inlining";
                }
            }
        }
        catch (Exception)
        {
            // A malformed or unreadable override is not a reason to change
            // threading behaviour; fall through to the default.
        }

        SmallRangeLimit = limit;
    }

    private static bool Prepare()
    {
        if (SmallRangeLimit > 0)
        {
            return true;
        }

        try
        {
            Logger.Log(
                "[EmmanimLagFix] Nested FastParallel dispatches were left in place: "
                + (FailureReason ?? "no reason recorded") + ".");
        }
        catch (Exception)
        {
            // The logger is not initialized in the standalone smoke host.
        }

        return false;
    }

    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(
            typeof(FastParallel),
            nameof(FastParallel.For),
            new[]
            {
                typeof(int), typeof(int), typeof(FastParallelAction), typeof(object),
                typeof(bool), typeof(int?), typeof(string),
            })
        ?? throw new MissingMethodException(typeof(FastParallel).FullName, nameof(FastParallel.For));

    /// <summary>
    /// One prefix for both levers, so Harmony never has to order two of them: a
    /// prefix returning false suppresses every prefix after it, which would make
    /// the batch-size refinement depend on undeclared patch order.
    ///
    /// <paramref name="batchSize"/> is taken by <c>ref</c> because a Harmony
    /// prefix's argument writes are visible to the original method.
    /// </summary>
    private static bool Prefix(
        int fromInclusive,
        int toExclusive,
        FastParallelAction body,
        object? data,
        bool copyStackData,
        ref int? batchSize)
    {
        if (ShouldRunInline(fromInclusive, toExclusive, body, copyStackData, batchSize))
        {
            Interlocked.Increment(ref InlinedCount);
            body(fromInclusive, toExclusive, data);
            return false;
        }

        Interlocked.Increment(ref VanillaCount);

        // A nested dispatch is left on vanilla's sizing: the outer dispatch has
        // already spread this work across the pool, so refining a nested range
        // only adds claim-counter traffic. A zero live count is what identifies
        // the outer dispatch, exactly as the inline decision uses it.
        if (IsTopLevelDispatch())
        {
            var refined = FastParallelBatchSize.RefineLive(
                fromInclusive, toExclusive, copyStackData, batchSize);
            if (refined.HasValue)
            {
                batchSize = refined;
            }
        }

        return true;
    }

    /// <summary>
    /// True when no task still has unclaimed batches, i.e. this call is not
    /// running inside another one's body.
    ///
    /// Note the one imprecision, which is harmless: <c>RunParallelTask</c> calls
    /// <c>RemoveFromLive</c> as soon as the <i>last</i> batch is claimed, while
    /// that batch's body is still running. A nested dispatch from that final
    /// batch therefore reads as top-level and gets the finer sizing. It is a
    /// sizing choice either way, never a correctness one.
    /// </summary>
    private static bool IsTopLevelDispatch()
    {
        try
        {
            return FastParallel.IsRunning && FastParallel.s_liveTasks.Count == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ShouldRunInline(
        int fromInclusive,
        int toExclusive,
        FastParallelAction? body,
        bool copyStackData,
        int? batchSize)
    {
        if (!IsInlinableRange(fromInclusive, toExclusive, copyStackData, batchSize) || body == null)
        {
            return false;
        }

        // IsRunning is set inside Start() after the live-task pool exists, so it
        // has to be read before the pool. EnableProfiling would otherwise lose a
        // ProfilerTask entry, and a zero live count means this is the outer
        // dispatch - the one that must actually reach the pool.
        return FastParallel.IsRunning
            && !FastParallel.EnableProfiling
            && FastParallel.s_liveTasks.Count > 0;
    }

    /// <summary>
    /// The part of the decision that depends on the arguments alone, so the smoke
    /// test can check the boundary without a running worker pool.
    /// </summary>
    internal static bool IsInlinableRange(
        int fromInclusive,
        int toExclusive,
        bool copyStackData,
        int? batchSize)
    {
        // An explicit batch size is a caller's deliberate partitioning, and
        // copyStackData asks for the dispatcher's stack data to be captured for
        // other threads; neither is ours to reinterpret. Vanilla owns the empty
        // range and every argument-validation throw.
        if (batchSize.HasValue || copyStackData || toExclusive <= fromInclusive)
        {
            return false;
        }

        return (long)toExclusive - fromInclusive <= SmallRangeLimit;
    }

    internal static string Counters() =>
        SmallRangeLimit <= 0
        ? "off"
        : Volatile.Read(ref InlinedCount).ToString(CultureInfo.InvariantCulture)
        + "/" + Volatile.Read(ref VanillaCount).ToString(CultureInfo.InvariantCulture)
        + "@" + SmallRangeLimit.ToString(CultureInfo.InvariantCulture);
}
