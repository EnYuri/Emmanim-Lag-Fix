using System.Reflection;
using System.Runtime.CompilerServices;
using Halfling.Scene2D;
using Halfling.Timing;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Reuses the immutable callback snapshot for a ship update bucket until its
/// backing list changes. Vanilla copies every list into a pooled temporary on
/// every update so callbacks may safely register or unregister callbacks while
/// it is being enumerated. Keying the cached array to List's mutation version
/// preserves that exact snapshot-on-entry behavior without the repeated
/// reference-array copy and write-barrier work.
/// </summary>
internal static class PartCallbackSnapshots<TCallback>
    where TCallback : Delegate
{
    private sealed class Snapshot
    {
        public int Version;
        public TCallback[]? Callbacks;
    }

    private static readonly ConditionalWeakTable<object, Snapshot> Snapshots = new();
    private static readonly AccessTools.FieldRef<List<TCallback>, int> ListVersion =
        AccessTools.FieldRefAccess<List<TCallback>, int>("_version");

    internal static TCallback[] Get(object owner, List<TCallback> callbacks)
    {
        var version = ListVersion(callbacks);
        var state = Snapshots.GetValue(owner, static _ => new Snapshot());
        if (state.Callbacks is null || state.Version != version)
        {
            // Publish the completed array before its matching version. Scene
            // callback lists are main-thread owned, just as in vanilla.
            state.Callbacks = callbacks.ToArray();
            state.Version = version;
        }

        return state.Callbacks;
    }
}

[HarmonyPatch]
internal static class PartUpdateCallbackSnapshotPatch
{
    private static readonly Type CallbackContainerType = AccessTools.TypeByName(
        "Cosmoteer.Ships.Parts.PartsManager+UpdateCallbacks")
        ?? throw new TypeLoadException("PartsManager.UpdateCallbacks was not found.");
    private static readonly AccessTools.FieldRef<object, List<SceneComponent.UpdateCallback>> Callbacks =
        AccessTools.FieldRefAccess<List<SceneComponent.UpdateCallback>>(
            CallbackContainerType,
            "<Callbacks>k__BackingField");

    private static MethodBase TargetMethod() =>
        AccessTools.Method(CallbackContainerType, "Update")
        ?? throw new MissingMethodException(CallbackContainerType.FullName, "Update");

    // Run after the existing blueprint-refresh gate has established its
    // thread-local state. Postfixes and finalizers still run when this skips the
    // vanilla body, so that gate restores its state normally.
    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(object __instance, SceneRoot root)
    {
        foreach (var callback in GetSnapshot(__instance))
        {
            callback(root);
        }

        return false;
    }

    internal static SceneComponent.UpdateCallback[] GetSnapshot(object instance)
    {
        var callbacks = Callbacks(instance);
        return PartCallbackSnapshots<SceneComponent.UpdateCallback>.Get(instance, callbacks);
    }
}

[HarmonyPatch]
internal static class PartFixedUpdateCallbackSnapshotPatch
{
    private static readonly Type CallbackContainerType = AccessTools.TypeByName(
        "Cosmoteer.Ships.Parts.PartsManager+FixedUpdateCallbacks")
        ?? throw new TypeLoadException("PartsManager.FixedUpdateCallbacks was not found.");
    private static readonly AccessTools.FieldRef<object, List<SceneComponent.FixedUpdateCallback>> Callbacks =
        AccessTools.FieldRefAccess<List<SceneComponent.FixedUpdateCallback>>(
            CallbackContainerType,
            "<Callbacks>k__BackingField");

    private static MethodBase TargetMethod() =>
        AccessTools.Method(CallbackContainerType, "FixedUpdate")
        ?? throw new MissingMethodException(CallbackContainerType.FullName, "FixedUpdate");

    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(object __instance, FixedUpdater fixedUpdater, SceneRoot root)
    {
        foreach (var callback in GetSnapshot(__instance))
        {
            callback(fixedUpdater, root);
        }

        return false;
    }

    internal static SceneComponent.FixedUpdateCallback[] GetSnapshot(object instance)
    {
        var callbacks = Callbacks(instance);
        return PartCallbackSnapshots<SceneComponent.FixedUpdateCallback>.Get(instance, callbacks);
    }
}
