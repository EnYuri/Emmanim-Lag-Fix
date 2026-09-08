using System.Globalization;
using System.Reflection;
using Cosmoteer.Game.Multiplayer;
using Halfling.Timing;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Optionally lets a client that renders slowly advance more than one input
/// tick per rendered frame.
///
/// <c>NetManager.GetTargetAdjustedDeltaTime</c> ends in
/// <c>Mathx.Min(value2, 1f / PhysicsUpdatesPerSecond)</c>. That second term
/// caps the game time credited per frame at exactly one input tick, so in
/// multiplayer the whole lockstep runs at no more than the slowest peer's
/// <b>frame rate</b>, whatever its CPU could actually simulate. Singleplayer
/// has no such cap: <c>SPManager.AdvanceNetworkTime</c> is a four-line stub and
/// <c>GameRoot.Update</c>'s do/while loop runs the simulation from the real
/// clock. This is why a save that is smooth alone crawls in multiplayer.
///
/// Measured on 2026-09-08 with a 12-core host (11 FastParallel workers) and a
/// 4-core client (3 workers): the client rendered 10-13 fps against the host's
/// 90-105, and the whole session ran at 4-8 of the nominal 30 input ticks per
/// second. The client's parallel width is 3.7x narrower, so its simulation tick
/// costs about 6.5x the host's, and the cap turns that into the world speed
/// both players see.
///
/// Raising the cap trades the slow client's frame rate for world speed: at N
/// ticks per frame its frame costs N simulation ticks, so its own rendering
/// falls while the world advances faster for everyone. Whether that is a good
/// trade depends on the players, which is why this is opt-in and off by default.
///
/// Deterministic and lockstep-safe. <c>Sim.OnInputTick</c> takes a fixed
/// <c>InputTickInterval</c>, so N ticks in one frame compute exactly what N
/// ticks in N frames compute; only local pacing changes. Peers may run
/// different values, exactly as they may already have different
/// "Minimum Target F.P.S." settings, which feed the same expression.
/// <c>IsReadyForTick</c> still gates every tick, so a client can never run
/// ahead of the inputs it has been sent.
/// </summary>
[HarmonyPatch(typeof(NetManager), "GetTargetAdjustedDeltaTime")]
internal static class NetworkTimeCatchUpPatch
{
    /// <summary>Input ticks a single frame may credit. 1 is vanilla.</summary>
    internal static readonly int TicksPerFrame;

    /// <summary>Frames whose credit this patch actually raised.</summary>
    private static long _raisedFrames;

    static NetworkTimeCatchUpPatch()
    {
        var ticks = 1;

        // Optional override, a single integer, beside the mod folder. Absent in a
        // normal install. Bounded: a large value would let a client with a long
        // input backlog burn through it in one frame, which is a visible speed-up
        // spike and the reason vanilla caps this at all.
        try
        {
            var path = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
                "..",
                "ticks-per-frame.txt"));
            if (File.Exists(path)
                && int.TryParse(
                    File.ReadAllText(path).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                ticks = Math.Clamp(parsed, 1, 4);
            }
        }
        catch (Exception)
        {
            // An unreadable override is not a reason to change pacing.
        }

        TicksPerFrame = ticks;
    }

    /// <summary>Harmony skips the class entirely when the override is absent.</summary>
    private static bool Prepare() => TicksPerFrame > 1;

    /// <summary>
    /// Vanilla returns <c>Min(frameCredit, oneTick)</c>. Multiply only the
    /// second term: a frame that was already inside its own delta time keeps
    /// vanilla's value, so a host rendering faster than 30 fps is untouched and
    /// only a client the cap was actually binding sees any change.
    /// </summary>
    private static void Postfix(NetManager __instance, ref Time __result)
    {
        var oneTick = 1f / __instance.Sim.Rules.PhysicsUpdatesPerSecond;
        if ((float)__result < oneTick)
        {
            return;
        }

        __result = oneTick * TicksPerFrame;
        _raisedFrames++;
    }

    /// <summary>Reads and clears the count of frames whose credit was raised.</summary>
    internal static long TakeRaisedFrames() => Interlocked.Exchange(ref _raisedFrames, 0);
}
