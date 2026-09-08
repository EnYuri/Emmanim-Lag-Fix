using System;
using System.Reflection;
using Cosmoteer.Ships.Networks;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Resources;
using HarmonyLib;

namespace ModsQol.Code;

/// <summary>
/// Refreshes the cached resource views that sit above a proxy, immediately after that proxy
/// binds or rebinds.
///
/// Three components on the delivery path cache what they report:
///
///   ResourceStorageProxy       _resources / _maxResources, mirrored from the proxied store
///   MultiResourceStorage       _cachedResources / _cachedMaxResources, summed over members
///   PartNetworkResourceStore   _cachedAvailableResources / _cachedAvailableCapacity
///
/// Each is invalidated only by a change event raised by the layer below it. A proxy that
/// rebinds to a different part is not such a change - the storage it mirrors was swapped
/// rather than altered - so a stale figure can survive the rebind and nothing later
/// disturbs it.
///
/// The consequence is not cosmetic. ResourceConverter.WantsResourceConversion gates on
/// `ToStorage.Resources &lt;= ToStorage.MaxResources - MinToQuantityForConversion`, so a
/// resources figure left too high makes a destination read as full forever and the transfer
/// simply stops, with the staging buffer above it pinned and the real store empty.
///
/// Observed on 2026-09-09: replacing an engine room with another tier in the same cell left
/// a wire terminal's staging buffer at 1000/1000 while the engine room it fed sat at
/// 0/36000 with every proxy correctly bound and every gate open. A sibling network store on
/// the same run reported holding 379,107 against a capacity of 31,428 - impossible, since
/// TotalCapacity is computed live while AvailableResources is cached, and therefore direct
/// evidence of a stale cache in this chain. Rotating the wire or reloading the save cleared
/// it, which is what a cache rebuild does and what a state fault would not.
///
/// Rather than identify which of the three caches goes stale in which order, this refreshes
/// all of them bottom-up at the one moment they can disagree. Each call recomputes from live
/// state and raises the same change events the layer would raise itself, so a value that was
/// already correct produces no semantic change - ResourceStorageProxy compares before
/// raising, and a redundant recomputation upstream costs a subnetwork query rebuild that a
/// construction event triggers anyway.
/// </summary>
internal static class ProxyCacheRefresh
{
    private static readonly MethodInfo? ProxyResources =
        AccessTools.DeclaredMethod(typeof(ResourceStorageProxy), "OnResourcesChanged",
            new[] { typeof(object), typeof(EventArgs) });

    private static readonly MethodInfo? ProxyMaxResources =
        AccessTools.DeclaredMethod(typeof(ResourceStorageProxy), "OnMaxResourcesChanged",
            new[] { typeof(object), typeof(EventArgs) });

    private static readonly MethodInfo? MultiResources =
        AccessTools.DeclaredMethod(typeof(MultiResourceStorage), "OnStorageResourcesChanged");

    private static readonly MethodInfo? MultiMaxResources =
        AccessTools.DeclaredMethod(typeof(MultiResourceStorage), "OnStorageMaxResourcesChanged");

    private static readonly MethodInfo? NetworkResources =
        AccessTools.DeclaredMethod(typeof(PartNetworkResourceStore), "OnStorageResourcesChanged");

    private static readonly MethodInfo? NetworkMaxResources =
        AccessTools.DeclaredMethod(typeof(PartNetworkResourceStore), "OnStorageMaxResourcesChanged");

    /// <summary>
    /// True when every member this refresh needs was found. A missing one means the game
    /// changed shape underneath us, and the refresh then does nothing rather than guessing.
    /// </summary>
    internal static readonly bool Available =
        ProxyResources != null && ProxyMaxResources != null
        && MultiResources != null && MultiMaxResources != null
        && NetworkResources != null && NetworkMaxResources != null;

    // Raising a change event can reach another proxy on the same part, whose own handler
    // would call back in here. One pass is enough - the second would recompute the same
    // live state - so re-entry is dropped rather than allowed to nest.
    [ThreadStatic]
    private static bool t_running;

    /// <summary>
    /// Recomputes the cached views on <paramref name="owner"/>, bottom-up: the proxies that
    /// mirror another part's store, then any MultiResourceStorage summing them, then any
    /// network store publishing the result to the subnetwork.
    /// </summary>
    internal static void After(Part? owner)
    {
        if (!Available || owner == null || t_running)
        {
            return;
        }

        var components = owner.Components;
        if (components == null)
        {
            return;
        }

        // Only parts that actually aggregate or publish a proxied store can go stale this
        // way. Skipping the rest keeps every ordinary proxy on exactly vanilla behaviour.
        var aggregates = false;
        for (var i = 0; i < components.Count && !aggregates; i++)
        {
            aggregates = components[i] is MultiResourceStorage or PartNetworkResourceStore;
        }

        if (!aggregates)
        {
            return;
        }

        t_running = true;
        try
        {
            Invoke<ResourceStorageProxy>(components, ProxyResources, ProxyMaxResources);
            Invoke<MultiResourceStorage>(components, MultiResources, MultiMaxResources);
            Invoke<PartNetworkResourceStore>(components, NetworkResources, NetworkMaxResources);
        }
        finally
        {
            t_running = false;
        }
    }

    private static void Invoke<T>(
        System.Collections.Generic.IReadOnlyList<PartComponent> components,
        MethodInfo? resources, MethodInfo? maxResources)
        where T : PartComponent
    {
        var args = new object?[] { null, EventArgs.Empty };
        for (var i = 0; i < components.Count; i++)
        {
            if (components[i] is not T target)
            {
                continue;
            }

            try
            {
                // Max first: a capacity that shrank must be visible before the resources
                // figure is re-read against it, or the intermediate state is the very
                // "holds more than it can" reading this exists to prevent.
                maxResources?.Invoke(target, args);
                resources?.Invoke(target, args);
            }
            catch (Exception)
            {
                // A refresh must never be the thing that breaks construction.
            }
        }
    }
}
