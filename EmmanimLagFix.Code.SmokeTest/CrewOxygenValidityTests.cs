using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

internal static class CrewOxygenValidityTests
{
    private static object Rules = null!;
    private static object GameRules = null!;
    private static object? Home;
    private delegate bool Check(object job, object crew);
    private delegate bool PrefixCheck(object crew, ref bool result);
    private static bool RulesGetter(ref object __result) { __result = Rules; return false; }
    private static bool GameRulesGetter(ref object __result) { __result = GameRules; return false; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Original(object job, object crew) => throw new NotImplementedException();

    internal static void Run(Assembly game, Assembly code)
    {
        var crewType = game.GetType("Cosmoteer.Crew.CrewSoul", true)!;
        var spaceType = game.GetType("Cosmoteer.Crew.SpaceCrew", true)!;
        var jobType = game.GetType("Cosmoteer.Ships.Crew.Jobs.RefillO2Job", true)!;
        var patchType = code.GetType("EmmanimLagFix.Code.CrewOxygenValidityAllocationPatch", true)!;
        var target = AccessTools.DeclaredMethod(jobType, "IsValidFor", [crewType]);
        if (Harmony.GetPatchInfo(target)?.Prefixes.Any(p => p.PatchMethod.DeclaringType == patchType) != true)
            throw new Exception("Crew oxygen validity prefix missing.");
        var jobArg = Expression.Parameter(typeof(object));
        var crewArg = Expression.Parameter(typeof(object));
        var check = Expression.Lambda<Check>(Expression.Call(Expression.Convert(jobArg, jobType),
            target, Expression.Convert(crewArg, crewType)), jobArg, crewArg).Compile();
        var resultArg = Expression.Parameter(typeof(bool).MakeByRefType());
        var prefix = Expression.Lambda<PrefixCheck>(Expression.Call(AccessTools.Method(patchType, "Prefix"),
            Expression.Convert(crewArg, crewType), resultArg), crewArg, resultArg).Compile();
        var probe = new Harmony("emmanim.smoke.oxygen-fixtures");
        var ruleType = AccessTools.PropertyGetter(crewType, "Rules").ReturnType;
        Rules = RuntimeHelpers.GetUninitializedObject(ruleType);
        AccessTools.Field(ruleType, "OxygenWarningLevel").SetValue(Rules, 0.25f);
        var gameRulesGetter = AccessTools.PropertyGetter(game.GetType("Cosmoteer.GameApp", true)!, "Rules");
        GameRules = RuntimeHelpers.GetUninitializedObject(gameRulesGetter.ReturnType);
        AccessTools.Field(gameRulesGetter.ReturnType, "Crew").SetValue(GameRules, Rules);
        try
        {
            probe.Patch(AccessTools.PropertyGetter(crewType, "Rules"), prefix: new HarmonyMethod(AccessTools.Method(typeof(CrewOxygenValidityTests), nameof(RulesGetter))));
            probe.Patch(gameRulesGetter, prefix: new HarmonyMethod(AccessTools.Method(typeof(CrewOxygenValidityTests), nameof(GameRulesGetter))));
            probe.CreateReversePatcher(target, new HarmonyMethod(AccessTools.Method(typeof(CrewOxygenValidityTests), nameof(Original)))).Patch();
            var crew = RuntimeHelpers.GetUninitializedObject(crewType);
            var job = RuntimeHelpers.GetUninitializedObject(jobType);
            Home = null;
            if (Original(job, crew) || check(job, crew)) throw new Exception("Onboard oxygen validity changed.");
            var space = RuntimeHelpers.GetUninitializedObject(spaceType);
            AccessTools.PropertySetter(crewType, "Form").Invoke(crew, [space]);
            Home = RuntimeHelpers.GetUninitializedObject(game.GetType("Cosmoteer.Ships.Ship", true)!);
            AccessTools.Field(crewType, "<HomeShip>k__BackingField").SetValue(crew, Home);
            var airlockType = game.GetType("Cosmoteer.Ships.Parts.Crew.Airlock", true)!;
            var registry = AccessTools.Field(airlockType, "s_shipAirlocks").GetValue(null)!;
            var airlocks = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(airlockType))!;
            airlocks.Add(RuntimeHelpers.GetUninitializedObject(airlockType));
            AccessTools.Method(registry.GetType(), "Add").Invoke(registry, [Home, airlocks]);
            foreach (var oxygen in new[] { -1f, 0f, 0.249f, 0.25f, 0.251f, 1f, float.NaN, float.PositiveInfinity })
            {
                AccessTools.PropertySetter(spaceType, "Oxygen").Invoke(space, [oxygen]);
                if (Original(job, crew) != check(job, crew)) throw new Exception($"Oxygen validity changed at {oxygen}.");
            }
            AccessTools.PropertySetter(spaceType, "Oxygen").Invoke(space, [0.1f]);
            airlocks.Clear();
            foreach (var home in new[] { Home, null })
            {
                Home = home;
                AccessTools.Field(crewType, "<HomeShip>k__BackingField").SetValue(crew, home);
                var result = false;
                if (!prefix(crew, ref result)) throw new Exception("Airlock spatial-search fallback was suppressed.");
            }
            AccessTools.PropertySetter(crewType, "Form").Invoke(crew, [null]);
            for (var i = 0; i < 10000; i++) { Original(job, crew); check(job, crew); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100000; i++) Original(job, crew);
            var vanilla = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100000; i++) check(job, crew);
            var patched = GC.GetAllocatedBytesForCurrentThread() - before;
            if (vanilla == 0 || patched != 0) throw new Exception($"Oxygen allocation regression: {vanilla} -> {patched}.");
            Console.WriteLine($"PASS: crew oxygen validity equivalence, spatial-search fallback, onboard allocations {vanilla} -> {patched} bytes / 100000 checks.");
        }
        finally { probe.UnpatchAll(probe.Id); Home = null; Rules = null!; GameRules = null!; }
    }
}
