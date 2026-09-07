using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.Resources;
using HarmonyLib;

namespace ModsQol.Code;

/// <summary>
/// Teaches ProxyHandler to accept a sentinel ComponentID meaning "whatever component on that
/// part actually stores this resource". See <see cref="AnyStorageBinder"/> for why.
///
/// Both closed generic instantiations are patched separately because Harmony patches a
/// concrete method, and the two are what the game builds:
///   ProxyHandler&lt;IResourceStorage&gt; backs ResourceStorageProxy;
///   ProxyHandler&lt;PartComponent&gt;   backs ComponentPresenceToggle.
/// A single BaseResourceStorage satisfies both, so one sentinel pair covers a wire contact's
/// capacity view and its presence test at once.
///
/// Every patch is a postfix/prefix that returns immediately unless the matched ProxyableComponent
/// carries the sentinel prefix, so an unpatched game and a game with no Mods QoL installed behave
/// identically to vanilla.
/// </summary>
internal static class ProxyHandlerSentinelPatch
{
    [HarmonyPatch(typeof(ProxyHandler<IResourceStorage>), "OnProxiedPartAdded")]
    internal static class StorageAdded
    {
        private static void Postfix(
            ProxyHandler<IResourceStorage> __instance,
            Part part,
            Part ____parentPart,
            int ____proxyableIndex)
        {
            AnyStorageBinder.TryBind(__instance, ____parentPart, part, ____proxyableIndex);
        }
    }

    [HarmonyPatch(typeof(ProxyHandler<PartComponent>), "OnProxiedPartAdded")]
    internal static class ComponentAdded
    {
        private static void Postfix(
            ProxyHandler<PartComponent> __instance,
            Part part,
            Part ____parentPart,
            int ____proxyableIndex)
        {
            AnyStorageBinder.TryBind(__instance, ____parentPart, part, ____proxyableIndex);
        }
    }

    [HarmonyPatch(typeof(ProxyHandler<IResourceStorage>), "OnProxiedPartComponentAdded")]
    internal static class StorageLateAdded
    {
        private static bool Prefix(
            ProxyHandler<IResourceStorage> __instance,
            Part part,
            PartComponent component,
            Part ____parentPart,
            int ____proxyableIndex)
        {
            return !AnyStorageBinder.TryBindLate(__instance, ____parentPart, part, component, ____proxyableIndex);
        }
    }

    [HarmonyPatch(typeof(ProxyHandler<PartComponent>), "OnProxiedPartComponentAdded")]
    internal static class ComponentLateAdded
    {
        private static bool Prefix(
            ProxyHandler<PartComponent> __instance,
            Part part,
            PartComponent component,
            Part ____parentPart,
            int ____proxyableIndex)
        {
            return !AnyStorageBinder.TryBindLate(__instance, ____parentPart, part, component, ____proxyableIndex);
        }
    }
}
