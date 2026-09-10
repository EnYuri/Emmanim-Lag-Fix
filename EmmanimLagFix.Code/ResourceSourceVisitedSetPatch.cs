using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Resources;
using Halfling.Pooling;
using HarmonyLib;

using SourceInfo = Cosmoteer.Ships.Resources.ResourceManager.SourceInfo;

namespace EmmanimLagFix.Code;

/// <summary>
/// <c>ResourceManager.SearchForSources(SinkInfo)</c> marks the sources it has
/// already considered in a pooled <c>TempHashSet&lt;SourceInfo&gt;</c>. That pool
/// is global per closed type, so once one sink on a large ship has grown the
/// set, every later sink pays <see cref="HashSet{T}.Clear"/> on disposal, and
/// Clear zeroes the whole bucket array however few sources that sink actually
/// saw. The search runs once per sink per fixed update, in parallel, inside
/// <c>ResourceManager.FixedUpdate</c> — 52.9% of ParallelFixedUpdate on a
/// 421-ship host — and a 20-second profile attributed 613.4 ms to that one
/// <c>Array.Clear</c>, making it the largest single zeroing cost in the process.
///
/// This is the same defect 2.0.29 fixed for the path-contiguity search, in a
/// method far too large to reimplement safely, so it is repaired in place
/// instead: a transpiler routes the allocation and the three Add calls through
/// a thread-local generation-stamped identity set. Reset advances a generation
/// and clears only the references written by the completed search; it never
/// zeroes the retained bucket capacity.
///
/// The set is only ever probed with Add and never enumerated, so nothing
/// observable can depend on its internal layout. <c>SourceInfo</c> inherits
/// reference equality from <c>object</c>, which is guarded at startup, so the
/// replacement accepts exactly the same first occurrence as vanilla. Any
/// nested or unrecognized round falls back to the real temporary HashSet and
/// Halfling's own deinitializer.
/// </summary>
[HarmonyPatch]
internal static class ResourceSourceVisitedSetPatch
{
    /// <summary>
    /// Set only when the target resolved with the expected shape and the pool
    /// deinitializer was replaced.
    /// </summary>
    internal static bool Applied { get; private set; }

    /// <summary>
    /// Halfling's own deinitializer, kept as the fallback for every round this
    /// patch did not observe from allocation to disposal.
    /// </summary>
    private static Deinitializer<TempHashSet<SourceInfo>>? _vanillaDeinitializer;

    /// <summary>
    /// The set whose round the current thread is recording, or null when the
    /// thread is not inside a tracked round.
    /// </summary>
    [ThreadStatic]
    private static TempHashSet<SourceInfo>? _trackedSet;

    /// <summary>
    /// Generation-stamped visited state for the current thread. Resource source
    /// searches are synchronous; a nested search falls back to vanilla.
    /// </summary>
    [ThreadStatic]
    private static IdentityGenerationSet? _visited;

    private static readonly MethodInfo AllocTarget = AccessTools.Method(
        typeof(TempHashSet<SourceInfo>),
        nameof(TempHashSet<SourceInfo>.Alloc),
        Type.EmptyTypes)
        ?? throw new MissingMethodException(typeof(TempHashSet<SourceInfo>).FullName, "Alloc()");

    private static readonly MethodInfo AddTarget = AccessTools.Method(
        typeof(HashSet<SourceInfo>),
        nameof(HashSet<SourceInfo>.Add),
        new[] { typeof(SourceInfo) })
        ?? throw new MissingMethodException(typeof(HashSet<SourceInfo>).FullName, "Add(SourceInfo)");

    private static readonly MethodInfo DeinitializeMethod = AccessTools.Method(
        typeof(ResourceSourceVisitedSetPatch), nameof(Deinitialize))
        ?? throw new MissingMethodException(
            typeof(ResourceSourceVisitedSetPatch).FullName, nameof(Deinitialize));

    private static bool Prepare()
    {
        // The replacement deliberately uses reference identity. Fail closed if
        // a future game version gives SourceInfo value equality.
        if (typeof(SourceInfo).GetMethod(nameof(object.GetHashCode), Type.EmptyTypes)?.DeclaringType != typeof(object)
            || typeof(SourceInfo).GetMethod(nameof(object.Equals), new[] { typeof(object) })?.DeclaringType != typeof(object))
        {
            Halfling.Logging.Logger.Log(
                "[EmmanimLagFix] ResourceManager.SourceInfo now overrides equality; "
                + "leaving its visited set at vanilla behavior.");
            return false;
        }

        // TempHashSet installs the deinitializer from its static constructor,
        // which has not necessarily run yet. Force it, or the assignment below
        // is overwritten the first time the type is touched.
        RuntimeHelpers.RunClassConstructor(typeof(TempHashSet<SourceInfo>).TypeHandle);

        var installed = ObjectPool<TempHashSet<SourceInfo>>.Deinitializer
            ?? throw new InvalidOperationException(
                "TempHashSet<SourceInfo> no longer installs a pool deinitializer; "
                + "skipping the source visited-set patch.");

        // Harmony calls Prepare once per patched target, and a second Harmony
        // instance may patch on top of the first. Capturing what is installed
        // unconditionally would make this delegate its own fallback.
        if (installed.Method != DeinitializeMethod)
        {
            _vanillaDeinitializer = installed;
            ObjectPool<TempHashSet<SourceInfo>>.Deinitializer = Deinitialize;
        }

        return true;
    }

    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(ResourceManager),
            "SearchForSources",
            new[] { typeof(ResourceManager.SinkInfo) })
            ?? throw new MissingMethodException(
                typeof(ResourceManager).FullName,
                "SearchForSources(SinkInfo)");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var allocReplacement = AccessTools.Method(typeof(ResourceSourceVisitedSetPatch), nameof(AllocTracked));
        var addReplacement = AccessTools.Method(typeof(ResourceSourceVisitedSetPatch), nameof(TrackedAdd));
        var allocs = 0;
        var adds = 0;

        foreach (var instruction in instructions)
        {
            if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt)
            {
                yield return instruction;
                continue;
            }

            if (Equals(instruction.operand, AllocTarget))
            {
                allocs++;
                yield return Retarget(instruction, allocReplacement);
            }
            else if (Equals(instruction.operand, AddTarget))
            {
                adds++;
                yield return Retarget(instruction, addReplacement);
            }
            else
            {
                yield return instruction;
            }
        }

        if (allocs != 1 || adds != 3)
        {
            throw new InvalidOperationException(
                "Expected exactly one TempHashSet<SourceInfo>.Alloc call and three Add calls in "
                + $"ResourceManager.SearchForSources(SinkInfo), found {allocs} and {adds}. "
                + "The game code shape has changed; skipping the source visited-set patch.");
        }

        Applied = true;
    }

    /// <summary>
    /// Both replacements take the same arguments and return the same type as the
    /// call they stand in for, so the evaluation stack is untouched.
    /// </summary>
    private static CodeInstruction Retarget(CodeInstruction instruction, MethodInfo replacement)
    {
        var rewritten = new CodeInstruction(OpCodes.Call, replacement);
        rewritten.labels.AddRange(instruction.labels);
        rewritten.blocks.AddRange(instruction.blocks);
        return rewritten;
    }

    private static TempHashSet<SourceInfo> AllocTracked()
    {
        var set = TempHashSet<SourceInfo>.Alloc();

        // A nested round would clobber the outer generation. Leave it untracked;
        // its Add calls and disposal then use the real temporary HashSet.
        if (_trackedSet is null)
        {
            _trackedSet = set;
            (_visited ??= new IdentityGenerationSet()).Begin();
        }

        return set;
    }

    private static bool TrackedAdd(HashSet<SourceInfo> set, SourceInfo source)
    {
        if (ReferenceEquals(_trackedSet, set))
        {
            return _visited!.Add(source);
        }

        return set.Add(source);
    }

    private static void Deinitialize(TempHashSet<SourceInfo> set)
    {
        if (!ReferenceEquals(_trackedSet, set))
        {
            _vanillaDeinitializer!(set);
            return;
        }

        _trackedSet = null;
        _visited!.End();

        // The tracked temporary HashSet remained empty. Keep invoking the
        // original deinitializer so pool lifecycle is exactly vanilla; Clear is
        // constant-time when Count is zero even if this pooled object once grew.
        _vanillaDeinitializer!(set);
    }

    /// <summary>
    /// A reference-identity set whose buckets are invalidated by generation
    /// instead of being cleared. It is never enumerated, so bucket and chain
    /// order are unobservable.
    /// </summary>
    private sealed class IdentityGenerationSet
    {
        private struct Entry
        {
            public int Hash;
            public int Next;
            public SourceInfo? Value;
        }

        private int[] _buckets = new int[16];
        private uint[] _bucketGenerations = new uint[16];
        private Entry[] _entries = new Entry[16];
        private int _count;
        private uint _generation = 1;

        public void Begin()
        {
            if (_count != 0)
            {
                throw new InvalidOperationException("A resource visited-set generation was not released.");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Add(SourceInfo value)
        {
            var hash = RuntimeHelpers.GetHashCode(value) & int.MaxValue;
            var bucket = hash & (_buckets.Length - 1);
            var entry = _bucketGenerations[bucket] == _generation
                ? _buckets[bucket] - 1
                : -1;

            while (entry >= 0)
            {
                ref var existing = ref _entries[entry];
                if (existing.Hash == hash && ReferenceEquals(existing.Value, value))
                {
                    return false;
                }
                entry = existing.Next;
            }

            if (_count == _entries.Length)
            {
                Resize();
                bucket = hash & (_buckets.Length - 1);
            }

            var head = _bucketGenerations[bucket] == _generation
                ? _buckets[bucket] - 1
                : -1;
            _entries[_count] = new Entry { Hash = hash, Next = head, Value = value };
            _buckets[bucket] = _count + 1;
            _bucketGenerations[bucket] = _generation;
            _count++;
            return true;
        }

        public void End()
        {
            // Release SourceInfo references without clearing either capacity
            // array. Stale bucket heads become invisible in the next generation.
            for (var i = 0; i < _count; i++)
            {
                _entries[i].Value = null;
            }
            _count = 0;

            _generation++;
            if (_generation == 0)
            {
                Array.Clear(_bucketGenerations);
                _generation = 1;
            }
        }

        private void Resize()
        {
            var newLength = checked(_entries.Length * 2);
            var buckets = new int[newLength];
            var bucketGenerations = new uint[newLength];
            Array.Resize(ref _entries, newLength);

            for (var i = 0; i < _count; i++)
            {
                ref var item = ref _entries[i];
                var bucket = item.Hash & (newLength - 1);
                item.Next = bucketGenerations[bucket] == _generation
                    ? buckets[bucket] - 1
                    : -1;
                buckets[bucket] = i + 1;
                bucketGenerations[bucket] = _generation;
            }

            _buckets = buckets;
            _bucketGenerations = bucketGenerations;
        }
    }
}
