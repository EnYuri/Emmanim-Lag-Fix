using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
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

    /// <summary>
    /// Per-frame samples of each player's lockstep input queue. The summed
    /// <c>inputQueued</c> field cannot say which peer is failing to supply
    /// inputs; vanilla's own <see cref="MPPlayer.IsDelayingGame"/> can, but it
    /// is only meaningful at the instant the readiness gate ran, so it is
    /// sampled every frame and reported as a fraction. Read-only.
    /// </summary>
    private sealed class PlayerSample
    {
        public string Name = string.Empty;
        public bool IsLocal;
        public long Frames;
        public long DelayingFrames;
        public long QueueSum;
        public int LastQueue;
        public double LatencyMs;
    }

    private static readonly Dictionary<object, PlayerSample> Samples = new();

    /// <summary>
    /// Frame times over the reporting window, bucketed by whole milliseconds so
    /// a percentile is exact enough without keeping every sample. The last
    /// bucket collects everything at or above its index.
    /// </summary>
    private static readonly int[] FrameBuckets = new int[1001];

    private static long _lastFrameStamp;
    private static long _frameCount;
    private static double _frameTotalMs;

    /// <summary>
    /// CPU seconds and wall-clock at the previous report, so the window's
    /// average number of busy cores can be differenced out of them. This is what
    /// separates "the machine is saturated" from "the machine is waiting", which
    /// no size or count in this line can say.
    /// </summary>
    private static TimeSpan _lastCpu;
    private static long _lastCpuStamp;

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

        SamplePlayers(__instance);
        SampleFrame();

        var now = Stopwatch.GetTimestamp();
        var next = Volatile.Read(ref _nextReport);
        if (now < next || Interlocked.CompareExchange(ref _nextReport, now + ReportIntervalTicks, next) != next)
        {
            return;
        }

        WriteReport(__instance);
    }

    /// <summary>
    /// Records the interval since the previous frame. BaseMPManager.Update runs
    /// on the main thread only, so this needs no synchronisation.
    /// </summary>
    private static void SampleFrame()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = _lastFrameStamp;
        _lastFrameStamp = now;

        if (previous == 0)
        {
            return;
        }

        var ms = (now - previous) * 1000d / Stopwatch.Frequency;
        _frameCount++;
        _frameTotalMs += ms;

        var bucket = (int)ms;
        FrameBuckets[bucket < 0 ? 0 : Math.Min(bucket, FrameBuckets.Length - 1)]++;
    }

    /// <summary>
    /// Mean and 95th-percentile frame time for the window, then resets it. The
    /// mean says how fast the machine is running and the percentile says whether
    /// it is hitching, which the mean alone hides.
    /// </summary>
    private static string FormatFrameTimes()
    {
        if (_frameCount == 0)
        {
            return "-/-";
        }

        var mean = _frameTotalMs / _frameCount;
        var target = _frameCount * 95 / 100;
        long running = 0;
        var p95 = FrameBuckets.Length - 1;
        for (var i = 0; i < FrameBuckets.Length; i++)
        {
            running += FrameBuckets[i];
            if (running >= target)
            {
                p95 = i;
                break;
            }
        }

        Array.Clear(FrameBuckets);
        _frameCount = 0;
        _frameTotalMs = 0;
        return $"{mean:F1}/{p95}";
    }

    /// <summary>
    /// Average number of cores busy over the window. At or above the core count
    /// the process is CPU-bound; near zero it is waiting on something else.
    /// </summary>
    private static string FormatCpuLoad(Process process)
    {
        var cpu = process.TotalProcessorTime;
        var stamp = Stopwatch.GetTimestamp();
        var previousCpu = _lastCpu;
        var previousStamp = _lastCpuStamp;
        _lastCpu = cpu;
        _lastCpuStamp = stamp;

        if (previousStamp == 0)
        {
            return "-";
        }

        var elapsed = (stamp - previousStamp) / (double)Stopwatch.Frequency;
        return elapsed <= 0 ? "-" : ((cpu - previousCpu).TotalSeconds / elapsed).ToString("F1");
    }

    private static void SamplePlayers(BaseMPManager manager)
    {
        var localID = manager.LocalPlayerID;
        foreach (var player in manager._playerInfos.Values)
        {
            if (!Samples.TryGetValue(player, out var sample))
            {
                sample = new PlayerSample();
                Samples.Add(player, sample);
            }

            sample.Name = player.Name;
            sample.IsLocal = player.PlayerID == localID;
            sample.Frames++;
            if (player.IsDelayingGame)
            {
                sample.DelayingFrames++;
            }

            sample.LastQueue = player.QueuedInputTicks;
            sample.QueueSum += sample.LastQueue;
            sample.LatencyMs = player.Latency.Milliseconds;
        }
    }

    /// <summary>
    /// The relayed copy drops the running queue average and shortens the field
    /// names, because the chat payload is capped at 200 characters and anything
    /// past it is cut off the end - which is where the per-player block sits.
    ///
    /// A player's own entry always reads <c>delay=0.0%</c>: the local player
    /// generates its own input tick, so it is never the one missing. Only each
    /// side's view of the *remote* player carries information.
    /// </summary>
    private static string FormatPlayerSamples(bool compact)
    {
        var text = new StringBuilder();
        foreach (var sample in Samples.Values)
        {
            if (text.Length > 0)
            {
                text.Append('|');
            }

            var name = sample.Name.Replace(' ', '_').Replace('|', '_').Replace('=', '_');
            var delaying = sample.Frames > 0 ? sample.DelayingFrames * 100d / sample.Frames : 0d;
            var averageQueue = sample.Frames > 0 ? sample.QueueSum / (double)sample.Frames : 0d;
            text.Append(name).Append(sample.IsLocal ? ":local" : ":remote");

            if (!compact)
            {
                text.Append(",q=").Append(sample.LastQueue)
                    .Append(",avg=").Append(averageQueue.ToString("F1"))
                    .Append(",delay=").Append(delaying.ToString("F1")).Append('%')
                    .Append(",lat=").Append(sample.LatencyMs.ToString("F0")).Append("ms");
                continue;
            }

            text.Append(",q=").Append(sample.LastQueue)
                .Append(",d=").Append(delaying.ToString("F0")).Append('%')
                .Append(",l=").Append(sample.LatencyMs.ToString("F0"));
        }

        return text.Length > 0 ? text.ToString() : "none";
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

        // Both lines are built from the same window, so the samples are read
        // twice and cleared once, after.
        var perPlayer = FormatPlayerSamples(compact: false);
        var perPlayerCompact = FormatPlayerSamples(compact: true);
        Samples.Clear();

        var frameTimes = FormatFrameTimes();
        var cpuLoad = FormatCpuLoad(process);

        Halfling.Logging.Logger.Log(
            "[EmmanimLagFix.MultiplayerMemoryDiagnostics] " +
            $"role={(manager is MPHostManager ? "host" : "client")} tick={manager.NetworkInputTick} " +
            $"privateMiB={ToMiB(process.PrivateMemorySize64):F0} workingMiB={ToMiB(process.WorkingSet64):F0} " +
            $"managedMiB={ToMiB(GC.GetTotalMemory(false)):F0} heapMiB={ToMiB(gcInfo.HeapSizeBytes):F0} " +
            $"fragmentedMiB={ToMiB(gcInfo.FragmentedBytes):F0} handles={process.HandleCount} " +
            $"gc={gen0Delta}/{gen1Delta}/{gen2Delta} frameMs={frameTimes} cpuCores={cpuLoad} " +
            $"players={manager._playerInfos.Count} " +
            $"inputQueued={queuedInputTicks} inputMax={maximumPlayerQueue} outgoingInputs={manager._outgoingInputs.Count} " +
            $"hashes={hostHashes}/{ourHashes}/{theirHashes} connectionQueued={connectionReceiveQueue} " +
            $"sentKiBs={bytesPerSecond / 1024d:F1} recordingMiB={ToMiB(recordingBytes):F1} " +
            $"game={RuntimeHelpers.GetHashCode(manager.Game):X8} sim={RuntimeHelpers.GetHashCode(sim):X8} " +
            $"ships={sim.Ships.Count} parts={liveParts}/{blueprintParts} " +
            $"stasis={sim.Stasis.Count}/{preloadedStasis} decals={decalPickers}/{decalItems} " +
            $"perPlayer=[{perPlayer}]");

        // The host cannot see why a client is late, only that it is, so the
        // client hands over the few fields that answer it. Kept short because
        // the game truncates chat text at 200 characters.
        PeerDiagnosticsRelayPatch.MaybeSend(
            manager,
            $"t={manager.NetworkInputTick} ft={frameTimes} cpu={cpuLoad} "
            + $"pv={ToMiB(process.PrivateMemorySize64):F0} hp={ToMiB(gcInfo.HeapSizeBytes):F0} "
            + $"gc={gen0Delta}/{gen1Delta}/{gen2Delta} q={queuedInputTicks}/{maximumPlayerQueue} "
            + $"cq={connectionReceiveQueue} sh={sim.Ships.Count} pt={liveParts} "
            + $"pp=[{perPlayerCompact}]");
    }

    private static double ToMiB(long bytes) => bytes / 1048576d;
}
