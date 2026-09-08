using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Splits the update phase into the simulation step and the game mode, and -
/// the point of the whole thing - counts how many times each runs per rendered
/// frame.
///
/// <c>GameRoot.Update</c> is a <c>do { ... } while (NetManager.AdvanceNetworkTime
/// (out isInputTick))</c> loop, so a peer that has fallen behind runs several
/// simulation ticks inside one frame. Without a count, a large
/// <c>phaseMs</c> update figure is ambiguous between two unrelated problems:
///
///   one heavy tick   -> simulation scaling, fight the part count
///   several cheap ticks -> a catch-up spiral, fight lockstep and frame pacing
///
/// A 2026-09-08 multiplayer session had the host at 4.1 ms of update per frame
/// and the client at 56.2 ms, with the session tick rate collapsing from 21/s to
/// 5/s as the sector grew from 6 to 346 ships. Which of the two above that was
/// could not be decided from the data being collected, which is why this exists.
///
/// <c>Mode.Update</c> runs unconditionally on every loop iteration, so its call
/// count is the true iteration count; <c>Sim.Update</c> is additionally gated on
/// the simulation not being frozen, so its count is the number of real steps.
/// Both are reported.
/// </summary>
[HarmonyPatch]
internal static class SimPhaseDiagnosticsPatch
{
    private static long _simTicks;
    private static long _simCalls;
    private static long _modeTicks;
    private static long _modeCalls;

    /// <summary>Set once both targets resolved, for the smoke test.</summary>
    internal static bool Applied { get; private set; }

    /// <summary>Harmony skips the whole class when this returns false.</summary>
    private static bool Prepare() => FramePhaseDiagnosticsPatch.Enabled;

    private static MethodBase? SimUpdate() =>
        AccessTools.DeclaredMethod(
            AccessTools.TypeByName("Cosmoteer.Simulation.SimRoot") ?? typeof(void), "Update", Type.EmptyTypes);

    /// <summary>
    /// Resolved through <c>GameRoot.Mode</c>'s declared type rather than by
    /// name, so a namespace change cannot silently turn the counter off.
    /// </summary>
    private static MethodBase? ModeUpdate()
    {
        var modeType = AccessTools.TypeByName("Cosmoteer.Game.GameRoot")
            ?.GetProperty("Mode", AccessTools.all)?.PropertyType;
        return modeType == null ? null : AccessTools.Method(modeType, "Update", Type.EmptyTypes);
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var sim = SimUpdate();
        var mode = ModeUpdate();

        if (sim == null)
        {
            Halfling.Logging.Logger.Log("[EmmanimLagFix] SimRoot.Update was not found; sim phase timing is off.");
        }
        else
        {
            yield return sim;
        }

        if (mode == null)
        {
            Halfling.Logging.Logger.Log("[EmmanimLagFix] GameModeManager.Update was not found; mode phase timing is off.");
        }
        else
        {
            yield return mode;
        }

        Applied = sim != null && mode != null;
    }

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(long __state, MethodBase __originalMethod)
    {
        var elapsed = Stopwatch.GetTimestamp() - __state;
        if (__originalMethod.DeclaringType?.Name == "SimRoot")
        {
            _simTicks += elapsed;
            _simCalls++;
        }
        else
        {
            _modeTicks += elapsed;
            _modeCalls++;
        }
    }

    /// <summary>
    /// <c>sim=&lt;ms per frame&gt;/&lt;steps per frame&gt; mode=&lt;ms per frame&gt;/&lt;iterations per frame&gt;</c>,
    /// then resets. Reads the frame count the frame-phase snapshot just took, so
    /// it must be called after that one in the same sample.
    /// </summary>
    internal static string Snapshot()
    {
        var simTicks = Interlocked.Exchange(ref _simTicks, 0);
        var simCalls = Interlocked.Exchange(ref _simCalls, 0);
        var modeTicks = Interlocked.Exchange(ref _modeTicks, 0);
        var modeCalls = Interlocked.Exchange(ref _modeCalls, 0);

        var frames = FramePhaseDiagnosticsPatch.LastFrames;
        if (!FramePhaseDiagnosticsPatch.Enabled || frames <= 0)
        {
            return "-/- -/-";
        }

        var perFrameMs = 1000d / Stopwatch.Frequency / frames;
        return $"sim={simTicks * perFrameMs:F1}/{(double)simCalls / frames:F2} "
            + $"mode={modeTicks * perFrameMs:F1}/{(double)modeCalls / frames:F2}";
    }
}
