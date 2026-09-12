using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Statuses;
using Halfling;
using Halfling.Geometry;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Always-on, exact vanilla heat-rule specialization. Rewrites only the status
/// context population call inside tile modulation, never effects or diffusion.
/// Vanilla dictionary allocation/clear and all value/event code remain intact.
/// </summary>
[HarmonyPatch]
internal static class HeatModulationContextSkipPatch
{
    internal static bool Applied { get; private set; }
    private static long _skipped;
    private static long _fallback;

    private static MethodBase TargetMethod() => StatusModulationListCapacityPatch.FindTarget(typeof(IntVector2));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var target = AccessTools.Method(typeof(IStatusEffectDataProvider<IntVector2>),
            "PopulateStatuses", new[] { typeof(Status<IntVector2>), typeof(Ship),
                typeof(Dictionary<StatusType, IStatusLocationInfo>) })!;
        var helper = AccessTools.Method(typeof(HeatModulationContextSkipPatch), nameof(Populate))!;
        var count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(target))
            {
                count++;
                var load = new CodeInstruction(OpCodes.Ldarg_0);
                load.labels.AddRange(instruction.labels);
                load.blocks.AddRange(instruction.blocks);
                yield return load;
                yield return new CodeInstruction(OpCodes.Call, helper);
            }
            else yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException(
            $"Expected one tile modulation PopulateStatuses call, found {count}.");
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

    internal static void PopulateCore(StatusType type,
        IStatusEffectDataProvider<IntVector2> provider, Status<IntVector2> status,
        Ship ship, Dictionary<StatusType, IStatusLocationInfo> dictionary)
    {
        var skip = CanSkip(type, provider, dictionary.Count);
        if (FramePhaseDiagnosticsPatch.Enabled)
        {
            if (skip) Interlocked.Increment(ref _skipped);
            else Interlocked.Increment(ref _fallback);
        }
        if (!skip) provider.PopulateStatuses(status, ship, dictionary);
    }

    internal static string TakeCounters() =>
        $"ctx={(Applied ? "on" : "off")} sk={Interlocked.Exchange(ref _skipped, 0)} vf={Interlocked.Exchange(ref _fallback, 0)}";
}
