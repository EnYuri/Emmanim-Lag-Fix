using System.Reflection;
using Cosmoteer.Modes;
using Cosmoteer.Ships;
using Halfling;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// <c>SimShipsManager.Remove</c> hands every lost ship to
/// <c>LostShipSaver.OnShipPotentiallyLost(..., asynchronous: true)</c>, which
/// posts the save onto a private <c>ThreadedTaskQueue</c>. That worker then
/// runs <c>Ship.SaveDesign</c> — and, when <c>disposeWhenDone</c> is set,
/// <c>Ship.Dispose</c> — against a ship the simulation is still tearing down
/// on the main thread. <c>Part.WriteTo</c> latches
/// <c>PartFlags.DebugSavingComponents</c> around its component loop with no
/// lock and no try/finally, so a concurrent teardown that reaches
/// <c>Part.RemoveComponents</c> throws "Can't modify components while they are
/// being saved" and, being unhandled, takes the process down.
///
/// Observed 2026-09-07 21:46 and 22:14: both crashes landed in the same second
/// as a file appearing in the Lost Ships folder, the second one straight
/// through <c>SimStasisManager.UpdateStasis</c> -> <c>SimShipsManager.Remove</c>
/// -> <c>PortHandler.DeregisterPort</c> -> <c>PartToggledComponents</c>.
///
/// Keep the feature and keep the work off the teardown itself, but move it from
/// a background worker to the Director's own queue, so it runs on the main
/// thread at a frame boundary. Vanilla already treats a synchronous save as
/// supported — its unhandled-exception handler saves every ship that way — and
/// <c>Ship.SaveDesignToTextureData</c> marshals its render capture to the main
/// thread regardless, so this removes a thread rather than adding a wait.
/// </summary>
[HarmonyPatch]
internal static class LostShipSaveThreadingPatch
{
    internal static bool Applied { get; private set; }

    private static MethodBase TargetMethod()
    {
        var target = AccessTools.Method(
            typeof(LostShipSaver),
            nameof(LostShipSaver.OnShipPotentiallyLost));
        if (target == null)
        {
            throw new MissingMethodException(
                typeof(LostShipSaver).FullName,
                nameof(LostShipSaver.OnShipPotentiallyLost));
        }
        Applied = true;
        return target;
    }

    private static bool Prefix(
        Ship ship,
        SimModeManager mode,
        bool disposeWhenDone,
        bool asynchronous)
    {
        // The synchronous path is already safe: it runs on its caller's thread,
        // which is the thread that owns the ship.
        if (!asynchronous)
        {
            return true;
        }

        // Without a Director there is no main-thread queue to defer onto, so
        // leave vanilla's worker in place rather than dropping the save.
        var director = App.Director;
        if (director == null)
        {
            return true;
        }

        // Post, never execute inline: running the save here would re-enter the
        // stasis sweep that is still unwinding this ship's removal. The
        // re-entrant call passes asynchronous: false, so this prefix lets it
        // through to vanilla.
        director.SynchronizationContext.Post(() =>
            LostShipSaver.OnShipPotentiallyLost(
                ship,
                mode,
                disposeWhenDone,
                asynchronous: false));
        return false;
    }
}
