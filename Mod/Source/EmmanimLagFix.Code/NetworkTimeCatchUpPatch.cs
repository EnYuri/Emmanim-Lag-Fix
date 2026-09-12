using System.Diagnostics;
using System.Globalization;
using Cosmoteer.Game.Multiplayer;
using Halfling.Timing;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Lets a multiplayer peer that renders slowly credit as much game time per
/// frame as real time actually elapsed, instead of the much smaller amount
/// vanilla allows.
///
/// <c>NetManager.GetTargetAdjustedDeltaTime</c> ends in
///
/// <code>
/// float num    = 1f / Settings.TargetFps;
/// float value2 = (App.Clock.DeltaTime &gt; num) ? (num.Squared() / App.Clock.DeltaTime)
///                                             : (float)App.Clock.DeltaTime;
/// return Mathx.Min(value2, 1f / Sim.Rules.PhysicsUpdatesPerSecond);
/// </code>
///
/// which holds a slow peer back twice over. The <c>Min</c> caps the credit at
/// one input tick, so the lockstep runs at no more than the slowest peer's
/// <b>frame rate</b> whatever its CPU could simulate; and the first term is a
/// <b>quadratic</b> penalty, so a peer that misses its target frame rate is
/// slowed by the square of how far it missed by. Singleplayer has neither:
/// <c>SPManager.AdvanceNetworkTime</c> is a stub and <c>GameRoot.Update</c>'s
/// do/while runs the simulation straight off the real clock. That is why a save
/// which is smooth alone crawls in multiplayer.
///
/// Measured on 2026-09-11, a 2h52m two-player session, 12-core host (11
/// FastParallel workers) against an 8-logical/3-worker client:
///
/// <list type="bullet">
/// <item>the world ran 139,484 ticks in 172.1 min = <b>13.4 of the nominal 30
/// input ticks per second</b>, 45% speed, and the last 12 minutes ran at 11%;</item>
/// <item>the host was never the limit - it spent a flat ~300 ms of simulation
/// per real second all session, 0 of 173 samples over the 33.3 ms budget, and
/// the same PC ran the same save in singleplayer at 45 ticks/s;</item>
/// <item><b>neither peer was CPU-saturated.</b> The host used 1.3-1.8 of its 16
/// logical processors and the client 1.5-2.2 of its 8, both flat from start to
/// finish. Simulation wall time per real second was flat too - host 287 to 363
/// ms/s, client about 395 to 487 - while the world tick rate fell 14.3 to 3.1
/// and the part count moved 2.6%. Work was not what collapsed; <b>pacing
/// was</b>;</item>
/// <item>the client's frame was input 2.3 ms | simulation 72.7 ms | draw 16.2 ms,
/// so <b>drawing is 18% of it</b> - which is what makes trading frames for ticks
/// nearly free here, and is why this is now on by default rather than opt-in.</item>
/// </list>
///
/// Note that <c>sim=</c> in the diagnostics is milliseconds of
/// <c>SimRoot.Update</c> <b>per frame</b>, and most of that method's cost is
/// per-frame work rather than per-tick, so it tracks frame rate and must not be
/// read as the cost of a world tick. An earlier estimate of "46 ticks/s of
/// unused client capacity" came from doing exactly that and is withdrawn; the
/// claim this patch actually rests on is the simpler one above, that both peers
/// sat far below their own CPUs while the world crawled.
///
/// The replacement credit is <c>Min(realElapsed, oneTick * MaxTicksPerFrame)</c>:
///
/// <list type="bullet">
/// <item><b>Never faster than real time.</b> Vanilla's expression is always at
/// most the elapsed frame time, and so is this one, so the world can never run
/// in fast-forward. This is the property the earlier draft of this patch lost
/// by writing <c>oneTick * N</c> unconditionally.</item>
/// <item><b>Never slower than vanilla.</b> The result is only written when it
/// exceeds what vanilla returned, so a host rendering above 30 fps is left
/// untouched and only a peer the cap was actually binding sees a change.</item>
/// <item><b>Bounded.</b> At most <c>MaxTicksPerFrame</c> ticks in one frame, so
/// a hitch cannot be repaid as one visible speed-up spike. A gap longer than a
/// second is treated as a load stall and skipped entirely.</item>
/// <item><b>Adaptively gated.</b> More than one tick per frame is only credited
/// while the peer's own frames are already shorter than a tick, i.e. while it
/// demonstrably has the headroom. See below for why this is not optional.</item>
/// </list>
///
/// <b>2.2.2: the default ceiling is 1, and more than 1 is measured to be
/// harmful on the peer that needs help.</b> 2.2.0 shipped a ceiling of 3 on the
/// theory that running N ticks in one frame is self-limiting, because the frame
/// simply becomes N times as expensive. The 2026-09-13 session refuted it. The
/// remote client pinned at exactly the ceiling and stayed there:
///
/// <code>
/// 01:46  ft=64.0/346   sim=40.0/1.79  fx=33.3   tc=3/503   lat=1545ms
/// 01:47  ft=436.7/627  sim=394.8/4.00 fx=353.5  tc=3/138   lat=924ms
/// 01:48  ft=412.9/608  sim=368.3/4.00 fx=327.5  tc=3/146   lat=751ms
/// </code>
///
/// Three things go wrong at once, and none of them is visible from the
/// single-peer reasoning above. Frame rate collapses to 2 fps, so input,
/// rendering and - the documented root cause of this mod's disconnects - the
/// Steam networking thread are all serviced twice a second. The per-tick cost
/// itself inflates rather than staying flat (18.6 ms/tick at 1.79 calls per
/// frame, 82 ms/tick at 4.00), so the trade is not the neutral one assumed.
/// And world speed, the entire point of the change, gets <b>worse</b>: 28
/// ticks/s before the ceiling bound, 9.7 after.
///
/// The half of the patch that is unambiguously right is the removal of the
/// quadratic term, and that needs no extra ticks at all. With
/// <c>Min(realElapsed, oneTick)</c> the credit is bounded by vanilla's own cap,
/// so a frame never carries more world-tick work than vanilla would have given
/// it and the collapse above cannot occur - while a peer holding 90 fps still
/// earns a full second of game time per second instead of the fraction the
/// quadratic term leaves it.
///
/// Real elapsed time is measured from a <c>Stopwatch</c> between consecutive
/// calls rather than read from <c>App.Clock.DeltaTime</c>, because that clock is
/// itself clamped - which is exactly why vanilla's <c>value2</c> pins to the cap
/// on a slow peer instead of falling away quadratically as the formula suggests.
///
/// Deterministic and lockstep-safe. <c>Sim.OnInputTick</c> takes a fixed
/// <c>InputTickInterval</c>, so N ticks in one frame compute exactly what N
/// ticks in N frames compute; only local pacing changes. Peers may run different
/// values, exactly as they may already have different "Minimum Target F.P.S."
/// settings, which feed the same expression. <c>IsReadyForTick</c> still gates
/// every tick, so a peer can never run ahead of the inputs it has been sent;
/// when it is not ready <c>BaseMPManager.AdvanceNetworkTime</c> zeroes the
/// accumulator, which discards surplus credit rather than banking it.
/// </summary>
[HarmonyPatch(typeof(NetManager), "GetTargetAdjustedDeltaTime")]
internal static class NetworkTimeCatchUpPatch
{
    /// <summary>
    /// Default ceiling when no override file is present. 1 means "credit at most
    /// one input tick per frame", which is vanilla's own cap - so the patch only
    /// removes the quadratic penalty and can never make a frame more expensive
    /// than vanilla would have. See the class remarks for the measurement that
    /// brought this down from 3.
    /// </summary>
    private const int DefaultMaxTicksPerFrame = 1;

    /// <summary>
    /// Weight of the newest frame in the rolling frame-time average that gates
    /// ceilings above 1. Low enough that one fast frame after a stall cannot
    /// re-open the gate.
    /// </summary>
    private const float FrameAverageWeight = 0.05f;

    /// <summary>
    /// A gap longer than this is a load stall or an alt-tab, not a slow frame,
    /// and is left to vanilla so nothing is credited for time the game was not
    /// running.
    /// </summary>
    private const float MaxCreditableGapSeconds = 1f;

    /// <summary>Most input ticks a single frame may credit. 1 disables the patch.</summary>
    internal static readonly int MaxTicksPerFrame;

    /// <summary>Frames whose credit this patch actually raised.</summary>
    private static long _raisedFrames;

    /// <summary>Timestamp of the previous call, for the real elapsed time.</summary>
    private static long _lastTimestamp;

    /// <summary>
    /// Rolling average of real frame time in seconds, used only to gate ceilings
    /// above 1. Single-threaded in practice (this runs on the game thread); a
    /// torn read would cost one frame's pacing and nothing else.
    /// </summary>
    private static float _averageFrameSeconds;

    static NetworkTimeCatchUpPatch()
    {
        var ticks = DefaultMaxTicksPerFrame;

        // Optional override, a single integer, beside the mod folder. Absent in a
        // normal install, which means a ceiling of one input tick per frame. 0
        // disables the patch outright and restores vanilla's quadratic pacing.
        // Values above 1 are accepted but are gated on measured frame time at
        // run time, because an ungated ceiling of 3 is what 2.2.0 shipped and
        // what collapsed a slow peer to 2 fps.
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
                ticks = Math.Clamp(parsed, 0, 8);
            }
        }
        catch (Exception)
        {
            // An unreadable override is not a reason to change pacing.
        }

        MaxTicksPerFrame = ticks;
    }

    /// <summary>Harmony skips the class entirely when the override asks for vanilla.</summary>
    private static bool Prepare() => MaxTicksPerFrame > 0;

    /// <summary>
    /// Raises the frame's game-time credit to the real time that elapsed,
    /// bounded by <see cref="MaxTicksPerFrame"/> input ticks, and by one tick
    /// whenever this peer's own frames are not already shorter than a tick.
    /// Multiplayer only: singleplayer already advances from the real clock and
    /// must not be paced by this.
    /// </summary>
    private static void Postfix(NetManager __instance, ref Time __result)
    {
        if (__instance is not BaseMPManager)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastTimestamp, now);
        if (previous == 0L)
        {
            return;
        }

        var elapsed = (float)((now - previous) / (double)Stopwatch.Frequency);
        if (elapsed <= 0f || elapsed > MaxCreditableGapSeconds)
        {
            return;
        }

        var average = _averageFrameSeconds <= 0f
            ? elapsed
            : _averageFrameSeconds + ((elapsed - _averageFrameSeconds) * FrameAverageWeight);
        _averageFrameSeconds = average;

        var oneTick = 1f / __instance.Sim.Rules.PhysicsUpdatesPerSecond;

        // Extra ticks are only affordable for a peer whose frames already fit
        // inside one tick. Anything else is at or past its own limit, and piling
        // N ticks into its frame multiplies the frame cost instead of buying
        // world speed - measured at 28 -> 9.7 ticks/s while frame rate fell to
        // 2 fps, which is also what starves the Steam networking thread.
        var allowed = average < oneTick ? MaxTicksPerFrame : 1;

        var credit = Math.Min(elapsed, oneTick * allowed);
        if (credit <= (float)__result)
        {
            return;
        }

        __result = credit;
        Interlocked.Increment(ref _raisedFrames);
    }

    /// <summary>Reads and clears the count of frames whose credit was raised.</summary>
    internal static long TakeRaisedFrames() => Interlocked.Exchange(ref _raisedFrames, 0);
}
