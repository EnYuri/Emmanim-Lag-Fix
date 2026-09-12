using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Statuses;
using Halfling.Geometry;
using Halfling.Pooling;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Gives the temporary changed-status list in
/// <c>StatusHandler&lt;TLocation&gt;._ModulateStatusValues</c> enough capacity
/// for the handler's current statuses before vanilla fills it.
///
/// A 2026-09-09 CPU trace placed <c>List.AddWithResize</c> under this method at
/// 5.7% of all parallel fixed-update work. The list can receive at most one
/// entry per status, so <c>StatusCount</c> is an exact upper bound. Vanilla's
/// pooled list, iteration, value calculations, event ordering and disposal all
/// remain untouched; the only difference is whether its backing array grows in
/// several geometric steps during the loop or once before it.
///
/// The transpiler targets the compiler-generated local function rather than
/// <c>FixedUpdate</c>, because Roslyn emitted the modulation body as a separate
/// method. It requires exactly one parameterless <c>TempList.Alloc()</c> of the
/// expected <c>(Status&lt;TLocation&gt;, float)</c> element type. Any shape change
/// throws during patch installation instead of silently rewriting another
/// temporary list.
/// </summary>
[HarmonyPatch]
internal static class StatusModulationListCapacityPatch
{
    private const string LocalFunctionNamePart = "g___ModulateStatusValues|";

    internal static bool AppliedToTiles { get; private set; }

    internal static bool AppliedToParts { get; private set; }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return FindTarget(typeof(IntVector2));
        yield return FindTarget(typeof(Part));
    }

    internal static MethodInfo FindTarget(Type locationType)
    {
        var handlerType = typeof(StatusHandler<>).MakeGenericType(locationType);
        var targets = AccessTools.GetDeclaredMethods(handlerType)
            .Where(method => method.Name.Contains(LocalFunctionNamePart, StringComparison.Ordinal))
            .ToArray();
        return targets.Length == 1
            ? targets[0]
            : throw new MissingMethodException(
                handlerType.FullName,
                $"unique compiler-generated {LocalFunctionNamePart} method (found {targets.Length})");
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        var locationType = original.DeclaringType?.GetGenericArguments() is { Length: 1 } arguments
            ? arguments[0]
            : throw new InvalidOperationException(
                $"{original} is no longer declared on StatusHandler<TLocation>.");
        var helper = AccessTools.DeclaredMethod(
                typeof(StatusModulationListCapacityPatch), nameof(AllocWithCapacity))!
            .MakeGenericMethod(locationType);

        var rewritten = 0;
        foreach (var instruction in instructions)
        {
            if (IsExpectedAllocation(instruction, locationType))
            {
                rewritten++;
                var loadHandler = new CodeInstruction(OpCodes.Ldarg_0);
                loadHandler.labels.AddRange(instruction.labels);
                loadHandler.blocks.AddRange(instruction.blocks);
                yield return loadHandler;
                yield return new CodeInstruction(OpCodes.Call, helper);
                continue;
            }

            yield return instruction;
        }

        if (rewritten != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one changed-status TempList.Alloc in {original}, "
                + $"found {rewritten}; refusing to rewrite it.");
        }

        if (locationType == typeof(IntVector2))
        {
            AppliedToTiles = true;
        }
        else if (locationType == typeof(Part))
        {
            AppliedToParts = true;
        }
        else
        {
            throw new InvalidOperationException(
                $"Unexpected StatusHandler location type {locationType.FullName}.");
        }
    }

    private static bool IsExpectedAllocation(CodeInstruction instruction, Type locationType)
    {
        if (instruction.opcode != OpCodes.Call
            || instruction.operand is not MethodInfo method
            || method.Name != nameof(TempList<object>.Alloc)
            || method.GetParameters().Length != 0
            || method.DeclaringType is not { IsGenericType: true } listType
            || listType.GetGenericTypeDefinition() != typeof(TempList<>))
        {
            return false;
        }

        var elementType = listType.GetGenericArguments()[0];
        if (!elementType.IsGenericType
            || elementType.GetGenericTypeDefinition() != typeof(ValueTuple<,>))
        {
            return false;
        }

        var tupleArguments = elementType.GetGenericArguments();
        return tupleArguments[1] == typeof(float)
            && tupleArguments[0].IsGenericType
            && tupleArguments[0].GetGenericTypeDefinition() == typeof(Status<>)
            && tupleArguments[0].GetGenericArguments()[0] == locationType;
    }

    private static TempList<(Status<TLocation>, float)> AllocWithCapacity<TLocation>(
        StatusHandler<TLocation> handler)
        where TLocation : notnull
    {
        var changed = TempList<(Status<TLocation>, float)>.Alloc();
        changed.EnsureCapacity(handler.StatusCount);
        return changed;
    }
}
