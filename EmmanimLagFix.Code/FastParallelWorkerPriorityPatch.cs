using System.Globalization;
using System.Reflection;
using Halfling.Logging;
using Halfling.Performance;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Optional override for the worker pool's thread priority, applied inside
/// each worker as it starts.
///
/// Vanilla creates its pool at <see cref="ThreadPriority.Lowest"/>. On a
/// machine where the game alone fills every core - fifteen workers plus the
/// main thread on sixteen - any additional runnable thread takes its slice
/// from a lowest-priority worker first. Diagnostics (<c>lg</c>/<c>fpw</c>)
/// measured manager calls of 40-90 ms whose real work is a millisecond or
/// less: a worker descheduled mid-call stretches the whole call, and a
/// worker descheduled while holding a claimed batch stretches every caller
/// spinning on that task.
///
/// The file <c>fastparallel-priority.txt</c> beside the mod's <c>Code</c>
/// folder holds one integer, the <see cref="ThreadPriority"/> value to apply
/// (0 = Lowest, 1 = BelowNormal, 2 = Normal, 3 = AboveNormal, 4 = Highest).
/// Absent or unparsable, the patch is inert and the pool stays exactly as
/// vanilla made it - the flag is a calibration tool, not a default change.
/// </summary>
[HarmonyPatch]
internal static class FastParallelWorkerPriorityPatch
{
    /// <summary>The configured priority, or -1 to leave vanilla's.</summary>
    internal static readonly int ConfiguredPriority = ReadOverride();

    /// <summary>Why the patch is inert, for the smoke test's failure message.</summary>
    internal static string? FailureReason;

    private static int ReadOverride()
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
                "..",
                "fastparallel-priority.txt"));
            if (!File.Exists(path))
            {
                FailureReason = "no fastparallel-priority.txt override present";
                return -1;
            }

            if (int.TryParse(
                    File.ReadAllText(path).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var configured)
                && configured is >= (int)ThreadPriority.Lowest and <= (int)ThreadPriority.Highest)
            {
                return configured;
            }

            FailureReason = "fastparallel-priority.txt did not hold a ThreadPriority value";
        }
        catch (Exception)
        {
            FailureReason = "fastparallel-priority.txt could not be read";
        }

        return -1;
    }

    private static bool Prepare()
    {
        if (ConfiguredPriority >= 0)
        {
            return true;
        }

        try
        {
            Logger.Log(
                "[EmmanimLagFix] Worker pool priority left at vanilla's: "
                + (FailureReason ?? "no reason recorded") + ".");
        }
        catch (Exception)
        {
            // The logger is not initialized in the standalone smoke host.
        }

        return false;
    }

    private static MethodBase TargetMethod() =>
        typeof(FastParallel)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "RunThread")
        ?? throw new MissingMethodException(typeof(FastParallel).FullName, "RunThread");

    /// <summary>
    /// Runs at the top of each worker thread, so the priority applies to the
    /// thread that actually claims and executes batches.
    /// </summary>
    private static void Prefix()
    {
        Thread.CurrentThread.Priority = (ThreadPriority)ConfiguredPriority;
    }
}
