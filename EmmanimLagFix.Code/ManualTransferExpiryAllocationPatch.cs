using Cosmoteer;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Ships.Crew.Jobs;
using Cosmoteer.Ships.Resources;
using Halfling.Pooling;
using Halfling.Timing;
using HarmonyLib;

namespace EmmanimLagFix.Code;

// Keep the vanilla partition, check order, MP values and deterministic queue.
// Isolated helpers create closure state only when an update/removal is queued.
[HarmonyPatch(typeof(ResourceManager), "ExpireManualTransferJobs")]
internal static class ManualTransferExpiryAllocationPatch
{
    private static bool Prefix(ResourceManager __instance, Time dt)
    {
        Expire(__instance, dt);
        return false;
    }

    internal static void Expire(ResourceManager manager, Time dt)
    {
        if (manager._manualTransferJobs.Count == 0)
        {
            manager._accumulatedManualJobExpensiveChecks = 0f;
            return;
        }
        TempList<ResourceTransferJob>? confirmed = null, displayed = null, toRemove = null;
        for (var i = 0; i < manager._manualTransferJobs.Count; i++)
        {
            var job = manager._manualTransferJobs[i];
            if (job.ResourcesRequested.Confirmed > 0)
                (confirmed ??= TempList<ResourceTransferJob>.Alloc()).Add(job);
            else
                (displayed ??= TempList<ResourceTransferJob>.Alloc()).Add(job);
        }
        if (confirmed != null)
        {
            manager._accumulatedManualJobExpensiveChecks += (float)confirmed.Count * (float)dt * GameApp.Rules.Crew.ManualTransferJobExpensiveCheckInterval;
            var count = (int)manager._accumulatedManualJobExpensiveChecks;
            manager._accumulatedManualJobExpensiveChecks -= count;
            var end = manager._nextManualJobExpensiveCheckIndex + count;
            for (var i = 0; i < confirmed.Count; i++)
            {
                var job = confirmed[i];
                var expensive = i >= manager._nextManualJobExpensiveCheckIndex && i < end;
                if (job.GetFinished(!job.IsNonActive ? MPValueType.Confirmed : MPValueType.Displayed, expensive))
                    (toRemove ??= TempList<ResourceTransferJob>.Alloc()).Add(job);
                else if (job.Source != null)
                {
                    if (job.Source.Sim != manager.Sim || (expensive && !job.Source.IsSinkReachable(job.Sink, allowDifferentShips: true, allowAirlockTraversal: true)))
                        QueueRequested(manager, job, new MPValue<int>(job.ResourcesInHand));
                    else if (job.ResourcesRequested.Confirmed - job.ResourcesInHand > job.Source.Resources)
                        QueueRequested(manager, job, new MPValue<int>(job.Source.Resources + job.ResourcesInHand));
                }
            }
            // Vanilla does not advance _nextManualJobExpensiveCheckIndex here.
        }
        else manager._accumulatedManualJobExpensiveChecks = 0f;
        if (displayed != null)
        {
            for (var i = 0; i < displayed.Count; i++)
            {
                var job = displayed[i];
                if (job.GetFinished(!job.IsNonActive ? MPValueType.Confirmed : MPValueType.Displayed, checkSourceValid: false))
                    (toRemove ??= TempList<ResourceTransferJob>.Alloc()).Add(job);
                else if (job.Source != null)
                {
                    if (job.Source.Sim != manager.Sim)
                        job.ResourcesRequested = new MPValue<int>(job.ResourcesInHand, 0);
                    else if (job.ResourcesRequested.Displayed - job.ResourcesInHand > job.Source.Resources)
                        job.ResourcesRequested = new MPValue<int>(job.Source.Resources + job.ResourcesInHand, 0);
                }
            }
        }
        confirmed?.Dispose();
        displayed?.Dispose();
        if (toRemove != null) QueueRemoval(manager, toRemove);
    }

    private static void QueueRequested(ResourceManager manager, ResourceTransferJob job, MPValue<int> requested)
    {
        manager.Sim!.EnqueueDeterministic(manager.Ship.UniqueID, () => job.ResourcesRequested = requested);
    }

    private static void QueueRemoval(ResourceManager manager, TempList<ResourceTransferJob> jobs)
    {
        manager.Sim!.EnqueueDeterministic(manager.Ship.UniqueID, () =>
        {
            for (var i = 0; i < jobs.Count; i++) manager.RemoveManualTransferJob(jobs[i]);
            jobs.Dispose();
        });
    }
}
