using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using Halfling.Logging;
using Halfling.Performance;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Bounds the spin at the end of <see cref="FastParallel.For"/> and parks past
/// the budget. This is <see cref="FastParallelIdleParkPatch"/>'s repair applied
/// to the other side of the pool: 2.1.3 stopped idle <i>workers</i> from
/// spinning forever, and the <i>dispatcher</i> was left on
/// <c>SpinWait.SpinOnce(-1)</c>.
///
/// <c>For</c> ends with a local function whose whole body is
/// <c>while (task.PendingBatches &gt; 0) spinWait.SpinOnce(-1);</c>. By the time
/// it runs, the calling thread has already executed batch zero and drained every
/// batch it could claim through <c>RunParallelTask</c>, so every batch still
/// outstanding is owned by a thread that is running it. The wait is pure
/// bookkeeping and cannot contribute work.
///
/// Measured on the 2026-09-20 two-player host trace (179.4 s of process CPU):
/// the local function was <b>12.33 s, 6.9% of all process CPU</b>, of which
/// <c>SpinWait.SpinOnceCore</c> was 9.34 s (75.8%) and - the part that matters
/// beyond the watts - <c>Thread.PollGCWorker</c> underneath the spin was 7.07 s,
/// roughly a third of every GC rendezvous in the process. The mod's own
/// <c>fpw=[0.3/30.9x18,m1.0]</c> field corroborates the magnitude from the other
/// direction: about 1.3 ms per frame, 1.0 of it on the main thread, against a
/// host <c>fixed=</c> of 2.9-5.6 ms.
///
/// <b>What this is expected to buy, and what it is not.</b> The main thread's
/// waits are not tail-dominated - roughly 20 dispatches a frame at some 50 us
/// each - and swapping a 50 us spin for a kernel round trip is not obviously a
/// win. So the budget is set to leave that common case spinning exactly as
/// vanilla does, and to park only the long tail: the same trace's worker-side
/// maximum single wait was 30.9 ms, which is the <c>ThreadPriority.Lowest</c>
/// descheduling convoy <see cref="FastParallelWaitDiagnosticsPatch"/> documents.
/// Removing the tail removes its CPU, its share of the GC rendezvous, and - on a
/// machine whose logical processors are already oversubscribed, where a spinning
/// dispatcher is stealing from the very workers it waits on - a scheduling
/// conflict. It does not shorten the wait itself; a frame still cannot finish
/// before its last batch does. <see cref="SpinBudget"/> is the calibration knob
/// and the counters below are how the next session reads which regime it is in.
///
/// The handshake is the same Dekker as the worker park, with the task identity
/// carried across it because there can be several dispatches in flight at once
/// (a nested <c>For</c> waits on a worker thread while the main thread waits on
/// the outer task). A waiter publishes the task it is waiting on, fences, then
/// tests <c>PendingBatches</c>; the completer's <c>Interlocked.Decrement</c> of
/// that field is already a full fence and its postfix then reads the waiter
/// count. Either the waiter sees the field reach zero and never parks, or the
/// completer sees the waiter and sets its event.
///
/// One thread is never waiting on two tasks at once, so one waiter per thread is
/// enough: a nested dispatch runs inside <c>RunParallelBatch</c>, which is called
/// <i>before</i> the outer wait, never during it.
///
/// This cannot deadlock and cannot lose work. Parking is entered only while some
/// other thread is executing an outstanding batch, a lost set costs one
/// <see cref="ParkMilliseconds"/> backstop, and vanilla's own loop - left in
/// place as the authority - re-tests the condition on every return, so this
/// helper is free to return early for any reason at all. Nothing here touches
/// simulation state: it changes when a thread resumes, never what is computed,
/// so it is lockstep-neutral.
/// </summary>
internal static class FastParallelWaitParkPatch
{
    /// <summary>True once the spin site was actually rewritten; the smoke test asserts it.</summary>
    internal static bool Applied;

    /// <summary>Why the rewrite was skipped, for the smoke test's message.</summary>
    internal static string? FailureReason;

    /// <summary>Parks so far.</summary>
    internal static long ParkCount;

    /// <summary>Completions that found a waiter on their own task to release.</summary>
    internal static long WakeCount;

    /// <summary>Parks that ended on the backstop timeout instead of on a set.</summary>
    internal static long TimeoutCount;

    /// <summary>
    /// One waiting dispatcher. An auto-reset event preserves a set that arrives
    /// before the wait and consumes it on the wait, and avoids the managed
    /// monitor inside <c>ManualResetEventSlim.Set</c>/<c>Reset</c> that 2.1.14
    /// measured as 66% of the monitor contention in <c>ParallelFixedUpdate</c>.
    /// </summary>
    private sealed class Waiter
    {
        public readonly AutoResetEvent Event = new(initialState: false);

        /// <summary>
        /// The task this thread is in, or about to enter, a wait on; null when it
        /// is not waiting. Written before the waiter count that publishes it.
        /// </summary>
        public volatile FastParallel.ParallelTask? Task;

        /// <summary>One when a wake is already deposited in <see cref="Event"/>.</summary>
        public int SignalPending;
    }

    [ThreadStatic]
    private static Waiter? t_waiter;

    // One entry per thread that has ever dispatched: the main thread, the pool,
    // and whatever else calls For. Grown by publishing a copy, never by mutating
    // the array a wake may be walking.
    private static Waiter?[] s_waiters = new Waiter?[32];
    private static int s_waiterCount;
    private static readonly object RegisterLock = new();

    /// <summary>Threads that have announced a wait. Read by <see cref="Complete"/>.</summary>
    private static int s_waiting;

    /// <summary>
    /// The <c>SpinWait.Count</c> at which a dispatcher stops spinning and parks.
    ///
    /// On this machine climbing from 0 to 20 costs 254 us and each further step
    /// about 12 us, so 60 is roughly 0.7 ms of hot spinning. That is deliberately
    /// well past the ~50 us a typical bucket join waits, so the common case is
    /// bit-for-bit vanilla and only the descheduling tail parks. Zero or less
    /// disables this patch entirely and restores vanilla's unbounded spin.
    /// </summary>
    internal static readonly int SpinBudget;

    /// <summary>
    /// Backstop timeout for a park. Unlike the worker park's 200 ms, a lost set
    /// here stalls a frame rather than costing parallelism, so this is short
    /// enough to be invisible at one miss and still above the ~11 ms timer
    /// granularity of a process that never calls <c>timeBeginPeriod</c> - below
    /// that the OS rounds up and the park becomes a treadmill.
    /// </summary>
    internal static readonly int ParkMilliseconds;

    private const int DefaultSpinBudget = 60;
    private const int DefaultParkMilliseconds = 20;

    static FastParallelWaitParkPatch()
    {
        var budget = DefaultSpinBudget;
        var park = DefaultParkMilliseconds;

        // Optional override, one line "<budget> [parkMs]", beside the mod folder.
        // Written for calibration runs; absent in a normal install.
        try
        {
            var path = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
                "..",
                "fastparallel-wait.txt"));
            if (File.Exists(path))
            {
                var fields = File.ReadAllText(path)
                    .Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 0
                    && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                {
                    budget = b;
                }
                if (fields.Length > 1
                    && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
                    && p > 0)
                {
                    park = p;
                }
            }
        }
        catch (Exception)
        {
            // A malformed or unreadable override is not a reason to change
            // threading behaviour; fall through to the defaults.
        }

        SpinBudget = budget;
        ParkMilliseconds = park;
    }

    /// <summary>
    /// Parks, wakes that matched a waiting task, and parks that ended on the
    /// backstop. A timeout share near 100% means the handshake is not firing and
    /// the dispatchers are back on a timer; a park count of zero means the budget
    /// is never reached and this patch is inert.
    /// </summary>
    internal static string Counters() =>
        SpinBudget <= 0
        ? "off(override)"
        : Volatile.Read(ref ParkCount).ToString(CultureInfo.InvariantCulture)
        + "/" + Volatile.Read(ref WakeCount).ToString(CultureInfo.InvariantCulture)
        + "/" + Volatile.Read(ref TimeoutCount).ToString(CultureInfo.InvariantCulture)
        + "@" + SpinBudget.ToString(CultureInfo.InvariantCulture);

    private static Waiter Register()
    {
        var waiter = new Waiter();
        lock (RegisterLock)
        {
            if (s_waiterCount == s_waiters.Length)
            {
                var grown = new Waiter?[s_waiters.Length * 2];
                Array.Copy(s_waiters, grown, s_waiterCount);
                Volatile.Write(ref s_waiters, grown);
            }

            // The slot is filled before the count that publishes it.
            s_waiters[s_waiterCount] = waiter;
            Volatile.Write(ref s_waiterCount, s_waiterCount + 1);
        }
        return waiter;
    }

    /// <summary>
    /// Replaces vanilla's <c>spinWait.SpinOnce(-1)</c> inside
    /// <c>For._WaitUntilFinished</c>. The <c>SpinWait</c> is the caller's own
    /// local, so <c>SpinWait.Count</c> is the budget's clock, and
    /// <paramref name="task"/> is the same field the loop condition reads.
    ///
    /// Returning is always safe: the caller re-tests <c>PendingBatches</c>.
    /// </summary>
    internal static void WaitSpin(ref SpinWait spin, FastParallel.ParallelTask task)
    {
        if (spin.Count < SpinBudget)
        {
            spin.SpinOnce(-1);
            return;
        }

        var waiter = t_waiter ??= Register();

        // A timeout racing a late set can leave one signal behind. Drain it
        // before announcing this wait, so the park below cannot return
        // immediately on a signal that belonged to an earlier dispatch.
        waiter.Event.WaitOne(0);
        Volatile.Write(ref waiter.SignalPending, 0);

        // Publish which task this thread waits on before the count that makes it
        // visible to Complete.
        waiter.Task = task;

        // Interlocked is a full fence, and Complete's own Interlocked.Decrement
        // of PendingBatches fences before it reads this counter, so the two
        // cannot miss each other: either the test below sees the task finished,
        // or Complete sees this waiter and sets its event.
        Interlocked.Increment(ref s_waiting);
        try
        {
            if (task.PendingBatches <= 0)
            {
                return;
            }

            ParkCount++;
            if (!waiter.Event.WaitOne(ParkMilliseconds))
            {
                TimeoutCount++;
            }
        }
        finally
        {
            waiter.Task = null;
            Interlocked.Decrement(ref s_waiting);
        }
    }

    /// <summary>
    /// Releases a dispatcher once the task it waits on has no batch outstanding.
    /// Runs after every batch on every thread, so the no-waiter path is one
    /// volatile read.
    /// </summary>
    internal static void Complete(FastParallel.ParallelTask task)
    {
        if (Volatile.Read(ref s_waiting) == 0)
        {
            return;
        }

        // Order vanilla's decrement of PendingBatches before the reads below.
        // The decrement is interlocked and therefore already a fence, but the
        // barrier documents that this read may not float above it.
        Thread.MemoryBarrier();
        if (task.PendingBatches > 0)
        {
            return;
        }

        var waiters = Volatile.Read(ref s_waiters);
        var count = Volatile.Read(ref s_waiterCount);
        for (var i = 0; i < count && i < waiters.Length; i++)
        {
            if (waiters[i] is not { } waiter || !ReferenceEquals(waiter.Task, task))
            {
                continue;
            }

            // Two threads can both observe zero when their decrements race, and
            // an auto-reset event collapses repeated sets anyway; the CAS keeps
            // the redundant kernel transition out as well.
            if (Interlocked.CompareExchange(ref waiter.SignalPending, 1, 0) == 0)
            {
                waiter.Event.Set();
                Interlocked.Increment(ref WakeCount);
            }
        }
    }

    private static void Fail(string reason)
    {
        FailureReason = reason;
        try
        {
            Logger.Log(
                "[EmmanimLagFix] FastParallel dispatchers were left spinning: " + reason + ".");
        }
        catch (Exception)
        {
            // The logger is not initialized in the standalone smoke host.
        }
    }

    /// <summary>Bounds the spin inside <c>For._WaitUntilFinished</c>.</summary>
    [HarmonyPatch]
    internal static class WaitUntilFinishedSpin
    {
        private static MethodBase TargetMethod()
        {
            // A local function, so the compiler emits a private static method
            // whose name embeds the source name. Matching on the substring
            // survives the ordinal suffix moving between compiler builds.
            var target = typeof(FastParallel)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(method => method.Name.Contains("WaitUntilFinished"));
            if (target == null)
            {
                throw new MissingMethodException(
                    typeof(FastParallel).FullName,
                    "For._WaitUntilFinished (local function)");
            }

            return target;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();

            if (SpinBudget <= 0)
            {
                Fail("the fastparallel-wait.txt override disables parking");
                return code;
            }

            var replacement = AccessTools.DeclaredMethod(
                typeof(FastParallelWaitParkPatch), nameof(WaitSpin));
            if (replacement == null)
            {
                Fail("the replacement wait helper was not found");
                return code;
            }

            // The loop condition reads the captured task out of the local
            // function's display struct. Take that pair from the method's own IL
            // rather than naming a compiler-generated type: ldarg.0 / ldfld
            // ParallelTask.
            var load = -1;
            for (var i = 1; i < code.Count; i++)
            {
                if (code[i].opcode == OpCodes.Ldfld
                    && code[i].operand is FieldInfo { } field
                    && field.FieldType == typeof(FastParallel.ParallelTask)
                    && code[i - 1].opcode == OpCodes.Ldarg_0)
                {
                    if (load >= 0)
                    {
                        Fail("its loop condition loads the task more than once");
                        return code;
                    }
                    load = i;
                }
            }

            if (load < 0)
            {
                Fail("its loop condition does not load a captured ParallelTask");
                return code;
            }

            // ldloca.s <spinWait> / ldc.i4.m1 / call SpinWait.SpinOnce(int32).
            // Requiring the -1 is what keeps this from matching a bounded spin a
            // future build might add somewhere else in the method.
            var sites = new List<int>();
            for (var i = 2; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Call
                    || code[i].operand is not MethodInfo { Name: "SpinOnce" } spinOnce
                    || spinOnce.DeclaringType != typeof(SpinWait)
                    || spinOnce.GetParameters().Length != 1
                    || code[i - 1].opcode != OpCodes.Ldc_I4_M1
                    || (code[i - 2].opcode != OpCodes.Ldloca_S && code[i - 2].opcode != OpCodes.Ldloca))
                {
                    continue;
                }
                sites.Add(i);
            }

            if (sites.Count != 1)
            {
                Fail("its wait is not a single unbounded SpinOnce(-1) on a local SpinWait "
                     + "(found " + sites.Count.ToString(CultureInfo.InvariantCulture) + ")");
                return code;
            }

            // Replace "ldc.i4.m1 / call SpinOnce" with "ldarg.0 / ldfld task /
            // call WaitSpin", leaving the ldloca that precedes it as the ref
            // argument. The first replacement instruction inherits the labels and
            // exception blocks of both removed ones, so anything branching to
            // either still lands on it.
            var call = sites[0];
            var loadThis = new CodeInstruction(OpCodes.Ldarg_0);
            loadThis.labels.AddRange(code[call - 1].labels);
            loadThis.labels.AddRange(code[call].labels);
            loadThis.blocks.AddRange(code[call - 1].blocks);
            loadThis.blocks.AddRange(code[call].blocks);

            code[call - 1] = loadThis;
            code[call] = new CodeInstruction(OpCodes.Ldfld, code[load].operand);
            code.Insert(call + 1, new CodeInstruction(OpCodes.Call, replacement));

            Applied = true;
            return code;
        }
    }

    /// <summary>Releases a waiting dispatcher as soon as its task completes.</summary>
    [HarmonyPatch]
    internal static class RunParallelBatchComplete
    {
        // With parking disabled by the override nothing ever waits, so this
        // postfix would run on every batch to read a counter that is always
        // zero. Skip it, so "0" in fastparallel-wait.txt leaves the dispatch
        // path entirely unpatched.
        private static bool Prepare() => SpinBudget > 0;

        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(typeof(FastParallel), "RunParallelBatch")
            ?? throw new MissingMethodException(typeof(FastParallel).FullName, "RunParallelBatch");

        // Postfix: vanilla's Interlocked.Decrement of PendingBatches is the last
        // statement of the method, so by here the count is final.
        private static void Postfix(FastParallel.ParallelTask task) => Complete(task);
    }
}
