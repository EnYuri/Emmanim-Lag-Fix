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
/// is found. Past it the worker parks on a monitor that <c>AddToLive</c> pulses,
/// so it wakes on the next dispatch rather than on a timer.
///
/// Raising <c>SpinOnce</c>'s sleep1Threshold instead — a one-token change — was
/// measured and rejected: Cosmoteer never calls <c>timeBeginPeriod</c>, and
/// <c>Thread.Sleep(1)</c> on this machine takes <b>10.6 ms</b>, which would park
/// a worker for most of a frame. The monitor path wakes in microseconds and keeps
/// its timeout only as a backstop.
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

    /// <summary>Parks and pulses so far, for diagnostics.</summary>
    internal static long ParkCount;
    internal static long WakeCount;

    private static readonly object Idle = new();

    /// <summary>Workers that have announced they are about to park. Read by <see cref="Wake"/>.</summary>
    private static int s_sleepers;

    /// <summary>
    /// The <c>SpinWait.Count</c> at which a worker stops spinning and parks.
    ///
    /// Measured on this machine: climbing from 0 to 20 costs 254 us, and each
    /// further step about 12 us, so 60 is roughly 0.7 ms of hot spinning — a
    /// twentieth of a 60 fps frame. Zero or less disables the patch entirely.
    /// </summary>
    internal static readonly int SpinBudget;

    /// <summary>Backstop timeout for a park. The pulse is the normal wake path.</summary>
    internal static readonly int ParkMilliseconds;

    static FastParallelIdleParkPatch()
    {
        var budget = 60;
        var park = 5;

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
    }

    /// <summary>
    /// Replaces vanilla's idle <c>spinWait.SpinOnce(-1)</c>.
    ///
    /// The parameter is the worker's own <c>SpinWait</c> local, so
    /// <c>SpinWait.Count</c> is the spin budget's clock and vanilla's existing
    /// <c>Reset</c> on finding work is what refills it. No state of our own is
    /// kept per worker.
    /// </summary>
    internal static void IdleSpin(ref SpinWait spin)
    {
        if (spin.Count < SpinBudget)
        {
            spin.SpinOnce(-1);
            return;
        }

        // Announce the park before testing for work. Interlocked is a full fence,
        // and Wake fences before reading this counter, so the two cannot miss each
        // other: either the dispatcher sees a sleeper and pulses, or its task was
        // published before our test below and we find it.
        Interlocked.Increment(ref s_sleepers);
        try
        {
            if (FastParallel.s_liveTasks.TryPeek(out _))
            {
                return;
            }

            lock (Idle)
            {
                // Wake pulses under this same lock, so a task published between the
                // test above and here is either visible now or arrives as a pulse.
                if (FastParallel.s_liveTasks.TryPeek(out _))
                {
                    return;
                }

                ParkCount++;
                Monitor.Wait(Idle, ParkMilliseconds);
            }
        }
        finally
        {
            Interlocked.Decrement(ref s_sleepers);
        }
    }

    /// <summary>Wakes parked workers after a task has been published.</summary>
    internal static void Wake()
    {
        // The task is already in the pool; order that write before this read, or
        // x86 store-load reordering can hide a sleeper that is about to test it.
        Thread.MemoryBarrier();
        if (Volatile.Read(ref s_sleepers) == 0)
        {
            return;
        }

        lock (Idle)
        {
            WakeCount++;
            Monitor.PulseAll(Idle);
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

    /// <summary>Pulses parked workers as soon as a task becomes claimable.</summary>
    [HarmonyPatch]
    internal static class AddToLiveWake
    {
        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(typeof(FastParallel), "AddToLive")
            ?? throw new MissingMethodException(typeof(FastParallel).FullName, "AddToLive");

        // Postfix: the task must be in the pool before a woken worker looks for it.
        private static void Postfix() => Wake();
    }
}
