using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Halfling.Geometry;
using HarmonyLib;

namespace ModsQol.Code;

/// <summary>
/// Runs alongside vanilla's proxy activation for every <see cref="ProxyHandler{TComponent}"/>
/// that watches a neighbouring cell, without patching ProxyHandler itself.
///
/// ProxyHandler is generic over reference types only, so the runtime shares one canonical
/// body between ProxyHandler&lt;IResourceStorage&gt; and ProxyHandler&lt;PartComponent&gt; -
/// their MethodInfos differ but their RuntimeMethodHandle and native entry point are
/// identical. Patching "both" therefore patches one method twice, which Harmony cannot see
/// because its registry is keyed by MethodInfo, and a postfix declaring a typed __instance
/// would receive the other instantiation's object. 2.1.0 did exactly that and never bound
/// anything.
///
/// So nothing here is patched. The owning components are ordinary non-generic classes, and
/// their attach/detach hooks are enough: vanilla's ActivateProxy has already run by the time
/// the postfix fires, so registering a second pair of cell handlers puts our pass right after
/// vanilla's on every part that appears at - or leaves - the proxied cell.
///
/// Two things happen there:
///
///  1. The sentinel bind (see <see cref="AnyStorageBinder"/>), for proxies that name one.
///
///  2. The rebind guard, for every proxy. Vanilla's OnProxiedPartRemoving never reads its
///     part argument and unbinds whatever is bound. Two parts occupy every cell - the placed
///     part and the structure tile base_part puts under it - so the structure leaving unbinds
///     a proxy whose real target is still sitting there, and nothing ever rebinds it. Our
///     handler runs immediately after and restores the binding when the part that left was
///     not the one being proxied.
/// </summary>
internal static class SentinelAttachment<TComponent> where TComponent : class
{
    private sealed class Registration
    {
        public PartsManager Parts = null!;
        public IntVector2 Cell;
        public Action<Part> CellAdd = null!;
        public Action<Part> CellRemoving = null!;

        // The part vanilla last bound to, remembered because OnProxiedPartRemoving has
        // already cleared _proxiedPart by the time we are called.
        public Part? Bound;

        public Part? LateWatch;
        public Action<Part, PartComponent>? LateHandler;
    }

    // Vanilla's own add path, reused verbatim for the rebind so criteria, component lookup,
    // the ComponentAdded subscription and _proxyableIndex all follow the same rules. Calling
    // a shared canonical method is fine; only patching one is not.
    private static readonly MethodInfo s_onProxiedPartAdded =
        AccessTools.DeclaredMethod(typeof(ProxyHandler<TComponent>), "OnProxiedPartAdded")
        ?? throw new MissingMethodException(
            typeof(ProxyHandler<TComponent>).FullName, "OnProxiedPartAdded");

    // Keyed by handler so a part carrying several proxies keeps them independent, and so a
    // detached ship's registrations are collectable even if Detach is somehow missed.
    private static readonly ConditionalWeakTable<ProxyHandler<TComponent>, Registration> s_registrations = new();

    /// <summary>
    /// Called after the owning component's part has attached to a ship and vanilla has
    /// activated its proxy.
    /// </summary>
    public static void Attach(ProxyHandler<TComponent> handler, Part? parentPart)
    {
        var rules = handler.Rules;
        if (parentPart == null || rules == null)
        {
            return;
        }

        // A ProxyToggle lets vanilla activate and deactivate the proxy repeatedly without
        // going through OnPartAttached, and no Mods QoL proxy uses one. Rather than mirror
        // that lifecycle, stay out: such a proxy keeps vanilla behaviour exactly.
        if (rules.ProxyToggle.HasValue)
        {
            return;
        }

        if (!rules.PartLocation.HasValue)
        {
            // Self-proxy. Vanilla registers no cell handlers, so there is no unbind to guard
            // and its single OnProxiedPartAdded call is the only chance to bind.
            if (AnyStorageBinder.HasSentinel(rules))
            {
                TryBind(handler, parentPart, parentPart);
            }
            return;
        }

        var cell = parentPart.Rules.GetShipRelativeCell(
            rules.PartLocation.Value, parentPart.Location, parentPart.Rotation, parentPart.FlipX);
        var parts = parentPart.Ship?.Parts;
        if (parts == null)
        {
            return;
        }

        Detach(handler);

        var registration = new Registration { Parts = parts, Cell = cell };
        registration.CellAdd = part => OnCellAdd(handler, parentPart, registration, part);
        registration.CellRemoving = part => OnCellRemoving(handler, parentPart, registration, part);
        s_registrations.Add(handler, registration);

        // Vanilla registered its handlers inside ActivateProxy, and PartsManager combines
        // them into one multicast delegate, so ours run second - after the binding vanilla
        // just made, or after the unbind it should not have made.
        parts.RegisterCellAddHandler(cell, registration.CellAdd);
        parts.RegisterCellRemovingHandler(cell, registration.CellRemoving);

        var existing = parts[cell, PartRectType.Normal];
        if (existing != null)
        {
            OnCellAdd(handler, parentPart, registration, existing);
        }
        else
        {
            registration.Bound = handler._proxiedPart;
        }
    }

    /// <summary>
    /// Called before the owning component's part detaches, while the ship is still reachable.
    /// </summary>
    public static void Detach(ProxyHandler<TComponent> handler)
    {
        if (!s_registrations.TryGetValue(handler, out var registration))
        {
            return;
        }

        registration.Parts.UnregisterCellAddHandler(registration.Cell, registration.CellAdd);
        registration.Parts.UnregisterCellRemovingHandler(registration.Cell, registration.CellRemoving);
        StopLateWatch(registration);
        registration.Bound = null;
        s_registrations.Remove(handler);
    }

    private static void OnCellAdd(
        ProxyHandler<TComponent> handler, Part parentPart, Registration registration, Part part)
    {
        if (AnyStorageBinder.HasSentinel(handler.Rules))
        {
            TryBind(handler, parentPart, part);
        }
        registration.Bound = handler._proxiedPart;
    }

    private static void OnCellRemoving(
        ProxyHandler<TComponent> handler, Part parentPart, Registration registration, Part part)
    {
        var bound = registration.Bound;
        if (bound == null)
        {
            return;
        }

        if (ReferenceEquals(bound, part))
        {
            // The proxied part really is leaving; vanilla's unbind was correct.
            StopLateWatch(registration);
            registration.Bound = null;
            return;
        }

        // Some other part at this cell left - the structure tile under the recipient, almost
        // always - and vanilla unbound anyway. Only step in if it actually did.
        if (handler._proxiedPart != null || handler.ProxiedComponent != null)
        {
            return;
        }

        // Replay vanilla's own add path against the part that is still there. This restores
        // _proxiedPart, _proxyableIndex and either ProxiedComponent or the ComponentAdded
        // subscription, exactly as the original binding had them.
        s_onProxiedPartAdded.Invoke(handler, new object[] { bound });

        // A binding that was originally made by the sentinel is not restored by vanilla's
        // path - it cannot resolve the sentinel ID - so run our pass again on top.
        if (AnyStorageBinder.HasSentinel(handler.Rules))
        {
            TryBind(handler, parentPart, bound);
        }

        registration.Bound = handler._proxiedPart;
    }

    private static void TryBind(ProxyHandler<TComponent> handler, Part parentPart, Part part)
    {
        if (AnyStorageBinder.TryBind(handler, parentPart, part))
        {
            if (s_registrations.TryGetValue(handler, out var bound))
            {
                StopLateWatch(bound);
            }
            return;
        }

        // Nothing to bind yet. A storage may still appear later - a ToggledComponents group
        // switching on - and vanilla's own late handler only ever accepts the component it
        // was named, so the sentinel needs its own watch.
        if (handler.ProxiedComponent != null
            || !s_registrations.TryGetValue(handler, out var registration))
        {
            return;
        }

        if (ReferenceEquals(registration.LateWatch, part))
        {
            return;
        }

        StopLateWatch(registration);
        registration.LateWatch = part;
        registration.LateHandler = (watched, component) =>
        {
            if (AnyStorageBinder.TryBindLate(handler, parentPart, watched, component))
            {
                StopLateWatch(registration);
            }
        };
        part.ComponentAdded += registration.LateHandler;
    }

    private static void StopLateWatch(Registration registration)
    {
        if (registration.LateWatch != null && registration.LateHandler != null)
        {
            registration.LateWatch.ComponentAdded -= registration.LateHandler;
        }
        registration.LateWatch = null;
        registration.LateHandler = null;
    }
}
