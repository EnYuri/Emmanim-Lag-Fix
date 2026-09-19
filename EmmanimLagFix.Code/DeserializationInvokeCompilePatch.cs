using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Halfling.Serialization;
using Halfling.Serialization.Generic;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Replaces the generic serializer's per-object
/// <see cref="MethodBase.Invoke(object, object[])"/> deserialization
/// constructor calls with cached compiled delegates, eliminating the
/// interpreter stub and its allocations on every deserialized object.
///
/// <c>Halfling.Serialization.Base.BaseSerializer</c> already compiles
/// field/property accessors into <see cref="DynamicMethod"/> delegates, but
/// its constructor deserialization paths
/// (<c>SpecificConstructorDeserializationMethod</c> and
/// <c>GenericConstructorDeserializationMethod</c>) still invoke the
/// attributed constructor or static factory through
/// <c>ConstructorInfo.Invoke(object[])</c> /
/// <c>MethodBase.Invoke(object, object[])</c>. Each such call allocates a
/// <c>RuntimeMethodInfoStub</c> on the interpreter path. Allocation
/// sampling of a running session attributed roughly half of the game's
/// total allocation rate to that stub: the game serializes and
/// deserializes on the order of a thousand ships' worth of state in the
/// background, and every part, component and ID inside them is built by a
/// reflected constructor call.
///
/// Harmony cannot touch these methods at all: both bodies - and the
/// deserialization dispatch loop above them - place the invoke inside a
/// <c>catch … when</c> filter region, and Harmony clones the original
/// body into a wrapper dynamic method for every patch shape, so prefix,
/// postfix and transpiler all die at patch time with
/// <c>Incorrect code generation for exception block</c>. This patch
/// therefore goes below Harmony: it installs a MonoMod.Core
/// <c>DetourFactory</c> managed detour on each <c>TryDeserialize</c>, a
/// raw native jump that never copies the original body. The detour
/// target is a real static method baked with
/// <see cref="AssemblyBuilder"/> whose signature exactly matches the
/// source (the platform's ABI glue strips any generic-context argument
/// and adapts the return buffer), forwarding the call's arguments
/// directly to <see cref="Dispatch"/> without another array allocation.
///
/// <see cref="Dispatch"/> re-implements <c>TryDeserialize</c> exactly:
/// same flag check, same argument loop, same constructor invocation,
/// same <see cref="DeserializeAsNullException"/> outcome. The virtual
/// <c>TryGetConstructorArgument</c> and <c>CreateGenericSerialReader</c>
/// are invoked via compiled callvirt delegates so per-serializer
/// overrides still run; the constructor itself runs through a compiled
/// <see cref="DynamicMethod"/>. If any state cannot be built for an
/// instance the dispatcher falls back to a C# re-implementation of the
/// original body - reflection invokes included - so failure can only
/// degrade to vanilla semantics, never change them. The original method
/// is never re-entered: once detoured it no longer exists as callable
/// code.
///
/// Serialization output is untouched - this patch changes only how the
/// same constructor is invoked - so synced multiplayer data and the
/// integrity hash are unaffected. Escape hatch:
/// <c>deserialization-invoke-compile.txt</c> &lt;= 0 disables the
/// detours at load.
/// </summary>
public static class DeserializationInvokeCompilePatch
{
    /// <summary>Why the patch is inert, for diagnostics.</summary>
    internal static string? FailureReason;

    /// <summary>Whether the detours are active; the override file can turn them off.</summary>
    internal static readonly bool Enabled;

    /// <summary>Invocations served and delegates compiled, for diagnostics.</summary>
    internal static long Entries;
    internal static long Invocations;
    internal static long Compiled;

    /// <summary>Targets resolved and detours applied, for diagnostics.</summary>
    internal static int Resolved { get; private set; }
    internal static int Applied { get; private set; }

    /// <summary>Keeps detour objects alive for the process lifetime.</summary>
    private static readonly List<object> LiveDetours = new();

    /// <summary>Serializes argument-loop state so concurrent first-use is safe.</summary>
    private static readonly ConditionalWeakTable<object, InvokeState> States = new();

    private static int _applyRan;

    static DeserializationInvokeCompilePatch()
    {
        var enabled = true;
        try
        {
            var path = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
                "..",
                "deserialization-invoke-compile.txt"));
            if (File.Exists(path)
                && int.TryParse(
                    File.ReadAllText(path).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var configured)
                && configured <= 0)
            {
                enabled = false;
                FailureReason = "the deserialization-invoke-compile.txt override disables compiled constructor invocation";
            }
        }
        catch (Exception)
        {
            // An unreadable override must not change behaviour; stay enabled.
        }

        Enabled = enabled;
    }

    /// <summary>
    /// Installs the native detours. Idempotent; any setup failure is
    /// recorded in <see cref="FailureReason"/> and leaves the game on the
    /// vanilla path - this method must never throw into mod
    /// initialization.
    /// </summary>
    internal static void Apply()
    {
        if (Interlocked.Exchange(ref _applyRan, 1) != 0)
        {
            return;
        }
        if (!Enabled)
        {
            return;
        }

        try
        {
            ApplyCore();
        }
        catch (Exception ex)
        {
            FailureReason = "detour setup failed: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static void ApplyCore()
    {
        var factoryType = typeof(Harmony).Assembly.GetType("MonoMod.Core.DetourFactory")
            ?? throw new MissingMemberException("MonoMod.Core.DetourFactory not present");
        var factory = (factoryType
                .GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException("DetourFactory.Current"))
            .GetValue(null)!;
        var createDetour = factoryType
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m =>
                m.Name == "CreateDetour"
                && m.GetParameters() is { Length: 4 } ps
                && ps[1].ParameterType == typeof(MethodBase)
                && ps[2].ParameterType == typeof(MethodBase))
            ?? throw new MissingMemberException("DetourFactory.CreateDetour(IDetourFactory, MethodBase, MethodBase, bool)");

        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("EmmanimLagFix.DetourTargets"),
            AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule("DetourTargets");

        var applied = 0;
        var failures = 0;
        string? lastFailure = null;
        foreach (var target in ScanTargets())
        {
            try
            {
                var shim = EmitShim(module, (MethodInfo)target, applied);
                var detour = createDetour.Invoke(
                    null, new object?[] { factory, target, shim, true })!;
                LiveDetours.Add(detour);
                applied++;
            }
            catch (Exception ex)
            {
                failures++;
                lastFailure = ex.GetType().Name + ": " + ex.Message;
            }
        }

        Applied = applied;
        if (failures > 0)
        {
            FailureReason = $"{failures} detour failures, last: {lastFailure}";
        }
        else if (applied == 0)
        {
            FailureReason = "no BaseSerializer constructor deserialization methods resolved";
        }
    }

    /// <summary>
    /// Finds every closed <c>BaseSerializer&lt;…&gt;</c> instantiation in the
    /// loaded assemblies (ObjectText, ObjectBits, Binary, and any
    /// serializer the game itself defines) and yields the
    /// <c>TryDeserialize</c> of both constructor deserialization methods on
    /// each. Filter regions no longer matter - a native detour never
    /// copies the body.
    /// </summary>
    internal static IEnumerable<MethodBase> ScanTargets()
    {
        var resolved = 0;
        var seen = new HashSet<MethodBase>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var type in types)
            {
                for (var b = type.BaseType; b != null; b = b.BaseType)
                {
                    if (!b.IsGenericType
                        || b.ContainsGenericParameters
                        || b.Namespace != "Halfling.Serialization.Base"
                        || !b.Name.StartsWith("BaseSerializer`", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (var nested in b.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (nested.Name is not ("SpecificConstructorDeserializationMethod"
                            or "GenericConstructorDeserializationMethod"))
                        {
                            continue;
                        }

                        // GetNestedTypes hands back the nested type still
                        // parameterized over the enclosing generic's own
                        // parameters; close it over the constructed base's
                        // arguments so the detour receives a real method.
                        var closedNested = nested;
                        if (closedNested.ContainsGenericParameters)
                        {
                            try
                            {
                                closedNested = nested.MakeGenericType(b.GetGenericArguments());
                            }
                            catch (Exception)
                            {
                                continue;
                            }
                        }

                        var target = closedNested.GetMethod(
                            "TryDeserialize",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (target == null || target.ContainsGenericParameters || !seen.Add(target))
                        {
                            continue;
                        }

                        resolved++;
                        yield return target;
                    }
                }
            }
        }

        Resolved = resolved;
    }

    /// <summary>
    /// Bakes a static method whose signature exactly mirrors the source
    /// instance method - first parameter is the declaring type, then the
    /// source's own parameters in order. The body forwards those
    /// arguments directly to <see cref="Dispatch"/>, passing the out slot
    /// through. Being
    /// a real emitted method rather than a <see cref="DynamicMethod"/>
    /// lets the platform's ABI-fixup glue import it when the source
    /// needs a generic-context argument.
    /// </summary>
    private static MethodInfo EmitShim(ModuleBuilder module, MethodInfo source, int index)
    {
        var sourceParams = source.GetParameters();
        if (sourceParams.Length == 0
            || !sourceParams[sourceParams.Length - 1].ParameterType.IsByRef)
        {
            throw new NotSupportedException("TryDeserialize has no trailing out parameter");
        }

        var shimParams = new Type[sourceParams.Length + 1];
        shimParams[0] = source.DeclaringType!;
        for (var i = 0; i < sourceParams.Length; i++)
        {
            shimParams[i + 1] = sourceParams[i].ParameterType;
        }

        var type = module.DefineType(
            "DeserializationDetour" + index,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var method = type.DefineMethod(
            "Invoke",
            MethodAttributes.Public | MethodAttributes.Static,
            source.ReturnType,
            shimParams);
        var il = method.GetILGenerator();
        for (var i = 0; i < shimParams.Length; i++)
        {
            il.Emit(OpCodes.Ldarg, i);
            // Dispatch accepts object for the serializer/source/context
            // positions. Closed serializer implementations currently use
            // reference types, but boxing here keeps the shim valid for a
            // future value-type source or context.
            if (i is >= 1 and <= 3 && shimParams[i].IsValueType)
            {
                il.Emit(OpCodes.Box, shimParams[i]);
            }
        }

        il.Emit(OpCodes.Call,
            typeof(DeserializationInvokeCompilePatch).GetMethod(
                nameof(Dispatch), BindingFlags.Public | BindingFlags.Static)!);
        il.Emit(OpCodes.Ret);

        return type.CreateType()!.GetMethod(
            "Invoke", BindingFlags.Public | BindingFlags.Static)!;
    }

    /// <summary>
    /// The detour target's shared entry point. Re-implements
    /// <c>TryDeserialize</c> exactly, falling back to a C# copy of the
    /// original body when compiled state is unavailable.
    /// </summary>
    public static bool Dispatch(
        object self,
        object serializer,
        object source,
        object? context,
        Type type,
        ReadFlags flags,
        ProgressTracker? progressTracker,
        MemberInfo? member,
        out object? obj)
    {
        Interlocked.Increment(ref Entries);
        obj = null;
        if ((flags & ReadFlags.SkipConstructorDeserializer) != 0)
        {
            return false;
        }

        var state = StateFor(self);
        if (state == null)
        {
            return VanillaDeserialize(
                self, serializer, source, progressTracker, member, out obj);
        }

        Interlocked.Increment(ref Invocations);
        var parameters = state.Params;
        if (parameters.Length == 0)
        {
            obj = InvokeCompiled(state, null);
            return true;
        }

        var array = ArrayPool<object?>.Shared.Rent(parameters.Length);
        try
        {
            if (state.Specific)
            {
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (!state.GetArg!(
                            serializer,
                            parameters[i].ParameterType,
                            source,
                            progressTracker,
                            member,
                            array,
                            i))
                    {
                        throw new Exception("Internal error.");
                    }
                }
            }
            else
            {
                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameterType = parameters[i].ParameterType;
                    if (parameterType == typeof(GenericSerialReader))
                    {
                        array[i] = state.MakeReader!(serializer, source);
                    }
                    else if (parameterType == typeof(ProgressTracker))
                    {
                        array[i] = progressTracker;
                    }
                    else if (parameterType == typeof(MemberInfo))
                    {
                        array[i] = member;
                    }
                    else
                    {
                        throw new Exception("Internal error.");
                    }
                }
            }

            obj = InvokeCompiled(state, array);
            return true;
        }
        finally
        {
            Array.Clear(array, 0, parameters.Length);
            ArrayPool<object?>.Shared.Return(array);
        }
    }

    /// <summary>
    /// A pure-C# copy of the original <c>TryDeserialize</c> body, used
    /// when compiled state cannot be built: the detoured original no
    /// longer exists as callable code, so the fallback must re-create
    /// vanilla behaviour itself - same argument sources, same reflective
    /// invoke, same <c>catch … when</c> outcome. Cold path only.
    /// </summary>
    private static bool VanillaDeserialize(
        object self,
        object serializer,
        object source,
        ProgressTracker? progressTracker,
        MemberInfo? member,
        out object? obj)
    {
        var selfType = self.GetType();
        var method = (MethodBase)selfType
            .GetField("_method", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(self)!;
        var parameters = (ParameterInfo[])selfType
            .GetField("_params", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(self)!;
        var sourceType = selfType.GetMethod(
            "TryDeserialize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetParameters()[1].ParameterType;

        var array = new object?[parameters.Length];
        if (selfType.Name == "SpecificConstructorDeserializationMethod")
        {
            var getArg = AccessTools.Method(
                serializer.GetType(),
                "TryGetConstructorArgument",
                new[]
                {
                    typeof(Type), sourceType, typeof(ProgressTracker), typeof(MemberInfo),
                    typeof(object).MakeByRefType(),
                })!;
            for (var i = 0; i < parameters.Length; i++)
            {
                var callArgs = new object?[]
                    { parameters[i].ParameterType, source, progressTracker, member, null };
                if (InvokeUnwrapped(getArg, serializer, callArgs) is not true)
                {
                    throw new Exception("Internal error.");
                }
                array[i] = callArgs[4];
            }
        }
        else
        {
            var makeReader = AccessTools.Method(
                serializer.GetType(), "CreateGenericSerialReader", new[] { sourceType })!;
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameterType = parameters[i].ParameterType;
                if (parameterType == typeof(GenericSerialReader))
                {
                    array[i] = InvokeUnwrapped(
                        makeReader, serializer, new object?[] { source });
                }
                else if (parameterType == typeof(ProgressTracker))
                {
                    array[i] = progressTracker;
                }
                else if (parameterType == typeof(MemberInfo))
                {
                    array[i] = member;
                }
                else
                {
                    throw new Exception("Internal error.");
                }
            }
        }

        try
        {
            obj = method is ConstructorInfo constructor
                ? constructor.Invoke(array)
                : method.Invoke(null, array);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is DeserializeAsNullException)
        {
            obj = null;
        }
        return true;
    }

    /// <summary>
    /// Reflection is used only in the cold fallback to reach protected
    /// virtual serializer helpers. Those helpers are direct calls in
    /// vanilla, so remove reflection's artificial wrapper while retaining
    /// the original exception and stack.
    /// </summary>
    private static object? InvokeUnwrapped(
        MethodInfo method,
        object target,
        object?[] args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// Runs the compiled constructor delegate and reproduces vanilla's
    /// exception contract: a <see cref="DeserializeAsNullException"/> -
    /// raw from the compiled path or wrapped from the reflection
    /// fallback - deserializes to null, and every other callee exception
    /// escapes as a <see cref="TargetInvocationException"/> exactly as
    /// <c>MethodBase.Invoke</c> would have thrown it.
    /// </summary>
    private static object? InvokeCompiled(InvokeState state, object?[]? args)
    {
        try
        {
            return state.Invoke(args);
        }
        catch (Exception ex) when (IsDeserializeAsNull(ex))
        {
            return null;
        }
        catch (Exception ex) when (state.DirectInvoker)
        {
            // MethodBase.Invoke wraps every callee exception, including a
            // TargetInvocationException thrown by the callee itself.
            throw new TargetInvocationException(ex);
        }
    }

    private static bool IsDeserializeAsNull(Exception ex) =>
        ex is DeserializeAsNullException
        || (ex is TargetInvocationException && ex.InnerException is DeserializeAsNullException);

    /// <summary>
    /// Per-instance compiled state: the reflected constructor's invoker,
    /// its parameter list, and the argument filler matching the enclosing
    /// deserialization-method shape.
    /// </summary>
    private sealed class InvokeState
    {
        internal ParameterInfo[] Params = null!;
        internal Func<object?[]?, object?> Invoke = null!;
        internal Func<object, Type, object?, object?, object?, object?[], int, bool>? GetArg;
        internal Func<object, object, object>? MakeReader;
        internal bool Specific;
        internal bool DirectInvoker;
    }

    /// <summary>
    /// Builds (once per deserialization-method instance) the compiled
    /// constructor invoker and the argument filler matching which of the
    /// two method shapes the instance is.
    /// </summary>
    private static InvokeState? StateFor(object instance)
    {
        try
        {
            return States.GetValue(instance, CreateState);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static InvokeState CreateState(object instance)
    {
        var instanceType = instance.GetType();
        var method = (MethodBase)instanceType
            .GetField("_method", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(instance)!;
        var parameters = (ParameterInfo[])instanceType
            .GetField("_params", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(instance)!;
        var tryDeserialize = instanceType.GetMethod(
            "TryDeserialize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var serializerType = tryDeserialize.GetParameters()[0].ParameterType;
        var sourceType = tryDeserialize.GetParameters()[1].ParameterType;

        var specific = instanceType.Name == "SpecificConstructorDeserializationMethod";
        var invoker = CompileForState(method, out var directInvoker);
        return new InvokeState
        {
            Params = parameters,
            Invoke = invoker,
            Specific = specific,
            DirectInvoker = directInvoker,
            GetArg = specific ? CompileGetArg(serializerType, sourceType) : null,
            MakeReader = specific ? null : CompileMakeReader(serializerType, sourceType),
        };
    }

    /// <summary>
    /// Emits <c>(s, argType, source, pt, member, arr, i) =&gt;
    /// s.TryGetConstructorArgument(argType, source, pt, member, out
    /// arr[i])</c> as a callvirt so serializer overrides still dispatch.
    /// </summary>
    private static Func<object, Type, object?, object?, object?, object?[], int, bool>
        CompileGetArg(Type serializerType, Type sourceType)
    {
        // Declared protected on the serializer's BaseSerializer base, so the
        // hierarchy must be walked - GetMethod alone only surfaces it when a
        // serializer overrides it.
        var getArg = AccessTools.Method(
            serializerType,
            "TryGetConstructorArgument",
            new[]
            {
                typeof(Type), sourceType, typeof(ProgressTracker), typeof(MemberInfo),
                typeof(object).MakeByRefType(),
            }) ?? throw new MissingMethodException(serializerType.FullName, "TryGetConstructorArgument");

        var dynamic = new DynamicMethod(
            "DeserializationGetArg",
            typeof(bool),
            new[]
            {
                typeof(object), typeof(Type), typeof(object), typeof(object),
                typeof(object), typeof(object[]), typeof(int),
            },
            serializerType.Module,
            skipVisibility: true);
        var il = dynamic.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, serializerType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, sourceType);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldarg, 4);
        il.Emit(OpCodes.Ldarg, 5);
        il.Emit(OpCodes.Ldarg, 6);
        il.Emit(OpCodes.Ldelema, typeof(object));
        il.Emit(OpCodes.Callvirt, getArg);
        il.Emit(OpCodes.Ret);
        return (Func<object, Type, object?, object?, object?, object?[], int, bool>)dynamic
            .CreateDelegate(typeof(Func<object, Type, object?, object?, object?, object?[], int, bool>));
    }

    /// <summary>
    /// Emits <c>(s, source) =&gt; s.CreateGenericSerialReader(source)</c>.
    /// </summary>
    private static Func<object, object, object> CompileMakeReader(Type serializerType, Type sourceType)
    {
        var makeReader = AccessTools.Method(
            serializerType,
            "CreateGenericSerialReader",
            new[] { sourceType }) ?? throw new MissingMethodException(serializerType.FullName, "CreateGenericSerialReader");

        var dynamic = new DynamicMethod(
            "DeserializationMakeReader",
            typeof(object),
            new[] { typeof(object), typeof(object) },
            serializerType.Module,
            skipVisibility: true);
        var il = dynamic.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, serializerType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, sourceType);
        il.Emit(OpCodes.Callvirt, makeReader);
        il.Emit(OpCodes.Ret);
        return (Func<object, object, object>)dynamic
            .CreateDelegate(typeof(Func<object, object, object>));
    }

    /// <summary>
    /// Emits <c>args =&gt; new T((P0)args[0], …)</c> (or the equivalent
    /// static-method call) as a <see cref="DynamicMethod"/>, mirroring the
    /// serializer's own <c>MakeGetValueFunc</c>/<c>MakeSetValueAction</c>
    /// pattern. Falls back to plain reflection when the signature cannot
    /// be emitted - a fallback can only be as slow as vanilla, never
    /// wrong.
    /// </summary>
    internal static Func<object?[]?, object?> Compile(MethodBase method)
    {
        return CompileForState(method, out _);
    }

    private static Func<object?[]?, object?> CompileForState(
        MethodBase method,
        out bool directInvoker)
    {
        try
        {
            var compiled = EmitInvoker(method);
            Interlocked.Increment(ref Compiled);
            directInvoker = true;
            return compiled;
        }
        catch (Exception)
        {
            directInvoker = false;
            return method is ConstructorInfo ctor
                ? args => ctor.Invoke(args)
                : args => method.Invoke(null, args);
        }
    }

    private static Func<object?[]?, object?> EmitInvoker(MethodBase method)
    {
        var parameters = method.GetParameters();
        if (parameters.Any(p =>
                p.ParameterType.IsByRef
                || p.ParameterType.IsPointer
                || p.ParameterType.IsByRefLike)
            || method.ContainsGenericParameters)
        {
            throw new NotSupportedException("signature cannot be compiled");
        }

        var dynamic = new DynamicMethod(
            "DeserializationInvoke_" + method.Name,
            typeof(object),
            new[] { typeof(object[]) },
            method.DeclaringType?.Module ?? typeof(DeserializationInvokeCompilePatch).Module,
            skipVisibility: true);
        var il = dynamic.GetILGenerator();
        for (var i = 0; i < parameters.Length; i++)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldelem_Ref);
            var parameterType = parameters[i].ParameterType;
            il.Emit(parameterType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, parameterType);
        }

        Type returnType;
        if (method is ConstructorInfo ctor)
        {
            il.Emit(OpCodes.Newobj, ctor);
            returnType = ctor.DeclaringType!;
        }
        else
        {
            il.Emit(OpCodes.Call, (MethodInfo)method);
            returnType = ((MethodInfo)method).ReturnType;
        }

        if (returnType.IsValueType)
        {
            il.Emit(OpCodes.Box, returnType);
        }

        il.Emit(OpCodes.Ret);
        return (Func<object?[]?, object?>)dynamic.CreateDelegate(typeof(Func<object?[]?, object?>));
    }

    internal static string Counters() =>
        Enabled
        ? Volatile.Read(ref Entries).ToString(CultureInfo.InvariantCulture)
        + "/" + Volatile.Read(ref Invocations).ToString(CultureInfo.InvariantCulture)
        + "/" + Volatile.Read(ref Compiled).ToString(CultureInfo.InvariantCulture)
        + " (detours " + Applied + "/" + Resolved + ")"
        : "off";
}
