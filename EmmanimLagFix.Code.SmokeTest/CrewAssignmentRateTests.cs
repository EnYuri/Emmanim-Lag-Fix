using System.Reflection;
using EmmanimLagFix.Code;
using Halfling.ObjectText;
using HarmonyLib;

internal static class CrewAssignmentRateTests
{
    internal static void Run(Assembly gameAssembly, string owner)
    {
        var patch = typeof(EntryPoint).Assembly.GetType("EmmanimLagFix.Code.CrewAssignmentRatePatch", true)!;
        var apply = AccessTools.Method(patch, "Apply");
        var target = AccessTools.Method(gameAssembly.GetType("Cosmoteer.Mods.ModInfo", true)!, "ApplyPreLoadMods");
        if (Harmony.GetPatchInfo(target)?.Postfixes.Any(p => p.owner == owner && p.PatchMethod.DeclaringType == patch) != true)
            throw new InvalidOperationException("Crew rate rule-load hook was not installed.");

        void Apply(string id, OTFile file) => apply.Invoke(null, new object[] { id, file, "Crew" });
        float Read(OTFile file, string field) => float.Parse(((OTFieldNode)file.FindAtPath("Crew/" + field)).Value, System.Globalization.CultureInfo.InvariantCulture);
        OTFile Make(float normal, float low) => new(new StringReader(FormattableString.Invariant(
            $"Rates {{ Normal = {normal}; Low = {low}; }} Crew {{ JobAssignmentsPerSecond = &~/Rates/Normal; LowPriorityJobAssignmentsPerSecond = &~/Rates/Low; ResourceSearchesPerSecond = 120; }}")));
        void Check(OTFile file, float normal, float low)
        {
            if (Read(file, "JobAssignmentsPerSecond") != normal || Read(file, "LowPriorityJobAssignmentsPerSecond") != low
                || Read(file, "ResourceSearchesPerSecond") != 120)
                throw new InvalidOperationException("Wrong crew assignment rates or changed resource search rate.");
        }

        foreach (var (normal, low, expectedNormal, expectedLow) in new[]
        {
            (1000f, 250f, 250f, 62.5f), (100f, 20f, 120f, 30f),
            (480f, 120f, 120f, 30f), (600f, 20f, 150f, 30f),
        })
        {
            var file = Make(normal, low);
            Apply("cosmoteer.huge_crews", file);
            // Manifest baseline may replace these after Huge Crews. The hook
            // must use the earlier snapshot and replace references locally.
            OTFieldNode.Replace(file.FindAtPath("Crew/JobAssignmentsPerSecond", false), "120");
            OTFieldNode.Replace(file.FindAtPath("Crew/LowPriorityJobAssignmentsPerSecond", false), "30");
            Apply("nayuri.emmanim_lag_fix", file);
            Check(file, expectedNormal, expectedLow);
            Apply("nayuri.emmanim_lag_fix", file);
            Check(file, expectedNormal, expectedLow); // No repeated halving.
            if (((OTFieldNode)file.FindAtPath("Rates/Normal")).Value != normal.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Changed Huge Crews' referenced template.");
        }
        var vanilla = Make(1000, 250);
        Apply("some.other_mod", vanilla);
        Check(vanilla, 1000, 250);
        Apply("nayuri.emmanim_lag_fix", vanilla);
        Check(vanilla, 120, 30); // Previous file's Huge Crews state must not leak.
        // Exercise the real patched method and external <crew/crew.rules>
        // navigation, rather than only invoking the in-memory helper.
        var folder = Path.Combine(Path.GetTempPath(), "emmanim-crew-rates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "crew"));
        File.WriteAllText(Path.Combine(folder, "cosmoteer.rules"), "");
        File.WriteAllText(Path.Combine(folder, "crew", "crew.rules"), "JobAssignmentsPerSecond = 1000; LowPriorityJobAssignmentsPerSecond = 250; ResourceSearchesPerSecond = 120;");
        var root = new OTFile(Path.Combine(folder, "cosmoteer.rules"));
        var modType = gameAssembly.GetType("Cosmoteer.Mods.ModInfo", true)!;
        var mod = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(modType);
        AccessTools.Field(modType, "ID").SetValue(mod, "cosmoteer.huge_crews");
        target.Invoke(mod, new object[] { root });
        AccessTools.Field(modType, "ID").SetValue(mod, "nayuri.emmanim_lag_fix");
        target.Invoke(mod, new object[] { root });
        var crewFile = (OTFile)root.FindAtPath("<crew/crew.rules>");
        if (((OTFieldNode)crewFile.FindAtPath("JobAssignmentsPerSecond")).Value != "250"
            || ((OTFieldNode)crewFile.FindAtPath("LowPriorityJobAssignmentsPerSecond")).Value != "62.5")
            throw new InvalidOperationException("Real mod-load hook did not set Huge Crews rates.");
        foreach (var file in new[] { Path.Combine(folder, "crew", "crew.rules"), Path.Combine(folder, "cosmoteer.rules") }) File.Delete(file);
        Directory.Delete(Path.Combine(folder, "crew"));
        Directory.Delete(folder);
        Console.WriteLine("PASS: crew assignment rates with/without Huge Crews, vanilla floors, repeat loads and resource-search preservation.");
    }
}
