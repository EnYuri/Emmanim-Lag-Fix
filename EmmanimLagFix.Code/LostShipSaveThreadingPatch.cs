using System.Reflection;
using Cosmoteer;
using Cosmoteer.Modes;
using Cosmoteer.Ships;
using Halfling;
using Halfling.Logging;
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
    /// <summary>Saves posted to the main-thread queue and not yet run.</summary>
    internal static int Pending;

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
        // which is the thread that owns the ship. Vanilla uses it from the
        // unhandled-exception handler, where no queue will ever be pumped.
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

        // Evaluate vanilla's guard here, on the thread that owns the ship and at
        // the moment vanilla evaluates it. Deferring the guard itself would read
        // mode.Game after the mode may have been torn down, silently turning a
        // ship that should be saved into one that is not.
        bool save;
        try
        {
            save = ship.Metadata.HasUnsavedChanges
                && Settings.SaveLostShips
                && mode.ShouldSaveLostShip(ship);
        }
        catch (Exception ex)
        {
            Logger.LogError("Emmanim Lag Fix: lost-ship guard threw, deferring to vanilla:");
            Logger.LogError(ex.ToString());
            return true;
        }

        if (!save)
        {
            if (disposeWhenDone)
            {
                ship.Dispose();
            }
            return false;
        }

        // Post, never execute inline: running the save here would re-enter the
        // stasis sweep that is still unwinding this ship's removal. The queue is
        // pumped at the top of the frame, before UpdateStates and before the
        // input/update/draw phases, so the save lands outside the simulation.
        Interlocked.Increment(ref Pending);
        director.SynchronizationContext.Post(() =>
        {
            try
            {
                LostShipSaver.SaveLostShip(ship, disposeWhenDone);
            }
            catch (Exception ex)
            {
                // PostInfo.Call() does not catch, so an escape here would be an
                // unhandled main-thread exception - the failure this patch exists
                // to remove.
                Logger.LogError("Emmanim Lag Fix: deferred lost-ship save failed:");
                Logger.LogError(ex.ToString());
            }
            finally
            {
                Interlocked.Decrement(ref Pending);
            }
        });
        return false;
    }
}

/// <summary>
/// GameApp.OnExiting drains lost-ship saves by spinning on
/// <c>LostShipSaver.IsReadyToExit</c> while pumping the Director's queue. That
/// property only reports vanilla's <c>ThreadedTaskQueue</c>, which
/// <see cref="LostShipSaveThreadingPatch"/> no longer uses, so without this the
/// loop is skipped entirely and a ship lost in the final frames is never
/// written. Kept in its own class: combining a class-level TargetMethod with a
/// method-level [HarmonyPatch] would let Harmony bind both patches to the same
/// target.
/// </summary>
[HarmonyPatch(typeof(LostShipSaver), nameof(LostShipSaver.IsReadyToExit), MethodType.Getter)]
internal static class LostShipSaveExitDrainPatch
{
    private static void Postfix(ref bool __result)
    {
        __result = __result && Volatile.Read(ref LostShipSaveThreadingPatch.Pending) == 0;
    }
}
