using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Splits <c>SimRoot.Update</c> into the deterministic world tick and the
/// per-frame visual pass, and attributes each to the scene bucket that spent
/// the time.
///
/// This exists because <c>sim=</c> alone is ambiguous, and that ambiguity has
/// already produced one withdrawn conclusion. <c>SimRoot.Update</c> is
///
/// <code>
/// FastParallel.Start();
/// ExecuteQueued(deterministic: false);
/// base.Update();              // SceneRoot.Update
/// ExecuteQueued(deterministic: false);
/// Mode.Update();
/// FastParallel.Stop();
/// </code>
///
/// and <c>SceneRoot.Update</c> in turn is <c>DoFixedUpdates()</c> - the world
/// ticks, which run zero or more times depending on accumulated time - followed
/// by one <c>UpdateForBucket</c> pass per bucket, which runs exactly once per
/// call and carries interpolation and visuals. So <c>sim=</c> is a sum over two
/// populations with different scaling laws, and reading it as the cost of a
/// world tick is wrong. <c>fixed=</c> below is the half that really is per tick.
///
/// The measurement that motivated this: in the 2026-09-11 session the remote
/// client reported <c>ph=3.0/245.2/29.9@4</c> with <c>sim=243.7/2.00</c> and
/// <c>mode=0.0/2.00</c>. So 243.7 ms of a 281 ms frame was inside this method
/// and only 29.9 ms was drawing - while the process used 1.6 of 8 logical
/// cores. A single-threaded critical path, not a saturated machine. Which
/// bucket owns it was not recorded, which is what this patch adds.
///
/// Timing only; no simulation behaviour changes. Gated on the same flag as the
/// rest of the frame diagnostics, so it is inert for ordinary players.
/// </summary>
internal static class SceneUpdateBreakdown
{
    /// <summary>Buckets listed in the host line. The peer line gets one each.</summary>
    private const int TopBuckets = 5;

    private static long _fixedTicks;
    private static long _fixedCalls;
    private static readonly Dictionary<int, long> FixedBucketTicks = new();
    private static readonly Dictionary<int, long> UpdateBucketTicks = new();
    private static readonly object Gate = new();

    private static Dictionary<int, string>? _fixedNames;
    private static Dictionary<int, string>? _updateNames;

    /// <summary>
    /// True once every target resolved, for the smoke test. Harmony builds the
    /// two patch classes independently and in no defined order, so this is a
    /// property over both rather than a flag either one writes.
    /// </summary>
    internal static bool Applied =>
        SceneFixedUpdatePhasePatch.Resolved && SceneBucketPhasePatch.Resolved;

    internal static void AddFixedUpdates(long ticks)
    {
        lock (Gate)
        {
            _fixedTicks += ticks;
            _fixedCalls++;
        }
    }

    /// <summary>
    /// Bucket passes nest inside <c>DoFixedUpdates</c> for the fixed side, so
    /// the fixed bucket figures are a breakdown of <c>fixed=</c> and not an
    /// addition to it. The update-side buckets are outside it and do add.
    /// </summary>
    internal static void AddBucket(bool fixedUpdate, int bucket, long ticks)
    {
        var map = fixedUpdate ? FixedBucketTicks : UpdateBucketTicks;
        lock (Gate)
        {
            map.TryGetValue(bucket, out var prior);
            map[bucket] = prior + ticks;
        }
    }

    internal readonly struct Report
    {
        public readonly string Full;
        public readonly string Compact;

        public Report(string full, string compact)
        {
            Full = full;
            Compact = compact;
        }
    }

    /// <summary>
    /// Formats and resets. Reads the frame count the frame-phase snapshot just
    /// took, so it must be called after that one within the same sample.
    /// </summary>
    internal static Report Take()
    {
        long fixedTicks;
        long fixedCalls;
        KeyValuePair<int, long>[] fixedBuckets;
        KeyValuePair<int, long>[] updateBuckets;

        lock (Gate)
        {
            fixedTicks = _fixedTicks;
            fixedCalls = _fixedCalls;
            _fixedTicks = 0;
            _fixedCalls = 0;
            fixedBuckets = FixedBucketTicks.ToArray();
            updateBuckets = UpdateBucketTicks.ToArray();
            FixedBucketTicks.Clear();
            UpdateBucketTicks.Clear();
        }

        var frames = FramePhaseDiagnosticsPatch.LastFrames;
        if (!FramePhaseDiagnosticsPatch.Enabled || frames <= 0)
        {
            return new Report("fixed=-/- fb=[] ub=[]", "fx=-");
        }

        var perFrameMs = 1000d / Stopwatch.Frequency / frames;
        var fixedPart = $"fixed={fixedTicks * perFrameMs:F1}/{(double)fixedCalls / frames:F2}";

        return new Report(
            $"{fixedPart} fb=[{Format(fixedBuckets, FixedNames(), perFrameMs, TopBuckets, 0)}] "
                + $"ub=[{Format(updateBuckets, UpdateNames(), perFrameMs, TopBuckets, 0)}]",
            $"fx={fixedTicks * perFrameMs:F1} "
                + $"fb={Format(fixedBuckets, FixedNames(), perFrameMs, 1, 6)} "
                + $"ub={Format(updateBuckets, UpdateNames(), perFrameMs, 1, 6)}");
    }

    private static string Format(
        KeyValuePair<int, long>[] buckets,
        IReadOnlyDictionary<int, string> names,
        double perFrameMs,
        int take,
        int nameLimit)
    {
        if (buckets.Length == 0)
        {
            return "-";
        }

        Array.Sort(buckets, static (a, b) => b.Value.CompareTo(a.Value));
        var rows = new List<string>(take);
        for (var i = 0; i < buckets.Length && i < take; i++)
        {
            var ms = buckets[i].Value * perFrameMs;
            if (ms < 0.05d && i > 0)
            {
                break;
            }

            var name = names.TryGetValue(buckets[i].Key, out var found)
                ? found
                : buckets[i].Key.ToString();
            if (nameLimit > 0 && name.Length > nameLimit)
            {
                name = name[..nameLimit];
            }

            rows.Add($"{name}:{ms:F1}");
        }

        return rows.Count == 0 ? "-" : string.Join(",", rows);
    }

    private static IReadOnlyDictionary<int, string> FixedNames() =>
        _fixedNames ??= BuildNames("Cosmoteer.FixedUpdateBuckets");

    private static IReadOnlyDictionary<int, string> UpdateNames() =>
        _updateNames ??= BuildNames("Cosmoteer.UpdateBuckets");

    /// <summary>
    /// Reflects the bucket constant class into a value-to-name map once.
    /// Vanilla's own <c>GetBucketName</c> walks every field on each call, which
    /// is fine at its own call sites but not on a reporting path. A missing
    /// type just leaves the numbers unnamed.
    /// </summary>
    private static Dictionary<int, string> BuildNames(string typeName)
    {
        var map = new Dictionary<int, string>();
        var type = AccessTools.TypeByName(typeName);
        if (type == null)
        {
            return map;
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!field.IsLiteral || field.IsInitOnly || field.FieldType != typeof(int))
            {
                continue;
            }

            if (field.GetRawConstantValue() is int value)
            {
                map.TryAdd(value, field.Name);
            }
        }

        return map;
    }
}

/// <summary>
/// Times <c>SceneRoot.DoFixedUpdates</c>, restricted to the simulation scene.
/// The method is not virtual-overridden by <c>SimRoot</c>, so the base is the
/// only target and the instance has to be filtered by hand.
/// </summary>
[HarmonyPatch]
internal static class SceneFixedUpdatePhasePatch
{
    private static Type? _simRootType;

    /// <summary>Set once the target resolved, for the smoke test.</summary>
    internal static bool Resolved { get; private set; }

    private static bool Prepare() => FramePhaseDiagnosticsPatch.Enabled;

    private static MethodBase? TargetMethod()
    {
        _simRootType = AccessTools.TypeByName("Cosmoteer.Simulation.SimRoot");
        var target = AccessTools.DeclaredMethod(
            AccessTools.TypeByName("Halfling.Scene2D.SceneRoot") ?? typeof(void),
            "DoFixedUpdates",
            Type.EmptyTypes);

        if (target == null || _simRootType == null)
        {
            Halfling.Logging.Logger.Log(
                "[EmmanimLagFix] SceneRoot.DoFixedUpdates was not found; fixed-update timing is off.");
            return null;
        }

        Resolved = true;
        return target;
    }

    private static void Prefix(object __instance, out long __state) =>
        __state = _simRootType?.IsInstanceOfType(__instance) == true ? Stopwatch.GetTimestamp() : 0L;

    private static void Postfix(long __state)
    {
        if (__state != 0L)
        {
            SceneUpdateBreakdown.AddFixedUpdates(Stopwatch.GetTimestamp() - __state);
        }
    }
}

/// <summary>
/// Times each scene bucket pass. <c>SimRoot</c> overrides both methods, so
/// patching the overrides both catches the virtual dispatch and restricts the
/// measurement to the simulation scene without an instance test.
/// </summary>
[HarmonyPatch]
internal static class SceneBucketPhasePatch
{
    /// <summary>Set once both targets resolved, for the smoke test.</summary>
    internal static bool Resolved { get; private set; }

    private static bool Prepare() => FramePhaseDiagnosticsPatch.Enabled;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var simRoot = AccessTools.TypeByName("Cosmoteer.Simulation.SimRoot");
        var fixedBucket = AccessTools.DeclaredMethod(simRoot ?? typeof(void), "FixedUpdateForBucket", new[] { typeof(int) });
        var updateBucket = AccessTools.DeclaredMethod(simRoot ?? typeof(void), "UpdateForBucket", new[] { typeof(int) });

        if (fixedBucket == null)
        {
            Halfling.Logging.Logger.Log("[EmmanimLagFix] SimRoot.FixedUpdateForBucket was not found; bucket timing is partial.");
        }
        else
        {
            yield return fixedBucket;
        }

        if (updateBucket == null)
        {
            Halfling.Logging.Logger.Log("[EmmanimLagFix] SimRoot.UpdateForBucket was not found; bucket timing is partial.");
        }
        else
        {
            yield return updateBucket;
        }

        Resolved = fixedBucket != null && updateBucket != null;
    }

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(int bucket, long __state, MethodBase __originalMethod) =>
        SceneUpdateBreakdown.AddBucket(
            __originalMethod.Name == "FixedUpdateForBucket",
            bucket,
            Stopwatch.GetTimestamp() - __state);
}
