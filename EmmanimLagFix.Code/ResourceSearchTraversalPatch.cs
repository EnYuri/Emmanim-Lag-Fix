using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Crew.Pathing;
using Cosmoteer.Ships.Resources;
using Cosmoteer.Resources;
using Halfling.Geometry;
using Halfling.Pooling;
using HarmonyLib;

namespace EmmanimLagFix.Code;

// Stop after processing the last possible source cell. Count the game's own
// successful lookups rather than doing a second hash lookup in an iterator.
[HarmonyPatch]
internal static class ResourceSearchTraversalPatch
{
    internal static bool Applied { get; private set; }
    private static readonly MethodInfo SearchCellsTarget = AccessTools.Method(
        typeof(PathManager), nameof(PathManager.SearchCellsFrom),
        new[] { typeof(IntRect), typeof(bool), typeof(int) })!;

    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(ResourceManager), "SearchForSources", new[] { typeof(ResourceManager.SinkInfo) })!;

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var il = instructions.ToList();
        var search = il.FindIndex(x => x.Calls(SearchCellsTarget));
        var lookupTarget = AccessTools.Method(
            typeof(Dictionary<IntVector2, TempList<ResourceManager.SourceInfo>>), "TryGetValue");
        var moveTarget = AccessTools.Method(typeof(IEnumerator), nameof(IEnumerator.MoveNext));
        var lookup = il.FindIndex(Math.Max(search + 1, 0), x => x.Calls(lookupTarget));
        var move = il.FindIndex(Math.Max(search + 1, 0), x => x.Calls(moveTarget));
        // Check the dictionary local and the loop's ordinary conditional branch.
        // On a changed game shape, return the original search without partial edits.
        if (search < 0 || il.Count(x => x.Calls(SearchCellsTarget)) != 1
            || lookup <= search + 3 || move <= lookup || move + 1 >= il.Count
            || il[lookup - 3].opcode != OpCodes.Ldloc_S
            || il[lookup - 3].operand is not LocalVariableInfo dictionary
            || dictionary.LocalType != typeof(Dictionary<IntVector2, TempList<ResourceManager.SourceInfo>>)
            || il[move + 1].opcode != OpCodes.Brtrue
            || il.Skip(search + 1).Take(move - search).Count(x => x.Calls(lookupTarget)) != 1)
        {
            Applied = false;
            return il;
        }
        var remaining = generator.DeclareLocal(typeof(int));
        var output = new List<CodeInstruction>(il.Count + 8);
        for (var i = 0; i < il.Count; i++)
        {
            var instruction = il[i];
            List<CodeInstruction>? replacement = null;
            if (i == search)
                replacement = new()
                {
                    new(OpCodes.Ldloc, dictionary), new(OpCodes.Ldarg_1),
                    new(OpCodes.Ldloca, remaining),
                    new(OpCodes.Call, AccessTools.Method(typeof(ResourceSearchTraversalPatch), nameof(StartSearch)))
                };
            else if (i == lookup)
                replacement = new()
                {
                    new(OpCodes.Ldloca, remaining),
                    new(OpCodes.Call, AccessTools.Method(typeof(ResourceSearchTraversalPatch), nameof(LookupAndCount))
                        .MakeGenericMethod(typeof(TempList<ResourceManager.SourceInfo>)))
                };
            else if (i == move)
                replacement = new()
                {
                    new(OpCodes.Ldloc, remaining),
                    new(OpCodes.Call, AccessTools.Method(typeof(ResourceSearchTraversalPatch), nameof(MoveNext)))
                };
            if (replacement == null) output.Add(instruction);
            else
            {
                replacement[0].labels.AddRange(instruction.labels);
                replacement[0].blocks.AddRange(instruction.blocks);
                output.AddRange(replacement);
            }
        }
        Applied = true;
        return output;
    }

    private static IEnumerable<(IntVector2 Cell, float Dist)> StartSearch(
        PathManager paths, IntRect origin, bool useTraffic, int maxIterations,
        Dictionary<IntVector2, TempList<ResourceManager.SourceInfo>> sourceCells,
        ResourceManager.SinkInfo sink, ref int remaining)
    {
        var vanilla = paths.SearchCellsFrom(origin, useTraffic, maxIterations);
        // Stackable searches may replace the dictionary while narrowing their
        // resource type. Empty dictionaries retain the previous vanilla behavior.
        remaining = sink.SourceType == ResourceRules.Stackable || sourceCells.Count == 0
            ? -1 : sourceCells.Count;
        return vanilla;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LookupAndCount<T>(Dictionary<IntVector2, T> cells,
        IntVector2 cell, out T value, ref int remaining)
    {
        var found = cells.TryGetValue(cell, out value!);
        if (found && remaining > 0) --remaining;
        return found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MoveNext(IEnumerator iterator, int remaining)
        => remaining != 0 && iterator.MoveNext();
}