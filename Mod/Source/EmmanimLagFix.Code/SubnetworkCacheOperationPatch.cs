using System.Reflection;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Networks.Queries;
using Cosmoteer.Ships.Parts;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Trims dead listener iteration out of
/// <see cref="SubnetworkQueryCache{TPart,TComponent}.OnResourceOperationStart"/>
/// / <see cref="SubnetworkQueryCache{TPart,TComponent}.OnResourceOperationEnd"/>.
///
/// Every network resource push/pull wraps its per-sink write loop in these two
/// calls, and each iterates every query result on the cache. Only
/// <c>OnCacheResourceOperationEnded</c> has real overrides (sink, source,
/// value, and toggle results flush deferred events); every
/// <c>OnCacheResourceOperationStarted</c> is the empty base implementation and
/// trigger results never override either. So the start iteration is pure dead
/// work, and the trigger dictionary contributes nothing to the end pass.
///
/// The flag lifecycle is preserved exactly: start sets
/// <c>ResourceOperationOngoing</c> true, end clears it first and then flushes
/// the four listener dictionaries in vanilla's iteration order. Nested
/// operations on the same cache behave identically to vanilla because the
/// flag is still set/cleared at the same points.
/// </summary>
[HarmonyPatch]
internal static class SubnetworkCacheOperationPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return Required(typeof(SubnetworkQueryCache<Part, PartComponent>), "OnResourceOperationStart");
        yield return Required(typeof(SubnetworkQueryCache<Part, PartComponent>), "OnResourceOperationEnd");
        yield return Required(typeof(SubnetworkQueryCache<BlueprintPart, BlueprintPartComponent>), "OnResourceOperationStart");
        yield return Required(typeof(SubnetworkQueryCache<BlueprintPart, BlueprintPartComponent>), "OnResourceOperationEnd");
    }

    private static MethodBase Required(Type type, string name) =>
        AccessTools.DeclaredMethod(type, name)
        ?? throw new MissingMethodException(type.FullName, name);

    private static bool Prefix(object __instance, MethodBase __originalMethod)
    {
        switch (__instance)
        {
            case SubnetworkQueryCache<Part, PartComponent> cache:
                Run(cache, __originalMethod.Name, CacheAccess<Part, PartComponent>.Ongoing);
                break;
            case SubnetworkQueryCache<BlueprintPart, BlueprintPartComponent> cacheBp:
                Run(cacheBp, __originalMethod.Name, CacheAccess<BlueprintPart, BlueprintPartComponent>.Ongoing);
                break;
        }
        return false;
    }

    private static void Run<TPart, TComponent>(
        SubnetworkQueryCache<TPart, TComponent> cache, string method,
        AccessTools.FieldRef<SubnetworkQueryCache<TPart, TComponent>, bool> ongoing)
        where TPart : CommonBasePart where TComponent : class, IPartComponent<TPart>
    {
        if (method == "OnResourceOperationStart")
        {
            ongoing(cache) = true;
            return;
        }

        ongoing(cache) = false;
        if (cache._resourceSourceQueries != null)
            foreach (var result in cache._resourceSourceQueries.Values)
                result.OnCacheResourceOperationEnded(cache);
        if (cache._resourceSinkQueries != null)
            foreach (var result in cache._resourceSinkQueries.Values)
                result.OnCacheResourceOperationEnded(cache);
        if (cache._valueQueries != null)
            foreach (var result in cache._valueQueries.Values)
                result.OnCacheResourceOperationEnded(cache);
        if (cache._toggleQueries != null)
            foreach (var result in cache._toggleQueries.Values)
                result.OnCacheResourceOperationEnded(cache);
    }

    /// <summary>
    /// The auto-property setter is compiler-generated and not publicized, so
    /// the backing field is referenced once per closed instantiation.
    /// </summary>
    private static class CacheAccess<TPart, TComponent>
        where TPart : CommonBasePart where TComponent : class, IPartComponent<TPart>
    {
        public static readonly AccessTools.FieldRef<SubnetworkQueryCache<TPart, TComponent>, bool>
            Ongoing = AccessTools.FieldRefAccess<SubnetworkQueryCache<TPart, TComponent>, bool>(
                "<ResourceOperationOngoing>k__BackingField");
    }
}
