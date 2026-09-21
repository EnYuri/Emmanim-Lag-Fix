using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Statuses;
using Cosmoteer.Ships.Statuses.Subhandlers;
using Halfling;
using Halfling.Geometry;
using Halfling.Pooling;
using Halfling.Timing;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Always-on, exact vanilla heat-rule specialization of tile modulation.
///
/// When the status type matches the exact vanilla heat shape and the store is
/// the tile handler's <c>StatusStore&lt;IntVector2&gt;</c>, the transpiler
/// redirects the whole
/// <c>_ModulateStatusValues</c> call to <see cref="SpecializedLoop"/>: a
/// bit-exact rewrite that removes provably-dead per-status work (dictionary
/// clear, buff/context lookups, modulation struct and dispatch, changed-status
/// list lookup) while preserving iteration order, event order and the
/// resistance read. Any shape deviation falls back into the vanilla body,
/// where the same guard still skips the individual <c>PopulateStatuses</c> and
/// <c>GetBuffs</c> calls via <see cref="PopulateCore"/>/<see cref="BuffsCore"/>.
///
/// <c>ValueModulationData.Buffs</c> is provably unread for this shape - the
/// single <c>Constant</c> modulator's <c>GetModifierValue</c> returns its
/// literal, <c>Add</c> uses only <c>Value</c>, <c>ValueResistance</c> and
/// <c>DeltaTime</c>, and <c>StatusFilter</c> being null is part of the guard.
/// <c>ValueResistance</c> is read, so the resistance lookup is never skipped.
/// </summary>
[HarmonyPatch]
internal static class HeatModulationContextSkipPatch
{
    internal static bool Applied { get; private set; }
    internal static bool LoopInstalled { get; private set; }

    private static readonly Func<StatusHandler<IntVector2>, IStatusStore<IntVector2>> _store =
        AccessTools.MethodDelegate<Func<StatusHandler<IntVector2>, IStatusStore<IntVector2>>>(
            AccessTools.PropertyGetter(typeof(StatusHandler<IntVector2>), "Store")!);

    private delegate void StatusModifiedDelegate(StatusHandler<IntVector2> self,
        Status<IntVector2> status, float oldValue, StatusList<IntVector2> list, bool silent);

    private static readonly StatusModifiedDelegate _onModified = AccessTools.MethodDelegate<StatusModifiedDelegate>(
        AccessTools.Method(typeof(StatusHandler<IntVector2>), "OnStatusValueModified",
            new[] { typeof(Status<IntVector2>), typeof(float), typeof(StatusList<IntVector2>), typeof(bool) })!);

    // Per-invocation part -> clamped resistance cache. Pass 1 of the loop is
    // read-only for every input GetStatusResistance observes (part.Statuses
    // membership, DamageFraction, buffs): Status.Value is a plain auto-property
    // and the dirty/event notifications all run in pass 2, so a part's
    // resistance cannot change between reads inside one call. A re-entrant
    // call (a modded StatusValueModified subscriber could in theory trigger
    // one) falls back to a fresh local map rather than sharing scratch state.
    [ThreadStatic]
    private static Dictionary<Part, float>? s_resistanceCache;

    [ThreadStatic]
    private static bool s_resistanceCacheInUse;

    private static MethodBase TargetMethod() => StatusModulationListCapacityPatch.FindTarget(typeof(IntVector2));

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var populateTarget = AccessTools.Method(typeof(IStatusEffectDataProvider<IntVector2>),
            "PopulateStatuses", new[] { typeof(Status<IntVector2>), typeof(Ship),
                typeof(Dictionary<StatusType, IStatusLocationInfo>) })!;
        var buffsTarget = AccessTools.Method(typeof(IStatusEffectDataProvider<IntVector2>),
            "GetBuffs", new[] { typeof(Status<IntVector2>), typeof(Ship) })!;
        var populateHelper = AccessTools.Method(typeof(HeatModulationContextSkipPatch), nameof(Populate))!;
        var buffsHelper = AccessTools.Method(typeof(HeatModulationContextSkipPatch), nameof(Buffs))!;
        var rewritten = new List<CodeInstruction>();
        var populates = 0;
        var buffs = 0;
        foreach (var instruction in instructions)
        {
            MethodBase? helper = instruction.Calls(populateTarget) ? populateHelper
                : instruction.Calls(buffsTarget) ? buffsHelper
                : null;
            if (helper != null)
            {
                if (helper == populateHelper) populates++;
                else buffs++;
                var load = new CodeInstruction(OpCodes.Ldarg_0);
                load.labels.AddRange(instruction.labels);
                load.blocks.AddRange(instruction.blocks);
                rewritten.Add(load);
                rewritten.Add(new CodeInstruction(OpCodes.Call, helper));
            }
            else rewritten.Add(instruction);
        }
        if (populates != 1 || buffs != 1) throw new InvalidOperationException(
            $"Expected one PopulateStatuses and one GetBuffs call in tile modulation, "
            + $"found {populates} and {buffs}.");

        // Whole-loop specialization: the local function receives its captured
        // variables as a byref display struct; resolve the FixedUpdater field so
        // the specialized path reads the exact same interval vanilla would.
        var parameters = original.GetParameters();
        if (parameters.Length != 1 || !parameters[0].ParameterType.IsByRef)
            throw new InvalidOperationException(
                $"{original} no longer takes a single byref display-struct parameter.");
        var updaterField = AccessTools.Field(parameters[0].ParameterType.GetElementType()!, "fixedUpdater")
            ?? throw new InvalidOperationException(
                $"{parameters[0].ParameterType.GetElementType()} no longer carries a fixedUpdater field.");
        var guard = AccessTools.Method(typeof(HeatModulationContextSkipPatch), nameof(LoopGuard))!;
        var loop = AccessTools.Method(typeof(HeatModulationContextSkipPatch), nameof(SpecializedLoop))!;

        var specialized = generator.DefineLabel();
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Call, guard);
        yield return new CodeInstruction(OpCodes.Brtrue, specialized);
        foreach (var instruction in rewritten) yield return instruction;
        var tail = new CodeInstruction(OpCodes.Ldarg_0);
        tail.labels.Add(specialized);
        yield return tail;
        yield return new CodeInstruction(OpCodes.Ldarg_1);
        yield return new CodeInstruction(OpCodes.Ldfld, updaterField);
        yield return new CodeInstruction(OpCodes.Call, loop);
        yield return new CodeInstruction(OpCodes.Ret);
        LoopInstalled = true;
        Applied = true;
    }

    internal static bool CanSkip(StatusType type, object provider, int dictionaryCount)
    {
        if (dictionaryCount != 0 || provider.GetType() != typeof(TileStatusEffectDataProvider)
            || type.GetType() != typeof(StatusType) || type.ID.ToString() != "cosmoteer.heat"
            || type.Layer != StatusLayer.Tile || type.ValueModulators is not { } rules
            || rules.GetType() != typeof(MultiValueModulatorRules)
            || rules.Modulators is not { Length: 1 } modulators
            || modulators[0] is not ConstantValueModulatorRules constant
            || constant.GetType() != typeof(ConstantValueModulatorRules)) return false;
        // Exact current vanilla shape, not a generic "no filter" shortcut.
        return constant.StatusFilter == null && constant.Value == 1f
            && constant.ModificationMode == SimpleValueModificationMode.Subtract
            && constant.ScaleByDeltaTime && constant.OutputValueType == StatusValueType.Raw
            && constant.AffectedValueRange.Min == 0f
            && float.IsPositiveInfinity(constant.AffectedValueRange.Max);
    }

    private static void Populate(IStatusEffectDataProvider<IntVector2> provider,
        Status<IntVector2> status, Ship ship, Dictionary<StatusType, IStatusLocationInfo> dictionary,
        StatusHandler<IntVector2> handler)
        => PopulateCore(handler.StatusType, provider, status, ship, dictionary);

    /// <summary>
    /// Returns null instead of the part's buff dictionary when the modulator
    /// shape provably never reads it - the dictionary is cleared immediately
    /// before this call each iteration, so the count argument is a literal.
    /// </summary>
    private static IReadOnlyDictionary<Cosmoteer.Ships.Buffs.BuffType, float>? Buffs(
        IStatusEffectDataProvider<IntVector2> provider,
        Status<IntVector2> status, Ship ship, StatusHandler<IntVector2> handler)
        => BuffsCore(handler.StatusType, provider, status, ship);

    internal static IReadOnlyDictionary<Cosmoteer.Ships.Buffs.BuffType, float>? BuffsCore(
        StatusType type, IStatusEffectDataProvider<IntVector2> provider,
        Status<IntVector2> status, Ship ship)
    {
        var skip = CanSkip(type, provider, 0);
        return skip ? null : provider.GetBuffs(status, ship);
    }

    internal static void PopulateCore(StatusType type,
        IStatusEffectDataProvider<IntVector2> provider, Status<IntVector2> status,
        Ship ship, Dictionary<StatusType, IStatusLocationInfo> dictionary)
    {
        var skip = CanSkip(type, provider, dictionary.Count);
        if (!skip) provider.PopulateStatuses(status, ship, dictionary);
    }

    /// <summary>
    /// Once-per-handler-per-tick gate for the specialized loop. Beyond the
    /// exact-shape <see cref="CanSkip"/> checks, the store must be the tile
    /// handler's <c>StatusStore&lt;IntVector2&gt;</c> because the loop relies on
    /// its flat enumerator and <c>GetAllStatusLists</c> traversing the same
    /// lists in the same order - identical by construction for that type (both
    /// enumerate <c>_statuses.Values</c>), unproven for any other.
    /// </summary>
    internal static bool LoopGuard(StatusHandler<IntVector2> handler)
        => _store(handler).GetType() == typeof(StatusStore<IntVector2>)
            && CanSkip(handler.StatusType, handler.DataProvider, 0);

    /// <summary>
    /// Bit-exact rewrite of vanilla <c>_ModulateStatusValues</c> for the guarded
    /// shape. Provably-dead work removed per status: the temp dictionary
    /// alloc/clear, the <c>GetBuffs</c> and <c>PopulateStatuses</c> calls (both
    /// unread by this modulator shape), the <c>ValueModulationData</c> struct
    /// and modulator dispatch (inlined: ConvertValue is identity for Raw->Raw,
    /// so Subtract of the constant is literally
    /// <c>value + (modifier*dt) * (0f - (resistance - 1f))</c>), and the
    /// <c>GetOrCreateStatusList</c> lookup in the two-argument
    /// <c>OnStatusValueModified</c> - enumerating status lists directly hands
    /// the same list the lookup would have returned. Iteration order, the
    /// changed-status event order and <c>_ShouldRemoveForMinValue</c> semantics
    /// are unchanged.
    /// </summary>
    private static void SpecializedLoop(StatusHandler<IntVector2> handler, FixedUpdater fixedUpdater)
    {
        var type = handler.StatusType;
        var ship = handler.Ship;
        var provider = handler.DataProvider;
        var clampRange = type.ValueClampRange;
        var resistRange = type.ValueModulationResistanceRange;
        var constant = (ConstantValueModulatorRules)type.ValueModulators!.Modulators[0];
        var affected = constant.AffectedValueRange;
        var modifier = 0f - constant.Value;
        var dt = (float)fixedUpdater.Interval;
        var changed = TempList<(Status<IntVector2>, float, StatusList<IntVector2>)>.Alloc();
        var resistanceCache = s_resistanceCacheInUse
            ? new Dictionary<Part, float>()
            : (s_resistanceCache ??= new Dictionary<Part, float>());
        var ownsCache = !s_resistanceCacheInUse;
        if (ownsCache)
        {
            s_resistanceCacheInUse = true;
        }
        resistanceCache.Clear();
        var nullPartResistance = Mathx.Clamp(0f, resistRange);
        try
        {
            changed.EnsureCapacity(handler.StatusCount);
            foreach (var list in _store(handler).GetAllStatusLists())
            {
                foreach (var item in list)
                {
                    float resistance;
                    if (!resistRange.IsRanged)
                    {
                        resistance = resistRange.Min;
                    }
                    else
                    {
                        var part = provider.GetPartAtLocation(item.Location, ship);
                        if (part == null)
                        {
                            resistance = nullPartResistance;
                        }
                        else if (!resistanceCache.TryGetValue(part, out resistance))
                        {
                            resistance = Mathx.Clamp(
                                part.GetStatusResistance(type.ID, null).value,
                                resistRange);
                            resistanceCache[part] = resistance;
                        }
                    }
                    var value = ModulateCore(item.Value, resistance, dt, modifier, affected, clampRange);
                    if (!item.Value.Equals(value))
                    {
                        changed.Add((item, item.Value, list));
                        item.Value = value;
                    }
                }
            }
            foreach (var (status, oldValue, list) in changed)
            {
                _onModified(handler, status, oldValue, list, silent: false);
            }
        }
        finally
        {
            changed.Dispose();
            resistanceCache.Clear();
            if (ownsCache)
            {
                s_resistanceCacheInUse = false;
            }
        }
    }

    /// <summary>
    /// One status' modulation for the guarded shape: the constant modulator's
    /// <c>AffectsValue</c> range check, then Subtract's
    /// <c>Add(data, 0f - ConvertValue(Value))</c> with
    /// <c>ScaleByDeltaTime</c>. Mirrors the vanilla expression order exactly so
    /// results are bit-identical; verified against the real modulator chain in
    /// the smoke test.
    /// </summary>
    internal static float ModulateCore(
        float value, float resistance, float dt, float modifier, Range<float> affected, Range<float> clampRange)
    {
        if (value >= affected.Min && value < affected.Max)
        {
            value += (modifier * dt) * (0f - (resistance - 1f));
        }
        return Mathx.Clamp(value, clampRange);
    }
}
