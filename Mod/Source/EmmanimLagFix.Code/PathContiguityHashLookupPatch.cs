using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships.Crew.Pathing;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// PathContiguityManager's breadth-first iterator tests every adjacent set with
/// HashSet.Contains and then immediately performs HashSet.Add for the same
/// value. HashSet.Add already returns whether the value was new, so use that
/// result directly and avoid the duplicate hash-table probe.
/// </summary>
[HarmonyPatch]
internal static class PathContiguityHashLookupPatch
{
    private static readonly Type IteratorType =
        typeof(PathContiguityManager).GetNestedType(
            "<SearchSetsFrom>d__21",
            BindingFlags.NonPublic)
        ?? throw new TypeLoadException("PathContiguityManager SearchSetsFrom iterator was not found.");

    private static readonly MethodInfo ContainsMethod = AccessTools.Method(
        typeof(HashSet<ContiguousPathSet>),
        nameof(HashSet<ContiguousPathSet>.Contains))
        ?? throw new MissingMethodException(typeof(HashSet<ContiguousPathSet>).FullName, "Contains");

    private static readonly MethodInfo AddMethod = AccessTools.Method(
        typeof(HashSet<ContiguousPathSet>),
        nameof(HashSet<ContiguousPathSet>.Add))
        ?? throw new MissingMethodException(typeof(HashSet<ContiguousPathSet>).FullName, "Add");

    private static MethodBase TargetMethod() =>
        AccessTools.Method(IteratorType, "MoveNext")
        ?? throw new MissingMethodException(IteratorType.FullName, "MoveNext");

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var replaced = 0;

        for (var index = 0; index + 6 < codes.Count; index++)
        {
            if (!Calls(codes[index], ContainsMethod)
                || (codes[index + 1].opcode != OpCodes.Brtrue
                    && codes[index + 1].opcode != OpCodes.Brtrue_S)
                || codes[index + 2].opcode != OpCodes.Ldarg_0
                || codes[index + 3].opcode != OpCodes.Ldfld
                || !IsLoadLocal(codes[index + 4])
                || !Calls(codes[index + 5], AddMethod)
                || codes[index + 6].opcode != OpCodes.Pop)
            {
                continue;
            }

            if (codes.Skip(index + 2).Take(5).Any(code =>
                    code.labels.Count != 0 || code.blocks.Count != 0))
            {
                throw new InvalidOperationException(
                    "Unexpected control-flow metadata inside PathContiguity duplicate Add sequence.");
            }

            codes[index].operand = AddMethod;
            codes[index + 1].opcode = codes[index + 1].opcode == OpCodes.Brtrue
                ? OpCodes.Brfalse
                : OpCodes.Brfalse_S;
            codes.RemoveRange(index + 2, 5);
            replaced++;
        }

        if (replaced != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one PathContiguity Contains/Add pair, found {replaced}. " +
                "The game code shape has changed; skipping the hash-lookup patch.");
        }

        return codes;
    }

    private static bool Calls(CodeInstruction instruction, MethodInfo method) =>
        (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
        && Equals(instruction.operand, method);

    private static bool IsLoadLocal(CodeInstruction instruction) =>
        instruction.opcode == OpCodes.Ldloc
        || instruction.opcode == OpCodes.Ldloc_S
        || instruction.opcode == OpCodes.Ldloc_0
        || instruction.opcode == OpCodes.Ldloc_1
        || instruction.opcode == OpCodes.Ldloc_2
        || instruction.opcode == OpCodes.Ldloc_3;
}
