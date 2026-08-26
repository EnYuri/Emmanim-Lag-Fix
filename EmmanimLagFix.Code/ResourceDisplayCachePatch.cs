using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Game.Gui;
using Cosmoteer.Resources;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Caches the expensive selected-ship resource aggregation behind the small
/// resource list in the upper-right corner. The surrounding widget still runs
/// every frame, so hover fading, flashing, layout and one-frame requests keep
/// their vanilla behaviour; only the displayed totals may be up to one second old.
/// </summary>
[HarmonyPatch(typeof(PlayerResourcesDisplay), "GetResourceCounts")]
internal static class ResourceDisplayCachePatch
{
    private static readonly ConditionalWeakTable<PlayerResourcesDisplay, Cache> Caches = new();
    private static readonly long CacheLifetimeTicks = Stopwatch.Frequency;

    private sealed class Cache
    {
        public long WindowStart;
        public Dictionary<ID<ResourceRules>, Values> Values { get; } = new();
    }

    private readonly record struct Values(int Quantity, int Capacity, bool CapacityLimited, int Anticipated);

    private static bool Prefix(
        PlayerResourcesDisplay __instance,
        ID<ResourceRules> resourceType,
        ref int quantity,
        ref int capacity,
        ref bool capacityLimited,
        ref int anticipated,
        out bool __state)
    {
        var cache = Caches.GetOrCreateValue(__instance);
        var now = Stopwatch.GetTimestamp();

        if (now - cache.WindowStart >= CacheLifetimeTicks)
        {
            cache.WindowStart = now;
            cache.Values.Clear();
        }

        if (cache.Values.TryGetValue(resourceType, out var values))
        {
            quantity = values.Quantity;
            capacity = values.Capacity;
            capacityLimited = values.CapacityLimited;
            anticipated = values.Anticipated;
            __state = false;
            return false;
        }

        __state = true;
        return true;
    }

    private static void Postfix(
        PlayerResourcesDisplay __instance,
        ID<ResourceRules> resourceType,
        int quantity,
        int capacity,
        bool capacityLimited,
        int anticipated,
        bool __state)
    {
        if (__state)
        {
            Caches.GetOrCreateValue(__instance).Values[resourceType] =
                new Values(quantity, capacity, capacityLimited, anticipated);
        }
    }
}
