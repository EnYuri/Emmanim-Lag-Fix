using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Game.Gui.Resources;
using Cosmoteer.Modes.Career.Comms;
using Halfling.Gui;
using Halfling.Application;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Both transfer UIs rebuild their ship/resource snapshots before input on
/// every rendered frame. A single refresh performs many resource-manager
/// traversals for every modded resource, even when nothing changed. Keep the
/// last rendered snapshot for at most 500 ms while leaving widget-local input
/// handlers untouched, so button presses and typed deltas still react
/// immediately.
/// </summary>
[HarmonyPatch]
internal static class TransferUiRefreshThrottlePatch
{
    private const int RefreshesPerSecond = 2;
    private static readonly long RefreshIntervalTicks = Stopwatch.Frequency / RefreshesPerSecond;
    private static readonly ConditionalWeakTable<object, Gate> Gates = new();

    private sealed class Gate
    {
        public long NextRefresh;
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CrewAndResourceTransferWindow), "OnUpdatingUIState")
            ?? throw new MissingMethodException(typeof(CrewAndResourceTransferWindow).FullName, "OnUpdatingUIState");
        yield return AccessTools.Method(typeof(CommTradeTab), "OnUpdatingUIState")
            ?? throw new MissingMethodException(typeof(CommTradeTab).FullName, "OnUpdatingUIState");
    }

    private static bool Prefix(object __instance)
    {
        TransferWidgetCreationThrottlePatch.Drain(__instance);

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

/// <summary>
/// Both transfer constructors build widgets on a worker thread, but enqueue all
/// AddChild calls onto the main thread at once. A heavily modded resource set can
/// therefore trigger a single fatal layout spike when the window first opens.
/// Capture those additions and admit only one row per rendered frame.
/// </summary>
[HarmonyPatch]
internal static class TransferWidgetCreationThrottlePatch
{
    private const int RowsPerFrame = 1;
    private static readonly ConditionalWeakTable<object, PendingRows> Queues = new();

    private sealed class PendingRows
    {
        public readonly Queue<Row> Rows = new();
    }

    private readonly record struct Row(Widget Parent, Widget Child, IList Destination);

    private sealed record ClosureFields(
        FieldInfo Widget,
        FieldInfo ParentLocals,
        FieldInfo? RootLocals,
        FieldInfo Owner,
        FieldInfo? Parent,
        FieldInfo Destination);

    private static readonly Type CrewClosureType = RequireType(
        "Cosmoteer.Game.Gui.Resources.CrewAndResourceTransferWindow+<>c__DisplayClass21_2");
    private static readonly Type TradeClosureType = RequireType(
        "Cosmoteer.Modes.Career.Comms.CommTradeTab+<>c__DisplayClass23_3");

    private static readonly ClosureFields CrewFields = CreateFields(
        CrewClosureType,
        "CS$<>8__locals2",
        rootLocalsName: null,
        "<>4__this",
        parentField: null,
        "_transferWidgets");

    private static readonly ClosureFields TradeFields = CreateFields(
        TradeClosureType,
        "CS$<>8__locals3",
        "CS$<>8__locals2",
        "<>4__this",
        "pageBox",
        "_tradeWidgets");

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(CrewClosureType, "<.ctor>b__9")
            ?? throw new MissingMethodException(CrewClosureType.FullName, "<.ctor>b__9");
        yield return AccessTools.Method(TradeClosureType, "<.ctor>b__8")
            ?? throw new MissingMethodException(TradeClosureType.FullName, "<.ctor>b__8");
    }

    private static bool Prefix(object __instance)
    {
        var fields = __instance.GetType() == CrewClosureType ? CrewFields : TradeFields;
        var widget = (Widget)(fields.Widget.GetValue(__instance)
            ?? throw new InvalidOperationException("Transfer row closure has no widget."));
        var locals = fields.ParentLocals.GetValue(__instance)
            ?? throw new InvalidOperationException("Transfer row closure has no parent locals.");
        if (fields.RootLocals != null)
        {
            locals = fields.RootLocals.GetValue(locals)
                ?? throw new InvalidOperationException("Transfer row closure has no root locals.");
        }
        var owner = fields.Owner.GetValue(locals)
            ?? throw new InvalidOperationException("Transfer row closure has no owner.");
        var parent = fields.Parent == null
            ? (Widget)owner
            : (Widget)(fields.Parent.GetValue(locals)
                ?? throw new InvalidOperationException("Trade row closure has no page box."));
        var destination = (IList)(fields.Destination.GetValue(owner)
            ?? throw new InvalidOperationException("Transfer window has no row collection."));

        Queues.GetOrCreateValue(owner).Rows.Enqueue(new Row(parent, widget, destination));
        return false;
    }

    internal static void Drain(object owner)
    {
        if (!Queues.TryGetValue(owner, out var pending))
        {
            return;
        }

        for (var i = 0; i < RowsPerFrame && pending.Rows.Count > 0; i++)
        {
            var row = pending.Rows.Dequeue();
            if (row.Parent is ScrollBox pageBox)
            {
                pageBox.AddChild(row.Child);
            }
            else
            {
                ((CrewAndResourceTransferWindow)row.Parent).AddChild(row.Child);
            }
            row.Destination.Add(row.Child);
        }
    }

    private static Type RequireType(string name)
    {
        return AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{name} was not found.");
    }

    private static ClosureFields CreateFields(
        Type closureType,
        string parentLocalsName,
        string? rootLocalsName,
        string ownerName,
        string? parentField,
        string destinationName)
    {
        var widget = RequireField(closureType, "tw");
        var parentLocals = RequireField(closureType, parentLocalsName);
        var rootLocals = rootLocalsName == null
            ? null
            : RequireField(parentLocals.FieldType, rootLocalsName);
        var localsType = rootLocals?.FieldType ?? parentLocals.FieldType;
        var owner = RequireField(localsType, ownerName);
        var parent = parentField == null
            ? null
            : RequireField(localsType, parentField);
        var destination = RequireField(owner.FieldType, destinationName);
        return new ClosureFields(widget, parentLocals, rootLocals, owner, parent, destination);
    }

    private static FieldInfo RequireField(Type type, string name)
    {
        return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.FullName, name);
    }
}

/// <summary>
/// Vanilla creates every transfer row in one uninterrupted worker task. That
/// avoids a direct main-thread block but can still contend with simulation and
/// networking on large mod lists. Yield briefly after each row is handed to the
/// main thread so construction remains cooperative.
/// </summary>
[HarmonyPatch]
internal static class TransferWidgetBackgroundPacingPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var crewType = AccessTools.TypeByName(
            "Cosmoteer.Game.Gui.Resources.CrewAndResourceTransferWindow+<>c__DisplayClass21_0")
            ?? throw new TypeLoadException("Crew transfer constructor closure was not found.");
        var tradeType = AccessTools.TypeByName(
            "Cosmoteer.Modes.Career.Comms.CommTradeTab+<>c__DisplayClass23_0")
            ?? throw new TypeLoadException("Trade constructor closure was not found.");

        yield return AccessTools.Method(crewType, "<.ctor>b__8")
            ?? throw new MissingMethodException(crewType.FullName, "<.ctor>b__8");
        yield return AccessTools.Method(tradeType, "<.ctor>b__6")
            ?? throw new MissingMethodException(tradeType.FullName, "<.ctor>b__6");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var execute = AccessTools.Method(
            typeof(Director),
            nameof(Director.ExecuteOnMainThread),
            new[] { typeof(Action) })
            ?? throw new MissingMethodException(typeof(Director).FullName, nameof(Director.ExecuteOnMainThread));
        var paced = AccessTools.Method(typeof(TransferWidgetBackgroundPacingPatch), nameof(ExecutePaced))
            ?? throw new MissingMethodException(typeof(TransferWidgetBackgroundPacingPatch).FullName, nameof(ExecutePaced));

        var replacements = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(execute))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = paced;
                replacements++;
            }
            yield return instruction;
        }

        if (replacements != 1)
        {
            throw new InvalidOperationException(
                $"Expected one ExecuteOnMainThread call in transfer row builder, found {replacements}.");
        }
    }

    private static void ExecutePaced(Director director, Action action)
    {
        director.ExecuteOnMainThread(action);
        Thread.Sleep(1);
    }
}
