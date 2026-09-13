using System.Globalization;
using Halfling.Performance;

namespace EmmanimLagFix.Code;

/// <summary>
/// Chooses a finer batch size for a <b>top-level</b> <c>FastParallel.For</c> than
/// vanilla's automatic one, so that a bucket containing one very expensive member
/// no longer sets the whole pass's finish time.
///
/// <b>The pool claims batches, it does not pre-assign them.</b>
/// <c>RunParallelTask</c> is
/// <code>
/// while ((num = Interlocked.Increment(ref task.CurBatch) - 1) &lt; task.BatchCount)
///     RunParallelBatch(task, num, profilerJobs);
/// </code>
/// so every participant takes the next unclaimed batch and the pass finishes when
/// the last batch finishes. That makes the tail latency of a pass the cost of its
/// <b>most expensive single batch</b>, not the average - which is the whole reason
/// batch granularity matters here rather than being a wash.
///
/// <b>Vanilla's granularity is eight batches per worker.</b>
/// <code>
/// int num2 = ((ThreadCount &lt;= 0) ? num : (batchSize ?? Mathx.Max(num / (ThreadCount * 8), 1)));
/// int num3 = (num + num2 - 1) / num2;
/// </code>
/// That is a sound default for uniform work. A fixed-update bucket is the opposite
/// of uniform: <c>SimRoot.ParallelFixedUpdate</c> dispatches one entry per ship,
/// and a 10,000-part megaship's <c>Jobs</c> or <c>Statuses</c> work is orders of
/// magnitude above a twenty-part fighter's. At 607 ships and seven workers vanilla
/// batches 10 ships together, so the megaship arrives bundled with nine other
/// ships and the seven remaining participants idle through the difference.
///
/// <b>What this costs.</b> One extra <c>Interlocked.Increment</c> on a single
/// shared counter per additional batch. Going from eight to
/// <see cref="BatchesPerParticipant"/> batches per participant at 607 ships is
/// about 240 extra increments per bucket pass; across the ten parallel buckets at
/// 11.5 world ticks per second that is roughly 28,000 per second, or single-digit
/// milliseconds of one core. The tail it removes is measured in milliseconds
/// <i>per tick</i>.
///
/// <b>Deliberately conservative in four ways.</b> An explicit caller-supplied
/// <c>batchSize</c> is never overridden - that is a caller's own partitioning
/// decision. A range vanilla already batches at 1 is left alone, since 1 is the
/// finest granularity that exists. A nested dispatch is left alone, because the
/// outer dispatch has already spread the work and a nested one only adds
/// contention (<see cref="FastParallelNestedDispatchPatch"/> removes the small
/// ones outright). And the refinement only ever <i>lowers</i> the batch size, so
/// it can never coarsen a pass that vanilla had already balanced.
///
/// <b>It cannot change results.</b> Batch partitioning and thread assignment are
/// already arbitrary in vanilla - the two peers of the 2026-09-14 session ran
/// eleven workers against three and their integrity hashes matched for the whole
/// session - and the body is invoked over the identical half-open range either
/// way. Anything order-sensitive already goes through
/// <c>SimRoot.EnqueueDeterministic</c>.
/// </summary>
internal static class FastParallelBatchSize
{
    /// <summary>
    /// Batches to aim for per participant. Vanilla's implied figure is 8 against
    /// <c>ThreadCount</c> (which excludes the calling thread, itself a full
    /// participant). Four times finer bounds the tail at about a quarter of a
    /// vanilla batch while multiplying the claim counter's traffic by four.
    /// Zero disables the refinement entirely.
    /// </summary>
    internal static readonly int BatchesPerParticipant;

    /// <summary>Ranges refined, for the diagnostics line.</summary>
    internal static long RefinedCount;

    internal static long UnchangedCount;

    internal static string? FailureReason;

    private const int DefaultBatchesPerParticipant = 32;

    static FastParallelBatchSize()
    {
        var batches = DefaultBatchesPerParticipant;

        // Optional override, one line holding the batches-per-participant target,
        // beside the mod folder. Written for calibration runs; absent in a normal
        // install. "0" restores vanilla's batch sizing exactly.
        try
        {
            var path = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
                "..",
                "fastparallel-batches.txt"));
            if (File.Exists(path)
                && int.TryParse(
                    File.ReadAllText(path).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var configured))
            {
                batches = configured;
                if (batches <= 0)
                {
                    FailureReason = "the fastparallel-batches.txt override restores vanilla batch sizing";
                }
            }
        }
        catch (Exception)
        {
            // A malformed or unreadable override is not a reason to change
            // threading behaviour; fall through to the default.
        }

        BatchesPerParticipant = batches;
    }

    /// <summary>
    /// Returns the batch size to install for a top-level dispatch, or null to
    /// leave vanilla's automatic sizing in place.
    ///
    /// Pure and parameterised on the worker count so the smoke test can walk the
    /// boundary without a running pool.
    /// </summary>
    internal static int? Refine(
        int fromInclusive,
        int toExclusive,
        bool copyStackData,
        int? batchSize,
        int threadCount,
        int batchesPerParticipant)
    {
        // An explicit batch size is the caller's own partitioning, copyStackData
        // asks for the dispatcher's stack data to be captured, and vanilla owns
        // the empty range and every argument-validation throw.
        if (batchSize.HasValue || copyStackData || toExclusive <= fromInclusive)
        {
            return null;
        }

        if (batchesPerParticipant <= 0 || threadCount <= 0)
        {
            return null;
        }

        var count = (long)toExclusive - fromInclusive;

        // Vanilla's own sizing. When it is already 1 there is nothing finer to
        // ask for, and the range is short enough that the nested-dispatch
        // inlining is the relevant lever instead.
        var vanilla = Math.Max(count / ((long)threadCount * 8), 1);
        if (vanilla <= 1)
        {
            return null;
        }

        // The calling thread runs batches too, so it is a participant.
        var participants = (long)threadCount + 1;
        var target = Math.Max(count / (participants * batchesPerParticipant), 1);

        // Only ever refine downward, and only when it actually changes something.
        return target < vanilla ? (int)target : null;
    }

    /// <summary>
    /// The live decision, reading the pool's current worker count. Counts both
    /// outcomes so a session can be checked for the patch doing anything at all.
    /// </summary>
    internal static int? RefineLive(
        int fromInclusive,
        int toExclusive,
        bool copyStackData,
        int? batchSize)
    {
        int threadCount;
        try
        {
            threadCount = FastParallel.ThreadCount;
        }
        catch (Exception)
        {
            return null;
        }

        var refined = Refine(
            fromInclusive, toExclusive, copyStackData, batchSize,
            threadCount, BatchesPerParticipant);

        if (refined.HasValue)
        {
            Interlocked.Increment(ref RefinedCount);
        }
        else
        {
            Interlocked.Increment(ref UnchangedCount);
        }

        return refined;
    }

    internal static string Counters() =>
        BatchesPerParticipant <= 0
        ? "off"
        : Volatile.Read(ref RefinedCount).ToString(CultureInfo.InvariantCulture)
        + "/" + Volatile.Read(ref UnchangedCount).ToString(CultureInfo.InvariantCulture)
        + "@" + BatchesPerParticipant.ToString(CultureInfo.InvariantCulture);
}
