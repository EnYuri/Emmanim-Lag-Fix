using HarmonyLib;

namespace EmmanimLagFix.Code;

public static class EntryPoint
{
    public const string HarmonyId = "nayuri.emmanim_lag_fix.code";

    public static void AssemblyLoadInitializer()
    {
        var harmony = new Harmony(HarmonyId);
        harmony.PatchAll(typeof(EntryPoint).Assembly);
        Halfling.Logging.Logger.Log("Emmanim Lag Fix code patches initialized.");
    }
}
