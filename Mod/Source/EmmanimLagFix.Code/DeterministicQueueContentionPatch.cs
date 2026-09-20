using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Game;
using Cosmoteer.Ships;
using Cosmoteer.Simulation;
using Cosmoteer.Simulation.HitEffects;
using Halfling.Geometry;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Removes the monitor convoy on the simulation's two deterministic callback
/// queues. Vanilla serializes every producer behind
/// <c>lock (_queuedDeterministic)</c> / <c>lock (_queuedHitEffects)</c>; a live
/// CPU trace put several seconds per minute of worker time on those locks'
/// slow path during beam-heavy combat. The lock only exists to serialize
/// producers: the enqueued actionID is just the arrival order, and
/// ExecuteQueued later sorts by (objectID, actionID) on the main thread.
///
/// The replacement enqueues onto a ConcurrentQueue and stamps each entry with
/// an Interlocked sequence number — the same "arrival order" vanilla records,
/// acquired the same racy way (CAS order instead of lock order). The drain
/// still runs inside ExecuteQueued at the same point in the tick, still sorts
/// by (objectID, sequence), and still re-drains entries that callbacks post
/// mid-drain — the appended-unsorted-tail semantics of vanilla's live-Count
/// loop. Entries that arrive after the last dequeue stay queued for the next
/// drain instead of being cleared unseen, which is strictly safer than
/// vanilla's concurrent Add/Clear race.
///
/// Ordering between producers racing on different threads remains exactly as
/// (un)defined as vanilla: a total order consistent with whichever thread won
/// the race, then grouped by objectID at drain. Nothing about the deterministic
/// lockstep inputs, execution content, or drain timing changes.
/// </summary>
[HarmonyPatch]
internal static class DeterministicQueueContentionPatch
{
    internal static bool Applied;

    internal readonly struct DetAction : IComparable<DetAction>
    {
        internal readonly ObjectID ShipID;
        internal readonly long Seq;
        internal readonly Action<object?> Action;
        internal readonly object? Data;

        internal DetAction(ObjectID shipID, long seq, Action<object?> action, object? data)
        {
            ShipID = shipID;
            Seq = seq;
            Action = action;
            Data = data;
        }

        public int CompareTo(DetAction other)
        {
            var byShip = ShipID.CompareTo(other.ShipID);
            return byShip != 0 ? byShip : Seq.CompareTo(other.Seq);
        }
    }

    internal readonly struct DetHitFx : IComparable<DetHitFx>
    {
        internal readonly ObjectID ShipID;
        internal readonly long Seq;
        internal readonly MultiHitEffectRules HitEffects;
        internal readonly HitEffectParams EffectParams;
        internal readonly Vector2? HitShipRelativePoint;

        internal DetHitFx(ObjectID shipID, long seq, MultiHitEffectRules hitEffects, HitEffectParams effectParams)
        {
            ShipID = shipID;
            Seq = seq;
            HitEffects = hitEffects;
            EffectParams = effectParams;
            // Captured at enqueue time exactly like vanilla's queued struct.
            HitShipRelativePoint =
                effectParams.HitShip?.DetTransformPointFromWorld(effectParams.WorldPoint);
        }

        public int CompareTo(DetHitFx other)
        {
            var byShip = ShipID.CompareTo(other.ShipID);
            return byShip != 0 ? byShip : Seq.CompareTo(other.Seq);
        }
    }

    internal sealed class QueueState
    {
        internal readonly ConcurrentQueue<DetAction> Actions = new();
        internal readonly ConcurrentQueue<DetHitFx> HitEffects = new();
        internal readonly List<DetAction> DrainActions = new();
        internal readonly List<DetHitFx> DrainHitEffects = new();
        internal long ActionSeq;
        internal long HitFxSeq;
    }

    private static readonly ConditionalWeakTable<object, QueueState> States = new();

    private sealed class Hot(object sim, QueueState state)
    {
        internal readonly object Sim = sim;
        internal readonly QueueState State = state;
    }

    private static Hot? _hot;

    private static QueueState? StateFor(object sim, bool create)
    {
        var hot = _hot;
        if (hot != null && ReferenceEquals(hot.Sim, sim))
            return hot.State;
        if (!create)
            return States.TryGetValue(sim, out var found) ? found : null;
        var state = States.GetValue(sim, static _ => new QueueState());
        _hot = new Hot(sim, state);
        return state;
    }

    private static bool ShapeOk()
    {
        var sim = typeof(SimRoot);
        var queuedAction = sim.GetNestedType("QueuedAction", BindingFlags.NonPublic);
        var queuedHitFx = sim.GetNestedType("QueuedHitEffects", BindingFlags.NonPublic);
        var detField = AccessTools.Field(sim, "_queuedDeterministic");
        var hitFxField = AccessTools.Field(sim, "_queuedHitEffects");
        var ok =
            detField != null
            && detField.FieldType.IsGenericType
            && detField.FieldType.GetGenericTypeDefinition() == typeof(List<>)
            && hitFxField != null
            && hitFxField.FieldType.IsGenericType
            && hitFxField.FieldType.GetGenericTypeDefinition() == typeof(List<>)
            && queuedAction != null
            && queuedHitFx != null
            && AccessTools.DeclaredMethod(sim, "EnqueueDeterministic",
                    new[] { typeof(ObjectID), typeof(object), typeof(Action<object>) }) != null
            && AccessTools.DeclaredMethod(sim, "EnqueueDeterministic",
                    new[] { typeof(ObjectID), typeof(MultiHitEffectRules), typeof(HitEffectParams) }) != null
            && AccessTools.DeclaredMethod(sim, "ExecuteQueued",
                    new[] { typeof(bool), typeof(bool) }) != null
            && AccessTools.Method(typeof(HitEffectParams), "Retain") != null
            && AccessTools.Method(typeof(HitEffectParams), "Release") != null
            && AccessTools.Method(typeof(MultiHitEffectRules), "DoEffect") != null
            && AccessTools.Method(typeof(Ship), "DetTransformPointFromWorld") != null
            && AccessTools.Method(typeof(Ship), "DetTransformPointToWorld") != null;
        if (!ok)
        {
            Logger.Log(
                "[EmmanimLagFix] SimRoot deterministic queue internals changed; leaving vanilla behaviour.");
        }
        else
        {
            Applied = true;
        }
        return ok;
    }

    internal static void EnqueueAction(object sim, ObjectID objectID, object? data, Action<object?> callback)
    {
        var state = StateFor(sim, create: true)!;
        var seq = Interlocked.Increment(ref state.ActionSeq) - 1;
        state.Actions.Enqueue(new DetAction(objectID, seq, callback, data));
    }

    internal static void EnqueueHitEffects(
        object sim, ObjectID objectID, MultiHitEffectRules hitEffects, HitEffectParams effectParams)
    {
        var state = StateFor(sim, create: true)!;
        var seq = Interlocked.Increment(ref state.HitFxSeq) - 1;
        state.HitEffects.Enqueue(new DetHitFx(objectID, seq, hitEffects, effectParams));
    }

    // Runs before vanilla's ExecuteQueued body: the vanilla lists stay empty
    // (enqueues were redirected), so its sort/loop/clear are no-ops and its
    // non-deterministic drain plus any postfix still run afterwards, keeping
    // the vanilla deterministic-before-nondeterministic ordering.
    internal static void Drain(object sim)
    {
        var state = StateFor(sim, create: false);
        if (state == null)
            return;

        var actions = state.Actions;
        if (!actions.IsEmpty)
        {
            var list = state.DrainActions;
            // The scratch list is reused across drains, so it has to be
            // emptied even when a callback throws. Vanilla leaks the same way
            // -- its exception leaves _queuedDeterministic uncleared and the
            // next ExecuteQueued re-sorts and re-runs the entries it already
            // executed -- but on the deterministic path a double-run is a
            // desync, so do not reproduce that.
            try
            {
                while (actions.TryDequeue(out var action))
                    list.Add(action);
                if (list.Count > 1)
                    list.Sort();
                var i = 0;
                while (true)
                {
                    for (; i < list.Count; i++)
                    {
                        var item = list[i];
                        item.Action(item.Data);
                    }
                    var arrived = false;
                    while (actions.TryDequeue(out var action))
                    {
                        list.Add(action);
                        arrived = true;
                    }
                    if (!arrived)
                        break;
                }
            }
            finally
            {
                list.Clear();
            }
        }

        var hitEffects = state.HitEffects;
        if (!hitEffects.IsEmpty)
        {
            var list = state.DrainHitEffects;
            // Same reuse hazard as the action list above; see the comment there.
            try
            {
                while (hitEffects.TryDequeue(out var effect))
                    list.Add(effect);
                if (list.Count > 1)
                    list.Sort();
                var i = 0;
                while (true)
                {
                    for (; i < list.Count; i++)
                    {
                        var item = list[i];
                        var relative = item.HitShipRelativePoint;
                        if (relative.HasValue)
                        {
                            item.EffectParams.WorldPoint =
                                item.EffectParams.HitShip!.DetTransformPointToWorld(relative.GetValueOrDefault());
                        }
                        item.HitEffects.DoEffect(item.EffectParams);
                        item.EffectParams.Release();
                    }
                    var arrived = false;
                    while (hitEffects.TryDequeue(out var effect))
                    {
                        list.Add(effect);
                        arrived = true;
                    }
                    if (!arrived)
                        break;
                }
            }
            finally
            {
                list.Clear();
            }
        }
    }

    internal static void Release(object sim)
    {
        var hot = Volatile.Read(ref _hot);
        if (hot != null && ReferenceEquals(hot.Sim, sim))
            Interlocked.CompareExchange(ref _hot, null, hot);
        States.Remove(sim);
    }

    [HarmonyPatch]
    private static class EnqueueActionPatch
    {
        private static bool Prepare() => ShapeOk();

        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(typeof(SimRoot), "EnqueueDeterministic",
                new[] { typeof(ObjectID), typeof(object), typeof(Action<object>) })
            ?? throw new MissingMethodException(nameof(SimRoot), "EnqueueDeterministic");

        private static bool Prefix(object __instance, ObjectID objectID, object? data, Action<object?> callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));
            EnqueueAction(__instance, objectID, data, callback);
            return false;
        }
    }

    [HarmonyPatch]
    private static class EnqueueHitEffectsPatch
    {
        private static bool Prepare() => ShapeOk();

        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(typeof(SimRoot), "EnqueueDeterministic",
                new[] { typeof(ObjectID), typeof(MultiHitEffectRules), typeof(HitEffectParams) })
            ?? throw new MissingMethodException(nameof(SimRoot), "EnqueueDeterministic(hitEffects)");

        private static bool Prefix(
            object __instance, ObjectID objectID, MultiHitEffectRules hitEffects, HitEffectParams effectParams)
        {
            if (hitEffects == null)
                throw new ArgumentNullException(nameof(hitEffects));
            if (effectParams == null)
                throw new ArgumentNullException(nameof(effectParams));
            effectParams.Retain();
            EnqueueHitEffects(__instance, objectID, hitEffects, effectParams);
            return false;
        }
    }

    [HarmonyPatch]
    private static class ExecutePatch
    {
        private static bool Prepare() => ShapeOk();

        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(typeof(SimRoot), "ExecuteQueued",
                new[] { typeof(bool), typeof(bool) })
            ?? throw new MissingMethodException(nameof(SimRoot), "ExecuteQueued");

        private static void Prefix(object __instance, bool deterministic)
        {
            if (deterministic)
                Drain(__instance);
        }
    }

    [HarmonyPatch]
    private static class DisposePatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(typeof(SimRoot), nameof(IDisposable.Dispose))
            ?? throw new MissingMethodException(nameof(SimRoot), nameof(IDisposable.Dispose));

        private static void Postfix(object __instance) => Release(__instance);
    }
}
