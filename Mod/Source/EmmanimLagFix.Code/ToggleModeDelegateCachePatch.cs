using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Ships.Blueprints.Logic.Values;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// <c>BlueprintPartStatProvider.UpdateOperational</c> and the equivalent
/// method on the internal blueprint network-port base class both evaluate
/// their operational toggle by constructing a brand-new
/// <c>Func&lt;ID&lt;PartToggleGuiRules&gt;, int?&gt;</c> bound to the
/// instance's own <c>GetToggleMode</c> method every time they run — once per
/// stat provider / network port, every simulation tick, for every part on the
/// ship. A ten-second allocation trace attributed roughly 815 MiB of managed
/// allocation to exactly this delegate type
/// (see Dev/emmanim_lag_fix_code/MEMORY_DIAGNOSTICS.md).
///
/// <c>GetToggleMode</c> only reads the instance's own part/ship state, so the
/// bound delegate is safe to reuse for the duration of the synchronous toggle
/// query. Both
/// methods have the identical
/// <c>ldarg.0; ldftn GetToggleMode; newobj Func`2::.ctor</c> IL shape, so a
/// narrowly-scoped transpiler splices in a thread-local reusable delegate
/// instead of the allocation. Keeping one mutable delegate target per thread
/// is important: an earlier per-instance <c>ConditionalWeakTable</c> version
/// removed the allocation storm but accumulated millions of dependent handles
/// while blueprint components were repeatedly reconstructed.
/// </summary>
internal static class ToggleModeDelegateCachePatch
{
    private static readonly Type ToggleModeDelegateType = typeof(Func<ID<PartToggleGuiRules>, int?>);

    private static readonly ConstructorInfo DelegateConstructor = AccessTools.Constructor(
        ToggleModeDelegateType,
        new[] { typeof(object), typeof(IntPtr) })
        ?? throw new MissingMethodException(ToggleModeDelegateType.FullName, ".ctor(object, IntPtr)");

    /// <summary>
    /// Finds the single <c>ldarg.0; ldftn getToggleMode; newobj Func`2::.ctor</c>
    /// sequence in <paramref name="instructions"/> and replaces it with
    /// <c>ldarg.0; call getOrCreateDelegate</c>, which pushes the same cached
    /// delegate instead of allocating a new one. Throws if the shape does not
    /// match exactly once, so a future game update that changes this code
    /// disables the optimization instead of silently doing nothing or patching
    /// the wrong site.
    /// </summary>
    internal static IEnumerable<CodeInstruction> ReplaceDelegateConstruction(
        IEnumerable<CodeInstruction> instructions,
        MethodInfo getToggleMode,
        MethodInfo getOrCreateDelegate)
    {
        var codes = new List<CodeInstruction>(instructions);
        var replaced = 0;

        for (var i = 0; i + 2 < codes.Count; i++)
        {
            if (codes[i].opcode != OpCodes.Ldarg_0
                || codes[i + 1].opcode != OpCodes.Ldftn
                || codes[i + 1].operand is not MethodInfo ldftnTarget
                || !ldftnTarget.Equals(getToggleMode)
                || codes[i + 2].opcode != OpCodes.Newobj
                || codes[i + 2].operand is not ConstructorInfo ctor
                || !ctor.Equals(DelegateConstructor))
            {
                continue;
            }

            codes[i + 1] = new CodeInstruction(OpCodes.Call, getOrCreateDelegate);
            codes.RemoveAt(i + 2);
            replaced++;
        }

        if (replaced != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one {ToggleModeDelegateType.Name} construction site bound to " +
                $"{getToggleMode.DeclaringType}.{getToggleMode.Name}, found {replaced}. " +
                "The game code shape has changed; skipping the delegate cache patch.");
        }

        return codes;
    }

    /// <summary>
    /// Owns one callback delegate and changes only the instance to which the
    /// callback forwards. <c>IsBlueprintToggleOn</c> consumes the callback
    /// synchronously, so a thread-local holder is sufficient and retains at
    /// most one component per participating thread.
    /// </summary>
    internal sealed class ThreadLocalDelegate<TInstance> where TInstance : class
    {
        private readonly Func<TInstance, ID<PartToggleGuiRules>, int?> _openDelegate;
        private readonly Func<ID<PartToggleGuiRules>, int?> _callback;
        private TInstance? _instance;

        internal ThreadLocalDelegate(Func<TInstance, ID<PartToggleGuiRules>, int?> openDelegate)
        {
            _openDelegate = openDelegate;
            _callback = Invoke;
        }

        internal Func<ID<PartToggleGuiRules>, int?> Bind(TInstance instance)
        {
            _instance = instance;
            return _callback;
        }

        private int? Invoke(ID<PartToggleGuiRules> toggle)
        {
            return _openDelegate(
                _instance ?? throw new InvalidOperationException("No toggle-mode delegate target is bound."),
                toggle);
        }
    }

    internal static Func<object, ID<PartToggleGuiRules>, int?> CreateOpenObjectDelegate(
        Type instanceType,
        MethodInfo method)
    {
        var dynamicMethod = new DynamicMethod(
            $"EmmanimLagFix_{instanceType.Name}_{method.Name}",
            typeof(int?),
            new[] { typeof(object), typeof(ID<PartToggleGuiRules>) },
            typeof(ToggleModeDelegateCachePatch).Module,
            skipVisibility: true);
        var il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        return (Func<object, ID<PartToggleGuiRules>, int?>)dynamicMethod.CreateDelegate(
            typeof(Func<object, ID<PartToggleGuiRules>, int?>));
    }
}

/// <summary>See <see cref="ToggleModeDelegateCachePatch"/>.</summary>
[HarmonyPatch(typeof(BlueprintPartStatProvider), "UpdateOperational")]
internal static class BlueprintPartStatProviderToggleModeDelegateCachePatch
{
    private static readonly MethodInfo GetToggleMode = AccessTools.Method(
        typeof(BlueprintPartStatProvider), "GetToggleMode")
        ?? throw new MissingMethodException(typeof(BlueprintPartStatProvider).FullName, "GetToggleMode");

    private static readonly Func<BlueprintPartStatProvider, ID<PartToggleGuiRules>, int?> OpenGetToggleMode =
        (Func<BlueprintPartStatProvider, ID<PartToggleGuiRules>, int?>)GetToggleMode.CreateDelegate(
            typeof(Func<BlueprintPartStatProvider, ID<PartToggleGuiRules>, int?>));

    [ThreadStatic]
    private static ToggleModeDelegateCachePatch.ThreadLocalDelegate<BlueprintPartStatProvider>? _threadDelegate;

    private static Func<ID<PartToggleGuiRules>, int?> GetOrCreateDelegate(BlueprintPartStatProvider instance)
    {
        return (_threadDelegate ??= new(OpenGetToggleMode)).Bind(instance);
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return ToggleModeDelegateCachePatch.ReplaceDelegateConstruction(
            instructions,
            GetToggleMode,
            AccessTools.Method(
                typeof(BlueprintPartStatProviderToggleModeDelegateCachePatch), nameof(GetOrCreateDelegate)));
    }
}

/// <summary>See <see cref="ToggleModeDelegateCachePatch"/>.</summary>
[HarmonyPatch]
internal static class BlueprintNetworkPortToggleModeDelegateCachePatch
{
    private static readonly Type PortType = AccessTools.TypeByName(
        "Cosmoteer.Source.Ships.Blueprints.BaseBlueprintPartNetworkPort")
        ?? throw new TypeLoadException("BaseBlueprintPartNetworkPort was not found.");

    private static readonly MethodInfo GetToggleMode = AccessTools.Method(PortType, "GetToggleMode")
        ?? throw new MissingMethodException(PortType.FullName, "GetToggleMode");

    private static readonly Func<object, ID<PartToggleGuiRules>, int?> OpenGetToggleMode =
        ToggleModeDelegateCachePatch.CreateOpenObjectDelegate(PortType, GetToggleMode);

    [ThreadStatic]
    private static ToggleModeDelegateCachePatch.ThreadLocalDelegate<object>? _threadDelegate;

    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(PortType, "UpdateOperational")
            ?? throw new MissingMethodException(PortType.FullName, "UpdateOperational");
    }

    private static Func<ID<PartToggleGuiRules>, int?> GetOrCreateDelegate(object instance)
    {
        return (_threadDelegate ??= new(OpenGetToggleMode)).Bind(instance);
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return ToggleModeDelegateCachePatch.ReplaceDelegateConstruction(
            instructions,
            GetToggleMode,
            AccessTools.Method(
                typeof(BlueprintNetworkPortToggleModeDelegateCachePatch), nameof(GetOrCreateDelegate)));
    }
}
