using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships.Parts.Thrusters;
using Halfling.Geometry;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// The solver prepares transformed SRFs before its iteration loop. Its first
/// iteration transforms the same raw SRF again just to zero torque. Read the
/// existing transformed vector and zero its Z instead. No retained state.
/// </summary>
[HarmonyPatch]
internal static class ThrusterFirstPassTransformPatch
{
    internal static bool Applied;

    internal static Vector3 WithoutTorque(Vector3 prepared)
    {
        prepared.Z = 0f;
        return prepared;
    }

    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(typeof(ThrusterManager), nameof(ThrusterManager.CalculateDesiredSRF))
        ?? throw new MissingMethodException(typeof(ThrusterManager).FullName, nameof(ThrusterManager.CalculateDesiredSRF));

    private static int LocalIndex(CodeInstruction instruction) => instruction.operand switch
    {
        LocalBuilder local => local.LocalIndex,
        int index => index,
        byte index => index,
        _ => instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Stloc_0 ? 0
           : instruction.opcode == OpCodes.Ldloc_1 || instruction.opcode == OpCodes.Stloc_1 ? 1
           : instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Stloc_2 ? 2
           : instruction.opcode == OpCodes.Ldloc_3 || instruction.opcode == OpCodes.Stloc_3 ? 3 : -1
    };

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var preparedField = code.FindIndex(i => i.opcode == OpCodes.Ldfld
            && i.operand is FieldInfo { Name: "ThrusterFSRFs" });
        var rawField = code.FindIndex(i => i.opcode == OpCodes.Ldfld
            && i.operand is FieldInfo { Name: "ThrusterSRFs" });
        if (preparedField < 0 || rawField < 0
            || !code[preparedField + 1].IsStloc() || !code[rawField + 1].IsStloc())
            return Fail("prepared/raw SRF locals were not found");
        var preparedLocal = LocalIndex(code[preparedField + 1]);
        var rawLocal = LocalIndex(code[rawField + 1]);
        var matches = new List<int>();
        for (var i = 10; i + 1 < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Call || code[i].operand is not MethodInfo method
                || !method.Name.StartsWith("<CalculateDesiredSRF>g___RotateAndFactorSRF|")
                || code[i - 2].opcode != OpCodes.Ldc_I4_1
                || (code[i - 1].opcode != OpCodes.Ldloca_S && code[i - 1].opcode != OpCodes.Ldloca)
                || code[i - 3].opcode != OpCodes.Ldelem || !Equals(code[i - 3].operand, typeof(Vector3))
                || !code[i - 5].IsLdloc() || LocalIndex(code[i - 5]) != rawLocal
                || !code[i - 9].IsLdloc() || LocalIndex(code[i - 9]) != preparedLocal
                || !code[i - 4].IsLdloc() || !code[i - 8].IsLdloc()
                || LocalIndex(code[i - 4]) != LocalIndex(code[i - 8])
                || code[i - 7].opcode != OpCodes.Ldelem || !Equals(code[i - 7].operand, typeof(Vector3))
                || (code[i - 6].opcode != OpCodes.Br && code[i - 6].opcode != OpCodes.Br_S)
                || !code[i + 1].labels.Contains((Label)code[i - 6].operand)
                || (code[i - 10].opcode != OpCodes.Brtrue && code[i - 10].opcode != OpCodes.Brtrue_S)
                || !code[i - 5].labels.Contains((Label)code[i - 10].operand))
                continue;
            matches.Add(i);
        }
        if (matches.Count != 1) return Fail($"expected one first-pass transform, found {matches.Count}");
        var call = matches[0];
        // Keep all labels and exception boundaries on their original nodes.
        code[call - 5].opcode = code[call - 9].opcode;
        code[call - 5].operand = code[call - 9].operand;
        code[call - 2].opcode = code[call - 1].opcode = OpCodes.Nop;
        code[call - 2].operand = code[call - 1].operand = null;
        code[call].operand = AccessTools.DeclaredMethod(typeof(ThrusterFirstPassTransformPatch), nameof(WithoutTorque));
        Applied = true;
        return code;

        IEnumerable<CodeInstruction> Fail(string reason)
        {
            Halfling.Logging.Logger.Log("[EmmanimLagFix] First-pass thruster transform left vanilla: " + reason + ".");
            return code;
        }
    }
}
