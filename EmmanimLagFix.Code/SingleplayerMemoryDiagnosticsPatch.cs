using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Simulation.Stasis;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Opt-in, low-frequency single-player memory/allocation correlation. This is
/// diagnostic only and never mutates the simulation or retained collections.
/// </summary>
[HarmonyPatch(typeof(GameRoot), nameof(GameRoot.Update), typeof(Action))]
internal static class SingleplayerMemoryDiagnosticsPatch
{
    private const int ReportSeconds = 60;
    private static readonly long ReportIntervalTicks = Stopwatch.Frequency * ReportSeconds;
    private static readonly bool Enabled = File.Exists(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
        "..",
        "singleplayer-memory-diagnostics.flag")));

    private static long _lastReport = Stopwatch.GetTimestamp();
    private static long _nextReport = _lastReport + ReportIntervalTicks;
    private static long _lastAllocated = GC.GetTotalAllocatedBytes(false);
    private static int _lastGen0 = GC.CollectionCount(0);
    private static int _lastGen1 = GC.CollectionCount(1);
    private static int _lastGen2 = GC.CollectionCount(2);

    private static void Postfix(GameRoot __instance)
    {
        if (!Enabled || __instance.NetManager is BaseMPManager)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var next = Volatile.Read(ref _nextReport);
        if (now < next || Interlocked.CompareExchange(ref _nextReport, now + ReportIntervalTicks, next) != next)
        {
            return;
        }

        WriteReport(__instance, now);
    }

    private static void WriteReport(GameRoot game, long now)
    {
        using var process = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        var elapsedSeconds = Math.Max((now - Interlocked.Exchange(ref _lastReport, now)) / (double)Stopwatch.Frequency, 0.001d);
        var allocated = GC.GetTotalAllocatedBytes(false);
        var allocatedDelta = allocated - Interlocked.Exchange(ref _lastAllocated, allocated);

        var sim = game.Sim;
        var liveParts = 0;
        var blueprintParts = 0;
        foreach (var ship in sim.Ships)
        {
            liveParts += ship.Parts.Count;
            blueprintParts += ship.BlueprintParts.Count;
        }

        var preloadedStasis = 0;
        foreach (var spawner in sim.Stasis)
        {
            if (MemoryDiagnosticsCommon.IsSpawnerPreloaded(spawner))
            {
                preloadedStasis++;
            }
        }

        var decalPickers = 0;
        var decalItems = 0;
        foreach (var groups in game.Gui.ShipGui.PaintToolbox._groupBoxes.Values)
        {
            decalPickers += groups.Count;
            foreach (var group in groups)
            {
                decalItems += group.Children.Count;
            }
        }

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var gen0Delta = gen0 - Interlocked.Exchange(ref _lastGen0, gen0);
        var gen1Delta = gen1 - Interlocked.Exchange(ref _lastGen1, gen1);
        var gen2Delta = gen2 - Interlocked.Exchange(ref _lastGen2, gen2);

        Halfling.Logging.Logger.Log(
            "[EmmanimLagFix.SingleplayerMemoryDiagnostics] " +
            $"game={RuntimeHelpers.GetHashCode(game):X8} sim={RuntimeHelpers.GetHashCode(sim):X8} " +
            $"mode={sim.Mode.GetType().Name} tick={sim.Tick} " +
            $"privateMiB={ToMiB(process.PrivateMemorySize64):F0} workingMiB={ToMiB(process.WorkingSet64):F0} " +
            $"managedMiB={ToMiB(GC.GetTotalMemory(false)):F0} heapMiB={ToMiB(gcInfo.HeapSizeBytes):F0} " +
            $"fragmentedMiB={ToMiB(gcInfo.FragmentedBytes):F0} handles={process.HandleCount} " +
            $"allocatedMiBs={ToMiB(allocatedDelta) / elapsedSeconds:F1} gc={gen0Delta}/{gen1Delta}/{gen2Delta} " +
            $"ships={sim.Ships.Count} parts={liveParts}/{blueprintParts} " +
            $"stasis={sim.Stasis.Count}/{preloadedStasis} decals={decalPickers}/{decalItems}");
    }

    private static double ToMiB(long bytes) => bytes / 1048576d;
}

internal static class MemoryDiagnosticsCommon
{
    internal static bool IsSpawnerPreloaded(StasisSpawner spawner) =>
        spawner.SupportsPreloading && spawner.IsPreloaded;
}
