using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Splits the main thread's frame three ways, so a slow peer can be told apart
/// from a waiting one without a profiler on its machine.
///
/// <c>frameMs</c> says a frame took 34 ms; it does not say why. The three
/// phases <c>Halfling.Application.Director</c> runs answer that directly:
///
/// <list type="bullet">
/// <item><c>input</c> — <c>DoInput</c>, message pump and input dispatch.</item>
/// <item><c>update</c> — <c>DoUpdate</c>, which contains the whole simulation
/// tick, so in multiplayer it also contains any wait for the lockstep gate.</item>
/// <item><c>draw</c> — <c>DoDraw</c>, which runs inside
/// <c>GraphicsManager.BeginDraw</c>, so it carries the present and any vsync or
/// GPU back-pressure with it.</item>
/// </list>
///
/// A client whose 34 ms is nearly all <c>draw</c> is GPU-bound and its
/// simulation is fine. One whose time is in <c>update</c> is simulation-bound,
/// and the tick rate it can offer the host is its own ceiling. The two call for
/// opposite fixes, and the multiplayer diagnostics line cannot currently tell
/// them apart.
///
/// The probe is six <c>Stopwatch.GetTimestamp</c> calls per frame, accumulated
/// into three longs and read once per reporting window. It rides the existing
/// memory-diagnostics flags rather than adding a flag of its own, so a peer that
/// already opted in needs no new file. With neither flag present
/// <c>Prepare</c> declines and nothing is patched at all.
/// </summary>
[HarmonyPatch]
internal static class FramePhaseDiagnosticsPatch
{
    internal static readonly bool Enabled =
        FlagExists("multiplayer-memory-diagnostics.flag")
        || FlagExists("singleplayer-memory-diagnostics.flag");

    private static long _inputTicks;
    private static long _updateTicks;
    private static long _drawTicks;
    private static long _frames;

    /// <summary>
    /// Frames counted by the most recent <see cref="Snapshot"/>. The sim-phase
    /// snapshot divides by this, so it must run after this one in each sample.
    /// </summary>
    internal static long LastFrames { get; private set; }

    private static bool FlagExists(string name) => File.Exists(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
        "..",
        name)));

    private static Type DirectorType =>
        AccessTools.TypeByName("Halfling.Application.Director")
        ?? throw new TypeLoadException("Halfling.Application.Director was not found.");

    /// <summary>Harmony skips the whole class when this returns false.</summary>
    private static bool Prepare() => Enabled;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "DoInput", "DoUpdate", "DoDraw" })
        {
            var method = AccessTools.DeclaredMethod(DirectorType, name);
            if (method == null)
            {
                Halfling.Logging.Logger.Log(
                    $"[EmmanimLagFix] Director.{name} was not found; frame phase timing is off.");
                continue;
            }

            yield return method;
        }
    }

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(long __state, MethodBase __originalMethod)
    {
        var elapsed = Stopwatch.GetTimestamp() - __state;
        switch (__originalMethod.Name)
        {
            case "DoInput":
                _inputTicks += elapsed;
                break;
            case "DoUpdate":
                _updateTicks += elapsed;
                break;
            default:
                _drawTicks += elapsed;
                // One frame draws once, so this is the frame counter.
                _frames++;
                break;
        }
    }

    /// <summary>
    /// Mean milliseconds per frame in each phase over the window, plus the
    /// frames the window actually drew, then resets. Returns dashes before the
    /// first frame or when the class was never patched.
    /// </summary>
    internal static string Snapshot(double elapsedSeconds)
    {
        var frames = Interlocked.Exchange(ref _frames, 0);
        var input = Interlocked.Exchange(ref _inputTicks, 0);
        var update = Interlocked.Exchange(ref _updateTicks, 0);
        var draw = Interlocked.Exchange(ref _drawTicks, 0);
        LastFrames = frames;

        if (!Enabled || frames <= 0)
        {
            return "-/-/-";
        }

        var perFrameMs = 1000d / Stopwatch.Frequency / frames;
        return $"{input * perFrameMs:F1}/{update * perFrameMs:F1}/{draw * perFrameMs:F1}"
            + $"@{frames / Math.Max(elapsedSeconds, 0.001):F0}";
    }
}
