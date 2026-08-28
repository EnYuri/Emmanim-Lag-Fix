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
/// cache. Refresh the candidate set once per second, retain an orange icon for
/// every distinct scheduled nugget, render at most 128 connection lines, and
/// retain the shared line cache across an empty companion overlay.
/// </summary>
[HarmonyPatch]
internal static class ResourcePickupOverlayPatch
{
    private const int MaxDisplayedPickups = 128;
    private static readonly long CandidateRefreshTicks = Stopwatch.Frequency;
    private static readonly ConditionalWeakTable<object, State> States = new();
    private static readonly Type OverlayType = typeof(SimOverlayRenderer);

    private sealed class State
    {
        public readonly List<ResourceTransferJob> LineJobs = new(MaxDisplayedPickups);
        public readonly List<ResourceTransferJob> IconJobs = new();
        public readonly HashSet<Nugget> UniqueNuggets = new();
        public long NextRefresh;
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

                if (state.UniqueNuggets.Add(nugget))
                {
                    state.IconJobs.Add(transferJob);
                }
            }
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
