using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cosmoteer.Ships.Rendering;
using Halfling.Collections;
using Halfling.Graphics;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// AtlasQuadManager's managed-quad setter dirties the complete backing GPU
/// buffer even when the replacement quad is byte-for-byte identical. Large
/// ships can therefore force a full dynamic-buffer Map/Copy/Unmap during draw
/// for redundant presentation updates. Keep every real write unchanged, but
/// avoid the GraphicsList setter when no byte changed.
/// </summary>
[HarmonyPatch]
internal static class AtlasQuadRedundantWritePatch
{
    private static readonly Type ManagedQuadType =
        typeof(AtlasQuadManager).GetNestedType(
            "InternalManagedAtlasQuad",
            BindingFlags.NonPublic)
        ?? throw new TypeLoadException("AtlasQuadManager.InternalManagedAtlasQuad was not found.");

    private static MethodBase TargetMethod() =>
        AccessTools.PropertySetter(ManagedQuadType, "Data")
        ?? throw new MissingMethodException(ManagedQuadType.FullName, "set_Data");

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        var codes = new List<CodeInstruction>(instructions);
        var replacement = AccessTools.Method(typeof(AtlasQuadRedundantWritePatch), nameof(SetIfChanged));
        var replaced = 0;
        var returns = codes.Where(code => code.opcode == OpCodes.Ret).ToArray();
        if (returns.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one return in AtlasQuad data setter, found {returns.Length}.");
        }
        var unchangedReturn = generator.DefineLabel();
        returns[0].labels.Add(unchangedReturn);

        for (var index = 0; index < codes.Count; index++)
        {
            var code = codes[index];
            if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt)
                && code.operand is MethodInfo method
                && IsAtlasQuadListSetter(method))
            {
                code.opcode = OpCodes.Call;
                code.operand = replacement;
                codes.Insert(index + 1, new CodeInstruction(OpCodes.Brfalse, unchangedReturn));
                replaced++;
                index++;
            }
        }

        if (replaced != 1)
        {
            var calls = string.Join(
                ", ",
                codes.Where(code => code.operand is MethodInfo)
                    .Select(code => (MethodInfo)code.operand)
                    .Select(method => $"{method.DeclaringType?.FullName}.{method.Name}"));
            throw new InvalidOperationException(
                $"Expected exactly one AtlasQuad GraphicsList setter call, found {replaced}. " +
                $"Observed calls: [{calls}]. The game code shape has changed; " +
                "skipping the redundant-write patch.");
        }

        return codes;
    }

    private static bool IsAtlasQuadListSetter(MethodInfo method)
    {
        var declaringType = method.DeclaringType;
        return method.Name == "set_Item"
            && declaringType is not null
            && declaringType.IsGenericType
            && declaringType.GetGenericTypeDefinition() == typeof(ListBase<>)
            && declaringType.GetGenericArguments() is var arguments
            && arguments.Length == 1
            && arguments[0] == typeof(AtlasQuad);
    }

    private static bool SetIfChanged(
        GraphicsList<AtlasQuad> list,
        int index,
        AtlasQuad value)
    {
        var current = list[index];
        if (!AreIdentical(in current, in value))
        {
            list[index] = value;
            return true;
        }

        return false;
    }

    internal static bool AreIdentical(in AtlasQuad left, in AtlasQuad right)
    {
        var leftBytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in left), 1));
        var rightBytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in right), 1));
        return leftBytes.SequenceEqual(rightBytes);
    }
}
