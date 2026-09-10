using System.Collections.Generic;
using System.Reflection;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Rendering;
using Halfling.Graphics;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// <c>ShipRenderer.SetupRoofRendering</c> switches to the full-viewport
/// <c>RoofDecalsTarget</c>, clears it, redraws every roof decal quad and
/// switches back. It runs once per visible ship per render stage, so its cost
/// scales with the number of ships in the camera view - a wreck field costs the
/// same as a live fleet, because a wreck is still a ship with a
/// <c>ShipRenderer</c>.
///
/// Two of the three passes are unconditional and consume nothing.
/// <c>ShipRenderer.DrawStage</c> hands the target to the Low and Middle stages
/// with no test at all, while the High stage already declines it when
/// <c>RoofOpacity</c> has faded to zero:
///
/// <code>
/// case ShipRenderStage.Low:    DrawStage(Rules.LowRenderLayers,  ..., Sim.Rendering.RoofDecalsTarget, ...);
/// case ShipRenderStage.Middle: DrawStage(Rules.MiddleRenderLayers, ..., Sim.Rendering.RoofDecalsTarget, ...);
/// case ShipRenderStage.High:   DrawStage(Rules.HighRenderLayers, ..., (RoofOpacity > 0f) ? Sim.Rendering.RoofDecalsTarget : null, ...);
/// </code>
///
/// Only a layer with <c>IsRoof</c> reads that target - <c>DrawLayer</c> is the
/// sole consumer, and it is also the only place the <c>RoofOpacity</c> shader
/// constant takes the fade value. In vanilla <c>terran.rules</c> the three
/// <c>IsRoof</c> layers (<c>roofs</c>, <c>roof_doodads</c>,
/// <c>roof_turrets</c>) are all in the High stage; Low carries floors, turrets
/// and low doodads, Middle carries wall, stencil and door layers. So the Low
/// and Middle passes clear a 1920x1080 target twice per ship per frame for
/// layers that never sample it.
///
/// That the High stage already skips the setup wholesale at zero opacity is the
/// proof that the sticky shader constants it leaves behind are not required by
/// the non-roof layers: vanilla renders weapons, high doodads, additive lights,
/// fire and construction without them on every faded-roof frame.
///
/// The predicate is read from <c>IsRoof</c> rather than from the stage enum, so
/// a mod that files a roof layer under a different stage keeps working. Nothing
/// about the rendered result changes; the skipped work has no consumer.
///
/// Measured on a 20-second trace of a 9 fps wreck-field frame:
/// <c>SetupRoofRendering</c> held 7,821 ms of the 8,544 ms the main thread
/// spent in D3D11 draw submission, 91.5% of all draw time, against 213 ms in
/// Present - so this was not vsync or present back-pressure.
/// </summary>
[HarmonyPatch]
internal static class RoofDecalTargetSkipPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(
            typeof(ShipRenderer),
            nameof(ShipRenderer.DrawStage),
            new[]
            {
                typeof(List<ShipRenderLayerRules>),
                typeof(RenderTarget),
                typeof(RenderTarget),
                typeof(RenderTarget),
                typeof(RenderTarget),
                typeof(RenderTarget),
                typeof(float),
                typeof(bool),
                typeof(Color?),
            })
        ?? throw new MissingMethodException(
            typeof(ShipRenderer).FullName,
            "DrawStage(List<ShipRenderLayerRules>, RenderTarget x5, float, bool, Color?)");

    private static void Prefix(
        List<ShipRenderLayerRules> layers,
        ref RenderTarget? roofDecalsTarget,
        float roofOpacity)
    {
        if (roofDecalsTarget is null)
        {
            return;
        }

        if (roofOpacity <= 0f || !HasRoofLayer(layers))
        {
            roofDecalsTarget = null;
        }
    }

    /// <summary>
    /// True when some layer in this stage actually samples the roof decals
    /// target. The lists are the static per-stage rule lists, but they are
    /// short enough that walking one costs less than a keyed cache lookup.
    /// </summary>
    internal static bool HasRoofLayer(List<ShipRenderLayerRules> layers)
    {
        for (var index = 0; index < layers.Count; index++)
        {
            if (layers[index].IsRoof)
            {
                return true;
            }
        }

        return false;
    }
}
