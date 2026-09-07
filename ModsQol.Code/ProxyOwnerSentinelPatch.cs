using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.Resources;
using HarmonyLib;

namespace ModsQol.Code;

/// <summary>
/// Attaches the sentinel binder to the two components that own a ProxyHandler.
///
/// Both are non-generic classes with non-generic hooks, which is the point: ProxyHandler's
/// own methods are shared canonical generic code and cannot be patched per-instantiation.
/// See <see cref="SentinelAttachment{TComponent}"/> for the full reasoning.
/// </summary>
internal static class ProxyOwnerSentinelPatch
{
    [HarmonyPatch(typeof(ResourceStorageProxy), "OnPartAttached2")]
    internal static class StorageProxyAttached
    {
        private static void Postfix(ResourceStorageProxy __instance) =>
            SentinelAttachment<IResourceStorage>.Attach(__instance._proxy, __instance.Part);
    }

    [HarmonyPatch(typeof(ResourceStorageProxy), "OnPartDetaching2")]
    internal static class StorageProxyDetaching
    {
        // Prefix: vanilla's OnPartDetaching clears _parentPart, and the ship must still be
        // reachable to unregister the cell handler.
        private static void Prefix(ResourceStorageProxy __instance) =>
            SentinelAttachment<IResourceStorage>.Detach(__instance._proxy);
    }

    [HarmonyPatch(typeof(ComponentPresenceToggle), "OnPartAttached")]
    internal static class PresenceToggleAttached
    {
        private static void Postfix(ComponentPresenceToggle __instance) =>
            SentinelAttachment<PartComponent>.Attach(__instance._proxy, __instance.Part);
    }

    [HarmonyPatch(typeof(ComponentPresenceToggle), "OnPartDetaching")]
    internal static class PresenceToggleDetaching
    {
        private static void Prefix(ComponentPresenceToggle __instance) =>
            SentinelAttachment<PartComponent>.Detach(__instance._proxy);
    }
}
