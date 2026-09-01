using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Removes the closure allocated on every resource-ID comparison.
///
/// Vanilla <c>Cosmoteer.Resources.ResourceIDComparer</c> is:
///
/// <code>
/// public int Compare(ID&lt;ResourceRules&gt; x, ID&lt;ResourceRules&gt; y)
/// {
///     return _GetIndex(x).CompareTo(_GetIndex(y));
///     static int _GetIndex(ID&lt;ResourceRules&gt; id)
///     {
///         if (!s_idIndexes.TryGetValue(id, out var value))
///             s_idIndexes.TryAdd(id, value = GameApp.Rules.Resources.FindIndex(rr => rr.ID == id));
///         return value;
///     }
/// }
/// </code>
///
/// It reads as cached and is not. <c>rr =&gt; rr.ID == id</c> captures the
/// parameter, so Roslyn emits a display class — present in the assembly as
/// <c>ResourceIDComparer.&lt;&gt;c__DisplayClass3_0</c> — and constructs it in
/// the method prologue rather than inside the <c>if</c>. So every call
/// allocates, at a 100% cache hit rate.
///
/// A 10-second allocation trace attributed 55.3% of all process allocation to
/// this one method. It is reached twice per <c>SortedDictionary</c> comparison,
/// O(log n) times per part, over every part, on every input frame, because
/// <c>BuildToolbox.OnBlueprintModeUpdatingUIState</c> re-aggregates the whole
/// blueprint's cost through <c>ShipUpdateInfo.GetTotalPhysicalCost</c>. That is
/// why a *paused* game still allocated 300--365 MiB/s and ran 2,300 Gen0
/// collections per minute, which sixteen FastParallel threads must rendezvous
/// at.
///
/// <c>ID&lt;T&gt;</c> is a readonly struct implementing
/// <c>IEquatable&lt;ID&lt;T&gt;&gt;</c>, so nothing here is boxing and the
/// vanilla <c>ConcurrentDictionary</c> really is hitting. The dictionary is not
/// the problem and must not be "fixed".
///
/// The parameter type is internal and generic, so a prefix cannot name it in
/// C#. A transpiler can: it replaces the body with a call to a generic helper
/// whose type argument is taken from the original method's own signature at
/// patch time. On a cache miss the helper calls vanilla <c>_GetIndex</c>
/// through reflection, so the value — including a <c>-1</c> for an unknown ID,
/// which vanilla also caches — is by construction identical, and vanilla's own
/// cache is populated too. A miss happens at most once per resource ID for the
/// life of the process, so the boxing on that path is irrelevant.
///
/// Returned values are bit-for-bit vanilla, so ordering, <c>AddCount</c>
/// aggregation and lockstep state are untouched. If any shape check fails the
/// original instructions are returned unchanged and vanilla behaviour stands.
/// </summary>
[HarmonyPatch]
internal static class ResourceIdComparerAllocationPatch
{
    /// <summary>Vanilla's local function, invoked only when our cache misses.</summary>
    private static MethodInfo? _vanillaGetIndex;

    private static readonly Type ComparerType =
        AccessTools.TypeByName("Cosmoteer.Resources.ResourceIDComparer")
        ?? throw new TypeLoadException("Cosmoteer.Resources.ResourceIDComparer was not found.");

    /// <summary>One cache per ID type; the key is a struct with IEquatable, so lookups do not box.</summary>
    private static class Cache<T> where T : notnull
    {
        internal static readonly ConcurrentDictionary<T, int> Indexes = new();
    }

    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(ComparerType, "Compare")
        ?? throw new MissingMethodException(ComparerType.FullName, "Compare");

    /// <summary>
    /// Resolves the compiler-generated local function by shape rather than by
    /// its mangled name (<c>&lt;Compare&gt;g___GetIndex|3_0</c>), which is not
    /// a stable contract across builds.
    /// </summary>
    private static MethodInfo? FindVanillaGetIndex(Type idType) =>
        ComparerType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m =>
                m.Name.Contains("GetIndex")
                && m.ReturnType == typeof(int)
                && m.GetParameters() is { Length: 1 } p
                && p[0].ParameterType == idType);

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var parameters = original.GetParameters();

        if (original.IsStatic
            || parameters.Length != 2
            || parameters[0].ParameterType != parameters[1].ParameterType
            || original is not MethodInfo { ReturnType: var ret } || ret != typeof(int))
        {
            Logger.Log(
                "[EmmanimLagFix] ResourceIDComparer.Compare has an unexpected shape; "
                + "leaving it at vanilla behaviour.");
            return instructions;
        }

        var idType = parameters[0].ParameterType;
        _vanillaGetIndex = FindVanillaGetIndex(idType);
        if (_vanillaGetIndex == null)
        {
            Logger.Log(
                "[EmmanimLagFix] ResourceIDComparer's index local function was not found; "
                + "leaving Compare at vanilla behaviour.");
            return instructions;
        }

        var helper = AccessTools
            .DeclaredMethod(typeof(ResourceIdComparerAllocationPatch), nameof(CompareIds))!
            .MakeGenericMethod(idType);

        // arg0 is the comparer instance, which the body never uses.
        return
        [
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Ldarg_2),
            new CodeInstruction(OpCodes.Call, helper),
            new CodeInstruction(OpCodes.Ret),
        ];
    }

    /// <summary>
    /// Vanilla's comparison, without the per-call closure. int.CompareTo(int)
    /// is not boxing, so a cache hit allocates nothing at all.
    /// </summary>
    private static int CompareIds<T>(T x, T y) where T : notnull =>
        GetIndex(x).CompareTo(GetIndex(y));

    private static int GetIndex<T>(T id) where T : notnull
    {
        if (Cache<T>.Indexes.TryGetValue(id, out var index))
        {
            return index;
        }

        // Miss: ask vanilla, so the answer is identical by construction and its
        // own cache is filled too. At most once per resource ID per process.
        index = (int)_vanillaGetIndex!.Invoke(null, [id])!;
        Cache<T>.Indexes.TryAdd(id, index);
        return index;
    }
}
