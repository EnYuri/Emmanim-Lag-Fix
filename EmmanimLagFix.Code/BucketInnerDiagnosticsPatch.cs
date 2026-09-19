using System.Diagnostics;
using System.Reflection;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Ships.Crew;
using Cosmoteer.Ships.Crew.Jobs;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Resources;
using Cosmoteer.Ships.Resources;
using Cosmoteer.Ships.Statuses;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Times the inner steps of the parallel fixed-update buckets that sit at the
/// top of <c>fb=[...]</c> in ordinary play but have no sub-bucket breakdown:
/// <c>ConvertResources</c>, <c>Jobs</c>, <c>ShipCrew</c> and
/// <c>SetThrusterActivations</c>.
///
/// The bucket names only go so far. <c>ConvertResources</c> is a priority
/// queue of due <see cref="ResourceConverter.OnConversionTick"/> calls - the
/// split between "many cheap conversions" and "few expensive ones" decides
/// whether the fix is in the tick body or in the queue's schedule. <c>Jobs</c>
/// spends its time inside <c>JobManager.AsyncGetCrewForNextJob</c>'s crew
/// search. <c>ShipCrew</c> is four passes - destination/movement, post-movement,
/// quads and the per-ship source autofill - with different scaling. And
/// <c>SetThrusterActivations</c> is one call per moving command whose solver
/// share is already patched, so what remains is the apply/avoidance side.
///
/// <c>im=[name:msxN,...]</c> reports milliseconds and call count per frame
/// over the report window, matching <see cref="SceneUpdateBreakdown"/>'s
/// <c>fb</c> units. Timing only; no simulation behaviour changes. Gated on
/// the same flag as the rest of the phase diagnostics.
/// </summary>
internal static class BucketInnerDiagnostics
{
    /// <summary>Per-step entries in the report; more than this never fits the relay budget anyway.</summary>
    private const int TopEntries = 8;

    /// <summary>Longest-single-call entries in the <c>lg</c> list.</summary>
    private const int TopLongest = 6;

    private static readonly Dictionary<string, long> Ticks = new();
    private static readonly Dictionary<string, long> Calls = new();
    private static readonly Dictionary<string, long> Max = new();
    private static readonly Dictionary<string, long> Second = new();
    private static readonly Dictionary<string, int> MaxParts = new();
    private static readonly object Gate = new();

    internal static void Add(string name, long ticks, int parts)
    {
        lock (Gate)
        {
            Ticks.TryGetValue(name, out var prior);
            Ticks[name] = prior + ticks;
            Calls.TryGetValue(name, out var priorCalls);
            Calls[name] = priorCalls + 1;
            Max.TryGetValue(name, out var longest);
            if (ticks > longest)
            {
                Second[name] = longest;
                Max[name] = ticks;
                MaxParts[name] = parts;
            }
            else if (!Second.TryGetValue(name, out var second) || ticks > second)
            {
                Second[name] = ticks;
            }
        }
    }

    /// <summary>
    /// Formats and resets. Reads the frame count the frame-phase snapshot just
    /// took, so it must be called after that one within the same sample - the
    /// same contract <see cref="SceneUpdateBreakdown.Take"/> already has.
    /// </summary>
    internal static string Take()
    {
        KeyValuePair<string, long>[] ticks;
        Dictionary<string, long> calls;
        KeyValuePair<string, long>[] longest;
        Dictionary<string, long> second;
        Dictionary<string, int> maxParts;
        lock (Gate)
        {
            ticks = Ticks.ToArray();
            Ticks.Clear();
            calls = new Dictionary<string, long>(Calls);
            Calls.Clear();
            longest = Max.ToArray();
            Max.Clear();
            second = new Dictionary<string, long>(Second);
            Second.Clear();
            maxParts = new Dictionary<string, int>(MaxParts);
            MaxParts.Clear();
        }

        var frames = FramePhaseDiagnosticsPatch.LastFrames;
        if (!FramePhaseDiagnosticsPatch.Enabled || frames <= 0 || ticks.Length == 0)
        {
            return "im=[] lg=[]";
        }

        var perFrameMs = 1000d / Stopwatch.Frequency / frames;
        Array.Sort(ticks, static (a, b) => b.Value.CompareTo(a.Value));
        var rows = new List<string>(TopEntries);
        for (var i = 0; i < ticks.Length && i < TopEntries; i++)
        {
            var ms = ticks[i].Value * perFrameMs;
            if (ms < 0.05d)
            {
                break;
            }

            calls.TryGetValue(ticks[i].Key, out var n);
            rows.Add($"{ticks[i].Key}:{ms:F1}x{n / (double)frames:F0}");
        }

        // Per-call maxima, in whole-call milliseconds - the straggler figure.
        // A bucket whose fb wall time sits near a single item's lg entry is
        // bound by that item, not by the dispatch; only call sites that are
        // bucket items (ship managers, command activations) make a lg entry
        // that compares like that. The second-longest call rides along as
        // max/2nd: a one-off stall inflates only the first figure, so lg=84/2
        // reads as a hitch while lg=84/70 reads as a chronic straggler. The
        // @N tail is the part count of the ship behind the longest call -
        // one huge number repeating across codes points at a single
        // megaship, while small or varying sizes point at event storms.
        var msPerTick = 1000d / Stopwatch.Frequency;
        Array.Sort(longest, static (a, b) => b.Value.CompareTo(a.Value));
        var longestRows = new List<string>(TopLongest);
        for (var i = 0; i < longest.Length && i < TopLongest; i++)
        {
            var ms = longest[i].Value * msPerTick;
            if (ms < 0.3d)
            {
                break;
            }

            second.TryGetValue(longest[i].Key, out var runnerUp);
            var size = maxParts.TryGetValue(longest[i].Key, out var parts) && parts >= 0
                ? $"@{parts}"
                : string.Empty;
            longestRows.Add($"{longest[i].Key}:{ms:F1}/{runnerUp * msPerTick:F1}{size}");
        }

        return $"im=[{string.Join(",", rows)}] lg=[{string.Join(",", longestRows)}]";
    }
}

/// <summary>
/// One prefix/postfix pair over every chosen inner method; the postfix reads
/// <c>__originalMethod.Name</c> through a short-code map, so command subclass
/// overrides of <c>SetThrusterActivations</c> all report under one entry.
/// </summary>
[HarmonyPatch]
internal static class BucketInnerTimingPatch
{
    /// <summary>Method names mapped onto the short codes used in the report.</summary>
    private static readonly Dictionary<string, string> Names = new()
    {
        [nameof(ResourceConverter.OnConversionTick)] = "cvt",
        ["AsyncGetCrewForNextJob"] = "job",
        ["UpdateCrewAsync"] = "cu",
        ["UpdateCrewPostMovement"] = "cp",
        ["UpdateCrewQuadsAsync"] = "cq",
        ["AutoFillCrewSources"] = "caf",
        [nameof(Command.SetThrusterActivations)] = "thr",
        // Bucket items themselves: one FixedUpdate call is one ship's whole
        // share of that bucket, so these give the straggler's lg entry - a
        // single call that approaches the bucket's fb wall time means the
        // pass is bound by one ship, not by work it could spread.
        ["ShipStatusManager.FixedUpdate"] = "shs",
        ["JobManager.FixedUpdate"] = "jm",
        ["ResourceConverterManager.FixedUpdate"] = "rcm",
        ["ResourceManager.FixedUpdate"] = "rm",
        ["ShipCrewManager.FixedUpdate"] = "scm",
    };

    /// <summary>Number of fixed targets that resolved, for the smoke test.</summary>
    internal static int ResolvedFixed { get; private set; }

    /// <summary>Number of <c>SetThrusterActivations</c> overrides found.</summary>
    internal static int ResolvedCommands { get; private set; }

    // Never install this shared patch in production. Its MethodBase
    // __originalMethod injection makes Harmony materialize a
    // RuntimeMethodInfoStub on every inner call; OnConversionTick alone
    // measured about 970 MiB/min. Keep target resolution and aggregation
    // code for offline diagnostics, but im/lg intentionally report empty.
    private static bool Prepare() => false;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var fixedTargets = new (Type Declaring, string Method, Type[] Args)[]
        {
            (typeof(ResourceConverter), nameof(ResourceConverter.OnConversionTick),
                new[] { typeof(ResourceConverter.ResourceConverterManager) }),
            (typeof(JobManager), "AsyncGetCrewForNextJob",
                new[] { typeof(int), typeof(int) }),
            (typeof(ShipCrewManager), "UpdateCrewAsync",
                new[] { typeof(int), typeof(int) }),
            (typeof(ShipCrewManager), "UpdateCrewPostMovement",
                new[] { typeof(int), typeof(int) }),
            (typeof(ShipCrewManager), "UpdateCrewQuadsAsync",
                new[] { typeof(int), typeof(int) }),
            (typeof(ShipCrewManager), "AutoFillCrewSources", Type.EmptyTypes),
            (typeof(ShipStatusManager), "FixedUpdate",
                new[] { typeof(Halfling.Timing.FixedUpdater), typeof(Halfling.Scene2D.SceneRoot) }),
            (typeof(JobManager), "FixedUpdate",
                new[] { typeof(Halfling.Timing.FixedUpdater), typeof(Halfling.Scene2D.SceneRoot) }),
            (typeof(ResourceConverter.ResourceConverterManager), "FixedUpdate",
                new[] { typeof(Halfling.Timing.FixedUpdater), typeof(Halfling.Scene2D.SceneRoot) }),
            (typeof(ResourceManager), "FixedUpdate",
                new[] { typeof(Halfling.Timing.FixedUpdater), typeof(Halfling.Scene2D.SceneRoot) }),
            (typeof(ShipCrewManager), "FixedUpdate",
                new[] { typeof(Halfling.Timing.FixedUpdater), typeof(Halfling.Scene2D.SceneRoot) }),
        };

        var resolved = 0;
        foreach (var (declaring, method, args) in fixedTargets)
        {
            var target = AccessTools.DeclaredMethod(declaring, method, args);
            if (target == null)
            {
                Halfling.Logging.Logger.Log(
                    $"[EmmanimLagFix] {declaring.Name}.{method} was not found; bucket inner timing is partial.");
                continue;
            }

            resolved++;
            yield return target;
        }

        ResolvedFixed = resolved;

        // Every non-abstract command can land its activation pass in the
        // SetThrusterActivations bucket, so bind every declared override -
        // a command that adds a new one after a game update is picked up
        // automatically, and one that vanished only shrinks the count.
        var commands = 0;
        foreach (var type in typeof(Command).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(Command).IsAssignableFrom(type))
            {
                continue;
            }

            var target = AccessTools.DeclaredMethod(
                type, nameof(Command.SetThrusterActivations), new[] { typeof(Halfling.Timing.Time) });
            if (target == null)
            {
                continue;
            }

            commands++;
            yield return target;
        }

        ResolvedCommands = commands;
        if (commands == 0)
        {
            Halfling.Logging.Logger.Log(
                "[EmmanimLagFix] No Command.SetThrusterActivations overrides found; thruster timing is off.");
        }
    }

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(MethodBase __originalMethod, object __instance, long __state)
    {
        // FixedUpdate is every ship manager's name, so those keys carry the
        // declaring type; everything else is unique by method name alone.
        var key = __originalMethod.Name == "FixedUpdate"
            ? __originalMethod.DeclaringType!.Name + "." + __originalMethod.Name
            : __originalMethod.Name;
        if (Names.TryGetValue(key, out var name))
        {
            // The ship behind the call, for the @N tag on the longest entry.
            // ShipComponent covers every per-ship manager, PartComponent the
            // one part-level target (OnConversionTick), and Command the
            // thruster activations; anything else reports without a size.
            var parts = __instance switch
            {
                ShipComponent component => component.Ship?.Parts.Count ?? -1,
                PartComponent component => component.Part?.Ship?.Parts.Count ?? -1,
                Command command when command.HasShip => command.Ship.Parts.Count,
                _ => -1,
            };
            BucketInnerDiagnostics.Add(name, Stopwatch.GetTimestamp() - __state, parts);
        }
    }
}
