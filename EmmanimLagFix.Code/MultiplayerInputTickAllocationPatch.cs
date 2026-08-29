using System.Runtime.CompilerServices;
using Cosmoteer.Game.Multiplayer;
using Halfling.Network;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// The host forwards every received client InputTick with a predicate that
/// excludes its sender. Vanilla captures senderID into a fresh closure on every
/// tick. Cache that immutable predicate once per host session and sender while
/// preserving the 30 Hz tick cadence and serialized payload exactly.
/// </summary>
[HarmonyPatch(typeof(MPHostManager), "ForwardInputTick")]
internal static class MultiplayerInputTickAllocationPatch
{
    private sealed class HostFilterCache
    {
        internal readonly Dictionary<MessengerID, Predicate<MessengerID>> Filters = new();
    }

    private static readonly ConditionalWeakTable<MPHostManager, HostFilterCache> FiltersByHost = new();

    private static bool Prefix(
        MPHostManager __instance,
        BaseMPManager.InputTick it,
        MessengerID senderID,
        SerializedChannel<BaseMPManager.InputTick> channel)
    {
        channel.SendMessage(
            it,
            TransmitMode.Reliable,
            0,
            GetOrCreateFilter(__instance, senderID));
        return false;
    }

    internal static Predicate<MessengerID> GetOrCreateFilter(
        MPHostManager host,
        MessengerID senderID)
    {
        var filters = FiltersByHost.GetOrCreateValue(host).Filters;
        if (!filters.TryGetValue(senderID, out var filter))
        {
            filter = candidateID => candidateID != senderID;
            filters.Add(senderID, filter);
        }

        return filter;
    }
}
