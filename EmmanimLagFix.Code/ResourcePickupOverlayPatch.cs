using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer;
using Cosmoteer.Game;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Crew.Jobs;
using Cosmoteer.Ships.Resources;
using Cosmoteer.Simulation;
using Cosmoteer.Simulation.Overlays;
using Halfling;
using Halfling.Application;
using Halfling.Graphics;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Large manual nugget collections can contain thousands of transfer jobs.
/// Vanilla enumerates all of them and rebuilds every pickup line/icon on every
/// rendered frame. It also clears the shared line renderer when a second
/// selected/hover overlay is empty, defeating the renderer's own geometry
/// cache. Refresh the candidate set once per second, render at most
/// <see cref="MaxDisplayedPickups"/> connection lines and the same number of
/// distinct nugget icons, and retain the shared line cache across an empty
/// companion overlay.
///
/// Both lists are capped. Version 2.0.11 capped only the lines and kept one
/// icon per distinct scheduled nugget, which is unbounded: a large manual
/// collection produces thousands of entries and the per-frame icon loop walks
/// all of them. A client's freeze dump taken on 2026-09-08 at 23:21:03 shows
/// this prefix as the only mod frame on the main thread, and that client's
/// draw phase ran 19.6 ms against the host's 3.8 ms on the same game. A single
/// stack sample cannot prove the loop was what spent the time -- a resync was
/// deserializing 73 MiB on a 12.3 GiB process in the same window -- but an
/// unbounded per-frame walk is a defect either way.
/// </summary>
[HarmonyPatch]
internal static class ResourcePickupOverlayPatch
{
    private const int MaxDisplayedPickups = 128;

    /// <summary>
    /// Largest visible-job count any refresh has seen since the last read. The
    /// diagnostics line reports it so a capped overlay can be told from a small
    /// one: a value far above <see cref="MaxDisplayedPickups"/> means the cap is
    /// doing real work on that machine.
    /// </summary>
    private static int _peakScannedJobs;

    /// <summary>Reads and clears the peak visible-job count.</summary>
    internal static int TakePeakScannedJobs() => Interlocked.Exchange(ref _peakScannedJobs, 0);

    private static readonly long CandidateRefreshTicks = Stopwatch.Frequency;
    private static readonly ConditionalWeakTable<object, State> States = new();
    private static readonly Type OverlayType = typeof(SimOverlayRenderer);

    private sealed class State
    {
        public readonly List<ResourceTransferJob> LineJobs = new(MaxDisplayedPickups);
        public readonly List<ResourceTransferJob> IconJobs = new(MaxDisplayedPickups);
        public readonly HashSet<Nugget> UniqueNuggets = new();
        public long NextRefresh;

        /// <summary>Visible transfer jobs seen by the last refresh, capped or not.</summary>
        public int ScannedJobs;
    }

    private static MethodBase TargetMethod() =>
        AccessTools.Method(OverlayType, "<OnDrawCrewUnderlays>g___DrawResourceNuggetPickups|98_3")
        ?? throw new MissingMethodException(
            OverlayType.FullName,
            "<OnDrawCrewUnderlays>g___DrawResourceNuggetPickups|98_3");

    private static bool Prefix(
        SimOverlayRenderer __instance,
        IEnumerable<Ship> ships,
        BatchedCappedLineRenderer<IResourceSink, IResourceSource> lineRenderer,
        BatchedIconRenderer<Nugget> iconRenderer,
        float alpha)
    {
        var state = States.GetOrCreateValue(iconRenderer);
        var now = Stopwatch.GetTimestamp();
        if (now >= state.NextRefresh)
        {
            RefreshCandidates(__instance, ships, state);
            state.NextRefresh = now + CandidateRefreshTicks;
        }

        var queuedLines = 0;
        foreach (var transferJob in state.LineJobs)
        {
            if (!TryGetVisibleNugget(__instance, transferJob, out var nugget))
            {
                continue;
            }

            var playerTimeIssued = transferJob.PlayerTimeIssued;
            var tween = Mathx.InverseLerp(
                playerTimeIssued,
                playerTimeIssued + __instance.Rules.ScheduledNuggetCollectTweenDuration,
                App.Clock.Time);

            if (Settings.ShowResourcePickupLines)
            {
                lineRenderer.QueueLine(
                    transferJob.Sink,
                    nugget,
                    transferJob.Sink.WorldCenter,
                    nugget.Location,
                    tween);
            }

            queuedLines++;
        }

        var queuedIcons = 0;
        foreach (var transferJob in state.IconJobs)
        {
            if (!TryGetVisibleNugget(__instance, transferJob, out var nugget))
            {
                continue;
            }

            var playerTimeIssued = transferJob.PlayerTimeIssued;
            var tween = Mathx.InverseLerp(
                playerTimeIssued,
                playerTimeIssued + __instance.Rules.ScheduledNuggetCollectTweenDuration,
                App.Clock.Time);
            var scale = Mathx.Lerp(
                __instance.Rules.ScheduledNuggetCollectTweenFromScale,
                1f,
                tween);
            iconRenderer.QueueIcon(nugget, nugget.Location, tween, scale);
            queuedIcons++;
        }

        if (queuedLines > 0 || queuedIcons > 0)
        {
            var color = GameApp.Rules.Game.GameGui.Resources.ResourceTransferLineUnselectedCrewColor;
            color.A *= alpha;
            if (queuedLines > 0 && Settings.ShowResourcePickupLines)
            {
                lineRenderer.Draw(__instance.Sim, color);
            }

            if (queuedIcons > 0)
            {
                var scale = Mathx.Sqrt(__instance.Sim.Camera.WorldUniformScale);
                iconRenderer.Draw(__instance.Sim, new Color(1f, 1f, 1f, alpha), scale);
            }
            else
            {
                iconRenderer.Clear();
            }
        }
        else
        {
            // The line renderer is shared by the selected and hover overlays.
            // Clearing it here destroys geometry queued by the other overlay.
            // With no Draw call, cached lines are not visible this frame.
            iconRenderer.Clear();
        }

        return false;
    }

    private static void RefreshCandidates(
        SimOverlayRenderer overlay,
        IEnumerable<Ship> ships,
        State state)
    {
        state.LineJobs.Clear();
        state.IconJobs.Clear();
        state.UniqueNuggets.Clear();
        state.ScannedJobs = 0;
        foreach (var ship in ships)
        {
            foreach (var transferJob in ship.Resources.GetTransferJobs(autoJobs: false))
            {
                if (!TryGetVisibleNugget(overlay, transferJob, out var nugget))
                {
                    continue;
                }

                if (state.LineJobs.Count < MaxDisplayedPickups)
                {
                    state.LineJobs.Add(transferJob);
                }

                if (state.IconJobs.Count < MaxDisplayedPickups && state.UniqueNuggets.Add(nugget))
                {
                    state.IconJobs.Add(transferJob);
                }

                state.ScannedJobs++;
            }
        }

        if (state.ScannedJobs > _peakScannedJobs)
        {
            _peakScannedJobs = state.ScannedJobs;
        }
    }

    private static bool TryGetVisibleNugget(
        SimOverlayRenderer overlay,
        ResourceTransferJob transferJob,
        out Nugget nugget)
    {
        nugget = null!;
        if (transferJob.ResourcesRequested.Displayed <= transferJob.ResourcesInHand ||
            transferJob.Source is not Nugget source ||
            source.Sim != overlay.Sim)
        {
            return false;
        }

        var sinkShip = transferJob.Sink.Ship;
        if (sinkShip == null || sinkShip.Sim != overlay.Sim)
        {
            return false;
        }

        nugget = source;
        return true;
    }
}
