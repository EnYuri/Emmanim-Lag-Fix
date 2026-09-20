using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Crew.Pathing;
using Halfling.Geometry;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// The breadth-first set search marks visited sets in a pooled
/// <c>TempHashSet&lt;ContiguousPathSet&gt;</c>. That pool is global per type, so
/// once one whole-ship search has grown it, every later search pays
/// <see cref="HashSet{T}.Clear"/>, which zeroes the entire bucket array
/// regardless of how few sets were actually visited. On a large ship the
/// resource source search runs this thousands of times per second: a 20-second
/// host profile attributed 647 ms — about 41% of all source-search time — to
/// that one <c>Array.Clear</c>.
///
/// This replaces the search with an identical breadth-first walk whose visited
/// set is emptied in proportion to the number of sets actually visited. The
/// seed loop, the queue order, the yielded values and the deferred-execution
/// and exception behaviour are all preserved exactly, and the visited set is
/// only ever probed with Add — never enumerated — so no ordering can depend on
/// its internal layout. The result is therefore bit-identical to vanilla and
/// safe against a peer running a different build.
/// </summary>
[HarmonyPatch]
internal static class PathContiguitySearchSetsPatch
{
    /// <summary>
    /// Set only when the target resolved with the expected shape.
    /// </summary>
    internal static bool Applied { get; private set; }

    /// <summary>
    /// Whether <see cref="ContiguousPathSet"/> still answers equality by
    /// reference, which is what lets the visited table below probe on object
    /// identity. Vanilla declares neither member; a build that gave the type
    /// value semantics would make the table accept a different first occurrence
    /// than <c>TempHashSet</c> did, so the patch stands down instead.
    /// </summary>
    internal static bool UsesReferenceEquality =>
        typeof(ContiguousPathSet).GetMethod(
            nameof(Equals), BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(object) }, null)?.DeclaringType == typeof(object)
        && typeof(ContiguousPathSet).GetMethod(
            nameof(GetHashCode), BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null)?.DeclaringType == typeof(object)
        && !typeof(IEquatable<ContiguousPathSet>).IsAssignableFrom(typeof(ContiguousPathSet));

    // Stand down rather than throw: an identity-probed table is only equivalent
    // while the type has no value semantics, and leaving vanilla in place is the
    // correct answer, not a failure.
    private static bool Prepare() => UsesReferenceEquality;

    private static MethodBase TargetMethod()
    {
        var target = AccessTools.Method(
            typeof(PathContiguityManager),
            nameof(PathContiguityManager.SearchSetsFrom),
            new[] { typeof(IReadOnlyList<(ContiguousPathSet, IntRect)>), typeof(IntRect?) })
            ?? throw new MissingMethodException(
                typeof(PathContiguityManager).FullName,
                "SearchSetsFrom(IReadOnlyList<(ContiguousPathSet, IntRect)>, IntRect?)");

        if (target.ReturnType != typeof(IEnumerable<(ContiguousPathSet Set, int Iters)>))
        {
            throw new InvalidOperationException(
                "PathContiguityManager.SearchSetsFrom no longer returns the expected sequence; "
                + "skipping the visited-set patch.");
        }

        Applied = true;
        return target;
    }

    private static bool Prefix(
        IReadOnlyList<(ContiguousPathSet Set, IntRect Rect)> searchOrigins,
        IntRect? fromRect,
        ref IEnumerable<(ContiguousPathSet Set, int Iters)> __result)
    {
        __result = SearchSetsFrom(searchOrigins, fromRect);
        return false;
    }

    /// <summary>
    /// The vanilla body, with the pooled visited set replaced. Kept as an
    /// iterator so that argument validation stays deferred to the first
    /// MoveNext exactly as the original does.
    /// </summary>
    private static IEnumerable<(ContiguousPathSet Set, int Iters)> SearchSetsFrom(
        IReadOnlyList<(ContiguousPathSet Set, IntRect Rect)> searchOrigins,
        IntRect? fromRect)
    {
        if (searchOrigins == null)
        {
            throw new ArgumentNullException(nameof(searchOrigins));
        }

        if (searchOrigins.Count == 0)
        {
            throw new ArgumentException("Must specify at least one search origin.", nameof(searchOrigins));
        }

        var scratch = SearchScratch.Rent();
        try
        {
            var queue = scratch.Queue;
            for (var i = 0; i < searchOrigins.Count; i++)
            {
                var (set, rect) = searchOrigins[i];
                if (!fromRect.HasValue || fromRect.Value.IntersectsWith(in rect))
                {
                    // Vanilla enqueues every matching origin unconditionally,
                    // so a repeated origin is yielded twice. Preserve that.
                    scratch.Add(set);
                    queue.Enqueue((set, 0));
                }
            }

            while (queue.Count > 0)
            {
                var (set, iters) = queue.Dequeue();
                yield return (set, iters);

                var adjacent = set.AdjacentSets;
                for (var j = 0; j < adjacent.Count; j++)
                {
                    var next = adjacent[j];
                    if (scratch.Add(next))
                    {
                        queue.Enqueue((next, iters + 1));
                    }
                }
            }
        }
        finally
        {
            scratch.Release();
        }
    }

    /// <summary>
    /// A visited set plus its queue, pooled per thread. Emptying costs one
    /// integer increment: the table stamps each slot with the generation that
    /// wrote it, so every slot from an earlier search reads as free without
    /// being touched. Do not switch back to <see cref="HashSet{T}.Clear"/> based
    /// on a guessed density threshold: a 2026-09-10 low-core client trace
    /// measured 686 ms in that bulk-zero branch over 30 seconds.
    ///
    /// Until 2.2.18 this was a <see cref="HashSet{T}"/> plus a list of the values
    /// added, removed one by one on release. That is proportional to the
    /// traversal rather than to the table, which was the whole point of the
    /// original repair, but it still pays two hash probes per visited set and
    /// both go through the shared <c>__Canon</c> instantiation and
    /// <c>ObjectEqualityComparer</c>'s virtual calls. On the 2026-09-20 host
    /// trace <c>HashSet.AddIfNotPresent</c> was <b>9.2% of the entire resource
    /// source search</b> - 1.18 s, of which 97.5% came from this method - with
    /// another 1.2% in <c>Release</c>. Open addressing over object identity
    /// leaves one probe per set, no comparer dispatch and no removal pass.
    ///
    /// <c>ContiguousPathSet</c> declares neither <c>Equals</c> nor
    /// <c>GetHashCode</c>, so the vanilla <c>TempHashSet</c>, the 2.0.x
    /// <c>HashSet</c> and this table all answer on reference identity.
    /// <see cref="UsesReferenceEquality"/> asserts that at startup and the patch
    /// stays on vanilla if a future build gives the type value semantics. The
    /// table is only ever probed with <see cref="Add"/> and never enumerated, so
    /// nothing observable can depend on its layout and the walk stays
    /// bit-identical.
    /// </summary>
    private sealed class SearchScratch
    {
        private const int MaximumPooled = 8;
        private const int InitialCapacity = 256;

        [ThreadStatic]
        private static Stack<SearchScratch>? _pool;

        /// <summary>Slot keys; meaningful only where the stamp is current.</summary>
        private ContiguousPathSet?[] _keys = new ContiguousPathSet?[InitialCapacity];

        /// <summary>The generation that wrote each slot.</summary>
        private uint[] _stamps = new uint[InitialCapacity];

        /// <summary>Slots written in the current generation.</summary>
        private int _count;

        /// <summary>
        /// Never zero, so a freshly allocated <see cref="_stamps"/> - which is
        /// all zeroes - reads as entirely stale.
        /// </summary>
        private uint _generation = 1;

        public readonly Queue<(ContiguousPathSet Set, int Iters)> Queue = new();

        public static SearchScratch Rent()
        {
            var pool = _pool;
            return pool is { Count: > 0 } ? pool.Pop() : new SearchScratch();
        }

        public bool Add(ContiguousPathSet set)
        {
            var keys = _keys;
            var stamps = _stamps;
            var generation = _generation;
            var mask = keys.Length - 1;

            // Fibonacci mixing: the runtime's identity hash is well distributed
            // in its high bits and weaker in the low ones a mask selects.
            var index = (int)((uint)RuntimeHelpers.GetHashCode(set) * 2654435769u >> 8) & mask;
            while (true)
            {
                if (stamps[index] != generation)
                {
                    stamps[index] = generation;
                    keys[index] = set;

                    // Linear probing degrades sharply past half full. Grow after
                    // the write so this entry survives the rehash.
                    if (++_count * 2 >= keys.Length)
                    {
                        Grow();
                    }

                    return true;
                }

                if (ReferenceEquals(keys[index], set))
                {
                    return false;
                }

                index = (index + 1) & mask;
            }
        }

        /// <summary>
        /// Doubles the table and reinserts the current generation's entries. The
        /// mask changes, so probe chains have to be rebuilt; stale slots are
        /// dropped by construction.
        /// </summary>
        private void Grow()
        {
            var oldKeys = _keys;
            var oldStamps = _stamps;
            var generation = _generation;

            var keys = new ContiguousPathSet?[oldKeys.Length * 2];
            var stamps = new uint[keys.Length];
            var mask = keys.Length - 1;

            for (var i = 0; i < oldKeys.Length; i++)
            {
                if (oldStamps[i] != generation || oldKeys[i] is not { } set)
                {
                    continue;
                }

                var index = (int)((uint)RuntimeHelpers.GetHashCode(set) * 2654435769u >> 8) & mask;
                while (stamps[index] == generation)
                {
                    index = (index + 1) & mask;
                }

                stamps[index] = generation;
                keys[index] = set;
            }

            _keys = keys;
            _stamps = stamps;
        }

        public void Release()
        {
            // Retiring the generation empties the table. The keys are left in
            // place; a slot whose stamp is stale is never read, and holding those
            // references until the slot is reused costs one traversal's worth of
            // ContiguousPathSet on a per-thread scratch that outlives them
            // anyway.
            if (_count > 0)
            {
                _count = 0;
                if (++_generation == 0)
                {
                    // Wrapped: every stamp now has to be made stale explicitly,
                    // once per 4.3 billion searches on this thread.
                    Array.Clear(_stamps, 0, _stamps.Length);
                    _generation = 1;
                }
            }

            Queue.Clear();

            var pool = _pool ??= new Stack<SearchScratch>();
            if (pool.Count < MaximumPooled)
            {
                pool.Push(this);
            }
        }
    }
}
