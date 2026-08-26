using EmmanimLagFix.Code;
using HarmonyLib;
using System.Reflection;

var gameAssembly = Assembly.Load("Cosmoteer");
var targets = new[]
{
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.PlayerResourcesDisplay", throwOnError: true)!, Method: "GetResourceCounts"),
    (Type: gameAssembly.GetType("Cosmoteer.Game.Gui.Resources.CrewAndResourceTransferWindow", throwOnError: true)!, Method: "OnUpdatingUIState"),
    (Type: gameAssembly.GetType("Cosmoteer.Modes.Career.Comms.CommTradeTab", throwOnError: true)!, Method: "OnUpdatingUIState")
};

const string smokeId = "nayuri.emmanim_lag_fix.smoke_test";
var harmony = new Harmony(smokeId);
harmony.PatchAll(typeof(EntryPoint).Assembly);

foreach (var targetInfo in targets)
{
    var target = AccessTools.Method(targetInfo.Type, targetInfo.Method)
        ?? throw new MissingMethodException(targetInfo.Type.FullName, targetInfo.Method);
    var info = Harmony.GetPatchInfo(target)
        ?? throw new InvalidOperationException($"Harmony did not patch {targetInfo.Type.FullName}.{targetInfo.Method}.");
    if (!info.Prefixes.Any(patch => patch.owner == smokeId))
    {
        throw new InvalidOperationException($"Expected Emmanim prefix was not installed on {targetInfo.Type.FullName}.{targetInfo.Method}.");
    }
}

harmony.UnpatchAll(smokeId);
Console.WriteLine("PASS: resource display and both transfer UI patches resolved on this game build.");
