using System.Diagnostics;
using System.Reflection;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Statuses;
using Cosmoteer.Ships.Statuses.Subhandlers;
using Halfling.Geometry;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Splits the <c>Statuses</c> fixed-update bucket into a total per status type
/// and, inside that, the two sub-steps that have each been measured dominant
/// before: diffusion and value modulation.
///
/// The bucket itself is already named - it was the peer's largest entry in the
/// 2026-09-18 session at up to 62 ms per frame - but the name stops there.
/// <c>ShipStatusManager.FixedUpdate</c> fans out to one
/// <c>StatusHandler&lt;TLocation&gt;</c> per active status type, and a handler's
/// tick is
///
/// <code>
/// _expiryTracker.Update            // MinQueue peek loop, cheap
/// _ticker?.Update                  // MinQueue peek loop, cheap
/// _ModulateStatusValues            // O(statuses): buffs, resistance, modulate
/// _customShapeEffectHandler?.UpdateIntensity
/// _effectApplicator.UpdateContinuousEffects
/// OnFixedUpdate                    // _diffuser?.PerformDiffusion() on tiles
/// </code>
///
/// so the bucket's cost could sit in heat decay, in fire's tickers and sinks,
/// in diffusion, or in the continuous-effects pass - and each names a different
/// patch. The existing <c>sk=</c>/<c>vf=</c> counters only say how often the
/// modulation loop ran, not what it cost next to everything else.
///
/// <c>stt=[type:ms,...]</c> is the per-type total - a handler runs inside the
/// parallel bucket, so it is attributed by <c>StatusType</c> rather than by
/// call site. <c>df=</c> is the share inside that total spent in
/// <c>StatusDiffuser.PerformDiffusion</c> (sparse or vanilla path, whichever
/// ran) and <c>mod=</c> the share inside the modulation local function, so
/// <c>stt - df - mod</c> is the expiry/ticker/effects remainder. All figures
/// are milliseconds per frame over the report window, matching
/// <see cref="SceneUpdateBreakdown"/>'s <c>fb</c> units so a bucket and its
/// contents can be compared directly.
///
/// Timing only; no simulation behaviour changes. Gated on the same flag as
/// the rest of the phase diagnostics, so it is inert for ordinary players.
/// </summary>
internal static class StatusPhaseDiagnostics
{
    /// <summary>Per-type entries in the report; the sixth tile status type is not worth a character.</summary>
    private const int TopTypes = 5;

    private static readonly Dictionary<StatusType, long> TypeTicks = new();
    private static long _diffusionTicks;
    private static long _modulationTicks;
    private static readonly object Gate = new();

    /// <summary>
    /// True once every target resolved, for the smoke test. Three patch classes
    /// resolve independently; all must find their methods for the split to be
    /// complete.
    /// </summary>
    internal static bool Applied =>
        StatusTypePhasePatch.Resolved
        && StatusDiffusionPhasePatch.Resolved
        && StatusModulationPhasePatch.Resolved;

    internal static void AddType(StatusType type, long ticks)
    {
        lock (Gate)
        {
            TypeTicks.TryGetValue(type, out var prior);
            TypeTicks[type] = prior + ticks;
        }
    }

    internal static void AddDiffusion(long ticks) =>
        Interlocked.Add(ref _diffusionTicks, ticks);

    internal static void AddModulation(long ticks) =>
        Interlocked.Add(ref _modulationTicks, ticks);

    /// <summary>
    /// Formats and resets. Reads the frame count the frame-phase snapshot just
    /// took, so it must be called after that one within the same sample - the
    /// same contract <see cref="SceneUpdateBreakdown.Take"/> already has.
    /// </summary>
    internal static string Take()
    {
        KeyValuePair<StatusType, long>[] types;
        long diffusion;
        long modulation;
        lock (Gate)
        {
            types = TypeTicks.ToArray();
            TypeTicks.Clear();
        }
        diffusion = Interlocked.Exchange(ref _diffusionTicks, 0);
        modulation = Interlocked.Exchange(ref _modulationTicks, 0);

        var frames = FramePhaseDiagnosticsPatch.LastFrames;
        if (!FramePhaseDiagnosticsPatch.Enabled || frames <= 0)
        {
            return "stt=[] df=- mod=-";
        }

        var perFrameMs = 1000d / Stopwatch.Frequency / frames;
        Array.Sort(types, static (a, b) => b.Value.CompareTo(a.Value));
        var rows = new List<string>(TopTypes);
        for (var i = 0; i < types.Length && i < TopTypes; i++)
        {
            var ms = types[i].Value * perFrameMs;
            if (ms < 0.05d && i > 0)
            {
                break;
            }

            // "cosmoteer.heat" -> "heat"; the namespace is always vanilla's or
            // a mod's and the payload budget is short either way.
            var id = types[i].Key.ID.ToString();
            var dot = id.LastIndexOf('.');
            rows.Add($"{id[(dot + 1)..]}:{ms:F1}");
        }

        var list = rows.Count == 0 ? "-" : string.Join(",", rows);
        return $"stt=[{list}] df={diffusion * perFrameMs:F1} mod={modulation * perFrameMs:F1}";
    }
}

/// <summary>
/// Times <c>StatusHandler&lt;TLocation&gt;</c>'s explicit
/// <c>IFixedUpdateableSceneObject.FixedUpdate</c> for the tile and part
/// instantiations. The method is one virtual call per ship per active status
/// type inside a parallel bucket pass, so the timer runs on worker threads and
/// the accumulation is locked.
/// </summary>
[HarmonyPatch]
internal static class StatusTypePhasePatch
{
    internal static bool Resolved { get; private set; }

    private static bool Prepare() => FramePhaseDiagnosticsPatch.Enabled;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var found = 0;
        foreach (var locationType in new[] { typeof(IntVector2), typeof(Part) })
        {
            var handlerType = typeof(StatusHandler<>).MakeGenericType(locationType);
            // The explicit interface implementation carries the interface's
            // full name in metadata; OnFixedUpdate and friends do not match.
            var targets = AccessTools.GetDeclaredMethods(handlerType)
                .Where(method => method.Name.EndsWith(
                    "IFixedUpdateableSceneObject.FixedUpdate", StringComparison.Ordinal))
                .ToArray();
            if (targets.Length != 1)
            {
                Halfling.Logging.Logger.Log(
                    $"[EmmanimLagFix] {handlerType.Name} FixedUpdate impl not uniquely found "
                    + $"({targets.Length}); per-status-type timing is off.");
                continue;
            }

            found++;
            yield return targets[0];
        }

        Resolved = found == 2;
    }

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(object __instance, long __state)
    {
        var type = __instance switch
        {
            StatusHandler<IntVector2> tile => tile.StatusType,
            StatusHandler<Part> part => part.StatusType,
            _ => null,
        };
        if (type != null)
        {
            StatusPhaseDiagnostics.AddType(type, Stopwatch.GetTimestamp() - __state);
        }
    }
}

/// <summary>
/// Times <see cref="StatusDiffuser.PerformDiffusion"/> itself rather than the
/// handler around it, so the figure holds whether the call took the vanilla
/// dense path or the <see cref="SparseHeatDiffusionPatch"/> sparse one - both
/// end inside the same method entry on this build.
/// </summary>
[HarmonyPatch]
internal static class StatusDiffusionPhasePatch
{
    internal static bool Resolved { get; private set; }

    private static bool Prepare() => FramePhaseDiagnosticsPatch.Enabled;

    private static MethodBase? TargetMethod()
    {
        var target = AccessTools.DeclaredMethod(typeof(StatusDiffuser), nameof(StatusDiffuser.PerformDiffusion));
        if (target == null)
        {
            Halfling.Logging.Logger.Log(
                "[EmmanimLagFix] StatusDiffuser.PerformDiffusion was not found; diffusion timing is off.");
            return null;
        }

        Resolved = true;
        return target;
    }

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(long __state) =>
        StatusPhaseDiagnostics.AddDiffusion(Stopwatch.GetTimestamp() - __state);
}

/// <summary>
/// Times the compiler-generated <c>_ModulateStatusValues</c> local function on
/// both <c>StatusHandler</c> instantiations - the same method object the two
/// modulation transpilers already rewrite, found through their resolver so a
/// rename fails loudly in the same place.
/// </summary>
[HarmonyPatch]
internal static class StatusModulationPhasePatch
{
    internal static bool Resolved { get; private set; }

    private static bool Prepare() => FramePhaseDiagnosticsPatch.Enabled;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodBase? tiles = null;
        MethodBase? parts = null;
        try
        {
            tiles = StatusModulationListCapacityPatch.FindTarget(typeof(IntVector2));
            parts = StatusModulationListCapacityPatch.FindTarget(typeof(Part));
        }
        catch (MissingMethodException)
        {
            Halfling.Logging.Logger.Log(
                "[EmmanimLagFix] _ModulateStatusValues was not found; modulation timing is off.");
        }

        Resolved = tiles != null && parts != null;
        if (tiles != null)
        {
            yield return tiles;
        }
        if (parts != null)
        {
            yield return parts;
        }
    }

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(long __state) =>
        StatusPhaseDiagnostics.AddModulation(Stopwatch.GetTimestamp() - __state);
}
