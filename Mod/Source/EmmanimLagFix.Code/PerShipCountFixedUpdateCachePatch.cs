using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Resources;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Reuses PerShipCount's allied-ship aggregation only while the requesting
/// ResourceManager is inside one FixedUpdate. The underlying list can be read
/// repeatedly by source search and sink-job validation during that update.
/// AddCount invalidates before changing the list, and leaving FixedUpdate
/// disables reuse, so paused/UI reads and later ticks retain vanilla behavior.
/// </summary>
internal static class PerShipCountFixedUpdateCache
{
    private static readonly ConditionalWeakTable<Ship, ScopeState> Scopes = new();
    private static readonly ConditionalWeakTable<object, CountState> Counts = new();

    internal sealed class ScopeState
    {
        public long Epoch;
        public int Active;
    }

    internal sealed class CountState
    {
        public readonly object Sync = new();
        public readonly WeakReference<Ship> PerspectiveShip = new(null!);
        public long Epoch;
        public long Version;
        public MPValueType ValueType;
        public int Value;
        public bool HasValue;
    }

    internal readonly struct ReadState
    {
        public readonly ScopeState? Scope;
        public readonly CountState? Count;
        public readonly Ship? PerspectiveShip;
        public readonly long Epoch;
        public readonly long Version;
        public readonly MPValueType ValueType;

        public ReadState(
            ScopeState scope,
            CountState count,
            Ship perspectiveShip,
            long epoch,
            long version,
            MPValueType valueType)
        {
            Scope = scope;
            Count = count;
            PerspectiveShip = perspectiveShip;
            Epoch = epoch;
            Version = version;
            ValueType = valueType;
        }
    }

    internal static void Begin(ResourceManager manager)
    {
        var scope = Scopes.GetValue(manager.Ship, static _ => new ScopeState());
        Interlocked.Increment(ref scope.Epoch);
        Volatile.Write(ref scope.Active, 1);
    }

    internal static void End(ResourceManager manager)
    {
        if (Scopes.TryGetValue(manager.Ship, out var scope))
        {
            Volatile.Write(ref scope.Active, 0);
        }
    }

    internal static bool TryRead(
        object countObject,
        Ship perspectiveShip,
        MPValueType valueType,
        out int value,
        out ReadState readState)
    {
        value = 0;
        readState = default;
        if (!Scopes.TryGetValue(perspectiveShip, out var scope) || Volatile.Read(ref scope.Active) == 0)
        {
            return false;
        }

        var epoch = Volatile.Read(ref scope.Epoch);
        var count = Counts.GetValue(countObject, static _ => new CountState());
        lock (count.Sync)
        {
            if (count.HasValue &&
                count.Epoch == epoch &&
                count.ValueType == valueType &&
                count.PerspectiveShip.TryGetTarget(out var cachedShip) &&
                ReferenceEquals(cachedShip, perspectiveShip))
            {
                value = count.Value;
                return true;
            }

            readState = new ReadState(scope, count, perspectiveShip, epoch, count.Version, valueType);
            return false;
        }
    }

    internal static void Store(int value, ReadState state)
    {
        if (state.Scope is null || state.Count is null || state.PerspectiveShip is null ||
            Volatile.Read(ref state.Scope.Active) == 0 || Volatile.Read(ref state.Scope.Epoch) != state.Epoch)
        {
            return;
        }

        lock (state.Count.Sync)
        {
            if (state.Count.Version != state.Version)
            {
                return;
            }

            state.Count.PerspectiveShip.SetTarget(state.PerspectiveShip);
            state.Count.Epoch = state.Epoch;
            state.Count.ValueType = state.ValueType;
            state.Count.Value = value;
            state.Count.HasValue = true;
        }
    }

    internal static void Invalidate(object countObject)
    {
        if (!Counts.TryGetValue(countObject, out var count))
        {
            return;
        }

        lock (count.Sync)
        {
            count.Version++;
            count.HasValue = false;
        }
    }
}

[HarmonyPatch]
internal static class ResourceManagerFixedUpdateScopePatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(typeof(ResourceManager), "FixedUpdate")
        ?? throw new MissingMethodException(typeof(ResourceManager).FullName, "FixedUpdate");

    private static void Prefix(ResourceManager __instance) => PerShipCountFixedUpdateCache.Begin(__instance);

    private static void Finalizer(ResourceManager __instance) => PerShipCountFixedUpdateCache.End(__instance);
}

[HarmonyPatch]
internal static class PerShipCountGetCountCachePatch
{
    private static MethodBase TargetMethod()
    {
        var type = AccessTools.Inner(typeof(ResourceManager), "PerShipCount")
            ?? throw new TypeLoadException("ResourceManager.PerShipCount was not found.");
        return AccessTools.Method(type, "GetCount", new[] { typeof(Ship), typeof(MPValueType) })
            ?? throw new MissingMethodException(type.FullName, "GetCount");
    }

    private static bool Prefix(
        object __instance,
        Ship perspectiveShip,
        MPValueType valueType,
        ref int __result,
        out PerShipCountFixedUpdateCache.ReadState __state)
    {
        return !PerShipCountFixedUpdateCache.TryRead(
            __instance, perspectiveShip, valueType, out __result, out __state);
    }

    private static void Postfix(int __result, PerShipCountFixedUpdateCache.ReadState __state) =>
        PerShipCountFixedUpdateCache.Store(__result, __state);
}

[HarmonyPatch]
internal static class PerShipCountAddCountInvalidationPatch
{
    private static MethodBase TargetMethod()
    {
        var type = AccessTools.Inner(typeof(ResourceManager), "PerShipCount")
            ?? throw new TypeLoadException("ResourceManager.PerShipCount was not found.");
        return AccessTools.Method(type, "AddCount")
            ?? throw new MissingMethodException(type.FullName, "AddCount");
    }

    private static void Prefix(object __instance) => PerShipCountFixedUpdateCache.Invalidate(__instance);
}
