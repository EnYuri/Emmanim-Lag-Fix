using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Simulation.Doodads;
using HarmonyLib;
using Cosmoteer.Data;
using Cosmoteer.Generators.Simulation;
using Cosmoteer.Ships.Crew.Jobs;
using Halfling.Geometry;

namespace EmmanimLagFix.Code;

/// <summary>
/// ResourceTransferJob.IsValidFor tests planetary avoidance tags for each
/// candidate crew member. HashSet.Overlaps enumerates its IEnumerable argument
/// through an interface, boxing a HashSet enumerator on every test. A live SP
/// allocation trace attributed 414 MiB in 15 seconds to this path. Keep the
/// receiver's comparer and the original iteration/early-return order, but use
/// the concrete set's struct enumerator. No cache or retained game references.
/// </summary>
[HarmonyPatch]
internal static class DoodadAvoidanceTagAllocationPatch
{
    internal static bool Applied;

    private static MethodBase TargetMethod()
    {
        var type = typeof(PlanetDoodad).GetNestedType("DamageAvoider", BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new TypeLoadException("PlanetDoodad.DamageAvoider was not found.");
        return type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(m => m.Name.EndsWith(".MatchesTags", StringComparison.Ordinal));
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var sites = code.Where(i => i.opcode == OpCodes.Callvirt
            && i.operand is MethodInfo m && m.Name == nameof(HashSet<int>.Overlaps)
            && m.DeclaringType is { IsGenericType: true } t
            && t.GetGenericTypeDefinition() == typeof(HashSet<>)).ToArray();
        if (sites.Length != 1)
        {
            Halfling.Logging.Logger.Log("[EmmanimLagFix] Planet avoidance tag comparison changed; leaving vanilla behaviour.");
            return code;
        }

        var original = (MethodInfo)sites[0].operand;
        var replacement = typeof(DoodadAvoidanceTagAllocationPatch)
            .GetMethod(nameof(Overlaps), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(original.DeclaringType!.GetGenericArguments()[0]);
        sites[0].opcode = OpCodes.Call;
        sites[0].operand = replacement;
        Applied = true;
        return code;
    }

    internal static bool Overlaps<T>(HashSet<T> receiver, IEnumerable<T> other)
    {
        if (other is not HashSet<T> concrete)
            return receiver.Overlaps(other);
        if (receiver.Count == 0)
            return false;
        foreach (var value in concrete)
        {
            if (receiver.Contains(value))
                return true;
        }
        return false;
    }
}

// Route the actual job call sites through the helper as well: the live trace
// still sees the original generic caller after patching the interface method.
// This avoids depending on interface devirtualization/inlining behaviour.
[HarmonyPatch]
internal static class ResourceTransferAvoidanceAllocationPatch
{
    internal static int ReplacedSites;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ResourceTransferJob), nameof(ResourceTransferJob.IsValidFor));
        yield return AccessTools.PropertyGetter(typeof(ResourceTransferJob), nameof(ResourceTransferJob.DesiredCrew));
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && instruction.operand is MethodInfo m && m.DeclaringType == typeof(SimDoodadsManager)
                && m.Name == nameof(SimDoodadsManager.ShouldAvoidLocation)
                && m.IsGenericMethod && m.GetParameters().Length == 3)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(ResourceTransferAvoidanceAllocationPatch), nameof(ShouldAvoidLocation))
                    .MakeGenericMethod(m.GetGenericArguments());
                ReplacedSites++;
            }
            yield return instruction;
        }
    }

    internal static bool ShouldAvoidLocation<TShape>(SimDoodadsManager manager, TShape loc,
        HashSet<ID<SimObjectSpawner>>? tags, float buffer) where TShape : IShapeMethods<Circle>
    {
        if (tags == null) return false;
        for (var i = 0; i < manager._avoidableDoodads.Count; i++)
        {
            var avoider = manager._avoidableDoodads[i];
            bool matches;
            if (avoider is PlanetDoodad.DamageAvoider planet)
            {
                var planetTags = planet._doodad.Tags;
                matches = planetTags != null && DoodadAvoidanceTagAllocationPatch.Overlaps(tags, planetTags);
            }
            else matches = avoider.MatchesTags(tags);
            if (matches)
            {
                var circle = avoider.WorldCircle.Inflate(buffer);
                if (loc.IntersectsWith(in circle)) return true;
            }
        }
        return false;
    }
}
