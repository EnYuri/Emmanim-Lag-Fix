using System;
using System.Collections.Generic;
using Cosmoteer.Data;
using Cosmoteer.Resources;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.Resources;
using HarmonyLib;

namespace ModsQol.Code;

/// <summary>
/// Resolves a Mods QoL sentinel ComponentID to a real resource storage on the proxied part.
///
/// Vanilla's ProxyHandler can only find a neighbouring part's storage by naming its
/// component, and RelativePartCriteria matches part identity only, so a .rules-only wire
/// network has to enumerate every recipient part by hand. That does not scale: any mod the
/// author has never seen ships parts whose battery lives under a private component name, and
/// those fall back to the rate-limited projectile path.
///
/// This binder lets a proxy say "whatever component on that part actually stores
/// &lt;resource&gt;" instead of naming one. The sentinel is a plain ID, so a game without this
/// library simply never matches it and the .rules degrade to the fallback on their own - no
/// version negotiation and no hard dependency in either direction.
/// </summary>
internal static class AnyStorageBinder
{
    /// <summary>
    /// Sentinel ComponentID prefix. The remainder of the ID is the resource ID,
    /// e.g. znayuri_any_battery.
    /// </summary>
    public const string SentinelPrefix = "znayuri_any_";

    private static readonly Dictionary<int, ID<ResourceRules>?> s_resolved = new();
    private static readonly object s_lock = new();

    private static bool s_announced;

    /// <summary>
    /// Number of successful sentinel binds, for the smoke test and the log line.
    /// </summary>
    public static int BindCount { get; private set; }

    /// <summary>
    /// Returns the resource a sentinel component ID asks for, or null when the ID is an
    /// ordinary component name. Results are cached by ID index, so the common (non-sentinel)
    /// case costs one dictionary probe.
    /// </summary>
    public static ID<ResourceRules>? ResolveSentinel(ID<PartComponentRules> componentID)
    {
        int key = (int)componentID;
        lock (s_lock)
        {
            if (s_resolved.TryGetValue(key, out var cached))
            {
                return cached;
            }

            ID<ResourceRules>? result = null;
            string name = componentID.ToString();
            if (name.StartsWith(SentinelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string resource = name.Substring(SentinelPrefix.Length);
                if (resource.Length > 0)
                {
                    result = new ID<ResourceRules>(resource);
                }
            }

            s_resolved[key] = result;
            return result;
        }
    }

    /// <summary>
    /// Picks the storage on the given part that a sentinel should bind to.
    ///
    /// Proxies and aggregates are skipped: ResourceStorageProxy is itself a view onto some
    /// other part, and MultiResourceStorage sums its members, so binding either would
    /// double-count or point off the part entirely. Of what remains the largest store wins,
    /// with list order breaking ties - the registry list is built in part-construction order,
    /// which is deterministic and therefore identical on every peer.
    /// </summary>
    public static BaseResourceStorage? FindStorage(Part part, ID<ResourceRules> resource)
    {
        BaseResourceStorage? best = null;
        foreach (var storage in BaseResourceStorage.GetAllOfTypeOnPart(resource, part))
        {
            if (storage is ResourceStorageProxy || storage is MultiResourceStorage)
            {
                continue;
            }
            if (storage.MaxResources <= 0)
            {
                continue;
            }
            if (best == null || storage.MaxResources > best.MaxResources)
            {
                best = storage;
            }
        }
        return best;
    }

    /// <summary>
    /// Completes a sentinel bind that vanilla's OnProxiedPartAdded left unbound.
    ///
    /// Vanilla stops at the first ProxyableComponent whose PartCriteria matches, whether or not
    /// the named component exists, so a name list gives no fallback semantics on its own. This
    /// resumes the scan from where vanilla stopped and honours any sentinel entry further down
    /// the list. That is what makes the sentinel a genuine last resort: a part that does expose
    /// the named component is bound by vanilla and never reaches here, so the named path and the
    /// sentinel path can share one proxy component instead of being two proxies that would both
    /// bind and double-count the same store.
    ///
    /// Returns true when a storage was found and connected.
    /// </summary>
    public static bool TryBind<TComponent>(
        ProxyHandler<TComponent> handler,
        Part parentPart,
        Part part,
        int matchedIndex) where TComponent : class
    {
        if (handler.ProxiedComponent != null)
        {
            return false;
        }

        // matchedIndex < 0 means no criteria matched at all, so vanilla never set _proxiedPart
        // and never subscribed. Binding from here would leave the handler half-initialized.
        if (matchedIndex < 0 || parentPart == null)
        {
            return false;
        }

        var proxyables = handler.Rules.ProxyableComponents;
        if (proxyables == null)
        {
            return false;
        }

        for (int i = matchedIndex; i < proxyables.Length; i++)
        {
            var proxyable = proxyables[i];
            var resource = ResolveSentinel(proxyable.ComponentID);
            if (!resource.HasValue)
            {
                continue;
            }
            if (proxyable.PartCriteria != null && !proxyable.PartCriteria.IsMatch(parentPart, part))
            {
                continue;
            }

            if (FindStorage(part, resource.Value) is not TComponent bound)
            {
                // Nothing to bind yet. Vanilla has already subscribed its own
                // OnProxiedPartComponentAdded to the part, and the late-bind prefix makes that
                // handler sentinel-aware, so a storage that appears later still binds.
                continue;
            }

            // Vanilla subscribed OnProxiedPartComponentAdded because its TryGetComponent failed.
            // Drop it: OnProxiedPartRemoving only unsubscribes when ProxiedComponent is null, so
            // leaving it attached would strand the handler on a part we no longer proxy.
            Unsubscribe(handler, part);
            Connect(handler, bound);
            BindCount++;
            Announce();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Completes a sentinel bind for a component that appeared on the proxied part after the
    /// proxy attached - a ToggledComponents group switching on, for instance.
    ///
    /// Returns true when this took the bind, in which case vanilla's handler must be skipped.
    /// </summary>
    public static bool TryBindLate<TComponent>(
        ProxyHandler<TComponent> handler,
        Part parentPart,
        Part part,
        PartComponent component,
        int matchedIndex) where TComponent : class
    {
        if (matchedIndex < 0 || parentPart == null)
        {
            return false;
        }

        var proxyables = handler.Rules.ProxyableComponents;
        if (proxyables == null || matchedIndex >= proxyables.Length)
        {
            return false;
        }

        // The component vanilla is waiting for has arrived. Its own handler binds it correctly,
        // and it outranks any sentinel further down the list, so stay out of the way.
        if (component.Rules.ID == proxyables[matchedIndex].ComponentID)
        {
            return false;
        }

        if (component is not BaseResourceStorage storage
            || storage is ResourceStorageProxy
            || storage is MultiResourceStorage
            || storage.MaxResources <= 0)
        {
            return false;
        }

        for (int i = matchedIndex; i < proxyables.Length; i++)
        {
            var proxyable = proxyables[i];
            var resource = ResolveSentinel(proxyable.ComponentID);
            if (!resource.HasValue || storage.ResourceType != resource.Value)
            {
                continue;
            }
            if (proxyable.PartCriteria != null && !proxyable.PartCriteria.IsMatch(parentPart, part))
            {
                continue;
            }

            // Prefer whatever FindStorage would have picked, so a late arrival cannot displace a
            // larger store that was already there.
            if ((FindStorage(part, resource.Value) ?? storage) is not TComponent bound)
            {
                continue;
            }

            Unsubscribe(handler, part);
            Connect(handler, bound);
            BindCount++;
            Announce();
            return true;
        }

        return false;
    }

    private static void Unsubscribe<TComponent>(ProxyHandler<TComponent> handler, Part part)
        where TComponent : class
    {
        var method = AccessTools.Method(typeof(ProxyHandler<TComponent>), "OnProxiedPartComponentAdded");
        if (method == null)
        {
            return;
        }
        var handlerDelegate = (Action<Part, PartComponent>)Delegate.CreateDelegate(
            typeof(Action<Part, PartComponent>), handler, method);
        part.ComponentAdded -= handlerDelegate;
    }

    private static void Connect<TComponent>(ProxyHandler<TComponent> handler, TComponent component)
        where TComponent : class
    {
        // The setter is private but raises the connect/disconnect events the proxy and the
        // presence toggle both depend on, so it must be used rather than the backing field.
        var setter = AccessTools.PropertySetter(typeof(ProxyHandler<TComponent>), "ProxiedComponent");
        setter.Invoke(handler, new object?[] { component });
    }

    private static void Announce()
    {
        if (s_announced)
        {
            return;
        }
        s_announced = true;
        Halfling.Logging.Logger.Log(
            "Mods QoL code layer: sentinel storage proxy active (first bind via '"
            + SentinelPrefix + "*').");
    }
}
