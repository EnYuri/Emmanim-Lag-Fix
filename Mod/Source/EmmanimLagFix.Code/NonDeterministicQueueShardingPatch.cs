using System.Collections.Concurrent;
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

    /// <summary>One shard set per SimRoot, collected with it.</summary>
    private static readonly ConditionalWeakTable<object, ConcurrentQueue<Action>[]> Shards = new();

    /// <summary>
    /// A SimRoot and its shards as one immutable pair, so a single read is
    /// always self-consistent. There is one simulation at a time, so this hits
    /// on essentially every call and keeps the lookup to a reference compare.
    /// </summary>
    private sealed class Hot(object sim, ConcurrentQueue<Action>[] queues)
    {
        internal readonly object Sim = sim;
        internal readonly ConcurrentQueue<Action>[] Queues = queues;
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

    private static ConcurrentQueue<Action>[] QueuesFor(object sim)
    {
        var hot = _hot;
        if (hot != null && ReferenceEquals(hot.Sim, sim))
        {
            return hot.Queues;
        }

        var queues = Shards.GetValue(sim, static _ =>
        {
            var created = new ConcurrentQueue<Action>[ShardMask + 1];
            for (var i = 0; i < created.Length; i++)
            {
                created[i] = new ConcurrentQueue<Action>();
            }

            return created;
        });

        _hot = new Hot(sim, queues);
        return queues;
    }

    /// <summary>
    /// Replaces vanilla's <c>_queuedNonDeterministic.Enqueue(callback)</c>. The
    /// thread id picks the shard, so one thread never changes shard and its own
    /// callbacks stay in order.
    /// </summary>
    internal static void ShardedEnqueue(object sim, Action callback) =>
        QueuesFor(sim)[Environment.CurrentManagedThreadId & ShardMask].Enqueue(callback);

    /// <summary>
    /// Runs everything the shards hold, repeating while callbacks post more —
    /// the same reentrancy vanilla's <c>while (TryDequeue)</c> loop allows.
    /// </summary>
    internal static void Drain(object sim)
    {
        if (!Applied || !Shards.TryGetValue(sim, out var queues))
        {
            return;
        }

        bool any;
        do
        {
            any = false;
            foreach (var queue in queues)
            {
                while (queue.TryDequeue(out var callback))
                {
                    any = true;
                    callback();
                }
            }
        }
        while (any);
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
}
