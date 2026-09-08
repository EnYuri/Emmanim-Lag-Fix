using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Cosmoteer.Ships.Networks;
using Cosmoteer.Ships.Parts;
using Halfling.Logging;
using HarmonyLib;

namespace ModsQol.Code;

/// <summary>
/// Opt-in instrumentation for the wire network itself, sharing
/// proxy-binding-diagnostics.flag and the ten-second snapshot in
/// <see cref="ProxyBindingDiagnostics"/>.
///
/// Added because the proxy capture ruled its own subject out. Both feeds off one
/// generator - the one to a shield that fills and the one to an engine room that
/// sits at exactly 0 - were bound correctly and to the right tier, and the
/// engine room's destination had 0 of 54,000 used, so its converter's
/// MinToQuantityForConversion gate was open the whole time. A converter with an
/// open gate that never fires has nothing to send, which puts the fault upstream
/// of every proxy: in what the network hands the staging buffer.
///
/// That the failure survives until the part is rebuilt, is repaired by a reload,
/// and lands on the shield or the engine room unpredictably across rebuilds all
/// point the same way - subnetwork membership settled by construction order.
///
/// Two records:
///
///   [NF.store] one staging sink - the 1,000-unit TerminalDeliveryStorage or
///              DirectStorage_* a wire terminal or generator face owns. Prints
///              what the network has actually put in it.
///   [NF.input] one generator's WirePowerInput. Its capacity figures are the
///              whole reachable network's, so they say how much the generator
///              believes is downstream of it.
///
/// Read the store line first. Held at 0 means the network never delivers there,
/// which is a membership or share fault; pinned at its maximum means delivery
/// works and the converter below it is stuck. The two have opposite fixes.
/// </summary>
internal static class NetworkFeedDiagnostics
{
    private static readonly List<WeakReference<PartNetworkResourceStore>> Stores = new();
    private static readonly List<WeakReference<PartNetworkResourceInput>> Inputs = new();

    // The subnetwork a node belongs to identifies the group a generator's push is split
    // across, which is the thing actually suspected. ComponentNode is generic over two
    // type arguments and awkward to name here, so it is read reflectively and treated as
    // optional: a diagnostic must not fail to build over a nicety.
    private static PropertyInfo? s_subnetwork;
    private static bool s_subnetworkResolved;

    private static string Subnet(object? node)
    {
        if (node == null)
        {
            return "-";
        }

        if (!s_subnetworkResolved)
        {
            s_subnetworkResolved = true;
            s_subnetwork = node.GetType().GetProperty("Subnetwork")
                ?? node.GetType().GetProperty("ParentSubnetwork");
        }

        try
        {
            var value = s_subnetwork?.GetValue(node);
            return value == null
                ? "-"
                : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value)
                    .ToString("x8", CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string Where(PartComponent component)
    {
        var part = component.Part;
        var id = component.Rules?.ID.ToString() ?? "?";
        return part == null
            ? id + "@-"
            : id + " on " + part.Rules.ID + "@["
                + part.Location.X.ToString(CultureInfo.InvariantCulture) + ","
                + part.Location.Y.ToString(CultureInfo.InvariantCulture) + "]";
    }

    internal static void Snapshot()
    {
        if (!ProxyBindingDiagnostics.Enabled)
        {
            return;
        }

        lock (Stores)
        {
            Stores.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in Stores)
            {
                if (!weak.TryGetTarget(out var store))
                {
                    continue;
                }

                try
                {
                    Logger.Log(
                        "[NF.store] " + Where(store)
                        + " type=" + store.ResourceType
                        + " held=" + store.AvailableResources.ToString(CultureInfo.InvariantCulture)
                        + "/" + store.TotalCapacity.ToString(CultureInfo.InvariantCulture)
                        + " free=" + store.AvailableCapacity.ToString(CultureInfo.InvariantCulture)
                        + " subnet=" + Subnet(store.NetworkNode));
                }
                catch (Exception)
                {
                    // Never let the instrumentation be the thing that throws.
                }
            }
        }

        lock (Inputs)
        {
            Inputs.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in Inputs)
            {
                if (!weak.TryGetTarget(out var input))
                {
                    continue;
                }

                try
                {
                    Logger.Log(
                        "[NF.input] " + Where(input)
                        + " networkHeld=" + input.Resources.ToString(CultureInfo.InvariantCulture)
                        + "/" + input.MaxResources.ToString(CultureInfo.InvariantCulture)
                        + " subnet=" + Subnet(input.NetworkNode));
                }
                catch (Exception)
                {
                    // As above.
                }
            }
        }
    }

    [HarmonyPatch(typeof(PartNetworkResourceStore), "OnNetworkRegistered")]
    internal static class StoreRegistered
    {
        private static bool Prepare() => ProxyBindingDiagnostics.Enabled;

        private static void Postfix(PartNetworkResourceStore __instance)
        {
            lock (Stores)
            {
                Stores.RemoveAll(w => !w.TryGetTarget(out var held) || ReferenceEquals(held, __instance));
                Stores.Add(new WeakReference<PartNetworkResourceStore>(__instance));
            }
        }
    }

    [HarmonyPatch(typeof(PartNetworkResourceInput), "OnNetworkRegistered")]
    internal static class InputRegistered
    {
        private static bool Prepare() => ProxyBindingDiagnostics.Enabled;

        private static void Postfix(PartNetworkResourceInput __instance)
        {
            lock (Inputs)
            {
                Inputs.RemoveAll(w => !w.TryGetTarget(out var held) || ReferenceEquals(held, __instance));
                Inputs.Add(new WeakReference<PartNetworkResourceInput>(__instance));
            }
        }
    }
}
