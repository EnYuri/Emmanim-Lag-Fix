using Halfling.Network;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Multiplayer client launch receives the complete GameInit into a
/// ChannelStream and then copies it into a fresh MemoryStream. Vanilla lets the
/// destination grow geometrically during that copy, repeatedly allocating and
/// copying large backing arrays. The final received byte count is already
/// known, so reserve it once before the otherwise-vanilla Stream.CopyTo call.
/// </summary>
[HarmonyPatch(typeof(Stream), nameof(Stream.CopyTo), new[] { typeof(Stream) })]
internal static class MultiplayerStreamCopyCapacityPatch
{
    private static void Prefix(Stream __instance, Stream destination)
    {
        if (__instance is not ChannelStream channel
            || destination is not MemoryStream memory
            || memory.Length != 0)
        {
            return;
        }

        var unreadBytes = channel.UnreadBytes;
        if (unreadBytes > 0 && unreadBytes <= int.MaxValue)
        {
            memory.Capacity = (int)unreadBytes;
        }
    }
}
