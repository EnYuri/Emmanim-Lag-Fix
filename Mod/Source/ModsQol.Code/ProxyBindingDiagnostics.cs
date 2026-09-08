using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.IO;
using System.Threading;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.Resources;
using Halfling.Geometry;
using Halfling.Logging;
using HarmonyLib;

namespace ModsQol.Code;

/// <summary>
/// Opt-in instrumentation for proxy binding. Inert unless
/// proxy-binding-diagnostics.flag exists beside the mod's Code directory.
///
/// Written because reading the decompiled source did not explain an observed
/// failure: a wire contact on an ordinary recipient - one exposing a plain
/// ResourceStorage literally named BatteryStorage - delivered nothing at all,
/// while a mirror-image contact on the same ship kept working. Every static
/// reading of the add/remove ordering says the current rebind guard repairs
/// that case, so this records what actually happens rather than what should.
///
/// Three read-only records, all prefixed so one grep separates them:
///
///   [PB.attach]  a proxy activating: owner, watched cell, who occupies it,
///                and the binding vanilla just made
///   [PB.cell]    every cell add/removing callback in the order it fires, with
///                the binding state before and after the rebind guard runs
///   [PB.after]   the state of every proxy watching a cell touched by a
///                completed PartsManager.AddPart / RemovePart. This runs after
///                _partsByCell has been updated, which the cell callbacks
///                cannot see - CLAUDE.md names exactly that blind spot.
///
/// A bound contact reports the ProxyableComponents index and ComponentID that
/// matched plus the live storage figures; an unbound one reports "none".
/// Remove the flag after the capture.
/// </summary>
internal static class ProxyBindingDiagnostics
{
    internal static readonly bool Enabled = File.Exists(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
        "..",
        "proxy-binding-diagnostics.flag")));

    // Every live cell-watching proxy, so a completed AddPart/RemovePart can report the ones
    // whose watched cell it touched. Handlers are held weakly: a detached ship must stay
    // collectable even if Detach is somehow missed, exactly as the registration table is.
    private static readonly List<Probe> Probes = new();

    internal abstract class Probe
    {
        public IntVector2 Cell;
        public string Owner = "";
        public abstract bool Alive { get; }
        public abstract string Binding();
        public abstract bool IsPower { get; }
    }

    private sealed class Probe<TComponent> : Probe where TComponent : class
    {
        public WeakReference<ProxyHandler<TComponent>> Handler = null!;

        public override bool Alive => Handler.TryGetTarget(out _);

        public override string Binding() =>
            Handler.TryGetTarget(out var handler) ? Describe(handler) : "collected";

        public override bool IsPower =>
            Handler.TryGetTarget(out var handler) && IsPowerProxy(handler);
    }

    /// <summary>
    /// A steady-state view, because the first capture showed every power contact binding
    /// correctly at load and then only reported again when construction touched it. An
    /// empty bar the player is looking at is a steady state, not an event, so it needs its
    /// own sample. Every power proxy is printed once per interval with what it is bound to
    /// and how full that store actually is.
    /// </summary>
    private static long s_nextSnapshotTicks;

    internal static void Snapshot()
    {
        if (!Enabled)
        {
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        if (now < Volatile.Read(ref s_nextSnapshotTicks))
        {
            return;
        }
        Volatile.Write(ref s_nextSnapshotTicks, now + TimeSpan.TicksPerSecond * 10);

        List<Probe> power;
        lock (Probes)
        {
            Probes.RemoveAll(probe => !probe.Alive);
            power = Probes.FindAll(probe => probe.IsPower);
        }

        for (var i = 0; i < power.Count; i++)
        {
            var probe = power[i];
            Logger.Log(
                "[PB.snap] owner=" + probe.Owner
                + " cell=" + Cell(probe.Cell)
                + " " + probe.Binding());
        }

        NetworkFeedDiagnostics.Snapshot();
    }

    /// <summary>Registers a proxy so completed part operations can report on it.</summary>
    internal static void Track<TComponent>(
        ProxyHandler<TComponent> handler, Part owner, IntVector2 cell)
        where TComponent : class
    {
        if (!Enabled)
        {
            return;
        }

        lock (Probes)
        {
            Probes.RemoveAll(probe => !probe.Alive);
            Probes.Add(new Probe<TComponent>
            {
                Handler = new WeakReference<ProxyHandler<TComponent>>(handler),
                Cell = cell,
                Owner = Name(owner),
            });
        }
    }

    internal static void Untrack<TComponent>(ProxyHandler<TComponent> handler)
        where TComponent : class
    {
        if (!Enabled)
        {
            return;
        }

        lock (Probes)
        {
            Probes.RemoveAll(probe =>
                !probe.Alive
                || (probe is Probe<TComponent> typed
                    && typed.Handler.TryGetTarget(out var held)
                    && ReferenceEquals(held, handler)));
        }
    }

    internal static void Attach<TComponent>(
        ProxyHandler<TComponent> handler, Part owner, IntVector2 cell, Part? existing)
        where TComponent : class
    {
        if (!Enabled)
        {
            return;
        }

        Logger.Log(
            "[PB.attach] owner=" + Name(owner)
            + " cell=" + Cell(cell)
            + " occupant=" + Name(existing)
            + " -> " + Describe(handler));
    }

    internal static void CellEvent<TComponent>(
        string phase, ProxyHandler<TComponent> handler, Part owner, IntVector2 cell,
        Part part, string before, Part? remembered, bool repaired)
        where TComponent : class
    {
        if (!Enabled)
        {
            return;
        }

        Logger.Log(
            "[PB.cell] " + phase
            + " owner=" + Name(owner)
            + " cell=" + Cell(cell)
            + " part=" + Name(part)
            + " remembered=" + Name(remembered)
            + " repaired=" + (repaired ? "yes" : "no")
            + " before=" + before
            + " after=" + Describe(handler));
    }

    /// <summary>
    /// Reports every tracked proxy whose watched cell lies in the rect of a part whose add
    /// or removal has just completed.
    /// </summary>
    internal static void AfterPartOperation(string phase, PartsManager parts, Part part)
    {
        if (!Enabled)
        {
            return;
        }

        var rect = part.Rect;
        List<Probe> hits;
        lock (Probes)
        {
            Probes.RemoveAll(probe => !probe.Alive);
            hits = Probes.FindAll(probe => rect.Contains(probe.Cell));
        }

        for (var i = 0; i < hits.Count; i++)
        {
            var probe = hits[i];
            Part? occupant = null;
            try
            {
                occupant = parts[probe.Cell, PartRectType.Normal];
            }
            catch (Exception)
            {
                // A diagnostic must never be the thing that throws mid-construction.
            }

            Logger.Log(
                "[PB.after] " + phase
                + " part=" + Name(part)
                + " owner=" + probe.Owner
                + " cell=" + Cell(probe.Cell)
                + " occupant=" + Name(occupant)
                + " binding=" + probe.Binding());
        }
    }

    /// <summary>
    /// The ComponentID of a proxy's first entry. This identifies the proxy itself and is
    /// available whether or not it is bound, which the matched entry is not - an unbound
    /// proxy has _proxyableIndex = -1 and would otherwise be indistinguishable from every
    /// other proxy the same part points at the same cell.
    /// </summary>
    internal static string Identity<TComponent>(ProxyHandler<TComponent> handler)
        where TComponent : class
    {
        var entries = handler.Rules?.ProxyableComponents;
        return entries is { Length: > 0 } ? entries[0].ComponentID.ToString() : "?";
    }

    internal static bool IsPowerProxy<TComponent>(ProxyHandler<TComponent> handler)
        where TComponent : class
    {
        var entries = handler.Rules?.ProxyableComponents;
        if (entries == null)
        {
            return false;
        }

        for (var i = 0; i < entries.Length; i++)
        {
            var id = entries[i].ComponentID.ToString();
            if (id == "BatteryStorage"
                || id == "CombinedBatteryStorage"
                || id == "BatteryDistributionStorage"
                || id.StartsWith(AnyStorageBinder.SentinelPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static string Describe<TComponent>(ProxyHandler<TComponent> handler)
        where TComponent : class
    {
        var proxy = "proxy=" + Identity(handler) + " ";
        var proxied = handler._proxiedPart;
        if (proxied == null)
        {
            return proxy + "none";
        }

        var index = handler._proxyableIndex;
        var componentId = "?";
        var entries = handler.Rules?.ProxyableComponents;
        if (entries != null && index >= 0 && index < entries.Length)
        {
            componentId = entries[index].ComponentID.ToString();
        }

        var component = handler.ProxiedComponent;
        var state = component == null ? "awaiting-component" : component.GetType().Name;
        if (component is IResourceStorage storage)
        {
            state += " " + storage.Resources.ToString(CultureInfo.InvariantCulture)
                + "/" + storage.MaxResources.ToString(CultureInfo.InvariantCulture);
        }

        return proxy + Name(proxied)
            + " idx=" + index.ToString(CultureInfo.InvariantCulture)
            + " id=" + componentId
            + " " + state;
    }

    private static string Name(Part? part) =>
        part == null ? "-" : part.Rules.ID.ToString() + "@" + Cell(part.Location);

    private static string Cell(IntVector2 cell) =>
        "[" + cell.X.ToString(CultureInfo.InvariantCulture)
        + "," + cell.Y.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>
    /// The post-operation view. RemovePart's tail is the moment CLAUDE.md names as the one
    /// the immediate cell-removing repair cannot see, because _partsByCell still holds the
    /// departing part while that callback runs.
    /// </summary>
    /// <summary>
    /// The tick the snapshot rides on. PartsManager's nested FixedUpdateCallbacks runs
    /// whenever a ship is simulated; the snapshot itself is wall-clock gated to one dump
    /// per ten seconds, so which bucket fires first does not matter.
    /// </summary>
    [HarmonyPatch]
    internal static class SnapshotTick
    {
        private static bool Prepare() => Enabled;

        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(
                AccessTools.Inner(typeof(PartsManager), "FixedUpdateCallbacks")
                ?? throw new TypeLoadException("PartsManager.FixedUpdateCallbacks"),
                "FixedUpdate")
            ?? throw new MissingMethodException("PartsManager.FixedUpdateCallbacks", "FixedUpdate");

        private static void Postfix() => Snapshot();
    }

    [HarmonyPatch(typeof(PartsManager), "AddPart")]
    internal static class AddPartTail
    {
        private static bool Prepare() => Enabled;

        private static void Postfix(PartsManager __instance, Part part) =>
            AfterPartOperation("added", __instance, part);
    }

    [HarmonyPatch(typeof(PartsManager), "RemovePart")]
    internal static class RemovePartTail
    {
        private static bool Prepare() => Enabled;

        private static void Postfix(PartsManager __instance, Part part) =>
            AfterPartOperation("removed", __instance, part);
    }
}
