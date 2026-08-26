using EmmanimLagFix.Code;
using HarmonyLib;
using System.Reflection;

var gameAssembly = Assembly.Load("Cosmoteer");
var displayType = gameAssembly.GetType("Cosmoteer.Game.Gui.PlayerResourcesDisplay", throwOnError: true)!;
var target = AccessTools.Method(displayType, "GetResourceCounts")
    ?? throw new MissingMethodException(displayType.FullName, "GetResourceCounts");

const string smokeId = "nayuri.emmanim_lag_fix.smoke_test";
var harmony = new Harmony(smokeId);
harmony.PatchAll(typeof(EntryPoint).Assembly);

var info = Harmony.GetPatchInfo(target)
    ?? throw new InvalidOperationException("Harmony did not patch GetResourceCounts.");
if (!info.Prefixes.Any(patch => patch.owner == smokeId) ||
    !info.Postfixes.Any(patch => patch.owner == smokeId))
{
    throw new InvalidOperationException("Expected Emmanim prefix and postfix were not installed.");
}

harmony.UnpatchAll(smokeId);
Console.WriteLine("PASS: PlayerResourcesDisplay.GetResourceCounts prefix/postfix resolved on this game build.");
