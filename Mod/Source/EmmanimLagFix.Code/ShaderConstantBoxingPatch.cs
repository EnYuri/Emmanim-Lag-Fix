using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Removes the box allocated on every shader-constant update.
///
/// <c>Halfling.Graphics.D3D11.D3D11Shader.D3D11BufferConstant</c> has eight
/// non-generic <c>Update(gfx, value)</c> overloads (RawBool, int, float,
/// Vector2, Vector3, Vector4, Color, Matrix*). All eight funnel into one
/// generic dirty check:
///
/// <code>
/// private unsafe bool IsDataDirty&lt;T&gt;(in T value) where T : unmanaged
///     =&gt; !((T*)(_bufState.Data + _bufOffset))-&gt;Equals(value);
/// </code>
///
/// <c>T</c> carries no <c>IEquatable&lt;T&gt;</c> constraint, so the only
/// <c>Equals</c> in scope is <c>object.Equals(object)</c> and the compiler has
/// to box the argument. The IL says so directly:
///
/// <code>
/// ldarg.1 / ldobj !!T / box !!T / constrained. !!T / callvirt object::Equals(object)
/// </code>
///
/// One heap allocation per constant per shader per draw call. A ten-second
/// allocation trace on a two-player session put 28.1% of all process
/// allocation (196 MiB) under <c>RefreshShaderConstants</c>, and 44.9% on an
/// earlier capture. That allocation rate is what drives roughly 45 GC
/// suspensions per second, and the main thread's share of those rendezvous is
/// the stutter the player actually feels: median frame work was healthy while
/// p99 ran 10-15x higher.
///
/// Every one of the eight value types implements <c>IEquatable&lt;T&gt;</c>,
/// and each one's <c>Equals(object)</c> override delegates to that same typed
/// comparison, so calling it directly is semantics-preserving by construction:
/// the boxed operand is always exactly <c>T</c>, so no other branch of an
/// <c>Equals(object)</c> override is reachable. (Halfling's <c>Color</c> has
/// extra branches for <c>IntColor</c>/<c>Vector4</c>/<c>Vector3</c>; a boxed
/// <c>Color</c> never reaches them.)
///
/// The declaring type is nested inside an <c>internal</c> class in an assembly
/// this project does not reference, so a prefix cannot name <c>__instance</c>
/// or the parameter types in C#. A transpiler can: it replaces only the
/// <c>call IsDataDirty&lt;T&gt;</c> instruction, taking <c>T</c> from the call
/// site's own generic arguments at patch time. The evaluation stack at that
/// point is already <c>(this, ref T)</c>, which is exactly the replacement's
/// signature, so no control flow and no local is touched.
///
/// Any failed shape check leaves that overload's instructions unchanged and
/// vanilla behaviour stands. <see cref="PatchedCount"/> is the number of
/// overloads actually rewritten, which the smoke test asserts, because a
/// transpiler that installed but fell back to vanilla is still installed.
/// </summary>
[HarmonyPatch]
internal static class ShaderConstantBoxingPatch
{
    internal const string ConstantTypeName =
        "Halfling.Graphics.D3D11.D3D11Shader+D3D11BufferConstant";

    /// <summary>Overloads whose dirty check was actually rewritten. Vanilla shape gives 8.</summary>
    internal static int PatchedCount;

    /// <summary>
    /// The constant types whose comparison was substituted, in patch order. The
    /// smoke test asserts the typed comparison agrees with the boxing one for
    /// exactly these, on this game build.
    /// </summary>
    internal static readonly List<Type> PatchedValueTypes = [];

    private static readonly Type? ConstantType = AccessTools.TypeByName(ConstantTypeName);

    /// <summary>
    /// <c>_bufState.Data + _bufOffset</c>, vanilla's own slot address, compiled
    /// once. The fields are private on a type this assembly cannot name, and a
    /// reflected read per call would cost more than the box it replaces.
    /// </summary>
    private static readonly Func<object, IntPtr>? SlotAddress = BuildSlotAddress();

    private static Func<object, IntPtr>? BuildSlotAddress()
    {
        if (ConstantType == null)
        {
            return null;
        }

        var bufState = AccessTools.DeclaredField(ConstantType, "_bufState");
        var bufOffset = AccessTools.DeclaredField(ConstantType, "_bufOffset");
        if (bufState == null || bufOffset == null || bufOffset.FieldType != typeof(int))
        {
            return null;
        }

        var data = AccessTools.DeclaredPropertyGetter(bufState.FieldType, "Data");
        if (data == null || data.ReturnType != typeof(IntPtr) || data.GetParameters().Length != 0)
        {
            return null;
        }

        var method = new DynamicMethod(
            "EmmanimLagFix_ShaderConstantSlotAddress",
            typeof(IntPtr),
            [typeof(object)],
            ConstantType.Module,
            skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, ConstantType);
        il.Emit(OpCodes.Ldfld, bufState);
        il.Emit(OpCodes.Callvirt, data);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, ConstantType);
        il.Emit(OpCodes.Ldfld, bufOffset);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<object, IntPtr>>();
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        if (ConstantType == null || SlotAddress == null)
        {
            Logger.Log(
                "[EmmanimLagFix] " + ConstantTypeName + " could not be resolved; "
                + "leaving shader-constant updates at vanilla behaviour.");
            return [];
        }

        var updates = ConstantType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "Update"
                && !m.IsGenericMethodDefinition
                && m.ReturnType == typeof(void)
                && m.GetParameters().Length == 2)
            .Cast<MethodBase>()
            .ToArray();

        if (updates.Length == 0)
        {
            Logger.Log(
                "[EmmanimLagFix] No shader-constant Update overloads were found; "
                + "leaving them at vanilla behaviour.");
        }

        return updates;
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var codes = new List<CodeInstruction>(instructions);
        var sites = codes
            .Where(code => code.opcode == OpCodes.Call
                && code.operand is MethodInfo { IsGenericMethod: true } called
                && called.Name == "IsDataDirty"
                && called.DeclaringType == ConstantType)
            .ToArray();

        if (sites.Length != 1)
        {
            Logger.Log(
                "[EmmanimLagFix] " + Describe(original) + " has " + sites.Length
                + " IsDataDirty call sites, expected 1; leaving it at vanilla behaviour.");
            return codes;
        }

        var called = (MethodInfo)sites[0].operand;
        var arguments = called.GetGenericArguments();
        if (arguments.Length != 1
            || called.ReturnType != typeof(bool)
            || called.GetParameters() is not { Length: 1 } parameters
            || !parameters[0].ParameterType.IsByRef)
        {
            Logger.Log(
                "[EmmanimLagFix] " + Describe(original)
                + " calls an unexpected IsDataDirty shape; leaving it at vanilla behaviour.");
            return codes;
        }

        MethodInfo replacement;
        try
        {
            replacement = AccessTools
                .DeclaredMethod(typeof(ShaderConstantBoxingPatch), nameof(IsDataDirty))!
                .MakeGenericMethod(arguments[0]);
        }
        catch (ArgumentException error)
        {
            // The value type does not implement IEquatable<T>, so there is no
            // typed comparison to substitute for the boxing one.
            Logger.Log(
                "[EmmanimLagFix] " + Describe(original) + " uses " + arguments[0].FullName
                + ", which has no typed equality (" + error.Message
                + "); leaving it at vanilla behaviour.");
            return codes;
        }

        // The stack here is already (this, ref T), which is the replacement's
        // own signature, so only the call target changes.
        sites[0].operand = replacement;
        PatchedValueTypes.Add(arguments[0]);
        PatchedCount++;
        return codes;
    }

    /// <summary>
    /// Vanilla's dirty check without the box. The typed comparison is the same
    /// one each of these types' <c>Equals(object)</c> override delegates to.
    /// </summary>
    private static unsafe bool IsDataDirty<T>(object constant, ref T value)
        where T : struct, IEquatable<T>
    {
        ref var slot = ref Unsafe.AsRef<T>((void*)SlotAddress!(constant));
        return !slot.Equals(value);
    }

    private static string Describe(MethodBase method) =>
        "Shader constant "
        + method.Name
        + "("
        + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))
        + ")";
}
