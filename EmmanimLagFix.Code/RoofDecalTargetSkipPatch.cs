using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer;
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
/// <c>ShipRenderer</c>. Measured on a 20-second trace of a 9 fps wreck-field
/// frame it held 7,821 ms of the 8,544 ms the main thread spent in D3D11 draw
/// submission, 91.5% of all draw time, against 213 ms in Present - so this was
/// never vsync or present back-pressure.
///
/// <c>ShipRenderer.DrawStage</c> hands the target to the Low, Middle and High
/// stages, and only the High stage tests anything (it declines the target once
/// <c>RoofOpacity</c> has faded to zero). This prefix drops the target for a
/// stage whose layers cannot read it, which skips the whole setup pass.
///
/// <para><b>The consumer is the shader, not <c>IsRoof</c>.</b> 2.1.17 keyed the
/// predicate on <c>ShipRenderLayerRules.IsRoof</c> and that was wrong.
/// <c>IsRoof</c> only gates the <c>RoofOpacity</c> fade constant inside
/// <c>DrawLayer</c>. What <c>SetupRoofRendering</c> actually publishes is four
/// sticky per-ship shader constants - <c>_roofBaseAlpha</c>,
/// <c>_roofBaseTexture</c>, <c>_roofBaseTextureScale</c> and
/// <c>_roofDecalsTarget</c> - and every shader compiled with
/// <c>ENABLE_ROOF_PAINT_COLOR</c> or <c>ENABLE_ROOF_PAINT_COLOR_KEYED</c> reads
/// them through <c>getRoofPaintColor()</c> in <c>base_atlas.shader</c>,
/// <c>IsRoof</c> or not. Two vanilla layers do exactly that:
///
/// <list type="bullet">
/// <item>the asteroid class's single <c>asteroid</c> material layer, which sits
/// in the <b>Low</b> stage with <c>IsRoof</c> unset and renders through
/// <c>roof_colored_lit.shader</c>; and</item>
/// <item>terran <c>external_walls</c>, in the <b>Middle</b> stage, through
/// <c>walls_external_lit.shader</c> / <c>walls_external.shader</c>.</item>
/// </list>
///
/// Skipping those two stages left both sampling whatever ship's roof texture
/// and decal target happened to be bound last, which is what made asteroids
/// render wrong. The <c>roofOpacity &lt;= 0f</c> early-out 2.1.17 also carried
/// was wrong for the same reason: neither of those layers fades with the roof.
///
/// So the predicate asks the shaders themselves.
/// <c>Halfling.Graphics.Shader.DefinesConstant</c> is reflection over the
/// compiled shader, and <c>getRoofPaintColor</c> is reachable only under those
/// two defines, so a shader that does not sample the target does not declare
/// the constant either. This is data-driven and needs no list of layer keys, so
/// a modded ship class or a custom shader is classified correctly without this
/// patch knowing about it.
///
/// What survives is the terran <b>Low</b> stage - floors, turrets and low
/// doodads, all on <c>parts.shader</c> - which is one of the three passes a
/// terran hull and every terran wreck pays for.
/// </summary>
[HarmonyPatch]
internal static class RoofDecalTargetSkipPatch
{
    /// <summary>
    /// Per-stage-list answers. The lists are the per-ship-class
    /// <c>Rules.*RenderLayers</c> instances, so there are a handful of them for
    /// the life of the process, and the answer cannot change: it is a property
    /// of the shader source, which survives a device reset unchanged.
    /// </summary>
    private static readonly ConditionalWeakTable<object, StrongBox<bool>> Answers = new();

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
        ref RenderTarget? roofDecalsTarget)
    {
        if (roofDecalsTarget is null || layers is null)
        {
            return;
        }

        if (!SamplesRoofDecalsTarget(layers))
        {
            roofDecalsTarget = null;
        }
    }

    /// <summary>
    /// True when some layer in this stage is drawn with a shader that declares
    /// the roof decals target, i.e. when <c>SetupRoofRendering</c> has a
    /// consumer. Conservative on anything it cannot read: an unknown shader
    /// reports true and is not cached, so the stage keeps vanilla behaviour.
    /// </summary>
    internal static bool SamplesRoofDecalsTarget(List<ShipRenderLayerRules> layers)
    {
        if (layers is null || layers.Count == 0)
        {
            return false;
        }

        if (Answers.TryGetValue(layers, out var cached))
        {
            return cached.Value;
        }

        var conclusive = true;
        var samples = Evaluate(layers, ref conclusive);

        if (conclusive)
        {
            Answers.AddOrUpdate(layers, new StrongBox<bool>(samples));
        }

        return samples;
    }

    private static bool Evaluate(List<ShipRenderLayerRules> layers, ref bool conclusive)
    {
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            if (layer is null)
            {
                continue;
            }

            if (Samples(layer.Material, ref conclusive)
                || Samples(layer.StencilMaterial, ref conclusive)
                || Samples(layer.DiffuseMaterial, ref conclusive)
                || Samples(layer.NormalsMaterial, ref conclusive)
                || Samples(layer.LightMaterial, ref conclusive)
                || Samples(layer.GhostMaterial, ref conclusive))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A material with no shader of its own inherits whatever the graphics
    /// manager last bound, which this patch cannot reason about, so it counts
    /// as a consumer and suppresses caching rather than being assumed inert.
    /// </summary>
    private static bool Samples(Material? material, ref bool conclusive)
    {
        if (material is null)
        {
            return false;
        }

        var shader = material.Shader;
        if (shader is null)
        {
            conclusive = false;
            return true;
        }

        try
        {
            return shader.DefinesConstant(
                ShaderConstantIDs.RoofDecalsTarget,
                ShaderConstantType.Texture);
        }
        catch (Exception)
        {
            conclusive = false;
            return true;
        }
    }
}
