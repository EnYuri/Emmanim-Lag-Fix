using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Stops the tutorial/lore codex from running its IronPython show-conditions on
/// every single frame.
///
/// <c>CodexHudGui</c> subscribes <c>OnUpdatingUIState</c> to
/// <c>BeforeFrameInput</c>, so it runs once per frame, and walks every codex
/// page:
///
/// <code>
/// foreach (CodexPageRules page in Rules.CodexPages)
/// {
///     page.UpdateState(_game);
///     ...add or remove the page's button...
/// }
/// </code>
///
/// <c>CodexPageRules.UpdateState</c> returns immediately only for a page that is
/// already shown or has no condition. For every other page it builds a fresh
/// script scope and evaluates Python:
///
/// <code>
/// using (App.Scripts.CreateTempScope(ShowCondition ?? TempShowCondition, out scope))
/// {
///     scope.SetVariable("game", game.ScriptAccess);
///     scope.SetVariable("stats", game.Stats);
///     scope.SetVariable("sim", game.Sim.ScriptAccess);
///     if (ShowCondition != null &amp;&amp; ShowCondition.Execute(scope)) { ... }
///     if (TempShowCondition != null) IsTemporarilyShown = TempShowCondition.Execute(scope);
/// }
/// </code>
///
/// Vanilla ships 67 such conditions, and some of them ask the simulation real
/// questions — <c>sim.HasShipWithLabelInSight('abandoned')</c>,
/// <c>sim.StationInSight</c>, <c>game.HasTechUnlocked(...)</c>.
///
/// A fresh scope per page per frame is what makes this expensive, and the cost
/// is not mainly CPU. Each evaluation rebinds through the DLR, so
/// <c>BuiltinFunction.BindToInstance</c>, <c>ScopeStorage.GetMemberNames</c> and
/// <c>DynamicOperations.TryGetMember</c> emit dynamic methods that are garbage
/// as soon as the scope is disposed. They come back on the finalizer thread as
/// <c>DynamicResolver+DestroyScout.Finalize</c>, which frees JIT-compiled code.
///
/// Measured on a 20-second capture of a two-player host session, that finalizer
/// accounted for 2,403.8 ms — 5.7% of all real (spin-excluded) process CPU, and
/// 56.1% of all worker-thread CPU spent inside frames longer than 20 ms. Every
/// sample of it landed inside such a frame; none landed outside one. In the
/// paired allocation trace every stack that created dynamic code ran through
/// IronPython, and the largest ones through <c>CodexPageRules.UpdateState</c>.
/// The codex subsystem allocated roughly 14.6 MiB in ten seconds while the whole
/// process allocated 136 MiB.
///
/// So the conditions are re-evaluated sixty times a second to decide whether to
/// pop up a tutorial hint. Four times a second is indistinguishable to a player
/// and removes about 93% of the churn.
///
/// What the delay can cost, in full: a codex page appears up to 250 ms later,
/// its button is added or removed up to 250 ms later, and a page carrying
/// <c>AutoPause</c> enqueues its pause input up to 250 ms later. That last one is
/// an ordinary queued player input, the same kind the pause button sends, not
/// simulation state — it stays ordered by the lockstep protocol. Nothing here
/// touches ships, crew, resources or the integrity hash.
///
/// The gate is per <c>CodexHudGui</c> instance and held weakly, so it is
/// collected with the GUI.
/// </summary>
[HarmonyPatch]
internal static class CodexConditionThrottlePatch
{
    private const int RefreshesPerSecond = 4;
    private static readonly long RefreshIntervalTicks = Stopwatch.Frequency / RefreshesPerSecond;
    private static readonly ConditionalWeakTable<object, Gate> Gates = new();

    private static readonly Type CodexHudGuiType =
        AccessTools.TypeByName("Cosmoteer.Codex.CodexHudGui")
        ?? throw new TypeLoadException("Cosmoteer.Codex.CodexHudGui was not found.");

    private sealed class Gate
    {
        public long NextRefresh;
    }

    private static MethodBase TargetMethod() =>
        AccessTools.Method(CodexHudGuiType, "OnUpdatingUIState")
        ?? throw new MissingMethodException(CodexHudGuiType.FullName, "OnUpdatingUIState");

    private static bool Prefix(object __instance)
    {
        var gate = Gates.GetOrCreateValue(__instance);
        var now = Stopwatch.GetTimestamp();
        if (now < gate.NextRefresh)
        {
            return false;
        }

        gate.NextRefresh = now + RefreshIntervalTicks;
        return true;
    }
}
