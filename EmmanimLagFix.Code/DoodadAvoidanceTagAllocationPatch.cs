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
/// ResourceTransferJob.IsValidFor tests avoidance tags for each candidate crew
/// member. Every IAvoidableDoodad.MatchesTags implementation calls
/// HashSet.Overlaps, which enumerates its IEnumerable argument through the
/// interface and boxes a HashSet enumerator on every test. A live MP
/// allocation trace attributed ~300 MiB in 60 seconds to this path, mostly
/// through the SpaceStation/StasisSpaceStation implementations that the
/// original single-target version of this patch did not cover. Keep the
/// receiver's comparer and the original membership semantics, but enumerate
/// the concrete set's struct enumerator. The result is a bool OR-reduce, so
/// enumeration order cannot affect the outcome. No cache or retained game
/// references.
/// </summary>
[HarmonyPatch]
internal static class DoodadAvoidanceTagAllocationPatch
{
    internal static int AppliedCount;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var iface = AccessTools.TypeByName("Cosmoteer.Simulation.Doodads.IAvoidableDoodad")
            ?? throw new TypeLoadException("IAvoidableDoodad was not found.");
        Type[] types;
        try
        {
            types = iface.Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t != null).Cast<Type>().ToArray();
        }
        foreach (var type in types)
        {
            if (type.IsInterface || type.IsAbstract || !iface.IsAssignableFrom(type))
                continue;
            var matchesTags = type
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .SingleOrDefault(m => m.DeclaringType == type
                    && (m.Name == "MatchesTags" || m.Name.EndsWith(".MatchesTags", StringComparison.Ordinal)));
            if (matchesTags == null)
            {
                Halfling.Logging.Logger.Log(
                    $"[EmmanimLagFix] IAvoidableDoodad.MatchesTags not found on {type.FullName}; leaving vanilla behaviour.");
                continue;
            }
            yield return matchesTags;
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var code = instructions.ToList();
        var sites = code.Where(i => i.opcode == OpCodes.Callvirt
            && i.operand is MethodInfo m && m.Name == nameof(HashSet<int>.Overlaps)
            && m.DeclaringType is { IsGenericType: true } t
            && t.GetGenericTypeDefinition() == typeof(HashSet<>)).ToArray();
        if (sites.Length != 1)
        {
            Halfling.Logging.Logger.Log(
                $"[EmmanimLagFix] Avoidance tag comparison changed in {original.DeclaringType?.Name}; leaving vanilla behaviour.");
            return code;
        }

        var overlapsMethod = (MethodInfo)sites[0].operand;
        var replacement = typeof(DoodadAvoidanceTagAllocationPatch)
            .GetMethod(nameof(Overlaps), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(overlapsMethod.DeclaringType!.GetGenericArguments()[0]);
        sites[0].opcode = OpCodes.Call;
        sites[0].operand = replacement;
        Interlocked.Increment(ref AppliedCount);
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
// still sees the original generic caller after patching the interface methods.
// The planet avoider is unwrapped directly to skip the interface dispatch;
// other avoiders call their (patched) MatchesTags. This avoids depending on
// interface devirtualization/inlining behaviour.
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
