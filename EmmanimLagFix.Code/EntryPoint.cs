using HarmonyLib;

namespace EmmanimLagFix.Code;

public static class EntryPoint
{
    public const string HarmonyId = "nayuri.emmanim_lag_fix.code";

    public static void AssemblyLoadInitializer()
    {
        KoreanImeInputPatch.ForceImm32Backend();
        // Before PatchAll, because the patch classes that derive a spin budget
        // and an inline limit from the worker count read it in their static
        // constructors. Idempotent, so whichever runs first still agrees.
        FastParallelPoolSize.Apply();
        var harmony = new Harmony(HarmonyId);
        harmony.PatchAll(typeof(EntryPoint).Assembly);
        // Below Harmony, not through it: these targets' filter regions make
        // them unpatchable by any Harmony patch shape, so they get native
        // detours. Self-contained - swallows its own failures.
        DeserializationInvokeCompilePatch.Apply();
        Halfling.Logging.Logger.Log(
            "[EmmanimLagFix] deserialization constructor detours: "
            + DeserializationInvokeCompilePatch.Applied
            + "/"
            + DeserializationInvokeCompilePatch.Resolved
            + (DeserializationInvokeCompilePatch.FailureReason == null
                ? " applied."
                : " (" + DeserializationInvokeCompilePatch.FailureReason + ")."));
        Halfling.Logging.Logger.Log(
            "[EmmanimLagFix] ThreadedTaskQueue direct delegate invocation: "
            + (ThreadedTaskQueueDynamicInvokePatch.Applied
                ? "on."
                : "UNAVAILABLE."));
        Halfling.Logging.Logger.Log(
            "[EmmanimLagFix] Crew oxygen validity guard and nonwrapping resource sink-job shards initialized (2.2.26); first-pass thruster transform: "
            + (ThrusterFirstPassTransformPatch.Applied ? "on" : "vanilla fallback")
            + "; resource traversal lookup reuse: "
            + (ResourceSearchTraversalPatch.Applied ? "on" : "vanilla fallback")
            + "; resource source visited local context: "
            + (ResourceSourceVisitedSetPatch.Applied ? "on." : "vanilla fallback."));
        Halfling.Logging.Logger.Log(
            "Emmanim Lag Fix code patches initialized (Windows IME backend: "
            + KoreanImeInputPatch.BackendName
            + ", result-string delivery: "
            + (KoreanImeResultStringPatch.Available ? "on" : "UNAVAILABLE")
            + (KoreanImeDiagnostics.Enabled ? ", input diagnostics ON" : "")
            + ").");
    }
}
