using System;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Halfling.Geometry;

namespace ModsQol.Code;

/// <summary>
/// Runs the sentinel bind alongside vanilla's own proxy activation, without patching
/// <see cref="ProxyHandler{TComponent}"/> itself.
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
/// the postfix fires, so registering a second cell-add handler puts the sentinel pass right
/// after vanilla's on every part that appears at the proxied cell.
/// </summary>
internal static class SentinelAttachment<TComponent> where TComponent : class
{
    private sealed class Registration
    {
        public PartsManager Parts = null!;
        public IntVector2 Cell;
        public Action<Part> CellAdd = null!;
        public Part? LateWatch;
        public Action<Part, PartComponent>? LateHandler;
    }

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
        if (parentPart == null || !AnyStorageBinder.HasSentinel(rules))
        {
            return;
        }

        // A ProxyToggle lets vanilla activate and deactivate the proxy repeatedly without
        // going through OnPartAttached, and no Mods QoL proxy uses one. Rather than mirror
        // that lifecycle, stay out: the rules degrade to vanilla's named-component path.
        if (rules.ProxyToggle.HasValue)
        {
            return;
        }

        if (!rules.PartLocation.HasValue)
        {
            // Self-proxy: there is no cell to watch, so vanilla's single OnProxiedPartAdded
            // call is the only chance to bind.
            TryBind(handler, parentPart, parentPart);
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
        registration.CellAdd = part => TryBind(handler, parentPart, part);
        s_registrations.Add(handler, registration);

        // Vanilla registered its handler inside ActivateProxy, so ours is second in line and
        // sees the binding vanilla just made - or failed to make.
        parts.RegisterCellAddHandler(cell, registration.CellAdd);

        var existing = parts[cell, PartRectType.Normal];
        if (existing != null)
        {
            TryBind(handler, parentPart, existing);
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
        StopLateWatch(registration);
        s_registrations.Remove(handler);
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
