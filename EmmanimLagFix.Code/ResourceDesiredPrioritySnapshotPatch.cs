using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Resources;
using Cosmoteer.Ships.Parts.Resources;
using Cosmoteer.Ships.Resources;
using Halfling.Timing;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// BaseResourceStorage priority checks repeatedly calculate the same ship-wide
/// resource-desire result for every sink/source pair. That calculation can
/// include a full scan of off-ship assigned crew. Capture each relevant result
/// once immediately before ResourceManager's parallel sink-job pass, publish
/// the completed dictionary read-only, and discard it when the pass ends.
/// </summary>
[HarmonyPatch]
internal static class ResourceDesiredPrioritySnapshotPatch
{
    private sealed class State
    {
        public readonly HashSet<ID<ResourceRules>> Types = new();
        public readonly Dictionary<ID<ResourceRules>, bool> Values = new();
        public Dictionary<ID<ResourceRules>, bool>? Published;
    }

    private static readonly ConditionalWeakTable<ResourceManager, State> States = new();

    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(ResourceManager), "UpdateSinkJobs", new[] { typeof(Time) })
        ?? throw new MissingMethodException(typeof(ResourceManager).FullName, "UpdateSinkJobs(Time)");

    private static void Prefix(ResourceManager __instance)
    {
        var state = States.GetValue(__instance, static _ => new State());
        Volatile.Write(ref state.Published, null);
        state.Types.Clear();
        state.Values.Clear();

        foreach (var source in __instance._sources)
        {
            AddIfDesired(source.Source.ResourceType);
        }
        foreach (var sink in __instance._sinks)
        {
            AddIfDesired(sink.Sink.ResourceType);
            AddIfDesired(sink.SourceType);
        }

        foreach (var resourceType in state.Types)
        {
            var total = __instance.GetResourceTotal(
                resourceType,
                includeStored: true,
                includeCarried: true,
                includeOffShipCarried: true,
                includeAnticipatedDeliveries: true,
                includeAnticipatedPickups: false,
                MPValueType.Confirmed);
            var desired = __instance.Ship.Metadata.GetResourceDesire(
                resourceType,
                MPValueType.Confirmed);
            state.Values.Add(resourceType, total < desired);
        }

        // Publish only after construction. Worker threads never observe a
        // dictionary that is still being mutated.
        Volatile.Write(ref state.Published, state.Values);

        void AddIfDesired(ID<ResourceRules> resourceType)
        {
            if (resourceType != ResourceRules.Stackable
                && __instance.Ship.Metadata.HasResourceDesire(
                    resourceType,
                    MPValueType.Confirmed))
            {
                state.Types.Add(resourceType);
            }
        }
    }

    private static void Finalizer(ResourceManager __instance)
    {
        if (States.TryGetValue(__instance, out var state))
        {
            Volatile.Write(ref state.Published, null);
        }
    }

    internal static bool TryGetSnapshot(
        BaseResourceStorage storage,
        out bool hasUnmetDesired)
    {
        var manager = storage.Part?.Ship?.Resources;
        if (manager is not null
            && States.TryGetValue(manager, out var state)
            && Volatile.Read(ref state.Published) is { } snapshot
            && snapshot.TryGetValue(storage.ResourceType, out hasUnmetDesired))
        {
            return true;
        }

        hasUnmetDesired = false;
        return false;
    }
}

[HarmonyPatch]
internal static class BaseResourceStorageDesiredPriorityPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(
            typeof(BaseResourceStorage),
            "<GetSortPriority>g___HasUnmetDesired|181_0")
        ?? throw new MissingMethodException(
            typeof(BaseResourceStorage).FullName,
            "<GetSortPriority>g___HasUnmetDesired|181_0");

    private static bool Prefix(BaseResourceStorage __instance, ref bool __result)
    {
        if (ResourceDesiredPrioritySnapshotPatch.TryGetSnapshot(
            __instance,
            out var hasUnmetDesired))
        {
            __result = hasUnmetDesired;
            return false;
        }

        return true;
    }
}
