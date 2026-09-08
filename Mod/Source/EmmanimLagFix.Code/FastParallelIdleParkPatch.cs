using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using Halfling.Logging;
using Halfling.Performance;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Lets an idle FastParallel worker block instead of spinning forever.
///
/// Halfling starts one worker per physical core minus one and never lets them
/// sleep. <c>FastParallel.RunThread</c> is an unbounded loop whose idle branch is
/// literally <c>spinWait.SpinOnce(-1)</c>, and the <c>-1</c> is the documented
/// "never call Thread.Sleep(1)" sentinel, so past <c>SpinWait</c>'s yield
/// threshold every worker alternates <c>Thread.Yield</c> and <c>Thread.Sleep(0)</c>
/// for as long as the game runs.
///
/// That is the single largest CPU item in this installation. A 20-second sampled
/// trace of a degraded 5.7-hour session recorded 65.9 s of process CPU, of which
/// <c>SpinWait.SpinOnce</c> was 39.0 s (59%) and <c>Thread.PollGCWorker</c>
/// underneath it 17.8 s (27%), against ~26.9 s of real work. The second number is
/// the reason this matters beyond wasted watts: a spinning thread runs in
/// cooperative GC mode and every collection must rendezvous with all of them,
/// while a thread blocked in a wait is already preemptive and costs the collector
/// nothing.
///
/// The repair is a bounded spin. Below <see cref="SpinBudget"/> the worker spins
/// exactly as vanilla does — which covers the back-to-back <c>For</c> dispatches
/// inside one frame, since vanilla calls <c>SpinWait.Reset</c> the moment a batch
/// is found. Past it the worker parks on an event that <c>AddToLive</c> sets, so
/// it wakes on the next dispatch rather than on a timer.
///
/// Every parked worker gets its <b>own</b> event and neither side takes a lock.
/// Version 2.1.3 parked all of them on one monitor and pulsed it, which moved the
/// cost rather than removing it: a 20-second trace showed <c>SpinWait.SpinOnce</c>
/// gone but <c>Monitor.Wait</c>, <c>Monitor.Enter_Slowpath</c> and
/// <c>Monitor.PulseAll</c> in its place — and the pulse plus part of the lock
/// contention landed on the <i>main</i> thread, because <c>AddToLive</c> runs on
/// whichever thread dispatched. Ten waiters on one monitor is a thundering herd
/// per dispatch; ten independent events are not.
///
/// Raising <c>SpinOnce</c>'s sleep1Threshold instead — a one-token change — was
/// measured and rejected: Cosmoteer never calls <c>timeBeginPeriod</c>, and
/// <c>Thread.Sleep(1)</c> on this machine takes <b>10.6 ms</b>, which would park
/// a worker for most of a frame.
///
/// This cannot deadlock or drop work. <c>For</c> runs batches on the calling
/// thread and spins on <c>PendingBatches</c> until the task is complete, so with
/// every worker asleep a dispatch still finishes — serially. The only thing at
/// risk is parallelism, bounded by the wake latency, which is why the handshake
/// below is written to be exact rather than merely usually right.
///
/// Nothing here touches simulation state, so it is lockstep-neutral: it changes
/// when work runs, never what it computes.
/// </summary>
internal static class FastParallelIdleParkPatch
{
    /// <summary>True once the idle branch was actually rewritten; the smoke test asserts it.</summary>
    internal static bool Applied;

    /// <summary>Why the rewrite was skipped, for the smoke test's message.</summary>
    internal static string? FailureReason;

    /// <summary>Parks so far.</summary>
    internal static long ParkCount;

    /// <summary>Dispatches that found at least one parked worker to release.</summary>
    internal static long WakeCount;

    /// <summary>Parks that ended on the backstop timeout instead of on a set.</summary>
    internal static long TimeoutCount;

    /// <summary>
    /// One parked worker. The event is level-triggered, so a set that arrives
    /// before the wait still releases it, and it is created with no spin count so
    /// the wait goes straight to a kernel block rather than burning the CPU this
    /// patch exists to save.
    /// </summary>
    private sealed class Waiter
    {
        public readonly ManualResetEventSlim Event = new(initialState: false, spinCount: 0);

        /// <summary>Set while this worker is in, or about to enter, its wait.</summary>
        public volatile bool Parked;
    }

    [ThreadStatic]
    private static Waiter? t_waiter;

    // Registered once per worker thread and never removed - FastParallel's threads
    // live for the process. Wake walks this without a lock, so the array grows by
    // publishing a copy, never by mutating the one a walk may be reading.
    private static Waiter?[] s_waiters = new Waiter?[32];
    private static int s_waiterCount;
    private static readonly object RegisterLock = new();

    /// <summary>Workers that have announced a park. Read by <see cref="Wake"/>.</summary>
    private static int s_sleepers;

    /// <summary>
    /// The <c>SpinWait.Count</c> at which a worker stops spinning and parks.
    ///
    /// Measured on this machine: climbing from 0 to 20 costs 254 us, and each
    /// further step about 12 us, so 60 is roughly 0.7 ms of hot spinning — a
    /// twentieth of a 60 fps frame. Zero or less disables the patch entirely.
    /// </summary>
    internal static readonly int SpinBudget;

    /// <summary>
    /// Backstop timeout for a park; a set is the normal wake path.
    ///
    /// 2.1.3 used 5 ms, which the OS rounds up to its ~11 ms timer granularity in
    /// a process that never calls <c>timeBeginPeriod</c>, and a trace showed
    /// 12,316 timeouts against 180 wakes in 20 seconds — every worker on a
    /// treadmill of wake, probe, re-park. The handshake below is exact, so this
    /// only has to cover a lost set, and a lost set cannot lose work either:
    /// vanilla's loop probes again the moment the park returns.
    /// </summary>
    internal static readonly int ParkMilliseconds;

    /// <summary>
    /// Logical processors below which parking is off by default.
    ///
    /// The trade this patch makes is not the same on every machine. Its benefit
    /// is CPU handed back to the rest of the process and a cheaper GC rendezvous;
    /// its cost is a kernel wake per parked worker per dispatch. On the 12-core
    /// host it was measured on, 11 spinning workers starve everything else and
    /// there are cores free to receive a woken one, so the benefit dominates.
    /// On a 4-core client - 3 workers, counted from that client's own freeze dump
    /// on 2026-09-08 - the game's main, render and audio threads already want
    /// those cores, so a woken worker waits for a scheduler slot and the wake
    /// latency is paid in full. One worker late is a third of the parallel width
    /// there against a fourteenth here.
    ///
    /// That client's own numbers carry the signature: whole-process CPU held at
    /// 1.4-1.7 cores while its update phase grew from 45 to 162 ms. More work
    /// raises CPU; lost parallelism raises wall time and leaves CPU flat.
    ///
    /// This threshold is a judgement, not a measurement, which is why
    /// fastparallel-park.txt overrides it in both directions: a positive budget
    /// forces parking on, and 0 forces it off.
    /// </summary>
    private const int ParkMinimumProcessors = 8;

    /// <summary>Logical processors this machine reports, for the diagnostics line.</summary>
    internal static readonly int ProcessorCount = Environment.ProcessorCount;

    /// <summary>True when the processor-count rule, not an override, disabled parking.</summary>
    internal static readonly bool DisabledByProcessorCount;

    static FastParallelIdleParkPatch()
    {
        var narrow = Environment.ProcessorCount < ParkMinimumProcessors;
        var budget = narrow ? 0 : 60;
        var park = 200;
        var overridden = false;

        // Optional override, one line "<budget> [parkMs]", beside the mod folder.
        // Written for calibration runs; absent in a normal install.
        try
        {
            var path = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
                "..",
                "fastparallel-park.txt"));
            if (File.Exists(path))
            {
                var fields = File.ReadAllText(path)
                    .Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 0
                    && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                {
                    budget = b;
                    overridden = true;
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
            // A malformed or unreadable override is not a reason to change threading
            // behaviour; fall through to the defaults.
        }

        SpinBudget = budget;
        ParkMilliseconds = park;
        DisabledByProcessorCount = narrow && !overridden;
    }

    /// <summary>
    /// Parks, wakes that found a sleeper, and parks that ended on the backstop,
    /// for the diagnostics log line. A timeout share near 100% means the wake
    /// handshake is not firing and the workers are back on a timer.
    /// </summary>
    /// <summary>
    /// Wakes and the share of parks that ended on the backstop instead, for the
    /// peer relay, where the full triple does not fit inside the chat limit. A
    /// share near 100% means the wake handshake is not firing.
    /// </summary>
    /// <summary>
    /// Timeout share only. The cumulative wake count was dropped from the peer
    /// relay in 2.1.9: it cost eight characters of a 195-character budget and
    /// only the share is diagnostic - a value near 100% means the wake handshake
    /// is not firing. Measured 0% on the peer and 0.2% locally across a full
    /// 2026-09-08 session, so the handshake added in 2.1.4 is healthy.
    /// </summary>
    internal static string CompactCounters()
    {
        if (SpinBudget <= 0)
        {
            return DisabledByProcessorCount ? "off" : "off!";
        }

        var parks = Volatile.Read(ref ParkCount);
        var timeouts = Volatile.Read(ref TimeoutCount);
        var share = parks > 0 ? 100d * timeouts / parks : 0d;
        return share.ToString("F0", CultureInfo.InvariantCulture) + "%";
    }

    internal static string Counters() =>
        SpinBudget <= 0
        ? (DisabledByProcessorCount
            ? $"off(cores={ProcessorCount})"
            : "off(override)")
        : Volatile.Read(ref ParkCount).ToString(CultureInfo.InvariantCulture)
        + "/" + Volatile.Read(ref WakeCount).ToString(CultureInfo.InvariantCulture)
        + "/" + Volatile.Read(ref TimeoutCount).ToString(CultureInfo.InvariantCulture);

    private static Waiter Register()
    {
        var waiter = new Waiter();
        lock (RegisterLock)
        {
            if (s_waiterCount == s_waiters.Length)
            {
                // Publish a copy rather than resizing in place: Wake walks the array
                // with no lock and must never see a half-built one.
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
    /// Replaces vanilla's idle <c>spinWait.SpinOnce(-1)</c>.
    ///
    /// The parameter is the worker's own <c>SpinWait</c> local, so
    /// <c>SpinWait.Count</c> is the spin budget's clock and vanilla's existing
    /// <c>Reset</c> on finding work is what refills it.
    /// </summary>
    internal static void IdleSpin(ref SpinWait spin)
    {
        if (spin.Count < SpinBudget)
        {
            spin.SpinOnce(-1);
            return;
        }

        var waiter = t_waiter ??= Register();

        // Reset before announcing the park, so a set that arrives from here on is
        // the one this park consumes.
        waiter.Event.Reset();
        waiter.Parked = true;

        // Announce the park before testing for work. Interlocked is a full fence,
        // and Wake fences before reading this counter, so the two cannot miss each
        // other: either the dispatcher sees a sleeper and sets our event, or its
        // task was published before the test below and we find it here.
        Interlocked.Increment(ref s_sleepers);
        try
        {
            if (FastParallel.s_liveTasks.TryPeek(out _))
            {
                return;
            }

            ParkCount++;
            if (!waiter.Event.Wait(ParkMilliseconds))
            {
                TimeoutCount++;
            }
        }
        finally
        {
            waiter.Parked = false;
            Interlocked.Decrement(ref s_sleepers);
        }
    }

    /// <summary>Wakes parked workers after a task has been published.</summary>
    internal static void Wake()
    {
        // The task is already in the pool; order that write before this read, or
        // x86 store-load reordering can hide a sleeper about to test for it.
        Thread.MemoryBarrier();
        if (Volatile.Read(ref s_sleepers) == 0)
        {
            return;
        }

        WakeCount++;

        // No lock on this path. It runs on whichever thread dispatched - usually
        // the main one - and a lock here is exactly what 2.1.3 got wrong.
        var waiters = Volatile.Read(ref s_waiters);
        var count = Volatile.Read(ref s_waiterCount);
        for (var i = 0; i < count && i < waiters.Length; i++)
        {
            if (waiters[i] is { Parked: true } waiter)
            {
                waiter.Event.Set();
            }
        }
    }

    private static void Fail(string reason)
    {
        FailureReason = reason;
        try
        {
            Logger.Log(
                "[EmmanimLagFix] FastParallel workers were left spinning: " + reason + ".");
        }
        catch (Exception)
        {
            // The logger is not initialized in the standalone smoke host.
        }
    }

    /// <summary>Rewrites the idle branch of the worker loop.</summary>
    [HarmonyPatch]
    internal static class RunThreadIdleBranch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(typeof(FastParallel), "RunThread")
            ?? throw new MissingMethodException(typeof(FastParallel).FullName, "RunThread");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();

            if (SpinBudget <= 0)
            {
                Fail("the fastparallel-park.txt override disables parking");
                return code;
            }

            var replacement = AccessTools.DeclaredMethod(
                typeof(FastParallelIdleParkPatch), nameof(IdleSpin));
            if (replacement == null)
            {
                Fail("the replacement idle helper was not found");
                return code;
            }

            // ldloca.s <spinWait> / ldc.i4.m1 / call SpinWait.SpinOnce(int32).
            // The -1 is what makes the wait unbounded, so requiring it is what keeps
            // this from matching some other, bounded spin a future build might add.
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
                Fail("its idle branch is not a single unbounded SpinOnce(-1) on a local "
                     + "SpinWait (found " + sites.Count.ToString(CultureInfo.InvariantCulture) + ")");
                return code;
            }

            // Put the call where the constant was, so anything branching to either
            // instruction still lands on the replacement.
            var call = sites[0];
            var rewritten = new CodeInstruction(OpCodes.Call, replacement);
            rewritten.labels.AddRange(code[call - 1].labels);
            rewritten.labels.AddRange(code[call].labels);
            rewritten.blocks.AddRange(code[call - 1].blocks);
            rewritten.blocks.AddRange(code[call].blocks);
            code[call - 1] = rewritten;
            code.RemoveAt(call);

            Applied = true;
            return code;
        }
    }

    /// <summary>Releases parked workers as soon as a task becomes claimable.</summary>
    [HarmonyPatch]
    internal static class AddToLiveWake
    {
        // With parking disabled by the override nothing ever parks, so the wake
        // postfix would run on every dispatch to find an empty list. Skip it, so
        // "0" in fastparallel-park.txt leaves FastParallel entirely unpatched.
        private static bool Prepare() => SpinBudget > 0;

        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(typeof(FastParallel), "AddToLive")
            ?? throw new MissingMethodException(typeof(FastParallel).FullName, "AddToLive");

        // Postfix: the task must be in the pool before a woken worker looks for it.
        private static void Postfix() => Wake();
    }
}
