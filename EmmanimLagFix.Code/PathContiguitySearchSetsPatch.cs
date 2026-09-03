using System.Reflection;
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
    /// A visited set plus its queue, pooled per thread. Emptying removes the
    /// values that were actually added instead of zeroing the whole bucket
    /// array, unless the set was filled densely enough that a clear is cheaper.
    /// </summary>
    private sealed class SearchScratch
    {
        private const int MaximumPooled = 8;

        [ThreadStatic]
        private static Stack<SearchScratch>? _pool;

        private readonly HashSet<ContiguousPathSet> _visited = new();
        private readonly List<ContiguousPathSet> _added = new();

        public readonly Queue<(ContiguousPathSet Set, int Iters)> Queue = new();

        public static SearchScratch Rent()
        {
            var pool = _pool;
            return pool is { Count: > 0 } ? pool.Pop() : new SearchScratch();
        }

        public bool Add(ContiguousPathSet set)
        {
            if (!_visited.Add(set))
            {
                return false;
            }

            _added.Add(set);
            return true;
        }

        public void Release()
        {
            if (_added.Count > 0)
            {
                // EnsureCapacity(0) reports the current entry capacity without
                // growing it; the set is non-empty here, so it never allocates.
                if (_added.Count * 4 >= _visited.EnsureCapacity(0))
                {
                    _visited.Clear();
                }
                else
                {
                    for (var i = 0; i < _added.Count; i++)
                    {
                        _visited.Remove(_added[i]);
                    }
                }

                _added.Clear();
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
