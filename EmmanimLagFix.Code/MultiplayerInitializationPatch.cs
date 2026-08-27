using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Creating the initial multiplayer simulation is deliberately performed on a
/// worker thread by the game. Large, heavily-modded saves can keep that worker
/// busy long enough for SteamNetworkingSockets' service thread to be starved,
/// producing the characteristic WaitingForAck disconnect with zero packet
/// loss. Run only those two multiplayer launch workers below normal priority so
/// networking and the UI remain schedulable. The original priority is restored
/// even when game creation throws.
/// </summary>
[HarmonyPatch]
internal static class MultiplayerInitializationPatch
{
    [ThreadStatic]
    private static int _activeLaunchWorkerDepth;

    internal static bool IsLaunchWorkerActive => _activeLaunchWorkerDepth > 0;

    private sealed class Scope
    {
        public required long StartTimestamp;
        public required Thread Thread;
        public required ThreadPriority PreviousPriority;
        public required string Phase;
        public bool PriorityChanged;
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var assembly = typeof(Cosmoteer.Game.GameRoot).Assembly;

        var hostWorker = assembly
            .GetType("Cosmoteer.Gui.Multiplayer.GameLaunchFlow+HostLaunchFlow+<>c__DisplayClass7_0", throwOnError: true)!
            .GetMethod("<DoHostLaunchFlow>b__1", BindingFlags.Instance | BindingFlags.NonPublic);
        yield return hostWorker
            ?? throw new MissingMethodException("GameLaunchFlow.HostLaunchFlow", "initialization worker");

        var clientWorker = assembly
            .GetType("Cosmoteer.Gui.Multiplayer.GameLaunchFlow+ClientLaunchFlow+<>c__DisplayClass11_0", throwOnError: true)!
            .GetMethod("<OnStreamMessageReceived>b__0", BindingFlags.Instance | BindingFlags.NonPublic);
        yield return clientWorker
            ?? throw new MissingMethodException("GameLaunchFlow.ClientLaunchFlow", "initialization worker");
    }

    private static void Prefix(MethodBase __originalMethod, out Scope __state)
    {
        var thread = Thread.CurrentThread;
        var previousPriority = thread.Priority;
        var phase = __originalMethod.Name.Contains("DoHostLaunchFlow", StringComparison.Ordinal)
            ? "host simulation creation"
            : "client data decode and simulation creation";

        __state = new Scope
        {
            StartTimestamp = Stopwatch.GetTimestamp(),
            Thread = thread,
            PreviousPriority = previousPriority,
            Phase = phase
        };

        _activeLaunchWorkerDepth++;

        try
        {
            if (previousPriority > ThreadPriority.BelowNormal)
            {
                thread.Priority = ThreadPriority.BelowNormal;
                __state.PriorityChanged = true;
            }
        }
        catch (Exception ex)
        {
            Halfling.Logging.Logger.Log($"Emmanim Lag Fix could not lower multiplayer initialization thread priority: {ex.Message}");
        }

        Halfling.Logging.Logger.Log($"Emmanim Lag Fix began {phase} (thread priority: {thread.Priority}).");
    }

    private static Exception? Finalizer(Exception? __exception, Scope? __state)
    {
        if (__state is null)
        {
            return __exception;
        }

        var elapsed = Stopwatch.GetElapsedTime(__state.StartTimestamp);

        _activeLaunchWorkerDepth = Math.Max(0, _activeLaunchWorkerDepth - 1);

        if (__state.PriorityChanged)
        {
            try
            {
                __state.Thread.Priority = __state.PreviousPriority;
            }
            catch (Exception ex)
            {
                Halfling.Logging.Logger.Log($"Emmanim Lag Fix could not restore multiplayer initialization thread priority: {ex.Message}");
            }
        }

        var outcome = __exception is null ? "completed" : $"failed: {__exception.GetType().Name}";
        Halfling.Logging.Logger.Log(
            $"Emmanim Lag Fix {__state.Phase} {outcome} in {elapsed.TotalSeconds:F2} seconds.");
        return __exception;
    }
}

/// <summary>
/// Separately time GameInit.CreateGame while it is called from one of the two
/// launch workers above. On clients, subtracting this from the outer worker
/// duration exposes the data-deserialization/setup portion of first sync.
/// </summary>
[HarmonyPatch]
internal static class MultiplayerGameCreationTimingPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var assembly = typeof(Cosmoteer.Game.GameRoot).Assembly;
        var baseType = assembly.GetType("Cosmoteer.Modes.GameInit", throwOnError: true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (var type in assembly.GetTypes())
        {
            if (type == baseType || type.IsAbstract || !baseType.IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetMethod("CreateGame", flags) is { } method)
            {
                yield return method;
            }
        }
    }

    private static void Prefix(out long __state)
    {
        __state = MultiplayerInitializationPatch.IsLaunchWorkerActive
            ? Stopwatch.GetTimestamp()
            : 0;
    }

    private static Exception? Finalizer(object __instance, Exception? __exception, long __state)
    {
        if (__state != 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(__state);
            var outcome = __exception is null ? "completed" : $"failed: {__exception.GetType().Name}";
            Halfling.Logging.Logger.Log(
                $"Emmanim Lag Fix {__instance.GetType().Name}.CreateGame {outcome} in {elapsed.TotalSeconds:F2} seconds.");
        }

        return __exception;
    }
}
