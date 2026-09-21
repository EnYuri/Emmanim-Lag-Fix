using System.Reflection;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Networks;
using Cosmoteer.Ships.Networks.Queries;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Resources;
using Halfling.Random;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Replaces the per-push <see cref="TempDictionary"/> in
/// <see cref="SubnetworkResourceSinkQueryResult{TPart,TComponent}.PushResources"/>
/// and the matching pull path with positional scratch buffers.
///
/// Vanilla allocs a pooled dictionary on every network resource transfer,
/// distributes into it keyed by the data struct, then clears it on recycle at
/// O(bucket capacity) - the pooled instance retains the largest network ever
/// pushed on that thread, so even small transfers pay the big clear. In the
/// conversion trace the alloc/recycle/hashing is ~9% of OnConversionTick.
///
/// This patch distributes over a list of positional indices into the result's
/// own <c>_data</c> list instead. The distribution algorithm is a line-for-line
/// port of <see cref="ResourceDistributor"/> (same comparison order, same
/// arithmetic, same <c>Rand.RandomizeOrder</c> call on an index list of the
/// same length) and the per-sink write loop preserves vanilla's dictionary
/// insertion order by tracking first-touch order explicitly - including
/// zero-quantity touches, which vanilla records as dictionary entries and
/// therefore writes. Suppression, cache operation boundaries, and trailing
/// events fire in the same order as vanilla.
///
/// Both known closed instantiations (live ships and blueprint queries) are
/// patched explicitly; any other instantiation falls back to vanilla.
/// </summary>
[HarmonyPatch]
internal static class NetworkResourceTransferPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(
            typeof(SubnetworkResourceSinkQueryResult<Part, PartComponent>), "PushResources")
            ?? throw new MissingMethodException("SubnetworkResourceSinkQueryResult<Part,PartComponent>", "PushResources");
        yield return AccessTools.DeclaredMethod(
            typeof(SubnetworkResourceSinkQueryResult<BlueprintPart, BlueprintPartComponent>), "PushResources")
            ?? throw new MissingMethodException("SubnetworkResourceSinkQueryResult<BlueprintPart,BlueprintPartComponent>", "PushResources");
        yield return AccessTools.DeclaredMethod(
            typeof(SubnetworkResourceSourceQueryResult<Part, PartComponent>), "PullResources")
            ?? throw new MissingMethodException("SubnetworkResourceSourceQueryResult<Part,PartComponent>", "PullResources");
        yield return AccessTools.DeclaredMethod(
            typeof(SubnetworkResourceSourceQueryResult<BlueprintPart, BlueprintPartComponent>), "PullResources")
            ?? throw new MissingMethodException("SubnetworkResourceSourceQueryResult<BlueprintPart,BlueprintPartComponent>", "PullResources");
    }

    private static bool Prefix(
        object __instance, MethodBase __originalMethod,
        int amount, MultiResourceStorageMode mode, Rand rand, ref int __result)
    {
        if (__originalMethod.Name == "PushResources")
        {
            switch (__instance)
            {
                case SubnetworkResourceSinkQueryResult<Part, PartComponent> sink:
                    __result = PushCore(sink, amount, mode, rand);
                    return false;
                case SubnetworkResourceSinkQueryResult<BlueprintPart, BlueprintPartComponent> sinkBp:
                    __result = PushCore(sinkBp, amount, mode, rand);
                    return false;
            }
            return true;
        }
        switch (__instance)
        {
            case SubnetworkResourceSourceQueryResult<Part, PartComponent> source:
                __result = PullCore(source, amount, mode, rand);
                return false;
            case SubnetworkResourceSourceQueryResult<BlueprintPart, BlueprintPartComponent> sourceBp:
                __result = PullCore(sourceBp, amount, mode, rand);
                return false;
        }
        return true;
    }

    private static int PushCore<TPart, TComponent>(
        SubnetworkResourceSinkQueryResult<TPart, TComponent> self,
        int amount, MultiResourceStorageMode mode, Rand rand)
        where TPart : CommonBasePart where TComponent : class, IPartComponent<TPart>
    {
        if (amount < 0)
            throw new NotSupportedException("Removing resources from sinks is not supported.");
        if (amount == 0)
            return 0;

        var data = self._data;
        var s = AcquireScratch();
        try
        {
            s.Begin(data.Count);
            int leftover = SinkDistribution.Distribute(
                s.Indices, amount, mode,
                (i, kind) => kind switch
                {
                    SinkDistribution.TotalCapacity => data[i].TotalCapacity,
                    SinkDistribution.RemainingCapacity => data[i].AvailableCapacity,
                    _ => data[i].DataSource.Resources,
                },
                rand, s);
            if (s.Order.Count == 0)
                return leftover;

            self._suppressEvents = true;
            self._cache.OnResourceOperationStart();
            foreach (int index in s.Order)
            {
                var sink = data[index].DataSource;
                int amount2 = s.Amounts[index];
                if (sink is ISubnetworkResourceSinkQueryResult nested)
                    nested.PushResources(amount2, mode, rand);
                else
                    sink.AddResources(amount2);
                self.UpdateDataFor(sink);
            }
            self._suppressEvents = false;
            self._cache.OnResourceOperationEnd();
            SinkFields<TPart, TComponent>.AvailableCapacityChanged(self)?.Invoke(self, EventArgs.Empty);
            SinkFields<TPart, TComponent>.TotalCapacityChanged(self)?.Invoke(self, EventArgs.Empty);
            return leftover;
        }
        finally
        {
            ReleaseScratch();
        }
    }

    private static int PullCore<TPart, TComponent>(
        SubnetworkResourceSourceQueryResult<TPart, TComponent> self,
        int amount, MultiResourceStorageMode mode, Rand rand)
        where TPart : CommonBasePart where TComponent : class, IPartComponent<TPart>
    {
        if (amount < 0)
            throw new NotSupportedException("Adding resources to sources is not supported.");
        if (amount == 0)
            return 0;

        var data = self._data;
        var s = AcquireScratch();
        try
        {
            s.Begin(data.Count);
            int leftover = SinkDistribution.Distribute(
                s.Indices, -amount, mode,
                (i, kind) => kind switch
                {
                    SinkDistribution.TotalCapacity => data[i].MaxResources,
                    // Vanilla _GetSourceValue reports remaining capacity as
                    // Max - cached AvailableResources, and "Resources" as the
                    // cached field - not a live DataSource read.
                    SinkDistribution.RemainingCapacity => data[i].MaxResources - data[i].AvailableResources,
                    _ => data[i].AvailableResources,
                },
                rand, s);
            if (s.Order.Count == 0)
                return leftover;

            self._suppressEvents = true;
            self._cache.OnResourceOperationStart();
            foreach (int index in s.Order)
            {
                var source = data[index].DataSource;
                int amount2 = s.Amounts[index];
                if (source is ISubnetworkResourceSourceQueryResult nested)
                    nested.PullResources(amount2, mode, rand);
                else
                    source.RemoveResources(amount2);
                self.UpdateDataFor(source);
            }
            self._suppressEvents = false;
            self._cache.OnResourceOperationEnd();
            SourceFields<TPart, TComponent>.AvailableResourcesChanged(self)?.Invoke(self, EventArgs.Empty);
            return leftover;
        }
        finally
        {
            ReleaseScratch();
        }
    }

    /// <summary>
    /// Field-like event backing fields are compiler-generated and therefore not
    /// publicized; resolved once per closed instantiation.
    /// </summary>
    private static class SinkFields<TPart, TComponent>
        where TPart : CommonBasePart where TComponent : class, IPartComponent<TPart>
    {
        public static readonly AccessTools.FieldRef<
            SubnetworkResourceSinkQueryResult<TPart, TComponent>, EventHandler<EventArgs>?>
            AvailableCapacityChanged = AccessTools.FieldRefAccess<
                SubnetworkResourceSinkQueryResult<TPart, TComponent>, EventHandler<EventArgs>?>(
                "AvailableCapacityChanged");
        public static readonly AccessTools.FieldRef<
            SubnetworkResourceSinkQueryResult<TPart, TComponent>, EventHandler<EventArgs>?>
            TotalCapacityChanged = AccessTools.FieldRefAccess<
                SubnetworkResourceSinkQueryResult<TPart, TComponent>, EventHandler<EventArgs>?>(
                "TotalCapacityChanged");
    }

    private static class SourceFields<TPart, TComponent>
        where TPart : CommonBasePart where TComponent : class, IPartComponent<TPart>
    {
        public static readonly AccessTools.FieldRef<
            SubnetworkResourceSourceQueryResult<TPart, TComponent>, EventHandler<EventArgs>?>
            AvailableResourcesChanged = AccessTools.FieldRefAccess<
                SubnetworkResourceSourceQueryResult<TPart, TComponent>, EventHandler<EventArgs>?>(
                "AvailableResourcesChanged");
    }

    // Nested pushes recurse while the outer push is mid-enumeration, so scratch
    // is a per-thread stack rather than a single buffer.
    [ThreadStatic]
    private static TransferScratch[]? s_scratch;
    [ThreadStatic]
    private static int s_scratchDepth;

    private static TransferScratch AcquireScratch()
    {
        var arr = s_scratch ??= new TransferScratch[4];
        if (s_scratchDepth == arr.Length)
        {
            var grown = new TransferScratch[arr.Length * 2];
            Array.Copy(arr, grown, arr.Length);
            s_scratch = arr = grown;
        }
        return arr[s_scratchDepth++] ??= new TransferScratch();
    }

    private static void ReleaseScratch() => s_scratchDepth--;
}

/// <summary>
/// Per-thread distribution scratch. Generation-stamped instead of cleared, so
/// reuse costs O(touched sinks) instead of O(largest network seen).
/// </summary>
internal sealed class TransferScratch
{
    public int[] Amounts = new int[64];
    public int[] Stamps = new int[64];
    public int Generation;
    public readonly List<int> Indices = new();
    public readonly List<int> Order = new();
    public readonly List<int> SortBuffer = new();
    public readonly List<int> ShuffleBuffer = new();
    public readonly List<int> Eligible = new();

    public void Begin(int count)
    {
        if (Stamps.Length < count)
        {
            int size = Math.Max(count, Stamps.Length * 2);
            Amounts = new int[size];
            Stamps = new int[size];
        }
        unchecked { Generation++; }
        if (Generation <= 0)
        {
            Array.Clear(Stamps);
            Generation = 1;
        }
        Order.Clear();
        Indices.Clear();
        for (int i = 0; i < count; i++)
            Indices.Add(i);
    }

    public int Read(int index) => Stamps[index] == Generation ? Amounts[index] : 0;

    public void AddQuantity(int index, int quantity, ref int total)
    {
        if (Stamps[index] != Generation)
        {
            Stamps[index] = Generation;
            Amounts[index] = 0;
            Order.Add(index);
        }
        Amounts[index] += quantity;
        total -= quantity;
    }
}

/// <summary>
/// Positional port of <see cref="ResourceDistributor"/> over container indices.
/// Every comparison, quantity computation, and iteration bound matches vanilla
/// so the emitted per-container amounts and first-touch order are identical.
/// </summary>
internal static class SinkDistribution
{
    // Mirrors ResourceDistributor.ContainerValue declaration order.
    internal const int TotalCapacity = 0;
    internal const int RemainingCapacity = 1;
    internal const int Resources = 2;

    internal static int Distribute(
        List<int> containers, int signedDelta, MultiResourceStorageMode mode,
        Func<int, int, int> getValue, Rand rand, TransferScratch s)
    {
        return mode switch
        {
            MultiResourceStorageMode.InOrder => signedDelta >= 0
                ? InOrder(signedDelta, containers, RemainingCapacity, getValue, s)
                : InOrder(-signedDelta, containers, Resources, getValue, s),
            MultiResourceStorageMode.InReverseOrder => signedDelta >= 0
                ? InReverseOrder(signedDelta, containers, RemainingCapacity, getValue, s)
                : InReverseOrder(-signedDelta, containers, Resources, getValue, s),
            MultiResourceStorageMode.PrioritizeMostEmptyCapacity => signedDelta >= 0
                ? MostEmptyAdditive(signedDelta, containers, getValue, s)
                : MostEmptySubtractive(-signedDelta, containers, getValue, s),
            MultiResourceStorageMode.PrioritizeLeastEmptyCapacity => signedDelta >= 0
                ? LeastEmptyAdditive(signedDelta, containers, getValue, s)
                : LeastEmptySubtractive(-signedDelta, containers, getValue, s),
            MultiResourceStorageMode.PrioritizeMostResources => signedDelta >= 0
                ? MostResourcesAdditive(signedDelta, containers, getValue, s)
                : MostResourcesSubtractive(-signedDelta, containers, getValue, s),
            MultiResourceStorageMode.PrioritizeLeastResources => signedDelta >= 0
                ? LeastResourcesAdditive(signedDelta, containers, getValue, s)
                : LeastResourcesSubtractive(-signedDelta, containers, getValue, s),
            MultiResourceStorageMode.DistributeEvenly => signedDelta >= 0
                ? Evenly(signedDelta, containers.Count, containers, RemainingCapacity, getValue, s)
                : Evenly(-signedDelta, containers.Count, containers, Resources, getValue, s),
            MultiResourceStorageMode.DistributeRandomly => signedDelta >= 0
                ? Randomly(signedDelta, containers, RemainingCapacity, getValue, rand, s)
                : Randomly(-signedDelta, containers, Resources, getValue, rand, s),
            MultiResourceStorageMode.DistributeProportionallyByAvailable => signedDelta >= 0
                ? Proportionally(signedDelta, containers, containers.Count, RemainingCapacity, RemainingCapacity, getValue, s)
                : Proportionally(-signedDelta, containers, containers.Count, Resources, Resources, getValue, s),
            MultiResourceStorageMode.DistributeProportionallyByCapacity => signedDelta >= 0
                ? Proportionally(signedDelta, containers, containers.Count, RemainingCapacity, TotalCapacity, getValue, s)
                : Proportionally(-signedDelta, containers, containers.Count, Resources, TotalCapacity, getValue, s),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    private static int InOrder(
        int total, List<int> containers, int capacityType, Func<int, int, int> getValue, TransferScratch s)
    {
        for (int i = 0; i < containers.Count; i++)
        {
            int container = containers[i];
            s.AddQuantity(container, Math.Min(total, getValue(container, capacityType)), ref total);
            if (total <= 0)
                break;
        }
        return total;
    }

    private static int InReverseOrder(
        int total, List<int> containers, int capacityType, Func<int, int, int> getValue, TransferScratch s)
    {
        for (int i = containers.Count - 1; i >= 0; i--)
        {
            int container = containers[i];
            s.AddQuantity(container, Math.Min(total, getValue(container, capacityType)), ref total);
            if (total <= 0)
                break;
        }
        return total;
    }

    private static int MostEmptyAdditive(
        int total, List<int> containers, Func<int, int, int> getValue, TransferScratch s)
    {
        var sorted = SortDescending(s.SortBuffer, containers, RemainingCapacity, getValue);
        int num = int.MaxValue;
        for (int i = 0; i < sorted.Count; i++)
        {
            int num2 = getValue(sorted[i], RemainingCapacity);
            if (num2 < num)
            {
                total = Evenly(total, i, sorted, RemainingCapacity, getValue, s, num - num2);
                num = num2;
            }
        }
        return Evenly(total, sorted.Count, sorted, RemainingCapacity, getValue, s);
    }

    private static int LeastEmptyAdditive(
        int total, List<int> containers, Func<int, int, int> getValue, TransferScratch s)
    {
        var sorted = SortAscending(s.SortBuffer, containers, RemainingCapacity, getValue);
        return InOrder(total, sorted, RemainingCapacity, getValue, s);
    }

    private static int MostEmptySubtractive(
        int total, List<int> containers, Func<int, int, int> getValue, TransferScratch s)
    {
        var sorted = SortDescending(s.SortBuffer, containers, RemainingCapacity, getValue);
        return InOrder(total, sorted, Resources, getValue, s);
    }

    private static int LeastEmptySubtractive(
        int total, List<int> containers, Func<int, int, int> getValue, TransferScratch s)
    {
        var sorted = SortAscending(s.SortBuffer, containers, RemainingCapacity, getValue);
        int num = -1;
        for (int i = 0; i < sorted.Count; i++)
        {
            int num2 = getValue(sorted[i], RemainingCapacity);
            if (num2 > num)
            {
                total = Evenly(total, i, sorted, Resources, getValue, s, num2 - num);
                num = num2;
            }
        }
        return Evenly(total, sorted.Count, sorted, Resources, getValue, s);
    }

    private static int MostResourcesAdditive(
        int total, List<int> containers, Func<int, int, int> getValue, TransferScratch s)
    {
        var sorted = SortDescending(s.SortBuffer, containers, Resources, getValue);
        return InOrder(total, sorted, RemainingCapacity, getValue, s);
    }

    private static int LeastResourcesAdditive(
        int total, List<int> containers, Func<int, int, int> getValue, TransferScratch s)
    {
        var sorted = SortAscending(s.SortBuffer, containers, Resources, getValue);
        int num = -1;
        for (int i = 0; i < sorted.Count; i++)
        {
            int num2 = getValue(sorted[i], RemainingCapacity);
            if (num2 > num)
            {
                // Vanilla distributes the additive least-resources pass by the
                // Resources value - quirk preserved verbatim.
                total = Evenly(total, i, sorted, Resources, getValue, s, num2 - num);
                num = num2;
            }
        }
        return Evenly(total, sorted.Count, sorted, Resources, getValue, s);
    }

    private static int MostResourcesSubtractive(
        int total, List<int> containers, Func<int, int, int> getValue, TransferScratch s)
    {
        var sorted = SortAscending(s.SortBuffer, containers, Resources, getValue);
        int num = int.MaxValue;
        for (int i = 0; i < sorted.Count; i++)
        {
            int num2 = getValue(sorted[i], Resources);
            if (num2 < num)
            {
                total = Evenly(total, i, sorted, Resources, getValue, s, num - num2);
                num = num2;
            }
        }
        return Evenly(total, sorted.Count, sorted, Resources, getValue, s);
    }

    private static int LeastResourcesSubtractive(
        int total, List<int> containers, Func<int, int, int> getValue, TransferScratch s)
    {
        var sorted = SortDescending(s.SortBuffer, containers, Resources, getValue);
        return InOrder(total, sorted, Resources, getValue, s);
    }

    private static int Evenly(
        int total, int containerCount, List<int> containers, int capacityType,
        Func<int, int, int> getValue, TransferScratch s, int maxPerStorage = int.MaxValue)
    {
        if (containerCount <= 0)
            return total;
        var eligible = s.Eligible;
        eligible.Clear();
        for (int i = 0; i < containerCount; i++)
        {
            int container = containers[i];
            if (getValue(container, capacityType) > 0)
                eligible.Add(container);
        }
        while (total > 0 && eligible.Count > 0)
        {
            if (total > eligible.Count)
            {
                int value = total / eligible.Count;
                for (int j = 0; j < eligible.Count; j++)
                {
                    int container = eligible[j];
                    int current = s.Read(container);
                    int num = Math.Min(maxPerStorage, getValue(container, capacityType));
                    int quantity = Math.Min(value, num - current);
                    s.AddQuantity(container, quantity, ref total);
                    if (current >= num)
                        eligible.RemoveAt(j--);
                }
                continue;
            }
            // Vanilla bounds the +1 round-robin by the unfiltered container
            // count while reading the filtered list - preserved verbatim,
            // including the potential out-of-range on a short eligible list.
            for (int k = 0; k < containers.Count; k++)
            {
                if (total <= 0)
                    break;
                s.AddQuantity(eligible[k], 1, ref total);
            }
        }
        return total;
    }

    private static int Randomly(
        int total, List<int> containers, int capacityType,
        Func<int, int, int> getValue, Rand rand, TransferScratch s)
    {
        var shuffled = s.ShuffleBuffer;
        shuffled.Clear();
        shuffled.AddRange(containers);
        // RandomizeOrder draws depend only on index/count, so shuffling the
        // index list consumes the identical Rand stream.
        rand.RandomizeOrder((IList<int>)shuffled);
        return InOrder(total, shuffled, capacityType, getValue, s);
    }

    private static int Proportionally(
        int total, List<int> containers, int containerCount, int capacityType, int proportionType,
        Func<int, int, int> getValue, TransferScratch s)
    {
        if (containerCount <= 0)
            return total;
        while (total > 0)
        {
            long num = 0L;
            for (int i = 0; i < containerCount; i++)
                num += getValue(containers[i], proportionType);
            int num2 = total;
            for (int j = 0; j < containerCount; j++)
            {
                int container = containers[j];
                int num3 = getValue(container, proportionType);
                if (num3 > 0)
                {
                    int value = getValue(container, capacityType);
                    float num4 = (float)num3 / (float)num;
                    int num5 = Math.Min((int)MathF.Ceiling(total * num4), value);
                    if (num5 > 0)
                        s.AddQuantity(container, num5, ref total);
                    num -= num3;
                    if (total <= 0 || num <= 0)
                        return total;
                }
            }
            if (total >= num2)
                break;
        }
        return total;
    }

    // List<T>.Sort decides purely on comparison outcomes, so sorting indices by
    // the same keys applies the identical permutation vanilla gets sorting the
    // data list itself - including unstable-sort artifacts on equal keys.
    private static List<int> SortAscending(
        List<int> buffer, List<int> containers, int kind, Func<int, int, int> getValue)
    {
        buffer.Clear();
        buffer.AddRange(containers);
        buffer.Sort((a, b) => getValue(a, kind).CompareTo(getValue(b, kind)));
        return buffer;
    }

    private static List<int> SortDescending(
        List<int> buffer, List<int> containers, int kind, Func<int, int, int> getValue)
    {
        buffer.Clear();
        buffer.AddRange(containers);
        buffer.Sort((a, b) => getValue(b, kind).CompareTo(getValue(a, kind)));
        return buffer;
    }
}
