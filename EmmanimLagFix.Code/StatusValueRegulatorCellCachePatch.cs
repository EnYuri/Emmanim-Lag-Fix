using System.Reflection;
using System.Runtime.CompilerServices;
using Halfling.Geometry;
using Halfling.Pooling;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// <c>StatusValueRegulator.GetAffectedCells()</c> (shield/heat-style per-tile
/// value regulation, e.g. sustained shield heat damage) rebuilds its region's
/// cell list via <c>Rules.Region.GetExactArea</c> and re-sorts it by distance
/// from the part's own local center on every trigger. Both the region shape
/// (<c>PartRelativeRadius</c>/<c>Area</c>/<c>EdgeDistance</c>/<c>Grid</c>) and
/// the sort key are pure functions of the calling part's own fixed local
/// geometry (verified by decompilation: they take only the part's
/// <c>LocalCenter</c>, never the rest of the ship), so the sorted cell set
/// never changes for the lifetime of the part and is safe to compute once per
/// <c>StatusValueRegulator</c> instance.
///
/// A 60-second live CPU trace taken while a ship's shield was under sustained
/// heat damage found this method's <c>Sort()</c> call occupying a single
/// thread for up to 146ms, during which every other simulation/render thread
/// sat spin-waiting in <c>PollGCWorker</c> because that one thread never
/// reached a GC safe point — a large-radius region can cover thousands of
/// cells. In multiplayer's lockstep model a stall like this pauses both
/// peers, not just the one with the affected ship.
///
/// The original method (and the pooled <c>TempList</c> contract its callers
/// rely on via <c>using</c>) is left untouched on the first call per
/// instance; only the repeat region-scan-plus-sort is skipped, by copying the
/// cached cell order into a freshly pooled <c>TempList</c> via the existing
/// <c>TempList.Alloc(IList, start, count)</c> overload.
/// </summary>
[HarmonyPatch]
internal static class StatusValueRegulatorCellCachePatch
{
    private static readonly Type RegulatorType = AccessTools.TypeByName(
        "Cosmoteer.Ships.Statuses.StatusValueRegulator")
        ?? throw new TypeLoadException("StatusValueRegulator was not found.");

    private static readonly ConditionalWeakTable<object, IntVector2[]> Caches = new();

    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(RegulatorType, "GetAffectedCells")
            ?? throw new MissingMethodException(RegulatorType.FullName, "GetAffectedCells");
    }

    private static bool Prefix(object __instance, ref TempList<IntVector2> __result, out bool __state)
    {
        if (Caches.TryGetValue(__instance, out var cached))
        {
            __result = TempList<IntVector2>.Alloc(cached, 0, cached.Length);
            __state = false;
            return false;
        }

        __state = true;
        return true;
    }

    private static void Postfix(object __instance, TempList<IntVector2> __result, bool __state)
    {
        if (__state)
        {
            Caches.AddOrUpdate(__instance, __result.ToArray());
        }
    }
}
