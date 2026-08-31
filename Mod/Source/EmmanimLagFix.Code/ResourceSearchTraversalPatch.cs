using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships.Crew.Pathing;
using Cosmoteer.Ships.Resources;
using Cosmoteer.Resources;
using Halfling.Geometry;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// ResourceManager normally keeps enumerating crew-path cells after it has
/// already visited every tile that can contain the requested resource. Stop
/// only at that exact point. Every possible source is still yielded in the
/// same order and all per-sink validity, priority, capacity and reachability
/// checks remain vanilla.
/// </summary>
[HarmonyPatch]
internal static class ResourceSearchTraversalPatch
{
    private static readonly MethodInfo SearchCellsTarget = AccessTools.Method(
        typeof(PathManager),
        nameof(PathManager.SearchCellsFrom),
        new[] { typeof(IntRect), typeof(bool), typeof(int) })
        ?? throw new MissingMethodException(typeof(PathManager).FullName, nameof(PathManager.SearchCellsFrom));

    private static MethodBase TargetMethod()
    {
        var sinkInfoType = AccessTools.Inner(typeof(ResourceManager), "SinkInfo")
            ?? throw new TypeLoadException("ResourceManager.SinkInfo was not found.");
        return AccessTools.Method(typeof(ResourceManager), "SearchForSources", new[] { sinkInfoType })
            ?? throw new MissingMethodException(typeof(ResourceManager).FullName, "SearchForSources(SinkInfo)");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var replacement = AccessTools.Method(typeof(ResourceSearchTraversalPatch), nameof(SearchCellsThroughLastSource));
        var replaced = 0;

        foreach (var instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && Equals(instruction.operand, SearchCellsTarget))
            {
                // The original four arguments are already on the evaluation
                // stack. Append this ResourceManager and the current SinkInfo.
                var managerLoad = new CodeInstruction(OpCodes.Ldarg_0);
                managerLoad.labels.AddRange(instruction.labels);
                managerLoad.blocks.AddRange(instruction.blocks);
                yield return managerLoad;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Call, replacement);
                replaced++;
            }
            else
            {
                yield return instruction;
            }
        }

        if (replaced != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one PathManager.SearchCellsFrom call in " +
                $"ResourceManager.SearchForSources(SinkInfo), found {replaced}. " +
                "The game code shape has changed; skipping the traversal patch.");
        }
    }

    private static IEnumerable<(IntVector2 Cell, float Dist)> SearchCellsThroughLastSource(
        PathManager paths,
        IntRect searchOrigin,
        bool useTraffic,
        int maxSearchIterations,
        ResourceManager manager,
        ResourceManager.SinkInfo sink)
    {
        var vanilla = paths.SearchCellsFrom(searchOrigin, useTraffic, maxSearchIterations);

        // A Stackable search can narrow to a concrete resource while it is
        // being enumerated, so retain vanilla traversal for that special case.
        if (sink.SourceType == ResourceRules.Stackable
            || !manager._tileSources.TryGetValue(sink.SourceType, out var sourceCells)
            || sourceCells.Count == 0)
        {
            return vanilla;
        }

        return EnumerateThroughLastSourceCell(vanilla, sourceCells);
    }

    private static IEnumerable<(IntVector2 Cell, float Dist)> EnumerateThroughLastSourceCell(
        IEnumerable<(IntVector2 Cell, float Dist)> vanilla,
        IReadOnlyDictionary<IntVector2, Halfling.Pooling.TempList<ResourceManager.SourceInfo>> sourceCells)
    {
        // FindAllShortestPaths yields each cell once. The resource dictionaries
        // are read-only throughout ResourceManager's parallel search phase.
        var remainingSourceCells = sourceCells.Count;
        foreach (var cellAndDistance in vanilla)
        {
            yield return cellAndDistance;

            if (sourceCells.ContainsKey(cellAndDistance.Cell)
                && --remainingSourceCells == 0)
            {
                yield break;
            }
        }
    }
}
