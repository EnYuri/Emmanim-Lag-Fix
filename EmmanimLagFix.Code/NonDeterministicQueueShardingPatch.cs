using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Removes the multi-producer contention on the simulation's single
/// non-deterministic callback queue.
///
/// <c>Cosmoteer.Simulation.SimRoot</c> holds one
/// <c>ConcurrentQueue&lt;Action&gt; _queuedNonDeterministic</c>. Work that runs
/// on a FastParallel worker but must touch the scene graph posts to it, and the
/// main thread drains it in <c>ExecuteQueued</c>:
///
/// <code>
/// public void EnqueueNonDeterministic(Action callback, bool force = false)
/// {
///     ...
///     if (force || IsDoingParallelUpdate || SerialState.CurrentOperation != null)
///         _queuedNonDeterministic.Enqueue(callback);   // every worker, same tail
///     else
///         callback();
/// }
/// </code>
///
/// A 20-second CPU trace on a 170-minute two-player host session measured
/// 1,433.9 ms in <c>EnqueueNonDeterministic</c> — 7.5% of all real
/// (spin-excluded) process CPU, and 50.5% of the whole effect-anchor subtree,
/// more than the anchor's own vector maths. Every sampled call came from
/// <c>MultiMediaEffectNode.EffectAnchor.Update</c>, which runs in update bucket
/// 8 under <c>SimRoot.ParallelUpdate</c>: one anchor per playing media effect,
/// every frame, across sixteen threads. Draining the queue on the main thread
/// cost only 507.9 ms, so the expense is entirely on the producer side —
/// sixteen cores contending for one queue tail, at roughly ten times the
/// latency of an uncontended enqueue.
///
/// The fix shards that queue by thread. Each thread always maps to the same
/// shard, so a given thread's callbacks still run in the order it posted them —
/// the only ordering vanilla actually establishes. Ordering *between* threads
/// is not preserved and does not need to be: two workers enqueuing concurrently
/// already race for the tail, so vanilla's total order carries no happens-before
/// and nothing may depend on it. Same-thread order, which does carry one, is
/// untouched.
///
/// Nothing else changes. The inline (main-thread, non-parallel) branch is not
/// rewritten; callbacks are neither reordered within a thread, deduplicated,
/// dropped nor delayed by a frame; and the drain happens inside the same
/// <c>ExecuteQueued</c> call at the same point in the tick. Simulation state,
/// lockstep input and the deterministic queue are not involved.
///
/// If the enqueue site is not the exact expected shape the transpiler returns
/// the original instructions and vanilla behaviour stands; the drain is then a
/// no-op, because no shard ever receives anything.
/// </summary>
[HarmonyPatch]
internal static class NonDeterministicQueueShardingPatch
{
    /// <summary>Set only after every shape check passed and the enqueue site was rewritten.</summary>
    internal static bool Applied;

    /// <summary>
    /// The shards for one SimRoot, plus a bit per shard that has been written
    /// since a drain last looked. The bitmap is what keeps the drain off the
    /// shards nobody posted to: a queue that was never written is never touched.
    /// </summary>
    private sealed class ShardSet
    {
        internal readonly ConcurrentQueue<Action>[] Queues;

        /// <summary>Bit <c>i</c> set means shard <c>i</c> was written to.</summary>
        internal long Written;

        internal ShardSet(int count)
        {
            Queues = new ConcurrentQueue<Action>[count];
            for (var i = 0; i < count; i++)
            {
                Queues[i] = new ConcurrentQueue<Action>();
            }
        }
    }

    /// <summary>One shard set per SimRoot, collected with it.</summary>
    private static readonly ConditionalWeakTable<object, ShardSet> Shards = new();

    /// <summary>
    /// A SimRoot and its shards as one immutable pair, so a single read is
    /// always self-consistent. There is one simulation at a time, so this hits
    /// on essentially every call and keeps the lookup to a reference compare.
    /// </summary>
    private sealed class Hot(object sim, ShardSet shards)
    {
        internal readonly object Sim = sim;
        internal readonly ShardSet Shards = shards;
    }

    private static Hot? _hot;

    /// <summary>Power of two, so a thread id maps with a mask rather than a modulo.</summary>
    private static readonly int ShardMask = ShardCount() - 1;

    private static int ShardCount()
    {
        var n = 1;
        while (n < Environment.ProcessorCount)
        {
            n <<= 1;
        }

        return Math.Clamp(n, 8, 64);
    }

    private static ShardSet ShardsFor(object sim)
    {
        var hot = _hot;
        if (hot != null && ReferenceEquals(hot.Sim, sim))
        {
            return hot.Shards;
        }

        var shards = Shards.GetValue(sim, static _ => new ShardSet(ShardMask + 1));
        _hot = new Hot(sim, shards);
        return shards;
    }

    /// <summary>
    /// Replaces vanilla's <c>_queuedNonDeterministic.Enqueue(callback)</c>. The
    /// thread id picks the shard, so one thread never changes shard and its own
    /// callbacks stay in order.
    /// </summary>
    internal static void ShardedEnqueue(object sim, Action callback)
    {
        var shards = ShardsFor(sim);
        var index = Environment.CurrentManagedThreadId & ShardMask;
        shards.Queues[index].Enqueue(callback);

        // Publish the shard only after the callback is in the queue. Both
        // operations carry a full barrier, so a drain that observes this bit is
        // guaranteed to observe the callback as well, and the bit can only ever
        // be set spuriously — never lost while an item is still queued.
        Interlocked.Or(ref shards.Written, 1L << index);
    }

    /// <summary>
    /// Runs everything the shards hold, repeating while callbacks post more —
    /// the same reentrancy vanilla's <c>while (TryDequeue)</c> loop allows.
    ///
    /// Only the shards the bitmap names are visited. The previous form swept
    /// every shard and then swept them all again to observe emptiness, which
    /// cost at least two <c>TryDequeue</c> calls per shard on every tick
    /// whether or not anything had been posted; a 20-second host trace put that
    /// sweep at 2.9% of all main-thread CPU. A tick that posted nothing now
    /// costs one interlocked read.
    /// </summary>
    internal static void Drain(object sim)
    {
        if (!Applied)
        {
            return;
        }

        var hot = _hot;
        ShardSet? shards;
        if (hot != null && ReferenceEquals(hot.Sim, sim))
        {
            shards = hot.Shards;
        }
        else if (!Shards.TryGetValue(sim, out shards))
        {
            return;
        }

        var pending = (ulong)Interlocked.Exchange(ref shards.Written, 0L);
        while (pending != 0UL)
        {
            var index = BitOperations.TrailingZeroCount(pending);
            pending &= pending - 1;

            var queue = shards.Queues[index];
            while (queue.TryDequeue(out var callback))
            {
                callback();
            }

            // A callback may have posted more work, to this shard or another.
            pending |= (ulong)Interlocked.Exchange(ref shards.Written, 0L);
        }
    }

    /// <summary>
    /// Drops every mod-owned reference and pending callback for a simulation
    /// after vanilla has disposed it. The weak table does not root its key, but
    /// the hot-path pair deliberately does; without this release an old SimRoot
    /// can survive a resync until the replacement first posts a callback.
    /// </summary>
    internal static void Release(object sim)
    {
        var hot = Volatile.Read(ref _hot);
        if (hot != null && ReferenceEquals(hot.Sim, sim))
        {
            Interlocked.CompareExchange(ref _hot, null, hot);
        }

        // Removing the value also releases callbacks whose closures refer back
        // into the disposed game. SimRoot.Dispose runs after its workers stop,
        // so no legitimate producer remains at this point.
        Shards.Remove(sim);
    }

    private static Type SimRootType =>
        AccessTools.TypeByName("Cosmoteer.Simulation.SimRoot")
        ?? throw new TypeLoadException("Cosmoteer.Simulation.SimRoot was not found.");

    [HarmonyPatch]
    private static class Enqueue
    {
        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(SimRootType, "EnqueueNonDeterministic")
            ?? throw new MissingMethodException(SimRootType.FullName, "EnqueueNonDeterministic");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();

            // The one site is: ldarg.0 / ldfld _queuedNonDeterministic / ldarg.1 / callvirt Enqueue.
            var sites = code.FindAll(IsQueueEnqueue);
            if (sites.Count != 1)
            {
                Logger.Log(
                    $"[EmmanimLagFix] SimRoot.EnqueueNonDeterministic has {sites.Count} queue enqueues "
                    + "rather than one; leaving it at vanilla behaviour.");
                return code;
            }

            var call = code.IndexOf(sites[0]);
            if (call < 3
                || !code[call - 3].IsLdarg(0)
                || code[call - 2].opcode != OpCodes.Ldfld
                || code[call - 2].operand is not FieldInfo { FieldType.IsGenericType: true } field
                || field.FieldType.GetGenericTypeDefinition() != typeof(ConcurrentQueue<>)
                || !code[call - 1].IsLdarg(1))
            {
                Logger.Log(
                    "[EmmanimLagFix] SimRoot.EnqueueNonDeterministic's enqueue site is not the expected "
                    + "shape; leaving it at vanilla behaviour.");
                return code;
            }

            // Drop the field load so the SimRoot itself becomes the first
            // argument, keeping any label that sat on it.
            code[call - 1].labels.AddRange(code[call - 2].labels);
            code.RemoveAt(call - 2);

            var rewritten = code[call - 1];
            rewritten.opcode = OpCodes.Call;
            rewritten.operand = AccessTools.DeclaredMethod(
                typeof(NonDeterministicQueueShardingPatch), nameof(ShardedEnqueue));

            Applied = true;
            return code;
        }

        private static bool IsQueueEnqueue(CodeInstruction instruction) =>
            (instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call)
            && instruction.operand is MethodInfo { Name: "Enqueue" } method
            && method.DeclaringType is { IsGenericType: true } declaring
            && declaring.GetGenericTypeDefinition() == typeof(ConcurrentQueue<>);
    }

    [HarmonyPatch]
    private static class Execute
    {
        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(SimRootType, "ExecuteQueued")
            ?? throw new MissingMethodException(SimRootType.FullName, "ExecuteQueued");

        /// <summary>
        /// Runs where vanilla's own non-deterministic drain finishes, so the
        /// callbacks execute on the same thread within the same tick.
        /// </summary>
        private static void Postfix(object __instance, bool nonDeterministic)
        {
            if (nonDeterministic)
            {
                Drain(__instance);
            }
        }
    }

    [HarmonyPatch]
    private static class Dispose
    {
        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(SimRootType, nameof(IDisposable.Dispose))
            ?? throw new MissingMethodException(SimRootType.FullName, nameof(IDisposable.Dispose));

        /// <summary>Let vanilla finish tearing the simulation down first.</summary>
        private static void Postfix(object __instance) => Release(__instance);
    }
}
