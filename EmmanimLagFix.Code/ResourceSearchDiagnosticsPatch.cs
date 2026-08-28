using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Cosmoteer.Ships.Resources;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Opt-in attribution for the expensive per-sink resource-source search. The
/// patch is inert unless resource-search-diagnostics.flag exists beside the
/// mod's Code directory. It records timing only; simulation behavior is not
/// changed. Remove the flag after the targeted capture.
/// </summary>
[HarmonyPatch]
internal static class ResourceSearchDiagnosticsPatch
{
    private const int ReportSeconds = 10;
    private static readonly long ReportIntervalTicks = Stopwatch.Frequency * ReportSeconds;
    private static readonly ConcurrentDictionary<string, Stats> Statistics = new();
    private static readonly bool Enabled = File.Exists(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
        "..",
        "resource-search-diagnostics.flag")));
    private static long s_nextReport = Stopwatch.GetTimestamp() + ReportIntervalTicks;

    private sealed class Stats
    {
        public long Count;
        public long TotalTicks;
        public long MaximumTicks;
        public long TotalCandidates;
    }

    private readonly struct SearchState
    {
        public readonly long Started;

        public SearchState(long started)
        {
            Started = started;
        }
    }

    private static MethodBase TargetMethod()
    {
        var sinkInfoType = AccessTools.Inner(typeof(ResourceManager), "SinkInfo")
            ?? throw new TypeLoadException("ResourceManager.SinkInfo was not found.");
        return AccessTools.Method(typeof(ResourceManager), "SearchForSources", new[] { sinkInfoType })
            ?? throw new MissingMethodException(typeof(ResourceManager).FullName, "SearchForSources(SinkInfo)");
    }

    private static void Prefix(out SearchState __state)
    {
        __state = new SearchState(Enabled ? Stopwatch.GetTimestamp() : 0);
    }

    private static void Postfix(ResourceManager __instance, ResourceManager.SinkInfo sink, SearchState __state)
    {
        if (__state.Started == 0)
        {
            return;
        }

        var elapsed = Stopwatch.GetTimestamp() - __state.Started;
        var sinkObject = sink.Sink;
        var part = sinkObject.Part;
        var sinkName = part?.Rules.ID.ToString() ?? sinkObject.GetType().FullName ?? sinkObject.GetType().Name;
        var key = $"ship={__instance.Ship.UniqueID};parts={__instance.Ship.Parts.Count};sink={sinkName};resource={sink.SourceType}";
        var stats = Statistics.GetOrAdd(key, static _ => new Stats());
        Interlocked.Increment(ref stats.Count);
        Interlocked.Add(ref stats.TotalTicks, elapsed);
        Interlocked.Add(ref stats.TotalCandidates, sink.Sources.Count);
        UpdateMaximum(ref stats.MaximumTicks, elapsed);

        var now = Stopwatch.GetTimestamp();
        var next = Volatile.Read(ref s_nextReport);
        if (now < next || Interlocked.CompareExchange(ref s_nextReport, now + ReportIntervalTicks, next) != next)
        {
            return;
        }

        WriteReport();
    }

    private static void UpdateMaximum(ref long maximum, long value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var prior = Interlocked.CompareExchange(ref maximum, value, observed);
            if (prior == observed)
            {
                return;
            }
            observed = prior;
        }
    }

    private static void WriteReport()
    {
        var rows = Statistics.Select(pair =>
        {
            var count = Volatile.Read(ref pair.Value.Count);
            var ticks = Volatile.Read(ref pair.Value.TotalTicks);
            var max = Volatile.Read(ref pair.Value.MaximumTicks);
            var candidates = Volatile.Read(ref pair.Value.TotalCandidates);
            return new
            {
                pair.Key,
                Count = count,
                TotalMs = ticks * 1000d / Stopwatch.Frequency,
                MaxMs = max * 1000d / Stopwatch.Frequency,
                AverageCandidates = count == 0 ? 0d : (double)candidates / count
            };
        })
        .OrderByDescending(row => row.TotalMs)
        .Take(12)
        .ToArray();

        Logger.Log("[EmmanimLagFix.ResourceSearchDiagnostics] top cumulative source searches:");
        foreach (var row in rows)
        {
            Logger.Log(
                $"[EmmanimLagFix.ResourceSearchDiagnostics] total={row.TotalMs:F1}ms max={row.MaxMs:F1}ms " +
                $"count={row.Count} avgCandidates={row.AverageCandidates:F1} {row.Key}");
        }
    }
}
