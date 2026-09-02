using System.Reflection;
using System.Reflection.Emit;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Stops every part's colour handler from unsubscribing itself out of a
/// thousand-entry multicast event, one linear scan at a time.
///
/// <c>PartGraphics</c> keeps two flags and a four-site state machine:
///
/// <code>
/// OnColorChanged:   if (!Dirty) { Dirty = true;
///                                 if (!Registered) { Ship.Renderer.BeforeDraw += UpdateColor;
///                                                    Registered = true; } }
/// UpdateColor:      if (!Dirty) { Ship.Renderer.BeforeDraw -= UpdateColor;   // (b)
///                                 Registered = false; return; }
///                   ...apply the colour to each sprite...
///                   Dirty = false;
/// OnPartDetaching:  if (Registered) { Ship.Renderer.BeforeDraw -= UpdateColor;
///                                     Registered = false; }
/// </code>
///
/// So a colour change costs one subscribe, and one frame later one unsubscribe.
/// The two halves are wildly asymmetric. <c>Delegate.Combine</c> allocates an
/// array one longer and copies pointers into it; <c>Delegate.Remove</c> walks
/// the invocation list comparing delegates until it finds the match. On a
/// twenty-second capture of a degraded session the combine side totalled 2.1 ms
/// and the remove side 1,469 ms — 19.9% of all CPU spent drawing, and every
/// sample of it arrived through this one path:
///
/// <code>
/// SceneRoot.Draw -> SceneComponent.Draw -> PartGraphics.UpdateColor
///   -> SceneComponent.remove_BeforeDraw -> MulticastDelegate.RemoveImpl
/// </code>
///
/// With the invocation list in the thousands and dozens of parts settling every
/// frame, that is a linear scan per settling part, per frame.
///
/// This makes two exact rewrites to <c>UpdateColor</c>, all or nothing:
///
/// (a) The dirty test is <c>_colorUpdateStatus.HasFlag(Dirty)</c>, and
///     <c>Enum.HasFlag</c> takes an <c>Enum</c>, so the IL boxes both operands —
///     two allocations per handler call per frame. It becomes <c>and</c> against
///     the same literal. <c>ColorUpdateFlags</c> is <c>[Flags]</c> over
///     <c>int32</c> (vanilla itself writes <c>ldc.i4.s -3 / and</c> to clear
///     <c>Registered</c>) and <c>Dirty</c> is a single bit, so
///     <c>(status &amp; 1) != 0</c> is exactly <c>HasFlag(Dirty)</c>.
///
/// (b) The self-unsubscribe block is deleted, leaving the early <c>ret</c>. A
///     settled part simply stays subscribed and returns.
///
/// Both are required together. On its own (b) would leave every idle handler
/// boxing twice per frame, trading a burst of scans for a permanent allocation
/// rate — the opposite of the intent.
///
/// The state machine stays consistent because (b) drops the flag clear along
/// with the unsubscribe, so <c>Registered</c> keeps meaning exactly "this
/// handler is in the invocation list":
/// <list type="bullet">
/// <item>Colours written are unchanged; the dirty branch is untouched.</item>
/// <item>The handler set becomes a superset of vanilla's. The extra entries are
/// precisely the settled ones, whose whole body is now the early return.</item>
/// <item><c>OnColorChanged</c> sees <c>Registered</c> still set and skips the
/// resubscribe — correctly, because the handler really is still there. That
/// removes the matching <c>Combine</c> too.</item>
/// <item><c>OnPartDetaching</c> still sees <c>Registered</c> and still removes
/// the handler exactly once, so nothing is leaked. Each part contributes at
/// most one entry, so the list stays bounded by the ship's part count.</item>
/// </list>
///
/// The cost moved onto the other side is one no-op invocation per settled part
/// per frame — a field load, a mask, a branch, a return, and no allocation —
/// against a linear scan of the whole list per settling part. Detaching a part
/// still pays one scan, but that happens per part destroyed or deconstructed,
/// far below the churn measured here.
///
/// Sprite colours are render state, not simulation state, so lockstep and
/// multiplayer hashing are unaffected. If either rewrite does not match, the
/// original instructions are returned and vanilla behaviour stands.
/// <see cref="Applied"/> is set only when both happened, which the smoke test
/// asserts, because a transpiler that fell back to vanilla is still installed.
/// </summary>
[HarmonyPatch]
internal static class PartGraphicsColorEventPatch
{
    /// <summary>The <c>Dirty</c> member of <c>PartGraphics.ColorUpdateFlags</c>.</summary>
    private const int DirtyFlag = 1;

    /// <summary>True only when both rewrites were really applied.</summary>
    internal static bool Applied;

    /// <summary>Why the rewrite was skipped, when it was; null when it was applied.</summary>
    internal static string? FailureReason;

    private static readonly Type PartGraphicsType =
        AccessTools.TypeByName("Cosmoteer.Ships.Parts.Graphics.PartGraphics")
        ?? throw new TypeLoadException(
            "Cosmoteer.Ships.Parts.Graphics.PartGraphics was not found.");

    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(PartGraphicsType, "UpdateColor")
        ?? throw new MissingMethodException(PartGraphicsType.FullName, "UpdateColor");

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();

        // (a) ldfld _colorUpdateStatus / box / ldc.i4.1 / box / call Enum.HasFlag.
        var status = code.FindIndex(i =>
            i.opcode == OpCodes.Ldfld
            && i.operand is FieldInfo { Name: "_colorUpdateStatus" });
        if (status < 0 || status + 4 >= code.Count)
        {
            return Fail("its dirty flag is not read where expected");
        }

        var hasFlag = code[status + 4].operand as MethodInfo;
        if (code[status + 1].opcode != OpCodes.Box
            || !code[status + 2].LoadsConstant(DirtyFlag)
            || code[status + 3].opcode != OpCodes.Box
            || code[status + 4].opcode != OpCodes.Call
            || hasFlag is null
            || hasFlag.Name != "HasFlag"
            || hasFlag.DeclaringType != typeof(Enum))
        {
            return Fail("its dirty test is not the boxing Enum.HasFlag shape");
        }

        // Only the ldfld may carry the method's entry label; nothing branches
        // into the middle of a comparison.
        for (var i = status + 1; i <= status + 4; i++)
        {
            if (code[i].labels.Count > 0 || code[i].blocks.Count > 0)
            {
                return Fail("its dirty test is a branch target or an exception region");
            }
        }

        // (b) The single self-unsubscribe. Located before anything is edited, so
        // that a failure here leaves the boxing test alone as well.
        var remove = -1;
        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].opcode == OpCodes.Callvirt
                && code[i].operand is MethodInfo { Name: "remove_BeforeDraw" })
            {
                if (remove >= 0)
                {
                    return Fail("it unsubscribes from BeforeDraw more than once");
                }

                remove = i;
            }
        }

        if (remove < 0)
        {
            return Fail("it does not unsubscribe from BeforeDraw");
        }

        // Ship.Renderer.BeforeDraw -= UpdateColor, as six instructions ending at
        // the call: ldarg.0 / get_Ship / get_Renderer / ldarg.0 / ldftn / newobj.
        var start = remove - 6;
        if (start < 0
            || !code[start].IsLdarg(0)
            || code[start + 1].operand is not MethodInfo { Name: "get_Ship" }
            || code[start + 2].operand is not MethodInfo { Name: "get_Renderer" }
            || !code[start + 3].IsLdarg(0)
            || code[start + 4].opcode != OpCodes.Ldftn
            || code[start + 5].opcode != OpCodes.Newobj)
        {
            return Fail("its unsubscribe is not the expected Ship.Renderer.BeforeDraw shape");
        }

        // Registered is then cleared and the method returns:
        // ldarg.0 / ldarg.0 / ldfld / ldc.i4 mask / and / stfld / ret.
        var end = remove + 6;
        if (end >= code.Count
            || !code[remove + 1].IsLdarg(0)
            || !code[remove + 2].IsLdarg(0)
            || code[remove + 3].opcode != OpCodes.Ldfld
            || code[remove + 3].operand is not FieldInfo { Name: "_colorUpdateStatus" }
            || !code[remove + 4].LoadsConstant(~2)
            || code[remove + 5].opcode != OpCodes.And
            || code[remove + 6].opcode != OpCodes.Stfld
            || code[remove + 6].operand is not FieldInfo { Name: "_colorUpdateStatus" }
            || end + 1 >= code.Count
            || code[end + 1].opcode != OpCodes.Ret)
        {
            return Fail("its unsubscribe is not followed by clearing Registered and returning");
        }

        // Nothing may branch into the block being deleted. The dirty test falls
        // through into it and jumps over it; no other edge should reach inside.
        for (var i = start; i <= end; i++)
        {
            if (code[i].labels.Count > 0 || code[i].blocks.Count > 0)
            {
                return Fail("its unsubscribe block is a branch target or an exception region");
            }
        }

        // Delete the block; the early `ret` after it stays, and with it the
        // vanilla behaviour of doing nothing more when the part is settled.
        code.RemoveRange(start, end - start + 1);

        // Replace the boxed comparison with a mask against the same literal.
        // The indexes above `status` moved, but `status` itself sits before the
        // deleted block, so its own window is still where it was.
        code.RemoveAt(status + 4);
        code.RemoveAt(status + 3);
        code.RemoveAt(status + 1);
        code.Insert(status + 2, new CodeInstruction(OpCodes.And));

        Applied = true;
        return code;

        IEnumerable<CodeInstruction> Fail(string reason)
        {
            FailureReason = reason;
            try
            {
                Logger.Log(
                    "[EmmanimLagFix] PartGraphics.UpdateColor was left at vanilla behaviour: "
                    + reason + ".");
            }
            catch
            {
                // The smoke test patches without a running game, so the logger may
                // not be initialised. The reason is still on FailureReason.
            }

            return code;
        }
    }
}
