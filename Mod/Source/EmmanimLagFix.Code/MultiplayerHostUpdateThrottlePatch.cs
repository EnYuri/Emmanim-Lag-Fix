using Cosmoteer.Game.Multiplayer;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// HostUpdate carries input-delay, latency, and integrity-hash reports. Keep the
/// authoritative input-delay calculation at 30 Hz, but serialize/send the update
/// only alongside the normal 6 Hz integrity hash - except when the input delay
/// has <b>risen</b>, which is sent at once. InputTick messages and simulation
/// cadence remain untouched. Desync-debug sessions retain vanilla 30 Hz
/// HostUpdates because they can queue multiple diagnostic hashes/tick.
///
/// The exception exists because the delay is not merely a report: it is the lead
/// by which <i>the client</i> stamps its own outgoing inputs.
/// <c>MPClientManager.OnHostUpdateReceived</c> assigns
/// <c>_inputTickDelay = hostUpdate.InputTickDelay</c>, and
/// <c>BaseMPManager.Update</c> then stamps
/// <c>_curInputTick + GetInputTickDelay()</c>. A peer that has not yet heard a
/// raised delay under-stamps its lead, and <c>IsReadyForTick</c> admits a tick
/// only when every player has queued that exact tick number - so an under-stamped
/// lead is not a late report, it is a stalled world tick, and the accumulated
/// <c>_realDeltaTime</c> is discarded rather than banked. At 6 Hz that window is
/// up to 167 ms wide. A 2026-09-14 two-player session had the remote peer at
/// <c>q=0 avg=0.0</c> for its entire length while holding the readiness gate on
/// 92.3% of host frames, i.e. with no slack for the window to be absorbed by.
///
/// Only a rise is urgent. A fall means peers are carrying more lead than the
/// current latency needs, which costs a little input latency and nothing else, so
/// it can wait for the scheduled send. That asymmetry is also what keeps the
/// throttle worthwhile in the steady state: a delay oscillating between two
/// adjacent tick counts sends on the rises only. The worst case is one small
/// reliable message per input tick, which is exactly vanilla's own rate, and the
/// integrity hash - the expensive half of the original 30 Hz cost - stays on its
/// own 6 Hz schedule either way.
/// </summary>
[HarmonyPatch(typeof(MPHostManager), "OnTick")]
internal static class MultiplayerHostUpdateThrottlePatch
{
    /// <summary>No delay has been sent yet, so the next one counts as a rise.</summary>
    internal const int UnknownDelay = int.MinValue;

    /// <summary>Extra sends caused by a risen delay, for the diagnostics line.</summary>
    internal static long UrgentSends;

    private static int _lastSentInputTickDelay = UnknownDelay;

    private static bool Prefix(MPHostManager __instance, int tick, out bool __state)
    {
        // A new session restarts the tick counter. Forget the previous one's
        // last-sent value rather than suppressing the first send of this one.
        if (tick <= 1)
        {
            _lastSentInputTickDelay = UnknownDelay;
        }

        // Vanilla begins OnTick with this calculation. Preserve it even when
        // skipping the allocation, serialization, local dispatch, and send. On the
        // sending path vanilla recomputes it, so what the postfix records is
        // always the value that actually went out, not this one.
        var delay = __instance.CalculateInputTickDelay();
        __instance._inputTickDelay = delay;

        __state = ShouldSendHostUpdate(
            tick,
            Math.Max(1, (int)Math.Round(__instance.Rules.InputTicksPerSecond)),
            __instance.DesyncDebuggingEnabled);

        if (!__state && IsUrgentDelayChange(delay, _lastSentInputTickDelay))
        {
            UrgentSends++;
            __state = true;
        }

        return __state;
    }

    // Harmony runs a postfix even when a prefix skipped the original, so the
    // prefix has to say whether anything was actually sent. OnTick runs on the
    // tick path only, so the last-sent field needs no interlock.
    private static void Postfix(MPHostManager __instance, bool __state)
    {
        if (__state)
        {
            _lastSentInputTickDelay = __instance._inputTickDelay;
        }
    }

    /// <summary>
    /// Whether <paramref name="delay"/> must reach the peers now rather than on
    /// the next scheduled HostUpdate.
    /// </summary>
    internal static bool IsUrgentDelayChange(int delay, int lastSentDelay) =>
        lastSentDelay == UnknownDelay || delay > lastSentDelay;

    internal static bool ShouldSendHostUpdate(
        int inputTick,
        int inputTicksPerSecond,
        bool desyncDebuggingEnabled) =>
        desyncDebuggingEnabled
        || MultiplayerIntegrityHashThrottlePatch.ShouldComputeHash(inputTick, inputTicksPerSecond);
}
