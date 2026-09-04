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
/// helpers that record what was added, and a replacement pool deinitializer
/// empties the set in proportion to that record rather than to its capacity.
///
/// The set is only ever probed with Add and never enumerated, so nothing
/// observable can depend on its internal layout, and the emptied set is
/// indistinguishable from a cleared one. Any round this patch did not see end
/// to end — a nested allocation, a set recycled by another call site, a shape
/// that stopped matching — falls back to Halfling's own deinitializer, so the
/// result is identical to vanilla in every case.
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
    /// The sources added during that round, in insertion order.
    /// </summary>
    [ThreadStatic]
    private static List<SourceInfo>? _trackedAdds;

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

        // A nested round would clobber the outer one's record. Leave the inner
        // set untracked; its disposal then falls back to vanilla.
        if (_trackedSet is null)
        {
            _trackedSet = set;
            (_trackedAdds ??= new List<SourceInfo>()).Clear();
        }

        return set;
    }

    private static bool TrackedAdd(HashSet<SourceInfo> set, SourceInfo source)
    {
        if (!set.Add(source))
        {
            return false;
        }

        if (ReferenceEquals(_trackedSet, set))
        {
            _trackedAdds!.Add(source);
        }

        return true;
    }

    private static void Deinitialize(TempHashSet<SourceInfo> set)
    {
        if (!ReferenceEquals(_trackedSet, set))
        {
            _vanillaDeinitializer!(set);
            return;
        }

        _trackedSet = null;
        var added = _trackedAdds!;

        // Every recorded Add returned true and nothing removes during a round,
        // so this equality holds exactly whenever the whole round was observed.
        // EnsureCapacity(0) reports the current capacity without growing it.
        if (added.Count == set.Count && added.Count * 4 < set.EnsureCapacity(0))
        {
            for (var i = 0; i < added.Count; i++)
            {
                set.Remove(added[i]);
            }

            added.Clear();
            return;
        }

        added.Clear();
        _vanillaDeinitializer!(set);
    }
}
