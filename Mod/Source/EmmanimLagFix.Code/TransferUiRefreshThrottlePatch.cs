using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Game.Gui.Resources;
using Cosmoteer.Modes.Career.Comms;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Both transfer UIs rebuild their ship/resource snapshots before input on
/// every rendered frame. A single refresh performs many resource-manager
/// traversals for every modded resource, even when nothing changed. Keep the
/// last rendered snapshot for at most 200 ms while leaving widget-local input
/// handlers untouched, so button presses and typed deltas still react
/// immediately.
/// </summary>
[HarmonyPatch]
internal static class TransferUiRefreshThrottlePatch
{
    private const int RefreshesPerSecond = 5;
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
