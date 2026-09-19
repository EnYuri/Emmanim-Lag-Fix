using System.Buffers;
using System.Globalization;
using System.Reflection;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Simulation;
using Halfling.Logging;
using Halfling.Scene2D;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Orders each parallel fixed-update bucket's dispatch longest-first so a
/// megaship's work starts while the pool still has idle workers, instead of
/// landing last and becoming the tail the whole bucket waits on.
///
/// <c>SimRoot.ParallelFixedUpdate</c> hands <c>FastParallel.For</c> a slice of
/// the bucket's item array, and the pool claims batches in array order. In
/// registration order the item sequence is arbitrary, so an 11k-part ship's
/// manager lands wherever it was added. Diagnostics (<c>lg</c>/<c>fpw</c>)
/// measured such calls at 40-90 ms: the manager's own nested
/// <c>FastParallel.For</c> (crew passes, sink jobs, status diffusion) goes out
/// while every worker is mid-item, its claimed batches stall behind the
/// outer pass, and the manager call - billed as one bucket item - stretches
/// into the straggler that holds the bucket open.
///
/// Starting the expensive items first attacks the mechanism rather than the
/// symptom: a megaship's nested dispatch reaches the pool while workers are
/// still free, its inner work genuinely spreads, and its call shrinks toward
/// its parallelizable floor. Smaller ships' calls fill the remainder of the
/// pass underneath it - the classical longest-processing-time schedule.
///
/// Ordering is semantics-neutral here. Vanilla already runs these items in
/// arbitrary worker-claim order, so nothing in a synced game may depend on
/// which item finishes first; deterministic effects go through
/// <c>EnqueueDeterministic(ship.UniqueID, ...)</c> and are re-ordered by ship
/// id regardless. The sort key (part count) is itself deterministic, so both
/// peers derive the identical order. The slice is copied, sorted in place for
/// the duration of the dispatch, and restored by the postfix - no code
/// outside the <c>For</c> ever observes the reordered array, including the
/// integrity-hash pass that runs after the bucket.
/// </summary>
[HarmonyPatch]
internal static class ParallelBucketLptPatch
{
    /// <summary>Why the patch is inert, for the smoke test's failure message.</summary>
    internal static string? FailureReason;

    /// <summary>Whether the ordering is active; the override file can turn it off.</summary>
    internal static readonly bool Enabled;

    /// <summary>Times the ordering ran, and items reordered, for the counters line.</summary>
    internal static long SortCount;
    internal static long ItemCount;

    static ParallelBucketLptPatch()
    {
        var enabled = true;
        try
        {
            var path = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
                "..",
                "parallel-bucket-lpt.txt"));
            if (File.Exists(path)
                && int.TryParse(
                    File.ReadAllText(path).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var configured)
                && configured <= 0)
            {
                enabled = false;
                FailureReason = "the parallel-bucket-lpt.txt override disables longest-first ordering";
            }
        }
        catch (Exception)
        {
            // An unreadable override must not change behaviour; stay enabled.
        }

        Enabled = enabled;
    }

    private static bool Prepare()
    {
        if (Enabled)
        {
            return true;
        }

        try
        {
            Logger.Log(
                "[EmmanimLagFix] Parallel bucket longest-first ordering left off: "
                + (FailureReason ?? "no reason recorded") + ".");
        }
        catch (Exception)
        {
            // The logger is not initialized in the standalone smoke host.
        }

        return false;
    }

    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(
            typeof(SimRoot),
            "ParallelFixedUpdate",
            new[] { typeof(int), typeof(IFixedUpdateableSceneObject[]), typeof(int), typeof(int) })
        ?? throw new MissingMethodException(typeof(SimRoot).FullName, "ParallelFixedUpdate");

    /// <summary>
    /// Cost proxy for one bucket item: the owning ship's part count. Covers
    /// the three item shapes in the parallel buckets - per-ship managers,
    /// per-part components and move commands - and returns 0 for anything
    /// else, which sorts last among equals without disturbing their order.
    /// </summary>
    internal static int CostOf(object item) => item switch
    {
        ShipComponent component => component.Ship?.Parts.Count ?? 0,
        PartComponent component => component.Part?.Ship?.Parts.Count ?? 0,
        Command command when command.HasShip => command.Ship.Parts.Count,
        _ => 0,
    };

    /// <summary>Scratch buffers for one ordering, rented and returned per call.</summary>
    internal struct Scratch
    {
        internal IFixedUpdateableSceneObject[] Saved;
        internal IFixedUpdateableSceneObject[] Items;
        internal int[] Keys;
        internal int Count;
    }

    /// <summary>
    /// Saves the slice, then rewrites it in descending cost order. The sort
    /// is deterministic - the same input array always produces the same
    /// output - so both peers compute identical batch boundaries. Buffers
    /// come from the shared array pool: allocating them fresh measured as
    /// ~4% of the game's total allocation rate.
    /// </summary>
    private static void Prefix(
        IFixedUpdateableSceneObject[] array,
        int startIndex,
        int count,
        out Scratch __state)
    {
        __state = default;
        if (count <= 1 || array == null || startIndex < 0 || startIndex + count > array.Length)
        {
            return;
        }

        var saved = ArrayPool<IFixedUpdateableSceneObject>.Shared.Rent(count);
        var items = ArrayPool<IFixedUpdateableSceneObject>.Shared.Rent(count);
        var keys = ArrayPool<int>.Shared.Rent(count);
        Array.Copy(array, startIndex, saved, 0, count);
        for (var i = 0; i < count; i++)
        {
            items[i] = saved[i];
            keys[i] = CostOf(saved[i]);
        }

        Array.Sort(keys, items, 0, count, Descending);
        Array.Copy(items, 0, array, startIndex, count);
        __state = new Scratch
        {
            Saved = saved,
            Items = items,
            Keys = keys,
            Count = count,
        };

        Interlocked.Increment(ref SortCount);
        Interlocked.Add(ref ItemCount, count);
    }

    /// <summary>Restores the slice so the dispatch order leaves no trace.</summary>
    private static void Postfix(
        IFixedUpdateableSceneObject[] array,
        int startIndex,
        Scratch __state)
    {
        if (__state.Count > 0)
        {
            Array.Copy(__state.Saved, 0, array, startIndex, __state.Count);
            ArrayPool<IFixedUpdateableSceneObject>.Shared.Return(__state.Saved);
            ArrayPool<IFixedUpdateableSceneObject>.Shared.Return(__state.Items);
            ArrayPool<int>.Shared.Return(__state.Keys);
        }
    }

    /// <summary>
    /// Rewrites <paramref name="array"/>'s slice in descending cost order. The
    /// keyed sort is deterministic for a given input, which is all the peers
    /// need: equal-cost items keep whatever order the algorithm produces for
    /// the identical input array on both sides.
    /// </summary>
    internal static void SortSlice<T>(T[] array, int startIndex, int count, Func<T, int> cost)
    {
        var keys = new int[count];
        var items = new T[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = array[startIndex + i];
            keys[i] = cost(items[i]);
        }

        Array.Sort(keys, items, Descending);
        Array.Copy(items, 0, array, startIndex, count);
    }

    private static readonly IComparer<int> Descending =
        Comparer<int>.Create(static (a, b) => b.CompareTo(a));

    internal static string Counters() =>
        Enabled
        ? Volatile.Read(ref SortCount).ToString(CultureInfo.InvariantCulture)
        + "/" + Volatile.Read(ref ItemCount).ToString(CultureInfo.InvariantCulture)
        : "off";
}
