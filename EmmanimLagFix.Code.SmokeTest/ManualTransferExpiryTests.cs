using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

using Halfling.Timing;
using HarmonyLib;

internal static class ManualTransferExpiryTests
{
    private static object Manager = null!, Sim = null!, Ship = null!;
    private static readonly List<string> Calls = new();
    private static readonly List<object> Removed = new();
    private static readonly Dictionary<object, int> JobIds = new();
    private static FieldInfo Requested = null!;
    private static object Rules = null!;
    private static bool RecordCalls = true;
    private static bool RulesGetter(ref object __result) { __result = Rules; return false; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Original(object manager, Time dt) => throw new NotImplementedException();
    private static bool Finished(object __instance, int valueType, bool checkSourceValid, ref bool __result)
    {
        if (!JobIds.TryGetValue(__instance, out var id)) return true;
        if (RecordCalls) Calls.Add($"{id}/{valueType}/{checkSourceValid}");
        __result = id % 3 == 0;
        return false;
    }
    private static bool SimGetter(object __instance, ref object __result)
    {
        if (!ReferenceEquals(__instance, Manager)) return true;
        __result = Sim; return false;
    }
    private static bool ShipGetter(object __instance, ref object __result)
    {
        if (!ReferenceEquals(__instance, Manager)) return true;
        __result = Ship; return false;
    }
    private static bool Remove(object __instance, object job)
    {
        if (!ReferenceEquals(__instance, Manager)) return true;
        Removed.Add(job); return false;
    }
    private static bool SetRequested(object __instance, object value)
    {
        if (!JobIds.ContainsKey(__instance)) return true;
        Requested.SetValue(__instance, value); return false;
    }

    private static object MakeRequested(int displayed, int confirmed) => Activator.CreateInstance(Requested.FieldType, new object[] { displayed, confirmed })!;
    private static int Confirmed(object job) => (int)AccessTools.Field(Requested.FieldType, "Confirmed").GetValue(Requested.GetValue(job))!;

    internal static void Run(Assembly game, Assembly patchAssembly)
    {
        var managerType = game.GetType("Cosmoteer.Ships.Resources.ResourceManager", true)!;
        var jobType = game.GetType("Cosmoteer.Ships.Crew.Jobs.ResourceTransferJob", true)!;
        var patchType = patchAssembly.GetType("EmmanimLagFix.Code.ManualTransferExpiryAllocationPatch", true)!;
        var target = AccessTools.Method(managerType, "ExpireManualTransferJobs");
        if (Harmony.GetPatchInfo(target)?.Prefixes.Any(p => p.PatchMethod.DeclaringType == patchType) != true)
            throw new Exception("Manual expiry prefix missing.");
        var managerParameter = System.Linq.Expressions.Expression.Parameter(typeof(object));
        var timeParameter = System.Linq.Expressions.Expression.Parameter(typeof(Time));
        var replacement = System.Linq.Expressions.Expression.Lambda<Action<object, Time>>(
            System.Linq.Expressions.Expression.Call(AccessTools.Method(patchType, "Expire"),
                System.Linq.Expressions.Expression.Convert(managerParameter, managerType), timeParameter),
            managerParameter, timeParameter).Compile();
        var probe = new Harmony("manual-expiry-fixtures");

        Requested = AccessTools.Field(jobType, "<ResourcesRequested>k__BackingField");
        var rulesGetter = AccessTools.Property(game.GetType("Cosmoteer.GameApp", true)!, "Rules");
        var rules = RuntimeHelpers.GetUninitializedObject(rulesGetter.PropertyType);
        var crewField = AccessTools.Field(rules.GetType(), "<Crew>k__BackingField") ?? AccessTools.Field(rules.GetType(), "Crew");
        var crew = RuntimeHelpers.GetUninitializedObject(crewField.FieldType);
        crewField.SetValue(rules, crew);
        AccessTools.Field(crew.GetType(), "ManualTransferJobExpensiveCheckInterval").SetValue(crew, 0.5f);
        Rules = rules;
        try
        {
            probe.Patch(rulesGetter.GetMethod!, prefix: new HarmonyMethod(AccessTools.Method(typeof(ManualTransferExpiryTests), nameof(RulesGetter))));
            probe.Patch(AccessTools.Method(jobType, "GetFinished"), prefix: new HarmonyMethod(AccessTools.Method(typeof(ManualTransferExpiryTests), nameof(Finished))));
            probe.Patch(AccessTools.DeclaredPropertyGetter(AccessTools.PropertyGetter(managerType, "Sim").DeclaringType!, "Sim"), prefix: new HarmonyMethod(AccessTools.Method(typeof(ManualTransferExpiryTests), nameof(SimGetter))));
            probe.Patch(AccessTools.DeclaredPropertyGetter(AccessTools.PropertyGetter(managerType, "Ship").DeclaringType!, "Ship"), prefix: new HarmonyMethod(AccessTools.Method(typeof(ManualTransferExpiryTests), nameof(ShipGetter))));
            probe.Patch(AccessTools.Method(managerType, "RemoveManualTransferJob"), prefix: new HarmonyMethod(AccessTools.Method(typeof(ManualTransferExpiryTests), nameof(Remove))));
            probe.Patch(AccessTools.PropertySetter(jobType, "ResourcesRequested"), prefix: new HarmonyMethod(AccessTools.Method(typeof(ManualTransferExpiryTests), nameof(SetRequested))));
        probe.CreateReversePatcher(target, new HarmonyMethod(AccessTools.Method(typeof(ManualTransferExpiryTests), nameof(Original)))).Patch();
            var jobsField = AccessTools.Field(managerType, "_manualTransferJobs");
            var accumulated = AccessTools.Field(managerType, "_accumulatedManualJobExpensiveChecks");
            var index = AccessTools.Field(managerType, "_nextManualJobExpensiveCheckIndex");
            var simType = game.GetType("Cosmoteer.Simulation.SimRoot", true)!;
            var queueField = AccessTools.Field(simType, "_queuedDeterministic");
            var shipType = game.GetType("Cosmoteer.Ships.Ship", true)!;
            foreach (var size in new[] { 0, 1, 7, 32 })
            foreach (var initial in new[] { 0f, 0.7f, 3.25f })
            {
                string? expected = null;
                for (var pass = 0; pass < 2; pass++)
                {
                    Manager = RuntimeHelpers.GetUninitializedObject(managerType);
                    Sim = RuntimeHelpers.GetUninitializedObject(simType);
                    Ship = RuntimeHelpers.GetUninitializedObject(shipType);
                    AccessTools.Field(managerType, "<Ship>k__BackingField").SetValue(Manager, Ship);
                    AccessTools.Field(managerType, "_node").SetValue(Manager, Ship);
                    AccessTools.Field(shipType, "_root").SetValue(Ship, Sim);
                    var queue = (IList)Activator.CreateInstance(queueField.FieldType)!;
                    queueField.SetValue(Sim, queue);
                    var jobs = (IList)Activator.CreateInstance(jobsField.FieldType)!;
                    jobsField.SetValue(Manager, jobs);
                    accumulated.SetValue(Manager, initial);
                    index.SetValue(Manager, 2);
                    Calls.Clear(); Removed.Clear(); JobIds.Clear();
                    for (var i = 0; i < size; i++)
                    {
                        var job = RuntimeHelpers.GetUninitializedObject(jobType);
                        Requested.SetValue(job, MakeRequested(10, i % 2 == 0 ? 10 : 0));
                        jobs.Add(job); JobIds.Add(job, i);
                    }
                    if (pass == 0) Original(Manager, (Time)0.1f); else replacement(Manager, (Time)0.1f);
                    if (Removed.Count != 0) throw new Exception("Removal executed before queue drain.");
                    foreach (var entry in queue)
                    {
                        var action = (Action<object?>)AccessTools.Field(entry!.GetType(), "Action").GetValue(entry)!;
                        action(AccessTools.Field(entry.GetType(), "Data").GetValue(entry));
                    }
                    var snapshot = string.Join(",", Calls) + "/" + string.Join(",", Removed.Select(j => JobIds[j]))
                        + "/" + accumulated.GetValue(Manager) + "/" + index.GetValue(Manager) + "/" + queue.Count;
                    if (pass == 0) expected = snapshot;
                    else if (snapshot != expected) throw new Exception("Manual expiry differs from vanilla: " + snapshot + " != " + expected);
                }
            }
            // Empty collections must no longer allocate a closure per tick.
            ((IList)jobsField.GetValue(Manager)!).Clear();
            for (var i = 0; i < 10000; i++) replacement(Manager, default);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10000; i++) replacement(Manager, default);
            if (GC.GetAllocatedBytesForCurrentThread() != before) throw new Exception("Empty expiry allocates.");
            var unchangedJobs = (IList)jobsField.GetValue(Manager)!;
            foreach (var job in JobIds.Keys.ToArray())
            {
                JobIds[job] = 1; // Mock valid unfinished jobs, with no source correction.
                Requested.SetValue(job, MakeRequested(10, 10));
                unchangedJobs.Add(job);
            }
            RecordCalls = false;
            for (var i = 0; i < 10000; i++) replacement(Manager, default);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10000; i++) replacement(Manager, default);
            if (GC.GetAllocatedBytesForCurrentThread() != before) throw new Exception("Unchanged expiry allocates per job.");
            RecordCalls = true;
            // Distinct queued changes keep their job/value, and stay deferred.
            var update = AccessTools.Method(patchType, "QueueRequested");
            var queued = (IList)queueField.GetValue(Sim)!; queued.Clear();
            var first = JobIds.Keys.First(); var last = JobIds.Keys.Last();
            update.Invoke(null, new object[] { Manager, first, MakeRequested(4, 3) });
            update.Invoke(null, new object[] { Manager, last, MakeRequested(8, 7) });
            if (Confirmed(first) == 3) throw new Exception("Update was immediate.");
            foreach (var entry in queued)
                ((Action<object?>)AccessTools.Field(entry!.GetType(), "Action").GetValue(entry)!)(AccessTools.Field(entry.GetType(), "Data").GetValue(entry));
            if (Confirmed(first) != 3 || Confirmed(last) != 7)
                throw new Exception("Queued requests lost per-job values.");
        }
        finally { probe.UnpatchAll(probe.Id); JobIds.Clear(); RecordCalls = true; }
    }
}
