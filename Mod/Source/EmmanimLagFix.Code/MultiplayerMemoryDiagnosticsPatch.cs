using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cosmoteer.Game.Multiplayer;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Opt-in, low-frequency correlation between process/managed memory and the
/// multiplayer queues that can legitimately retain pooled messages. This is
/// diagnostic only and never mutates a queue or simulation state.
/// </summary>
[HarmonyPatch(typeof(BaseMPManager), nameof(BaseMPManager.Update))]
internal static class MultiplayerMemoryDiagnosticsPatch
{
    private const int ReportSeconds = 60;
    private static readonly long ReportIntervalTicks = Stopwatch.Frequency * ReportSeconds;
    private static readonly bool Enabled = File.Exists(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
        "..",
        "multiplayer-memory-diagnostics.flag")));

    private static long _nextReport = Stopwatch.GetTimestamp() + ReportIntervalTicks;
    private static int _lastGen0 = GC.CollectionCount(0);
    private static int _lastGen1 = GC.CollectionCount(1);
    private static int _lastGen2 = GC.CollectionCount(2);

    private static void Postfix(BaseMPManager __instance)
    {
        if (!Enabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var next = Volatile.Read(ref _nextReport);
        if (now < next || Interlocked.CompareExchange(ref _nextReport, now + ReportIntervalTicks, next) != next)
        {
            return;
        }

        WriteReport(__instance);
    }

    private static void WriteReport(BaseMPManager manager)
    {
        using var process = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();

        var queuedInputTicks = 0;
        var maximumPlayerQueue = 0;
        foreach (var player in manager._playerInfos.Values)
        {
            queuedInputTicks += player.QueuedInputTicks;
            maximumPlayerQueue = Math.Max(maximumPlayerQueue, player.QueuedInputTicks);
        }

        var connectionReceiveQueue = 0;
        long bytesPerSecond = 0;
        foreach (var connection in manager._connections)
        {
            connectionReceiveQueue += connection.QueuedMessageCount;
            bytesPerSecond += connection.CurrentBytesSentPerSecond;
        }

        var hostHashes = manager is MPHostManager host ? host._queuedIntegrityHashes.Count : 0;
        var ourHashes = manager is MPClientManager client ? client._ourIntegrityHashQueue.Count : 0;
        var theirHashes = manager is MPClientManager client2 ? client2._theirIntegrityHashQueue.Count : 0;
        var recordingBytes = manager._recording?.BaseStream.Length ?? 0;

        var sim = manager.Game.Sim;
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
        foreach (var groups in manager.Game.Gui.ShipGui.PaintToolbox._groupBoxes.Values)
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
            "[EmmanimLagFix.MultiplayerMemoryDiagnostics] " +
            $"role={(manager is MPHostManager ? "host" : "client")} tick={manager.NetworkInputTick} " +
            $"privateMiB={ToMiB(process.PrivateMemorySize64):F0} workingMiB={ToMiB(process.WorkingSet64):F0} " +
            $"managedMiB={ToMiB(GC.GetTotalMemory(false)):F0} heapMiB={ToMiB(gcInfo.HeapSizeBytes):F0} " +
            $"fragmentedMiB={ToMiB(gcInfo.FragmentedBytes):F0} handles={process.HandleCount} " +
            $"gc={gen0Delta}/{gen1Delta}/{gen2Delta} players={manager._playerInfos.Count} " +
            $"inputQueued={queuedInputTicks} inputMax={maximumPlayerQueue} outgoingInputs={manager._outgoingInputs.Count} " +
            $"hashes={hostHashes}/{ourHashes}/{theirHashes} connectionQueued={connectionReceiveQueue} " +
            $"sentKiBs={bytesPerSecond / 1024d:F1} recordingMiB={ToMiB(recordingBytes):F1} " +
            $"game={RuntimeHelpers.GetHashCode(manager.Game):X8} sim={RuntimeHelpers.GetHashCode(sim):X8} " +
            $"ships={sim.Ships.Count} parts={liveParts}/{blueprintParts} " +
            $"stasis={sim.Stasis.Count}/{preloadedStasis} decals={decalPickers}/{decalItems}");
    }

    private static double ToMiB(long bytes) => bytes / 1048576d;
}
