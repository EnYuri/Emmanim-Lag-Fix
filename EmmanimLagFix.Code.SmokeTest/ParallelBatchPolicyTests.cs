using System.Reflection;
using System.Runtime.CompilerServices;
using EmmanimLagFix.Code;
using Halfling.Pooling;
using Halfling.Scene2D;
using HarmonyLib;

internal static class ParallelBatchPolicyTests
{
    internal static void Run(Assembly gameAssembly)
    {
        var policy = typeof(EntryPoint).Assembly.GetType("EmmanimLagFix.Code.FastParallelBatchSize", true)!;
        var refine = AccessTools.Method(policy, "RefineForWorkload");
        int? Choose(object? data, int from = 0, int to = 607, bool copy = false, int? explicitBatch = null) =>
            (int?)refine.Invoke(null, new object?[] { from, to, copy, explicitBatch, 7, 32, data });
        var simType = gameAssembly.GetType("Cosmoteer.Simulation.SimRoot", true)!;
        foreach (var sceneInterface in new[] { typeof(IUpdateableSceneObject), typeof(IFixedUpdateableSceneObject) })
        {
            var tupleType = typeof(ValueTuple<,>).MakeGenericType(simType, sceneInterface.MakeArrayType());
            var wrapperType = typeof(TempWrapper<>).MakeGenericType(tupleType);
            var wrapper = RuntimeHelpers.GetUninitializedObject(wrapperType);
            if (Choose(wrapper) != 2) throw new InvalidOperationException("Scene bucket lost fine partitioning.");
            if (Choose(wrapper, 0, 607, explicitBatch: 4) != null || Choose(wrapper, copy: true) != null
                || Choose(wrapper, 0, 0) != null || Choose(wrapper, 0, 16) != null)
                throw new InvalidOperationException("Scene batch policy overrode a guarded vanilla case.");
        }
        foreach (var other in new object?[]
        {
            null, new object(), RuntimeHelpers.GetUninitializedObject(typeof(TempWrapper<(object, IUpdateableSceneObject[])>)),
        })
            if (Choose(other) != null) throw new InvalidOperationException("Unknown work was split into finer batches.");
        Console.WriteLine("PASS: parallel batch policy retains scene-bucket balancing and vanilla sizing for other workloads.");
    }
}
