using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// The build toolbox recomputes every blueprint statistic before every input
/// frame. Large modded blueprints make those display-only totals expensive.
/// Refresh the labels and bars four times per second; construction state,
/// editor input, affordability checks, and authoritative ship data are not
/// changed.
/// </summary>
[HarmonyPatch]
internal static class BuildToolboxStatsThrottlePatch
{
    private const int RefreshesPerSecond = 4;
    private static readonly long RefreshIntervalTicks = Stopwatch.Frequency / RefreshesPerSecond;
    private static readonly ConditionalWeakTable<object, Gate> Gates = new();
    private static readonly Type StatsGuiType = AccessTools.TypeByName(
        "Cosmoteer.Game.Gui.Build.Stats.BuildToolboxStatsGui")
        ?? throw new TypeLoadException("BuildToolboxStatsGui was not found.");

    private sealed class Gate
    {
        public long NextRefresh;
    }

    private static MethodBase TargetMethod() =>
        AccessTools.Method(StatsGuiType, "Update")
        ?? throw new MissingMethodException(StatsGuiType.FullName, "Update");

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
