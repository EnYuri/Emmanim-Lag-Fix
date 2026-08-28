using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Gui;
using Cosmoteer.Modes.Career.Comms;
using Halfling.Gui;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// The blueprint-purchase constructor creates every locked technology card in
/// one pass. Keep construction vanilla, but postpone attaching cards to their
/// scroll boxes so layout and activation work is spread across frames.
/// </summary>
[HarmonyPatch]
internal static class TechPurchaseRowAdmissionPatch
{
    private const int RowsPerFrame = 1;
    private static readonly ConditionalWeakTable<object, State> States = new();

    [ThreadStatic]
    private static State? s_constructing;

    private sealed class State
    {
        public readonly Queue<(ScrollBox Parent, Widget Child)> Rows = new();

        public void OnUpdating(object? sender, EventArgs e)
        {
            for (var i = 0; i < RowsPerFrame && Rows.Count > 0; i++)
            {
                var (parent, child) = Rows.Dequeue();
                parent.AddChild(child);
            }
        }
    }

    private static MethodBase TargetMethod() =>
        typeof(CommTechsTab)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();

    private static void Prefix(object __instance)
    {
        var state = new State();
        States.Remove(__instance);
        States.Add(__instance, state);
        s_constructing = state;
    }

    private static void Postfix(ScrollBox pageBox)
    {
        var state = s_constructing;
        s_constructing = null;
        if (state != null)
        {
            pageBox.BeforeFrameInput += state.OnUpdating;
        }
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        s_constructing = null;
        return __exception;
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var replacement = AccessTools.Method(typeof(TechPurchaseRowAdmissionPatch), nameof(AddChildPaced))
            ?? throw new MissingMethodException(typeof(TechPurchaseRowAdmissionPatch).FullName, nameof(AddChildPaced));
        var replacements = 0;

        for (var i = 0; i < code.Count; i++)
        {
            var instruction = code[i];
            if (instruction.operand is MethodInfo method &&
                method.Name == "AddChild" &&
                i >= 3 &&
                code[i - 3].operand is FieldInfo parentField && parentField.Name == "tabBox" &&
                code[i - 1].operand is FieldInfo childField && childField.Name == "box")
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replacements++;
            }
            yield return instruction;
        }

        if (replacements != 1)
        {
            throw new InvalidOperationException(
                $"Expected one technology-card AddChild call in {__originalMethod}, found {replacements}.");
        }
    }

    private static void AddChildPaced(LayoutBox<Widget, Widget> parent, Widget child)
    {
        if (s_constructing != null && parent is ScrollBox scrollBox && child is LayoutBox)
        {
            s_constructing.Rows.Enqueue((scrollBox, child));
            return;
        }
        parent.AddChild(child);
    }
}

/// <summary>
/// Each visible technology card rebuilds prerequisite markup, colors, price,
/// and button state before input. Two updates per second are sufficient for a
/// purchase menu and retain the vanilla purchase validation path.
/// </summary>
[HarmonyPatch]
internal static class TechPurchaseCardRefreshPatch
{
    private const int RefreshesPerSecond = 2;
    private static readonly long RefreshIntervalTicks = Stopwatch.Frequency / RefreshesPerSecond;
    private static readonly ConditionalWeakTable<object, Gate> Gates = new();
    private static readonly Type ClosureType = AccessTools.TypeByName(
        "Cosmoteer.Modes.Career.Comms.CommTechsTab+<>c__DisplayClass8_3")
        ?? throw new TypeLoadException("Technology card closure was not found.");

    private sealed class Gate
    {
        public long NextRefresh;
    }

    private static MethodBase TargetMethod() =>
        AccessTools.Method(ClosureType, "<.ctor>b__2")
        ?? throw new MissingMethodException(ClosureType.FullName, "<.ctor>b__2");

    private static bool Prefix(object __instance)
    {
        var gate = Gates.GetOrCreateValue(__instance);
        var now = Stopwatch.GetTimestamp();
        if (now < gate.NextRefresh)
        {
            return false;
        }
        gate.NextRefresh = now + RefreshIntervalTicks;
        return true;
    }
}
