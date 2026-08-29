using Cosmoteer.Game.Multiplayer;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// HostUpdate carries input-delay, latency, and integrity-hash reports. Keep
/// the authoritative input-delay calculation at 30 Hz, but serialize/send the
/// update only alongside the normal 6 Hz integrity hash. InputTick messages and
/// simulation cadence remain untouched. Desync-debug sessions retain vanilla
/// 30 Hz HostUpdates because they can queue multiple diagnostic hashes/tick.
/// </summary>
[HarmonyPatch(typeof(MPHostManager), "OnTick")]
internal static class MultiplayerHostUpdateThrottlePatch
{
    private static bool Prefix(MPHostManager __instance, int tick)
    {
        if (ShouldSendHostUpdate(
            tick,
            Math.Max(1, (int)Math.Round(__instance.Rules.InputTicksPerSecond)),
            __instance.DesyncDebuggingEnabled))
        {
            return true;
        }

        // Vanilla begins OnTick with this calculation. Preserve it even when
        // skipping the allocation, serialization, local dispatch, and send.
        __instance._inputTickDelay = __instance.CalculateInputTickDelay();
        return false;
    }

    internal static bool ShouldSendHostUpdate(
        int inputTick,
        int inputTicksPerSecond,
        bool desyncDebuggingEnabled) =>
        desyncDebuggingEnabled
        || MultiplayerIntegrityHashThrottlePatch.ShouldComputeHash(inputTick, inputTicksPerSecond);
}
