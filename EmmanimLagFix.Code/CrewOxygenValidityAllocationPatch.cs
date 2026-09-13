using System.Reflection;
using Cosmoteer.Crew;
using Cosmoteer.Ships.Crew.Jobs;
using Cosmoteer.Ships.Parts.Crew;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Vanilla allocates the captured crew context before checking whether the
/// crew is even in space. Leave the airlock search entirely vanilla, but answer
/// its common early returns before entering that allocating method.
/// </summary>
[HarmonyPatch]
internal static class CrewOxygenValidityAllocationPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(typeof(RefillO2Job), nameof(RefillO2Job.IsValidFor), [typeof(CrewSoul)])
        ?? throw new MissingMethodException(typeof(RefillO2Job).FullName, nameof(RefillO2Job.IsValidFor));

    private static bool Prefix(CrewSoul crew, ref bool __result)
    {
        var spaceCrew = crew.SpaceCrew;
        // Keep the original <= test, including its behaviour for NaN.
        if (spaceCrew is null || !(spaceCrew.Oxygen <= crew.Rules.OxygenWarningLevel))
        {
            __result = false;
            return false;
        }

        var homeShip = crew.HomeShip;
        if (homeShip is not null && Airlock.HasOnShip(homeShip))
        {
            __result = true;
            return false;
        }

        // The spatial query, mode-specific permission predicate, pooled-list
        // lifetime, and candidate order are all owned by the original body.
        return true;
    }
}
