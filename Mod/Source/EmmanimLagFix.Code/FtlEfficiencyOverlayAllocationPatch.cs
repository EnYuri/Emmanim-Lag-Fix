using System.Reflection;
using Cosmoteer;
using Cosmoteer.Data;
using Cosmoteer.Game.Gui.Build;
using Cosmoteer.Game.Gui.Build.Stats;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Ftl;
using Halfling;
using Halfling.Geometry;
using Halfling.Graphics;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// <c>BuildToolbox.DrawFtlEfficiencyOverlay</c> runs every frame while a ship
/// is being edited. Its steady-state gate is a LINQ
/// <c>SequenceEqual</c> over <c>List&lt;PendingFtlDrive&gt;</c>, which boxes
/// both list enumerators every frame. When the pending-drive list changes (or
/// <c>_needRecalculateFtlEfficiency</c> was flagged) it also allocates a fresh
/// cached list, a per-cell callback closure, and inside
/// <c>BlueprintPartsManager.CalculateJumpEfficiency</c> a
/// <c>List&lt;Tuple&lt;…&gt;&gt;</c> plus one heap <c>Tuple</c> per FTL drive
/// component, an <c>OfType</c> iterator per drive part, an <c>Any</c> closure
/// per drive part, and one boxed <c>IEnumerator&lt;IntVector2&gt;</c> per ship
/// part from <c>IntRect</c>'s interface-returning enumerator. A live
/// allocation trace attributed ~120 MiB per minute to this path.
///
/// This prefix re-implements the method with identical semantics and identical
/// floating-point accumulation order, but with concrete <c>List&lt;T&gt;</c>
/// indexing / <c>is</c> checks instead of LINQ, a value-tuple drive list, a
/// reused cached-pending list, and direct texture writes instead of the
/// per-cell callback. The math is unchanged: drives are still enumerated in
/// <c>GetPartsInCategory(Ftl)</c> order then <c>Components</c> order, cells are
/// still visited in <c>IntRect</c>'s top-to-bottom, left-to-right order, and
/// per-cell efficiencies still accumulate in drive-list order.
/// </summary>
[HarmonyPatch]
internal static class FtlEfficiencyOverlayAllocationPatch
{
    private static bool ShapeOk()
    {
        var toolbox = typeof(BuildToolbox);
        var ok =
            AccessTools.Field(toolbox, "_cachedPendingFtlDrives") != null
            && AccessTools.Field(toolbox, "_needRecalculateFtlEfficiency") != null
            && AccessTools.Field(toolbox, "_ftlOverlayTex") != null
            && AccessTools.Field(toolbox, "_largestShipBounds") != null
            && AccessTools.Field(toolbox, "_statsGui") != null
            && AccessTools.Field(toolbox, "_ship") != null
            && AccessTools.PropertyGetter(typeof(BuildToolboxStatsGui), "IsFtlHovered") != null
            && AccessTools.Method(typeof(BuildToolboxStatsGui), "UpdateFtlBar") != null
            && AccessTools.PropertyGetter(typeof(BaseBuildState), "PendingFtlDrives") != null
            && AccessTools.Method(typeof(BlueprintPartsManager), "GetPartsInCategory",
                    new[] { typeof(ID<PartCategory>) }) != null
            && AccessTools.Method(typeof(BlueprintPartsManager), "GetEnumerator", Type.EmptyTypes) != null
            && AccessTools.Method(typeof(FtlDriveRules), "GetJumpEfficiency",
                    new[] { typeof(float) }) != null
            && AccessTools.Method(typeof(PartComponentRules), "GetTotalLocation",
                    new[] { typeof(PartRules) }) != null
            && AccessTools.PropertyGetter(typeof(BlueprintPart), "PhysicalRect") != null;
        if (!ok)
        {
            Logger.Log(
                "[EmmanimLagFix] BuildToolbox FTL-overlay internals changed; leaving vanilla behaviour.");
        }
        return ok;
    }

    private static bool Prepare() => ShapeOk();

    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(typeof(BuildToolbox), "DrawFtlEfficiencyOverlay")
        ?? throw new MissingMethodException(nameof(BuildToolbox), "DrawFtlEfficiencyOverlay");

    // Matches vanilla's gate: a null cached list is equal only to a null
    // current list; otherwise the cached list is sequence-equal to the current
    // list (with null counting as empty). List indexers keep this allocation
    // free where SequenceEqual boxed both enumerators.
    private static bool PendingDrivesEqual(List<PendingFtlDrive>? cached, List<PendingFtlDrive>? current)
    {
        if (cached == null)
            return current == null;
        var cachedCount = cached.Count;
        var currentCount = current?.Count ?? 0;
        if (cachedCount != currentCount)
            return false;
        for (var i = 0; i < cachedCount; i++)
        {
            if (!cached[i].Equals(current![i]))
                return false;
        }
        return true;
    }

    private static unsafe bool Prefix(BuildToolbox __instance)
    {
        var list = (__instance.Game.Gui.GetSelectionState() as BaseBuildState)?.PendingFtlDrives;
        if (__instance._needRecalculateFtlEfficiency
            || !PendingDrivesEqual(__instance._cachedPendingFtlDrives, list))
        {
            // Refresh the cached list in place; vanilla allocated a new one.
            if (list == null)
            {
                __instance._cachedPendingFtlDrives = null;
            }
            else
            {
                var cached = __instance._cachedPendingFtlDrives;
                if (cached == null)
                {
                    __instance._cachedPendingFtlDrives = cached = new List<PendingFtlDrive>(list.Count);
                }
                else
                {
                    cached.Clear();
                }
                for (var i = 0; i < list.Count; i++)
                    cached.Add(list[i]);
            }

            var tex = __instance._ftlOverlayTex;
            var texPtr = tex.GetUnsafeTempPointer(0, out var texStride, readOnly: false);
            var size = tex.SourceSize;
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    *(IntColor*)(texPtr + y * texStride + (nint)x * (nint)sizeof(IntColor)) =
                        IntColor.TransparentWhite;
                }
            }

            var efficiency = CalculateJumpEfficiency(
                __instance._ship!.BlueprintParts,
                list,
                texPtr,
                texStride,
                __instance._largestShipBounds,
                __instance.Rules);
            tex.UploadHint();
            __instance._statsGui.UpdateFtlBar(efficiency, list != null);
            __instance._needRecalculateFtlEfficiency = false;
        }

        if (__instance._statsGui.IsFtlHovered || list != null)
        {
            using (App.Graphics.PushTransform(
                       __instance.Sim.Viewport.WorldToViewportTransform * __instance._ship!.WorldTransform))
            {
                App.Graphics.SetToMaterial(__instance._ftlOverlayTex);
                App.Graphics.Draw(new Quad(__instance._largestShipBounds, __instance._ftlOverlayTex));
            }
        }
        return false;
    }

    // One scratch list shared by every call; the overlay only runs on the GUI
    // thread. Value tuples keep the per-drive entries inside the list's
    // backing array instead of allocating one Tuple per drive.
    private static readonly List<(BlueprintPart Part, FtlDriveRules Rules, Vector2 Center)> DriveScratch =
        new();

    // Allocation-free re-implementation of
    // BlueprintPartsManager.CalculateJumpEfficiency with the efficiencyPerCell
    // callback inlined as the texture write it always was. The accumulation
    // order is preserved exactly, so the returned efficiency is bit-identical.
    private static unsafe float CalculateJumpEfficiency(
        BlueprintPartsManager parts,
        List<PendingFtlDrive>? pendingFtlDrives,
        nint texPtr,
        int texStride,
        IntRect bounds,
        BuildGuiRules rules)
    {
        var partsInCategory = parts.GetPartsInCategory(PartCategories.Ftl);
        var pendingCount = pendingFtlDrives?.Count ?? 0;
        if (partsInCategory.Count == 0 && pendingCount == 0)
            return float.NaN;

        var drives = DriveScratch;
        drives.Clear();

        // Vanilla enumerates an IReadOnlyCollection; on this build it is always
        // a HashSet, whose struct enumerator yields the identical order.
        if (partsInCategory is HashSet<BlueprintPart> driveSet)
        {
            foreach (var drivePart in driveSet)
                CollectDrive(parts, pendingFtlDrives, pendingCount, drivePart, drives);
        }
        else
        {
            foreach (var drivePart in partsInCategory)
                CollectDrive(parts, pendingFtlDrives, pendingCount, drivePart, drives);
        }

        if (drives.Count == 0 && pendingCount == 0)
            return float.NaN;

        var totalDensity = 0f;
        var weightedEfficiency = 0f;
        using (var enumerator = parts.GetEnumerator())
        {
            while (enumerator.MoveNext())
            {
                var part = enumerator.Current;
                // IntRect's interface-returning GetEnumerator allocates an
                // iterator per part; visit its cells directly in the same
                // top-to-bottom, left-to-right order.
                var rect = part.PhysicalRect;
                for (var y = rect.Top; y < rect.Bottom; y++)
                {
                    for (var x = rect.Left; x < rect.Right; x++)
                    {
                        var cell = new IntVector2(x, y);
                        var vector = (Vector2)cell + Vector2.Half;
                        var cellEfficiency = 0f;
                        var isDrivePart = false;
                        foreach (var (drivePart, driveRules, center) in drives)
                        {
                            if (drivePart == part)
                                isDrivePart = true;
                            cellEfficiency += driveRules.GetJumpEfficiency(vector.DistanceTo(center));
                        }
                        for (var i = 0; i < pendingCount; i++)
                        {
                            var pending = pendingFtlDrives![i];
                            cellEfficiency +=
                                pending.Rules.GetJumpEfficiency(vector.DistanceTo(pending.Location));
                        }
                        var density = part.Rules.Density;
                        totalDensity += density;
                        cellEfficiency = Mathx.Min(cellEfficiency, 1f);
                        weightedEfficiency += cellEfficiency * density;
                        // Vanilla only invoked the per-cell callback for cells
                        // that do not belong to a drive part.
                        if (!isDrivePart && bounds.Contains(in cell))
                        {
                            var hue = Mathx.Lerp(rules.FtlOverlayHueRange, cellEfficiency);
                            var color = Color.FromHSVA(hue, 1f, 1f, rules.FtlOverlayAlpha);
                            var px = cell - bounds.Location;
                            *(IntColor*)(texPtr + px.Y * texStride + (nint)px.X * (nint)sizeof(IntColor)) =
                                color.ToIntColor();
                        }
                    }
                }
            }
        }
        return weightedEfficiency / totalDensity;
    }

    private static void CollectDrive(
        BlueprintPartsManager parts,
        List<PendingFtlDrive>? pendingFtlDrives,
        int pendingCount,
        BlueprintPart drivePart,
        List<(BlueprintPart Part, FtlDriveRules Rules, Vector2 Center)> drives)
    {
        var overlapsPending = false;
        for (var i = 0; i < pendingCount; i++)
        {
            if (drivePart.Rect.IntersectsWith(pendingFtlDrives![i].PartRect))
            {
                overlapsPending = true;
                break;
            }
        }
        if (overlapsPending)
            return;
        var components = drivePart.Rules.Components;
        for (var i = 0; i < components.Count; i++)
        {
            if (components[i] is FtlDriveRules driveRules)
            {
                drives.Add((drivePart, driveRules,
                    drivePart.TransformPointFromRules(driveRules.GetTotalLocation(drivePart.Rules))));
            }
        }
    }
}
