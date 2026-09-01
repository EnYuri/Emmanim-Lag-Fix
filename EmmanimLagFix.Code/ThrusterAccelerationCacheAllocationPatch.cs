using System.Reflection;
using System.Reflection.Emit;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Stops building a throwaway dictionary on every uncacheable acceleration query.
///
/// Vanilla <c>ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached</c>
/// caches per (direction, srfFactors, activationRangeType), storing each
/// thruster's uncommitted activation level so a later hit can replay it. On a
/// miss it does this:
///
/// <code>
/// value.Item1 = CalculateMaximumAcceleration(...);
/// value.Item2 = new Dictionary&lt;Thruster, float&gt;();          // built first
/// foreach (var t in _orderedThrusters)
///     value.Item2.Add(t, t.UncomittedActivationLevel);
/// if (s_allowedCachedDirections.Contains(direction))            // checked after
///     _cachedAccelerations.Add(key, value);
/// </code>
///
/// The dictionary is built and filled to the ship's thruster count *before*
/// asking whether the direction may be cached at all. When it may not, the whole
/// dictionary is immediately garbage. Nothing else reads <c>Item2</c>: the
/// return value is <c>Item1</c> plus the ramp times.
///
/// <c>s_allowedCachedDirections</c> holds only the six axis directions plus the
/// fixed <c>ShipFlightDirection</c> angles, while the dominant caller —
/// <c>MoveCommand.SetThrusterActivations</c> through
/// <c>Command.GetDesiredLinearSRA</c>, on the parallel fixed update — passes an
/// arbitrary vector toward a move target. So the discarded path is the common
/// one, for every moving ship, every tick. It measured 13.1% of all allocation.
///
/// The repair hoists vanilla's own guard: the existing
/// <c>ldsfld / ldarg.1 / callvirt Contains</c> triple is cloned in front of the
/// dictionary construction and branches to the same target the original test
/// already branches to, which is the instruction after the cache insert. The
/// three cloned instructions carry the original operands, so no field or method
/// is resolved by name here. When the direction is cacheable the test simply
/// runs twice — a HashSet lookup — and the original path is untouched.
///
/// Skipping leaves <c>Item2</c> at the <c>null</c> that the failed
/// <c>TryGetValue</c> already wrote, which is only ever read back out of the
/// cache this branch does not populate. Thruster activation levels are set by
/// <c>CalculateMaximumAcceleration</c>, which is not touched, so simulation
/// state and lockstep are unchanged.
/// </summary>
[HarmonyPatch]
internal static class ThrusterAccelerationCacheAllocationPatch
{
    /// <summary>True once the guard was actually hoisted; the smoke test asserts it.</summary>
    internal static bool Applied;

    private static readonly Type ThrusterManagerType =
        AccessTools.TypeByName("Cosmoteer.Ships.Parts.Thrusters.ThrusterManager")
        ?? throw new TypeLoadException(
            "Cosmoteer.Ships.Parts.Thrusters.ThrusterManager was not found.");

    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(
            ThrusterManagerType, "CalculateMaximumAccelerationAndRampTimeCached")
        ?? throw new MissingMethodException(
            ThrusterManagerType.FullName, "CalculateMaximumAccelerationAndRampTimeCached");

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();

        // Vanilla's own guard, which we are going to run earlier as well.
        var guard = code.FindIndex(i =>
            i.opcode == OpCodes.Ldsfld
            && i.operand is FieldInfo { Name: "s_allowedCachedDirections" });

        // The construction of the per-thruster activation snapshot.
        var construct = code.FindIndex(i =>
            i.opcode == OpCodes.Newobj
            && i.operand is ConstructorInfo ctor
            && ctor.DeclaringType is { IsGenericType: true } dt
            && dt.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            && ctor.GetParameters().Length == 0);

        if (guard < 0 || construct < 1 || construct >= guard)
        {
            return Fail("its cache guard and activation-snapshot construction were not "
                        + "found in the expected order");
        }

        // ldsfld s_allowedCachedDirections / ldarg.1 (direction) / callvirt Contains / brfalse.
        var load = code[guard + 1];
        var contains = code[guard + 2];
        var branch = code[guard + 3];

        if (!load.IsLdarg(1)
            || contains.opcode != OpCodes.Callvirt
            || contains.operand is not MethodInfo { Name: "Contains" }
            || (branch.opcode != OpCodes.Brfalse && branch.opcode != OpCodes.Brfalse_S)
            || branch.operand is not Label skipTarget)
        {
            return Fail("its cache guard does not have the expected "
                        + "ldsfld/ldarg/Contains/brfalse shape");
        }

        // The snapshot is stored through the address of the result tuple, so the
        // insertion point is that address load, where the evaluation stack is empty.
        var insertAt = construct - 1;
        if (code[insertAt].opcode != OpCodes.Ldloca_S && code[insertAt].opcode != OpCodes.Ldloca)
        {
            return Fail("the activation snapshot is not stored through a local address");
        }

        // Keep any labels or exception-block flags already sitting on that instruction
        // where they are: the clones go in front of it, carrying nothing of their own.
        code.InsertRange(insertAt,
        [
            new CodeInstruction(OpCodes.Ldsfld, code[guard].operand),
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Callvirt, contains.operand),
            // Long form: the jump now spans the loop the original short branch skipped.
            new CodeInstruction(OpCodes.Brfalse, skipTarget),
        ]);

        Applied = true;
        return code;

        IEnumerable<CodeInstruction> Fail(string reason)
        {
            Logger.Log(
                "[EmmanimLagFix] ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached "
                + "was left at vanilla behaviour: " + reason + ".");
            return code;
        }
    }
}
