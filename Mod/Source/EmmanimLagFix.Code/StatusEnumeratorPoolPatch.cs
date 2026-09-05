using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Crew;
using Cosmoteer.Ships.Statuses;
using Cosmoteer.Simulation.HitEffects;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// A part keeps its statuses in a <c>Dictionary&lt;StatusType, IStatusLocationInfo&gt;</c>
/// but exposes it as <c>IReadOnlyDictionary</c>, and the ship's status manager
/// exposes its handler dictionaries as <c>IEnumerable</c>. Enumerating either
/// through the interface boxes the dictionary's struct enumerator, so every
/// damage-resistance, penetration-resistance, crew-blocking and status-effect
/// lookup allocates one. <c>Part</c> itself shows this was unintended: the two
/// loops that read the private field directly use the struct enumerator and
/// allocate nothing, while the three that go through the public property do not.
///
/// A ten-second allocation trace on a 900-ship host attributed 21.24 MiB to
/// <c>Enumerator&lt;StatusType, IStatusLocationInfo&gt;</c> and 9.96 MiB to
/// <c>Enumerator&lt;StatusType, TileStatusHandler&gt;</c> - 19% of everything
/// allocated in the process, and the two largest entries in the whole profile.
/// That matters more than its own cost: this session spent 40% of all CPU in
/// <c>PollGCWorker</c>, because Halfling's parallel workers spin rather than
/// block and so multiply every stop-the-world pause by the worker count.
///
/// The transpiler replaces only the interface <c>GetEnumerator</c> call with a
/// pooled wrapper around the very same struct enumerator, so the iteration, its
/// order and its collection-modified check are the dictionary's own. The
/// wrapper returns itself to a per-thread free list when the foreach disposes
/// it, and anything that is not the expected dictionary - or a call this patch
/// did not rewrite - still gets vanilla's boxed enumerator.
/// </summary>
[HarmonyPatch]
internal static class StatusEnumeratorPoolPatch
{
    /// <summary>
    /// Set only once every target resolved and each one really had a call
    /// rewritten.
    /// </summary>
    internal static bool Applied { get; private set; }

    /// <summary>
    /// The value types enumerated out of a <c>Dictionary&lt;StatusType, T&gt;</c>
    /// through an interface anywhere in the game, and the rewrite each needs.
    /// Keyed by the generic argument of the <c>IEnumerable&lt;T&gt;</c> the call
    /// is made on.
    /// </summary>
    private static readonly Dictionary<Type, MethodInfo> Replacements = new()
    {
        [typeof(IStatusLocationInfo)] = RentValuesMethod(typeof(IStatusLocationInfo)),
        [typeof(TileStatusHandler)] = RentValuesMethod(typeof(TileStatusHandler)),
        [typeof(PartStatusHandler)] = RentValuesMethod(typeof(PartStatusHandler)),
        [typeof(KeyValuePair<StatusType, IStatusLocationInfo>)] =
            RentPairsMethod(typeof(IStatusLocationInfo)),
        [typeof(KeyValuePair<StatusType, StatusLocationValueSource>)] =
            RentPairsMethod(typeof(StatusLocationValueSource)),
    };

    private static MethodInfo RentValuesMethod(Type value) =>
        (AccessTools.Method(typeof(StatusEnumeratorPoolPatch), nameof(RentValues))
            ?? throw new MissingMethodException(
                typeof(StatusEnumeratorPoolPatch).FullName, nameof(RentValues)))
        .MakeGenericMethod(value);

    private static MethodInfo RentPairsMethod(Type value) =>
        (AccessTools.Method(typeof(StatusEnumeratorPoolPatch), nameof(RentPairs))
            ?? throw new MissingMethodException(
                typeof(StatusEnumeratorPoolPatch).FullName, nameof(RentPairs)))
        .MakeGenericMethod(value);

    /// <summary>
    /// Harmony moves a patched method's body into a dynamic method owned by
    /// another type, which removes the runtime's implicit static-constructor
    /// trigger that a call to a static method carried. <c>HitEffectParams</c>
    /// installs its pool's allocator from its own static constructor and its
    /// <c>Alloc</c> is static, so 2.0.32 left
    /// <c>ObjectPool&lt;HitEffectParams&gt;.Allocator</c> null and the first beam
    /// hit after unpausing threw "Must first set the static Allocator property
    /// before calling Alloc()". Running the constructor here restores what the
    /// call site used to guarantee; a type that has none is a no-op, and running
    /// one twice is too. Only static targets can lose the trigger - an instance
    /// method cannot be reached before its type is initialized.
    /// </summary>
    private static bool Prepare()
    {
        foreach (var target in TargetMethods())
        {
            if (target.IsStatic && target.DeclaringType is { } declaring)
            {
                RuntimeHelpers.RunClassConstructor(declaring.TypeHandle);
            }
        }

        return true;
    }

    /// <summary>
    /// Every method in the game that enumerates one of those dictionaries
    /// through an interface. The list is closed: it is the complete set of such
    /// call sites in Cosmoteer.dll, and each entry is required to yield at least
    /// one rewrite, so a shape change disables the patch rather than silently
    /// covering less than it claims.
    /// </summary>
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return Method(typeof(Part), "GetDamageResistance");
        yield return Method(typeof(Part), "GetStatusResistance");
        yield return Method(typeof(Part), "ModifyPenetrationResistance");
        yield return Method(typeof(PartCrew), "IsBlockedByStatuses");
        // Both Alloc overloads enumerate a status dictionary, one of location
        // info and one of value sources.
        foreach (var target in Methods(typeof(HitEffectParams), "Alloc"))
        {
            yield return target;
        }

        // Both PopulateStatuses overloads on each provider, and the tile
        // provider's local function, which is a separate method the transpiler
        // would otherwise never see.
        foreach (var target in Methods(typeof(PartStatusEffectDataProvider), "PopulateStatuses"))
        {
            yield return target;
        }

        foreach (var target in Methods(typeof(TileStatusEffectDataProvider), "PopulateStatuses"))
        {
            yield return target;
        }

        foreach (var target in LocalFunctions(typeof(TileStatusEffectDataProvider), "AddPartLayerStatuses"))
        {
            yield return target;
        }

        yield return Method(typeof(ShipStatusManager), "ClearPlayerSources");

        foreach (var target in LocalFunctions(typeof(ShipStatusManager), "ClearStatuses"))
        {
            yield return target;
        }
    }

    private static MethodBase Method(Type type, string name) =>
        AccessTools.Method(type, name)
        ?? throw new MissingMethodException(type.FullName, name);

    private static List<MethodBase> Methods(Type type, string name)
    {
        var found = AccessTools.GetDeclaredMethods(type)
            .Where(method => method.Name == name)
            .Cast<MethodBase>()
            .ToList();
        if (found.Count == 0)
        {
            throw new MissingMethodException(type.FullName, name);
        }

        return found;
    }

    /// <summary>
    /// A local function compiles to a private method whose mangled name carries
    /// both the containing method and an index that moves whenever the file is
    /// edited, so it can only be matched on the readable part.
    /// </summary>
    private static List<MethodBase> LocalFunctions(Type type, string name)
    {
        var found = AccessTools.GetDeclaredMethods(type)
            .Where(method => method.Name.Contains($"g___{name}|", StringComparison.Ordinal))
            .Cast<MethodBase>()
            .ToList();
        if (found.Count == 0)
        {
            throw new MissingMethodException(type.FullName, name);
        }

        return found;
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        var rewritten = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Callvirt
                && instruction.operand is MethodInfo call
                && call.Name == nameof(IEnumerable<object>.GetEnumerator)
                && call.DeclaringType is { IsGenericType: true } declaring
                && declaring.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && Replacements.TryGetValue(declaring.GetGenericArguments()[0], out var replacement))
            {
                rewritten++;
                var call2 = new CodeInstruction(OpCodes.Call, replacement);
                call2.labels.AddRange(instruction.labels);
                call2.blocks.AddRange(instruction.blocks);
                yield return call2;
                continue;
            }

            yield return instruction;
        }

        if (rewritten == 0)
        {
            throw new InvalidOperationException(
                $"{original.DeclaringType?.FullName}.{original.Name} no longer enumerates a status "
                + "dictionary through an interface; skipping the status enumerator pool patch.");
        }

        Applied = true;
    }

    /// <summary>
    /// Both replacements return <see cref="IEnumerator{T}"/> for the same T as
    /// the call they stand in for and take the same single argument, so the
    /// evaluation stack and the try/finally that disposes the result are
    /// untouched.
    /// </summary>
    private static IEnumerator<TValue> RentValues<TValue>(IEnumerable<TValue> source) =>
        source is Dictionary<StatusType, TValue>.ValueCollection values
            ? ValueEnumerator<TValue>.Rent(values)
            : source.GetEnumerator();

    private static IEnumerator<KeyValuePair<StatusType, TValue>> RentPairs<TValue>(
        IEnumerable<KeyValuePair<StatusType, TValue>> source) =>
        source is Dictionary<StatusType, TValue> dictionary
            ? PairEnumerator<TValue>.Rent(dictionary)
            : source.GetEnumerator();

    /// <summary>
    /// Holds the dictionary's own struct enumerator and forwards to it. The free
    /// list is per thread and chained through the instances themselves, so a
    /// nested enumeration needs no bookkeeping collection; an exhausted list
    /// simply constructs one more.
    /// </summary>
    private sealed class ValueEnumerator<TValue> : IEnumerator<TValue>
    {
        [ThreadStatic]
        private static ValueEnumerator<TValue>? _free;

        private ValueEnumerator<TValue>? _next;
        private Dictionary<StatusType, TValue>.ValueCollection? _source;
        private Dictionary<StatusType, TValue>.ValueCollection.Enumerator _inner;

        internal static IEnumerator<TValue> Rent(Dictionary<StatusType, TValue>.ValueCollection source)
        {
            var rented = _free;
            if (rented is null)
            {
                rented = new ValueEnumerator<TValue>();
            }
            else
            {
                _free = rented._next;
                rented._next = null;
            }

            rented._source = source;
            rented._inner = source.GetEnumerator();
            return rented;
        }

        public TValue Current => _inner.Current;

        object? IEnumerator.Current => _inner.Current;

        public bool MoveNext() => _inner.MoveNext();

        /// <summary>
        /// The struct enumerator cannot be reset in place, so take a fresh one
        /// from the same collection, which is what resetting it means.
        /// </summary>
        public void Reset() => _inner = _source is { } source
            ? source.GetEnumerator()
            : throw new ObjectDisposedException(nameof(ValueEnumerator<TValue>));

        public void Dispose()
        {
            // A second Dispose must not put this on the free list twice, or two
            // enumerations would share one instance.
            if (_source is null)
            {
                return;
            }

            _inner.Dispose();
            _inner = default;
            _source = null;
            _next = _free;
            _free = this;
        }
    }

    private sealed class PairEnumerator<TValue> : IEnumerator<KeyValuePair<StatusType, TValue>>
    {
        [ThreadStatic]
        private static PairEnumerator<TValue>? _free;

        private PairEnumerator<TValue>? _next;
        private Dictionary<StatusType, TValue>? _source;
        private Dictionary<StatusType, TValue>.Enumerator _inner;

        internal static IEnumerator<KeyValuePair<StatusType, TValue>> Rent(
            Dictionary<StatusType, TValue> source)
        {
            var rented = _free;
            if (rented is null)
            {
                rented = new PairEnumerator<TValue>();
            }
            else
            {
                _free = rented._next;
                rented._next = null;
            }

            rented._source = source;
            rented._inner = source.GetEnumerator();
            return rented;
        }

        public KeyValuePair<StatusType, TValue> Current => _inner.Current;

        object IEnumerator.Current => _inner.Current;

        public bool MoveNext() => _inner.MoveNext();

        public void Reset() => _inner = _source is { } source
            ? source.GetEnumerator()
            : throw new ObjectDisposedException(nameof(PairEnumerator<TValue>));

        public void Dispose()
        {
            if (_source is null)
            {
                return;
            }

            _inner.Dispose();
            _inner = default;
            _source = null;
            _next = _free;
            _free = this;
        }
    }
}
