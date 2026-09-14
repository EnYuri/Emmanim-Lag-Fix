using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Cosmoteer.Ships.Resources;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Splits the <c>Resources</c> fixed-update bucket into the phases
/// <c>ResourceManager.FixedUpdate</c> actually runs, because the bucket is the
/// largest single item in a world tick and the existing diagnostics cannot say
/// which half of it is expensive.
///
/// <c>Resources</c> measured 38-40% of the whole fixed update on a 570-ship
/// single-player save, and 14.3 ms per world tick on the slow peer of the
/// 2026-09-13 session against 2.16 ms on the host - a 6.6x gap where
/// <c>Statuses</c> showed only 2.5x. Nothing published so far distinguishes the
/// two phases inside it, and they have completely different shapes:
///
/// <list type="bullet">
/// <item><c>SearchForSources</c> is <b>rate limited</b>. It searches
/// <c>floor(ResourceSearchesPerSecond * interval)</c> sinks per tick - four at
/// vanilla's 120/s and 30 physics ticks - round robin, or every sink when the
/// ship has no more than that. So its cost per ship is capped and scales with
/// <i>ship count</i>, not ship size.</item>
/// <item><c>UpdateSinkJobs</c> is <b>not</b> rate limited. It dispatches over
/// every sink on the ship on every tick, and each sink with candidate sources
/// walks them revalidating priority and reachability. So its cost scales with
/// <i>total sinks</i>, i.e. with fleet size.</item>
/// </list>
///
/// A per-sink timing patch for the first phase already exists
/// (<see cref="ResourceSearchDiagnosticsPatch"/>) but it interpolates a
/// dictionary key string on every search. At roughly 120 searches per second
/// per ship over several hundred ships that is tens of thousands of string
/// allocations per second, which distorts exactly the measurement it is taken
/// for. This one adds two <c>Interlocked</c> adds per phase per ship per tick
/// and allocates nothing.
///
/// The phases run on FastParallel workers, one ship per bucket entry, so these
/// totals are <b>aggregate CPU across workers</b>, not the wall time of the
/// bucket pass. Compare them with each other - their ratio is the answer - and
/// not against the <c>Resources</c> figure in <c>fb=[...]</c>, which is wall
/// time on the dispatching thread.
///
/// Inert unless a memory-diagnostics flag is present, exactly as the frame and
/// scene probes are.
/// </summary>
[HarmonyPatch]
internal static class ResourcePhaseSplitPatch
{
    internal static bool Enabled => FramePhaseDiagnosticsPatch.Enabled;

    private static long _totalTicks;
    private static long _searchTicks;
    private static long _sinkJobTicks;
    private static long _manualExpiryTicks;
    private static long _calls;
    private static long _sinks;
    private static long _sinkSearchTicks;
    private static long _sinkSearches;

    /// <summary>
    /// Phase identity, resolved once per patched method so the postfix does not
    /// have to compare strings.
    /// </summary>
    private enum Phase
    {
        Total,
        Search,
        SinkSearch,
        SinkJobs,
        ManualExpiry,
    }

    private static readonly Dictionary<MethodBase, Phase> Phases = new();

    private static MethodBase Declared(string name, string parameterTypeName)
    {
        foreach (var candidate in typeof(ResourceManager).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (candidate.Name != name)
            {
                continue;
            }

            var parameters = candidate.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType.Name == parameterTypeName)
            {
                return candidate;
            }
        }

        throw new MissingMethodException(typeof(ResourceManager).FullName, $"{name}({parameterTypeName})");
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        // FixedUpdate is the whole bucket entry for one ship. Timing it as well
        // as its parts is what makes the residual - salvage expiry, refunds, and
        // the dispatch overhead of the two nested FastParallel.For calls -
        // visible rather than merely absent.
        var fixedUpdate = AccessTools.DeclaredMethod(typeof(ResourceManager), "FixedUpdate")
            ?? throw new MissingMethodException(typeof(ResourceManager).FullName, "FixedUpdate");
        var search = Declared("SearchForSources", "FixedUpdater");
        var sinkJobs = Declared("UpdateSinkJobs", "Time");
        var manualExpiry = Declared("ExpireManualTransferJobs", "Time");

        // The per-sink search, inside the rate-limited phase above. Vanilla runs
        // at most floor(ResourceSearchesPerSecond / PhysicsUpdatesPerSecond)
        // of these per ship per tick - four at stock rates - so timing each one
        // costs two Stopwatch reads per ship-tick and answers the only question
        // the phase split leaves open: whether the phase is expensive because it
        // runs often or because one search is.
        var sinkSearch = Declared("SearchForSources", "SinkInfo");

        Phases[fixedUpdate] = Phase.Total;
        Phases[search] = Phase.Search;
        Phases[sinkSearch] = Phase.SinkSearch;
        Phases[sinkJobs] = Phase.SinkJobs;
        Phases[manualExpiry] = Phase.ManualExpiry;

        yield return fixedUpdate;
        yield return search;
        yield return sinkSearch;
        yield return sinkJobs;
        yield return manualExpiry;
    }

    private static bool Prepare() => Enabled;

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(ResourceManager __instance, long __state, MethodBase __originalMethod)
    {
        var elapsed = Stopwatch.GetTimestamp() - __state;
        switch (Phases[__originalMethod])
        {
            case Phase.Total:
                Interlocked.Add(ref _totalTicks, elapsed);
                Interlocked.Increment(ref _calls);
                // Read once per ship per tick on the phase that walks them all,
                // so a large per-sink cost can be told from simply having many.
                Interlocked.Add(ref _sinks, __instance._sinks.Count);
                break;
            case Phase.Search:
                Interlocked.Add(ref _searchTicks, elapsed);
                break;
            case Phase.SinkSearch:
                Interlocked.Add(ref _sinkSearchTicks, elapsed);
                Interlocked.Increment(ref _sinkSearches);
                break;
            case Phase.SinkJobs:
                Interlocked.Add(ref _sinkJobTicks, elapsed);
                break;
            case Phase.ManualExpiry:
                Interlocked.Add(ref _manualExpiryTicks, elapsed);
                break;
        }
    }

    /// <summary>
    /// Drains the counters and formats them as aggregate worker milliseconds per
    /// rendered frame, matching the unit every other field in the diagnostics
    /// line uses. <c>rest</c> is the total minus the three named phases;
    /// <c>sk</c> is <b>not</b> one of them - it is the part of <c>srch</c> spent
    /// inside individual searches, so it nests rather than adds, and the trailing
    /// <c>x</c> count is searches per ship-tick. <c>srch - sk</c> is then the
    /// rate limiter, the queue bookkeeping and the nested dispatch around them.
    /// </summary>
    internal static string Snapshot()
    {
        var total = Interlocked.Exchange(ref _totalTicks, 0);
        var search = Interlocked.Exchange(ref _searchTicks, 0);
        var sinkSearch = Interlocked.Exchange(ref _sinkSearchTicks, 0);
        var searches = Interlocked.Exchange(ref _sinkSearches, 0);
        var sinkJobs = Interlocked.Exchange(ref _sinkJobTicks, 0);
        var manualExpiry = Interlocked.Exchange(ref _manualExpiryTicks, 0);
        var calls = Interlocked.Exchange(ref _calls, 0);
        var sinks = Interlocked.Exchange(ref _sinks, 0);

        var frames = FramePhaseDiagnosticsPatch.LastFrames;
        if (!Enabled || frames <= 0 || calls == 0)
        {
            return "rphase=-";
        }

        var perFrameMs = 1000d / Stopwatch.Frequency / frames;
        string Ms(long ticks) =>
            (ticks * perFrameMs).ToString("F2", CultureInfo.InvariantCulture);

        var rest = total - search - sinkJobs - manualExpiry;
        return "rphase=[all:" + Ms(total)
            + ",srch:" + Ms(search)
            + ",sk:" + Ms(sinkSearch)
            + ",jobs:" + Ms(sinkJobs)
            + ",mexp:" + Ms(manualExpiry)
            + ",rest:" + Ms(rest)
            + "]/" + calls.ToString(CultureInfo.InvariantCulture)
            + "x" + ((double)searches / calls).ToString("F1", CultureInfo.InvariantCulture)
            + " sinks=" + ((double)sinks / calls).ToString("F1", CultureInfo.InvariantCulture);
    }
}
