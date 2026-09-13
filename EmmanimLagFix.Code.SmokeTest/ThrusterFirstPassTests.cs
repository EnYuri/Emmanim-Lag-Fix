using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Halfling.Geometry;
using Halfling.Timing;
using HarmonyLib;

internal static class ThrusterFirstPassTests
{
    private delegate Vector3 Rotate(Vector3 value, bool zero, float sin, float cos, Vector3 factors);
    private delegate Vector3 Solve(object manager, Vector3 desired, Time interval, Vector3 factors, int mode, bool magic);
    private static readonly Dictionary<object, Vector3> Srfs = new(ReferenceEqualityComparer.Instance);
    private static Direction Flight;
    private static float Sink;
    private static bool SrfGetter(object __instance, ref Vector3 __result)
    { if (!Srfs.TryGetValue(__instance, out __result)) return true; return false; }
    private static bool FlightGetter(ref Direction __result) { __result = Flight; return false; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector3 OriginalSolve(object manager, Vector3 desired, Time interval, Vector3 factors, int activationRangeType, bool setMagicSRF) => throw new NotImplementedException();

    private static void Equal(float expected, float actual)
    {
        if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual))
            throw new Exception($"Thruster result changed: {expected} -> {actual}");
    }
    private static void Equal(Vector3 expected, Vector3 actual)
    { Equal(expected.X, actual.X); Equal(expected.Y, actual.Y); Equal(expected.Z, actual.Z); }

    internal static void Run(Assembly game, Assembly code)
    {
        var patch = code.GetType("EmmanimLagFix.Code.ThrusterFirstPassTransformPatch", true)!;
        if (AccessTools.Field(patch, "Applied").GetValue(null) is not true)
            throw new Exception("First-pass thruster transform did not match the game IL.");
        var withoutTorque = AccessTools.Method(patch, "WithoutTorque").CreateDelegate<Func<Vector3, Vector3>>();
        var managerType = game.GetType("Cosmoteer.Ships.Parts.Thrusters.ThrusterManager", true)!;
        var transform = managerType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name.StartsWith("<CalculateDesiredSRF>g___RotateAndFactorSRF|"));
        var contextType = transform.GetParameters()[2].ParameterType.GetElementType()!;
        var context = Expression.Variable(contextType);
        var value = Expression.Parameter(typeof(Vector3));
        var zero = Expression.Parameter(typeof(bool));
        var sin = Expression.Parameter(typeof(float));
        var cos = Expression.Parameter(typeof(float));
        var factors = Expression.Parameter(typeof(Vector3));
        var rotate = Expression.Lambda<Rotate>(Expression.Block([context],
            Expression.Assign(Expression.Field(context, "sinRot"), sin),
            Expression.Assign(Expression.Field(context, "cosRot"), cos),
            Expression.Assign(Expression.Field(context, "srfFactors"), factors),
            Expression.Call(transform, value, zero, context)), value, zero, sin, cos, factors).Compile();
        var random = new Random(12500);
        float Number() => (float)(random.NextDouble() * 200000 - 100000);
        for (var i = 0; i < 100000; i++)
        {
            var raw = new Vector3(Number(), Number(), Number());
            var f = new Vector3(Number(), Number(), Number());
            var s = Number(); var c = Number();
            var prepared = rotate(raw, false, s, c, f);
            var before = prepared;
            Equal(rotate(raw, true, s, c, f), withoutTorque(prepared));
            Equal(before, prepared);
        }
        foreach (var edge in new[] { 0f, -0f, float.Epsilon, float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var raw = new Vector3(edge, edge, edge);
            var f = new Vector3(0f, 1f, -1f);
            Equal(rotate(raw, true, 0f, 1f, f), withoutTorque(rotate(raw, false, 0f, 1f, f)));
        }

        var target = AccessTools.DeclaredMethod(managerType, "CalculateDesiredSRF");
        var modeType = target.GetParameters()[3].ParameterType;
        var probe = new Harmony("emmanim.smoke.thruster-first-pass-fixtures");
        var thrusterType = game.GetType("Cosmoteer.Ships.Parts.Thrusters.Thruster", true)!;
        var shipType = game.GetType("Cosmoteer.Ships.Ship", true)!;
        try
        {
            probe.Patch(AccessTools.Method(thrusterType, "GetSRF"), prefix: new HarmonyMethod(AccessTools.Method(typeof(ThrusterFirstPassTests), nameof(SrfGetter))));
            probe.Patch(AccessTools.PropertyGetter(shipType, "FlightDirection"), prefix: new HarmonyMethod(AccessTools.Method(typeof(ThrusterFirstPassTests), nameof(FlightGetter))));
            probe.CreateReversePatcher(target, new HarmonyMethod(AccessTools.Method(typeof(ThrusterFirstPassTests), nameof(OriginalSolve)))).Patch();
            var managerArg = Expression.Parameter(typeof(object));
            var desiredArg = Expression.Parameter(typeof(Vector3));
            var intervalArg = Expression.Parameter(typeof(Time));
            var factorsArg = Expression.Parameter(typeof(Vector3));
            var modeArg = Expression.Parameter(typeof(int));
            var magicArg = Expression.Parameter(typeof(bool));
            var solve = Expression.Lambda<Solve>(Expression.Call(Expression.Convert(managerArg, managerType), target,
                desiredArg, intervalArg, factorsArg, Expression.Convert(modeArg, modeType), magicArg),
                managerArg, desiredArg, intervalArg, factorsArg, modeArg, magicArg).Compile();
            var ship = RuntimeHelpers.GetUninitializedObject(shipType);
            var rulesType = AccessTools.PropertyGetter(shipType, "Rules").ReturnType;
            var rules = RuntimeHelpers.GetUninitializedObject(rulesType);
            AccessTools.Field(shipType, "<Rules>k__BackingField").SetValue(ship, rules);
            AccessTools.Field(rulesType, "MaxLinearMagicAcceleration").SetValue(rules, 0f);
            AccessTools.Field(rulesType, "MaxAngularMagicAcceleration").SetValue(rules, 0f);
            AccessTools.Field(rulesType, "MaxRampUpTimeForDeceleration").SetValue(rules, 0.5f);
            var manager = RuntimeHelpers.GetUninitializedObject(managerType);
            AccessTools.Field(managerType, "<Ship>k__BackingField").SetValue(manager, ship);
            AccessTools.Field(managerType, "_node").SetValue(manager, ship);
            var thrusters = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(thrusterType))!;
            AccessTools.Field(managerType, "_orderedSoftOpThrusters").SetValue(manager, thrusters);
            AccessTools.Field(managerType, "_orderedThrusters").SetValue(manager, thrusters);
            var uncommitted = AccessTools.Field(thrusterType, "<UncomittedActivationLevel>k__BackingField");
            foreach (var count in new[] { 0, 1, 7, 32, 128, 512 })
            {
                thrusters.Clear(); Srfs.Clear();
                for (var i = 0; i < count; i++)
                {
                    var thruster = RuntimeHelpers.GetUninitializedObject(thrusterType);
                    AccessTools.Field(thrusterType, "p_minActivation").SetValue(thruster, i % 3 == 0 ? -1f : 0f);
                    AccessTools.Field(thrusterType, "p_maxActivation").SetValue(thruster, 1f);
                    AccessTools.Field(thrusterType, "p_activationIncreaseTime").SetValue(thruster, (Time)0.7f);
                    AccessTools.Field(thrusterType, "<ActivationLevel>k__BackingField").SetValue(thruster, 0.1f);
                    AccessTools.Field(thrusterType, "<ActivationRecoveryLevel>k__BackingField").SetValue(thruster, 0.2f);
                    thrusters.Add(thruster); Srfs.Add(thruster, new Vector3(Number(), Number(), Number()));
                }
                foreach (var iterations in new[] { 1, 3, 5 })
                foreach (var mode in new[] { 0, 1, 2 })
                foreach (var magic in new[] { false, true })
                {
                    AccessTools.Field(rulesType, "ThrusterSolverIterations").SetValue(rules, iterations);
                    Flight = new Direction((float)(random.NextDouble() * 6.28));
                    var desired = count % 2 == 0 && iterations == 1 ? Vector3.Zero : new Vector3(Number(), Number(), Number());
                    var f = new Vector3(0.7f, 1f, 0.4f);
                    var expected = OriginalSolve(manager, desired, (Time)0.033f, f, mode, magic);
                    var activations = thrusters.Cast<object>().Select(t => (float)uncommitted.GetValue(t)!).ToArray();
                    var expectedMagic = (Vector3)AccessTools.Field(managerType, "_magicSRF").GetValue(manager)!;
                    foreach (var t in thrusters) uncommitted.SetValue(t, 0.75f);
                    var actual = solve(manager, desired, (Time)0.033f, f, mode, magic);
                    Equal(expected, actual);
                    Equal(expectedMagic, (Vector3)AccessTools.Field(managerType, "_magicSRF").GetValue(manager)!);
                    for (var i = 0; i < count; i++) Equal(activations[i], (float)uncommitted.GetValue(thrusters[i])!);
                }
            }
            // Benchmark the whole solver, including list preparation and sort,
            // on the same warmed fixture. Alternate order to reduce clock drift.
            AccessTools.Field(rulesType, "ThrusterSolverIterations").SetValue(rules, 3);
            Flight = new Direction(0.3f);
            var benchmarkDesired = new Vector3(180000f, -260000f, 320000f);
            var benchmarkFactors = new Vector3(0.7f, 1f, 0.4f);
            long Measure(Solve method, int calls)
            {
                var watch = Stopwatch.StartNew();
                var checksum = 0f;
                for (var i = 0; i < calls; i++)
                    checksum += method(manager, benchmarkDesired, (Time)0.033f, benchmarkFactors, 1, false).X;
                Sink = checksum;
                return watch.ElapsedTicks;
            }
            Measure(OriginalSolve, 1000); Measure(solve, 1000);
            var vanillaTimes = new List<long>(); var patchedTimes = new List<long>();
            for (var trial = 0; trial < 9; trial++)
            {
                if (trial % 2 == 0)
                { vanillaTimes.Add(Measure(OriginalSolve, 2500)); patchedTimes.Add(Measure(solve, 2500)); }
                else
                { patchedTimes.Add(Measure(solve, 2500)); vanillaTimes.Add(Measure(OriginalSolve, 2500)); }
            }
            vanillaTimes.Sort(); patchedTimes.Sort();
            Console.WriteLine($"Whole thruster solver benchmark (512 thrusters, 3 iterations, 2500 calls, median of 9): vanilla={vanillaTimes[4] * 1000d / Stopwatch.Frequency:F2}ms patched={patchedTimes[4] * 1000d / Stopwatch.Frequency:F2}ms ratio={(double)patchedTimes[4] / vanillaTimes[4]:F3}");
            Console.WriteLine("PASS: first-pass thruster transform, 100000 bitwise expression comparisons, full vanilla/patched solver equivalence across 0/1/7/32/128/512 thrusters, 1/3/5 iterations and all activation modes.");
        }
        finally { probe.UnpatchAll(probe.Id); Srfs.Clear(); }
    }
}
