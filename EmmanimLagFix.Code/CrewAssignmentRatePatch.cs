using System.Globalization;
using System.Runtime.CompilerServices;
using Cosmoteer.Mods;
using Halfling.ObjectText;
using HarmonyLib;

namespace EmmanimLagFix.Code;

// Change the text rules before deserialization and portable hashing, rather
// than changing live crew AI budgets after a session starts.
[HarmonyPatch(typeof(ModInfo), nameof(ModInfo.ApplyPreLoadMods))]
internal static class CrewAssignmentRatePatch
{
    private sealed record Rates(float Normal, float Low);
    private static readonly ConditionalWeakTable<OTFile, Rates> HugeRates = new();

    private static void Postfix(ModInfo __instance, OTFile rulesFile) =>
        Apply(__instance.ID, rulesFile, "<crew/crew.rules>");

    internal static float SelectRate(float vanilla, float huge) =>
        float.IsFinite(huge) ? Math.Max(vanilla, huge * 0.25f) : vanilla;

    // Each rule-file load has its own snapshot. An earlier Huge Crews load must
    // not affect a later load with that mod disabled. Mod IDs put Huge Crews
    // before this mod; capture its actual applied values, not constants.
    internal static void Apply(string modId, OTFile rulesFile, string crewPath)
    {
        if (modId == "cosmoteer.huge_crews")
        {
            var rates = new Rates(Read("JobAssignmentsPerSecond"), Read("LowPriorityJobAssignmentsPerSecond"));
            HugeRates.Remove(rulesFile);
            HugeRates.Add(rulesFile, rates);
        }
        else if (modId == "nayuri.emmanim_lag_fix")
        {
            HugeRates.TryGetValue(rulesFile, out var huge);
            var normal = huge == null ? 120f : SelectRate(120f, huge.Normal);
            var low = huge == null ? 30f : SelectRate(30f, huge.Low);
            Write("JobAssignmentsPerSecond", normal);
            Write("LowPriorityJobAssignmentsPerSecond", low);
            if (Halfling.App.Platform != null)
                Halfling.Logging.Logger.Log($"[EmmanimLagFix] Crew assignment rates: {normal.ToString(CultureInfo.InvariantCulture)}/{low.ToString(CultureInfo.InvariantCulture)} per second; Huge Crews: {huge != null}.");
        }

        float Read(string field) => float.Parse(
            ((OTFieldNode)rulesFile.FindAtPath(crewPath + "/" + field)).Value,
            CultureInfo.InvariantCulture);

        void Write(string field, float value) => OTFieldNode.Replace(
            rulesFile.FindAtPath(crewPath + "/" + field, dereferenceFinalNode: false),
            value.ToString("R", CultureInfo.InvariantCulture));
    }
}
