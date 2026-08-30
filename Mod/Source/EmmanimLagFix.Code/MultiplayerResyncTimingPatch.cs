using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// A resync rebuilds the game independently on the host and every client.
/// Record the three expensive background phases so a future long resync can be
/// attributed without changing serialization, scheduling, or game state.
/// </summary>
[HarmonyPatch]
internal static class MultiplayerResyncTimingPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var assembly = typeof(Cosmoteer.Game.GameRoot).Assembly;

        var hostWorker = assembly.GetType(
            "Cosmoteer.Game.Multiplayer.GameResyncFlow+HostResyncFlow+<>c__DisplayClass7_0",
            throwOnError: true)!;
        yield return AccessTools.Method(hostWorker, "<DoHostResyncFlow>b__0")
            ?? throw new MissingMethodException(hostWorker.FullName, "host save worker");
        yield return AccessTools.Method(hostWorker, "<DoHostResyncFlow>b__2")
            ?? throw new MissingMethodException(hostWorker.FullName, "host load worker");

        var clientWorker = assembly.GetType(
            "Cosmoteer.Game.Multiplayer.GameResyncFlow+ClientResyncFlow+<>c__DisplayClass9_0",
            throwOnError: true)!;
        yield return AccessTools.Method(clientWorker, "<OnStreamMessageReceived>b__0")
            ?? throw new MissingMethodException(clientWorker.FullName, "client load worker");
    }

    private static void Prefix(out long __state) =>
        __state = Stopwatch.GetTimestamp();

    private static Exception? Finalizer(
        MethodBase __originalMethod,
        Exception? __exception,
        long __state)
    {
        var phase = __originalMethod.DeclaringType!.FullName!.Contains("ClientResyncFlow", StringComparison.Ordinal)
            ? "client game load"
            : __originalMethod.Name.EndsWith("b__0", StringComparison.Ordinal)
                ? "host game save"
                : "host game load";
        var outcome = __exception is null ? "completed" : $"failed: {__exception.GetType().Name}";
        var elapsed = Stopwatch.GetElapsedTime(__state);
        Halfling.Logging.Logger.Log(
            $"Emmanim Lag Fix resync {phase} {outcome} in {elapsed.TotalSeconds:F2} seconds.");
        return __exception;
    }
}
