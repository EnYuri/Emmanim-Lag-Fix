using System.Globalization;
using System.Reflection;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Runs scene update bucket 7 on the worker pool, using vanilla's own
/// <c>SimRoot.ParallelUpdate</c> handler rather than a reimplementation.
///
/// <c>SimRoot</c>'s constructor marks exactly four update buckets parallel -
/// <c>EffectAnchor</c> (8), <c>AudioEffects</c> (10), <c>ParticleUpdate</c> (15)
/// and <c>ShipQuadEffects</c> (12). Bucket 7 is not among them, and on a
/// 2026-09-14 single-player save it was the largest item on the update side of
/// the sim at 4.6-6.7 ms per rendered frame, all of it on the main thread.
///
/// <b>Bucket 7 is not what its name says.</b> <c>UpdateBuckets.Oscillators</c>
/// is 7, so vanilla's <c>GetBucketName</c> and this mod's breakdown both label
/// it "Oscillators" - but <c>Oscillator</c> registers there only when its rules
/// set <c>Interpolate</c>, and no part in vanilla, the workshop set or the local
/// mods does. Its sole registrant is
/// <c>PartSmoothedValue.SmoothedValueManager</c>, one instance per ship, whose
/// <c>Update</c> walks that ship's <c>_nonDeterministicValues</c>. In practice
/// those are the radiator and heat-exchanger families - the only components
/// anywhere that set <c>Deterministic = false</c>, the field defaulting to true.
///
/// Three things make this safe to spread:
///
/// <list type="bullet">
/// <item>It cannot desync. Bucket 7 is by construction the non-deterministic
/// half of <c>PartSmoothedValue</c>; the deterministic half runs in fixed bucket
/// -26 and is not touched here. Vanilla's own rules files warn against wiring
/// game logic to an interpolated value for the same reason.</item>
/// <item>The unit of work is one ship. Managers are per-ship
/// (<c>ConditionalWeakTable&lt;Ship, SmoothedValueManager&gt;</c>), which is the
/// same independence the four buckets above already rely on.</item>
/// <item>Nothing downstream writes shared state. <c>PartNetworkValue</c> is a
/// pass-through - <c>Value</c> forwards to its source and its handler only
/// re-raises <c>ValueChanged</c> - so a smoothed value is pulled by receivers
/// lazily, never pushed into a subnetwork from here.</item>
/// </list>
///
/// The handler is vanilla's, so batch sizing, the single-item branch,
/// <c>IsDoingParallelUpdate</c> and the profiler's naming all stay vanilla's
/// too. A <c>smoothed-value-serial.txt</c> file beside the mod folder restores
/// the main-thread walk without a rebuild, for the case where a subscriber in
/// some future part turns out not to tolerate a worker thread.
/// </summary>
[HarmonyPatch]
internal static class SmoothedValueBucketParallelPatch
{
    /// <summary>The bucket <c>PartSmoothedValue.SmoothedValueManager</c> uses.</summary>
    private const int SmoothedValueBucket = 7;

    internal static string? FailureReason { get; private set; }

    /// <summary>Roots this patch applied to, for the diagnostics line.</summary>
    internal static int AppliedRoots;

    private static readonly bool Disabled = File.Exists(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
        "..",
        "smoothed-value-serial.txt")));

    /// <summary>
    /// SimRoot is internal to Cosmoteer, so it is reached by name exactly as the
    /// sim breakdown patch reaches it. <c>SetCustomUpdateBucketHandler</c> is
    /// inherited from <c>SceneRoot</c>, hence the non-declared lookup, and the
    /// handler delegate type is read off its own parameter rather than named, so
    /// a delegate rename fails here instead of binding something else.
    /// </summary>
    internal static Type? SimRootType => AccessTools.TypeByName("Cosmoteer.Simulation.SimRoot");

    internal static MethodInfo? ParallelUpdate =>
        SimRootType == null ? null : AccessTools.DeclaredMethod(SimRootType, "ParallelUpdate");

    internal static MethodInfo? Setter =>
        SimRootType == null ? null : AccessTools.Method(SimRootType, "SetCustomUpdateBucketHandler");

    private static bool Prepare()
    {
        if (Disabled)
        {
            FailureReason = "the smoothed-value-serial.txt override keeps bucket 7 on the main thread";
            return false;
        }

        if (SimRootType == null)
        {
            FailureReason = "Cosmoteer.Simulation.SimRoot was not found";
            return false;
        }

        if (ParallelUpdate == null)
        {
            FailureReason = "SimRoot.ParallelUpdate was not found";
            return false;
        }

        if (Setter == null)
        {
            FailureReason = "SceneRoot.SetCustomUpdateBucketHandler was not found";
            return false;
        }

        if (AccessTools.DeclaredMethod(SimRootType, "StartInit") == null)
        {
            FailureReason = "SimRoot.StartInit was not found";
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>StartInit</c>, not a constructor. SimRoot has two - the ordinary one
    /// and a <c>GenericConstructor</c> that rebuilds a saved sim - and both route
    /// through <c>StartInit</c>, which is also where vanilla's own four
    /// <c>SetCustomUpdateBucketHandler</c> calls live. Patching the constructors
    /// instead would need both bound and would miss any future third.
    /// </summary>
    internal static MethodBase StartInit =>
        AccessTools.DeclaredMethod(SimRootType!, "StartInit")
        ?? throw new MissingMethodException(SimRootType!.FullName, "StartInit");

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return StartInit;
    }

    private static void Postfix(object __instance)
    {
        try
        {
            var handler = Delegate.CreateDelegate(
                Setter!.GetParameters()[1].ParameterType, __instance, ParallelUpdate!);
            Setter.Invoke(__instance, new object?[] { SmoothedValueBucket, handler });
            AppliedRoots++;
        }
        catch (Exception exception)
        {
            // A scene that failed to take the handler simply keeps vanilla's
            // serial walk, which is slower and never wrong.
            FailureReason = exception.Message;
            Logger.Log(
                "[EmmanimLagFix] Update bucket "
                + SmoothedValueBucket.ToString(CultureInfo.InvariantCulture)
                + " stayed on the main thread: " + exception.Message);
        }
    }
}
