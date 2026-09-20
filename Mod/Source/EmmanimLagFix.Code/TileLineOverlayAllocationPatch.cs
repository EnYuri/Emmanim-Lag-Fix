using System.Reflection;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Blueprints.Graphics;
using Cosmoteer.Ships.Blueprints.Logic.Values;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Halfling.Pooling;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// The build-mode tile-line overlay rebuilds its draw list every time the
/// editor refreshes, which happens on both the draw and input paths while
/// parts are dragged. Vanilla enumerates BlueprintPart.Components through
/// IEnumerable with LINQ OfType, so every part allocates an iterator object
/// plus a boxed List enumerator, and RefreshData allocates a fresh LINQ chain
/// and HashSet for the primary-part membership test. A live allocation trace
/// attributed over 1 GiB per minute to this path on a large ship. Replace
/// RefreshData with the same traversal over the concrete List fields, a pooled
/// set for the membership test, and the struct List enumerator for
/// ship.BlueprintParts. Emitted draw data, ordering, and filtering are
/// identical to vanilla; this is UI-only and touches no simulation state.
/// </summary>
[HarmonyPatch]
internal static class TileLineOverlayAllocationPatch
{
    private static bool Prepare()
    {
        var renderer = typeof(TileLineBlueprintOverlayRenderer);
        var refresh = AccessTools.Method(renderer, nameof(TileLineBlueprintOverlayRenderer.RefreshData));
        var refreshParams = refresh?.GetParameters();
        var ok =
            refreshParams is { Length: 4 }
            && refreshParams[1].ParameterType == typeof(IReadOnlyList<PartOverlayRenderData>)
            && AccessTools.Field(renderer, "_drawData") != null
            && AccessTools.Method(renderer, "AddDrawData",
                    new[] { typeof(Ship), typeof(PartInfo), typeof(PartTileLineScoreValueRules) }) != null
            && AccessTools.Method(renderer, "AddDrawData",
                    new[] { typeof(BlueprintPartTileLineScoreValue) }) != null
            && AccessTools.Field(typeof(BlueprintPart), "_components") != null
            && AccessTools.Field(typeof(PartRules), nameof(PartRules.Components)) != null
            && AccessTools.Field(typeof(PartToggledComponentsRules),
                    nameof(PartToggledComponentsRules.Components)) != null
            && typeof(BlueprintPartsManager).GetMethod("GetEnumerator", Type.EmptyTypes)?.ReturnType
                == typeof(List<BlueprintPart>.Enumerator);
        if (!ok)
        {
            Halfling.Logging.Logger.Log(
                "[EmmanimLagFix] Tile-line overlay internals changed; leaving vanilla behaviour.");
        }
        return ok;
    }

    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(TileLineBlueprintOverlayRenderer),
            nameof(TileLineBlueprintOverlayRenderer.RefreshData))
        ?? throw new MissingMethodException(
            nameof(TileLineBlueprintOverlayRenderer),
            nameof(TileLineBlueprintOverlayRenderer.RefreshData));

    private static bool Prefix(
        TileLineBlueprintOverlayRenderer __instance,
        Ship ship,
        IReadOnlyList<PartOverlayRenderData> parts)
    {
        __instance._drawData.Clear();
        for (var i = 0; i < parts.Count; i++)
        {
            var info = parts[i].Info;
            VisitLineRules(info.Rules.Components, (r: __instance, ship, info),
                static (s, line) => s.r.AddDrawData(s.ship, s.info, line));
        }
        using var primaryParts = TempHashSet<BlueprintPart>.Alloc();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i].Part;
            if (part != null)
                primaryParts.Add(part);
        }
        CollectObstructedSecondaryLines(ship, primaryParts, __instance,
            static (r, line) => r.AddDrawData(line));
        return false;
    }

    // Preorder DFS over Rules.Components recursing into
    // PartToggledComponentsRules, matching PartRules.GetComponentsRecursive().
    internal static void VisitLineRules<TState>(
        List<PartComponentRules> components,
        TState state,
        Action<TState, PartTileLineScoreValueRules> visit)
    {
        for (var i = 0; i < components.Count; i++)
        {
            var component = components[i];
            if (component is PartTileLineScoreValueRules line)
                visit(state, line);
            if (component is PartToggledComponentsRules toggled)
                VisitLineRules(toggled.Components, state, visit);
        }
    }

    // Same order and filter as the vanilla UpdateObstructedSecondaryLines
    // loop: _orderedParts order via the struct enumerator, _components order
    // per part, BlockingPart must be non-null and a primary part.
    internal static void CollectObstructedSecondaryLines<TState>(
        Ship ship,
        HashSet<BlueprintPart> primaryParts,
        TState state,
        Action<TState, BlueprintPartTileLineScoreValue> visit)
    {
        foreach (var blueprintPart in ship.BlueprintParts)
        {
            var components = blueprintPart._components;
            for (var i = 0; i < components.Count; i++)
            {
                if (components[i] is BlueprintPartTileLineScoreValue line
                    && line.BlockingPart != null
                    && primaryParts.Contains(line.BlockingPart))
                {
                    visit(state, line);
                }
            }
        }
    }
}
