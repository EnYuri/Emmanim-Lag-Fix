using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Resources;
using Halfling.Logging;
using Halfling.Timing;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Removes the multi-producer contention on ResourceManager's two sink-job
/// collection lists.
///
/// <c>UpdateSinkJobs(Time)</c> fans the per-sink pass out over every
/// FastParallel worker, and each worker publishes its result through one of two
/// shared lists:
///
/// <code>
/// if (sink.WasSatisfied &amp;&amp; !flag)
///     lock (_highPriorityFlags) { _highPriorityFlags.Add(index); }
/// ...
/// if (jobUpdates != null)
///     lock (_jobUpdates) { _jobUpdates.Add((index, jobUpdates)); }
/// </code>
///
/// Both bodies are a single <c>List.Add</c>, so the whole cost is the convoy. A
/// 20-second host trace measured 637 ms of <c>Monitor.Enter_Slowpath</c>
/// underneath <c>UpdateSinkJobs</c> - the largest single lock in the process,
/// ahead of the audio mixer - which also lengthens the main thread's wait in
/// <c>FastParallel.WaitUntilFinished</c>.
///
/// The fix gives each thread its own list. A transpiler rewrites every load of
/// the two fields inside <c>UpdateSinkJobs(int)</c> so it yields that thread's
/// shard instead. Both the lock target and the <c>Add</c> target come from the
/// same field load, so they remain the same object and the lock survives - it is
/// simply never contended, which is the cheap thin-lock path. A second
/// transpiler makes the first load of each field in <c>UpdateSinkJobs(Time)</c>
/// drain every shard back into the real list before vanilla reads it.
///
/// Determinism is preserved by construction rather than by argument. Vanilla
/// sorts both lists immediately after the parallel pass and before touching
/// them - <c>_jobUpdates</c> by SinkIndex, <c>_highPriorityFlags</c> by value -
/// and a given sink index is added at most once, so each sort is a total order
/// over distinct keys. Whatever order the shards drain in, the sorted result is
/// identical to vanilla's. Nothing else about the pass changes: no job is added,
/// dropped, reordered or delayed by a tick.
///
/// The two halves fail independently and safely. If the drain site is not the
/// expected shape, <c>Shard</c> hands back the real list and vanilla's contended
/// lock stands. If the shard site is not the expected shape, no shard is ever
/// created and the drain finds nothing.
/// </summary>
[HarmonyPatch]
internal static class ResourceSinkJobShardingPatch
{
    /// <summary>Set once the drain site was rewritten; sharding is only safe after that.</summary>
    internal static bool DrainApplied;

    /// <summary>Set once the per-sink field loads were rewritten.</summary>
    internal static bool ShardApplied;

    /// <summary>
    /// Shard lists per owning list. Keyed by the real list rather than by the
    /// ResourceManager, so the two fields cannot collide and a ship's shards are
    /// collected with it.
    /// </summary>
    private static readonly ConditionalWeakTable<object, object?[]> Slots = new();

    /// <summary>Power of two, so a thread id maps with a mask rather than a modulo.</summary>
    private static readonly int ShardMask = ShardCountForWorkers(
        FastParallelIdleParkPatch.WorkerCount) - 1;

    // FastParallel owns a stable worker set. Assign those threads and the
    // calling thread consecutive slots on first use; hashing ManagedThreadId
    // with a mask allowed two live producers to collide even when enough slots
    // existed, which put Monitor.Enter_Slowpath back inside the sharded path.
    private static int s_nextShardIndex;

    [ThreadStatic]
    private static int t_shardIndexPlusOne;

    /// <summary>
    /// One producer is the calling thread and the rest are FastParallel workers.
    /// Size for those actual producers rather than logical processors, but keep
    /// eight as the minimum. Reducing a three-worker client to four slots looked
    /// cheaper on paper, yet a later trace measured 803 ms of lock contention in
    /// the supposedly sharded per-sink path. The spare slots tolerate producer
    /// turnover or an unexpected helper thread; scanning four additional nulls
    /// is cheaper than allowing two live producers to share a lock.
    /// </summary>
    internal static int ShardCountForWorkers(int workerCount)
    {
        var producers = Math.Max(8, Math.Clamp(workerCount, 1, 63) + 1);
        var n = 1;
        while (n < producers)
        {
            n <<= 1;
        }

        return n;
    }

    internal static int ConfiguredShardCount => ShardMask + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CurrentShardIndex()
    {
        var registered = t_shardIndexPlusOne;
        if (registered != 0)
        {
            return registered - 1;
        }

        var index = (Interlocked.Increment(ref s_nextShardIndex) - 1) & ShardMask;
        t_shardIndexPlusOne = index + 1;
        return index;
    }

    /// <summary>
    /// Replaces a load of <c>_jobUpdates</c> / <c>_highPriorityFlags</c> inside
    /// the per-sink pass. Stable FastParallel workers receive distinct slots
    /// while capacity remains. A wrapped slot still retains the original lock,
    /// so unexpected extra producer threads are slower but remain correct.
    /// </summary>
    internal static List<T> Shard<T>(List<T> real)
    {
        if (!DrainApplied)
        {
            return real;
        }

        var slots = Slots.GetValue(real, static _ => new object?[ShardMask + 1]);
        var index = CurrentShardIndex();
        if (slots[index] is List<T> existing)
        {
            return existing;
        }

        var created = new List<T>();
        return Interlocked.CompareExchange(ref slots[index], created, null) as List<T> ?? created;
    }

    /// <summary>
    /// Replaces the first load of each field in <c>UpdateSinkJobs(Time)</c>,
    /// which happens after the parallel pass has finished and before vanilla
    /// sorts.
    /// </summary>
    internal static List<T> DrainInto<T>(List<T> real)
    {
        if (!Slots.TryGetValue(real, out var slots))
        {
            return real;
        }

        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i] is List<T> { Count: > 0 } shard)
            {
                real.AddRange(shard);
                shard.Clear();
            }
        }

        return real;
    }

    /// <summary>
    /// The two collection fields, in the order <c>found</c> counts them. Looked
    /// up so a rename fails loudly at startup rather than silently leaving the
    /// lock in place.
    /// </summary>
    private static readonly string[] FieldNames = ["_jobUpdates", "_highPriorityFlags"];

    private static void RequireFields()
    {
        foreach (var name in FieldNames)
        {
            _ = AccessTools.DeclaredField(typeof(ResourceManager), name)
                ?? throw new MissingFieldException(typeof(ResourceManager).FullName, name);
        }
    }

    /// <summary>
    /// Matches by name and declaring type rather than by FieldInfo identity,
    /// which reflection does not guarantee to be reference-stable.
    /// </summary>
    private static int IndexOfField(FieldInfo loaded) =>
        loaded.DeclaringType == typeof(ResourceManager)
            ? Array.IndexOf(FieldNames, loaded.Name)
            : -1;

    /// <summary>
    /// Routes loads of the two fields through <paramref name="helper"/>, which
    /// takes and returns the same list type, so the stack shape is unchanged.
    /// Returns whether every field matched <paramref name="expected"/>; the
    /// caller discards the rewrite when it did not.
    /// </summary>
    private static bool Redirect(
        List<CodeInstruction> code,
        string helper,
        bool firstLoadOnly,
        Predicate<int> expected,
        string what)
    {
        RequireFields();
        var found = new int[FieldNames.Length];

        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Ldfld || code[i].operand is not FieldInfo loaded)
            {
                continue;
            }

            var which = IndexOfField(loaded);
            if (which < 0 || (firstLoadOnly && found[which] > 0))
            {
                if (which >= 0)
                {
                    found[which]++;
                }

                continue;
            }

            found[which]++;
            code.Insert(
                i + 1,
                new CodeInstruction(
                    OpCodes.Call,
                    AccessTools.DeclaredMethod(typeof(ResourceSinkJobShardingPatch), helper)
                        .MakeGenericMethod(loaded.FieldType.GetGenericArguments()[0])));
            i++;
        }

        if (Array.TrueForAll(found, expected))
        {
            return true;
        }

        Logger.Log(
            $"[EmmanimLagFix] ResourceManager.{what} loads _jobUpdates {found[0]} time(s) and "
            + $"_highPriorityFlags {found[1]} time(s), which is not the expected shape; "
            + "leaving sink-job collection at vanilla behaviour.");
        return false;
    }

    /// <summary>The per-sink pass, which runs on every FastParallel worker.</summary>
    [HarmonyPatch]
    private static class PerSink
    {
        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(typeof(ResourceManager), "UpdateSinkJobs", new[] { typeof(int) })
            ?? throw new MissingMethodException(typeof(ResourceManager).FullName, "UpdateSinkJobs(int)");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = instructions.ToList();
            var code = original.Select(instruction => new CodeInstruction(instruction)).ToList();

            // Each field is loaded exactly twice - once for the lock target and
            // once for the Add - and both loads have to be rewritten together or
            // the lock would guard a list it does not write.
            ShardApplied = Redirect(
                code, nameof(Shard), firstLoadOnly: false, count => count == 2, "UpdateSinkJobs(int)");
            return ShardApplied ? code : original;
        }
    }

    /// <summary>The serial merge, which runs on the calling thread once the pass is done.</summary>
    [HarmonyPatch]
    private static class Merge
    {
        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(typeof(ResourceManager), "UpdateSinkJobs", new[] { typeof(Time) })
            ?? throw new MissingMethodException(typeof(ResourceManager).FullName, "UpdateSinkJobs(Time)");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = instructions.ToList();
            var code = original.Select(instruction => new CodeInstruction(instruction)).ToList();

            // Only the first load of each field drains. It is the Count check
            // that guards everything else the method does with that list, so
            // every later read already sees the merged contents.
            DrainApplied = Redirect(
                code, nameof(DrainInto), firstLoadOnly: true, count => count >= 1, "UpdateSinkJobs(Time)");
            return DrainApplied ? code : original;
        }
    }
}
