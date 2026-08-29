using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Game.Multiplayer;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Cosmoteer computes a whole-game integrity hash after every 30 Hz multiplayer
/// input tick. Preserve input, HostUpdate, and simulation cadence, but compute
/// the validation hash at an evenly-spaced 6 Hz. Both peers must use the same
/// code build because their integrity-hash sequences must remain identical.
/// </summary>
[HarmonyPatch(typeof(BaseMPManager), nameof(BaseMPManager.AdvanceNetworkTime))]
internal static class MultiplayerIntegrityHashThrottlePatch
{
    private const int TargetHashesPerSecond = 6;
    private const int ReportSeconds = 10;
    private static readonly long ReportIntervalTicks = Stopwatch.Frequency * ReportSeconds;

    private static readonly MethodInfo OriginalCheckMethod = AccessTools.Method(
        typeof(NetManager),
        nameof(NetManager.CheckGameSync),
        new[] { typeof(IntegrityHashPhase), typeof(int) })
        ?? throw new MissingMethodException(typeof(NetManager).FullName, nameof(NetManager.CheckGameSync));

    private static readonly MethodInfo ThrottledCheckMethod = AccessTools.Method(
        typeof(MultiplayerIntegrityHashThrottlePatch),
        nameof(CheckGameSyncAtReducedRate))
        ?? throw new MissingMethodException(
            typeof(MultiplayerIntegrityHashThrottlePatch).FullName,
            nameof(CheckGameSyncAtReducedRate));

    private static readonly bool DiagnosticsEnabled = File.Exists(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
        "..",
        "integrity-hash-diagnostics.flag")));

    private static long _windowStart = Stopwatch.GetTimestamp();
    private static long _nextReport = Stopwatch.GetTimestamp() + ReportIntervalTicks;
    private static long _windowCalls;
    private static long _windowElapsedTicks;
    private static long _windowMaximumTicks;

    internal static int SuccessfulTranspilerCount;

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        var codes = new List<CodeInstruction>(instructions);
        var matches = codes.Count(code => code.Calls(OriginalCheckMethod));
        if (matches != 1)
        {
            Halfling.Logging.Logger.Log(
                $"Emmanim Lag Fix did not reduce multiplayer integrity-hash frequency in " +
                $"{__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name}: " +
                $"expected one CheckGameSync call, found {matches}. Vanilla behavior was preserved.");
            return codes;
        }

        foreach (var code in codes)
        {
            if (code.Calls(OriginalCheckMethod))
            {
                code.opcode = OpCodes.Call;
                code.operand = ThrottledCheckMethod;
            }
        }

        Interlocked.Increment(ref SuccessfulTranspilerCount);
        return codes;
    }

    private static void CheckGameSyncAtReducedRate(
        BaseMPManager manager,
        IntegrityHashPhase phase,
        int bucket)
    {
        var inputTicksPerSecond = Math.Max(1, (int)Math.Round(manager.Rules.InputTicksPerSecond));
        if (!ShouldComputeHash(manager.NetworkInputTick, inputTicksPerSecond))
        {
            return;
        }

        var started = DiagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        manager.CheckGameSync(phase, bucket);
        if (started != 0)
        {
            RecordDiagnostics(manager.NetworkInputTick, Stopwatch.GetTimestamp() - started);
        }
    }

    internal static bool ShouldComputeHash(int inputTick, int inputTicksPerSecond)
    {
        if (inputTick <= 0 || inputTicksPerSecond <= TargetHashesPerSecond)
        {
            return true;
        }

        // For the vanilla 30 Hz input clock this selects ticks
        // 1, 6, 11, 16, 21, 26, ...: exactly six evenly-spaced hashes/second.
        return ((long)(inputTick - 1) * TargetHashesPerSecond) % inputTicksPerSecond
            < TargetHashesPerSecond;
    }

    private static void RecordDiagnostics(int inputTick, long elapsedTicks)
    {
        Interlocked.Increment(ref _windowCalls);
        Interlocked.Add(ref _windowElapsedTicks, elapsedTicks);
        UpdateMaximum(ref _windowMaximumTicks, elapsedTicks);

        var now = Stopwatch.GetTimestamp();
        var next = Volatile.Read(ref _nextReport);
        if (now < next || Interlocked.CompareExchange(ref _nextReport, now + ReportIntervalTicks, next) != next)
        {
            return;
        }

        var started = Interlocked.Exchange(ref _windowStart, now);
        var calls = Interlocked.Exchange(ref _windowCalls, 0);
        var total = Interlocked.Exchange(ref _windowElapsedTicks, 0);
        var maximum = Interlocked.Exchange(ref _windowMaximumTicks, 0);
        var seconds = Math.Max(0.001, (double)(now - started) / Stopwatch.Frequency);
        var totalMs = total * 1000d / Stopwatch.Frequency;
        var maximumMs = maximum * 1000d / Stopwatch.Frequency;

        Halfling.Logging.Logger.Log(
            $"[EmmanimLagFix.IntegrityHashDiagnostics] tick={inputTick} calls={calls} " +
            $"window={seconds:F2}s rate={calls / seconds:F2}Hz total={totalMs:F1}ms " +
            $"avg={(calls == 0 ? 0 : totalMs / calls):F2}ms max={maximumMs:F2}ms");
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
}
