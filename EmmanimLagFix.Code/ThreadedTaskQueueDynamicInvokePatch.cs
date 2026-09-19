using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Halfling.Performance;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Replaces ThreadedTaskQueue's per-task Delegate.DynamicInvoke with a
/// delegate-type-specific direct Invoke call. DynamicInvoke creates a
/// RuntimeMethodInfoStub on every queued operation; stasis/background ship
/// generation makes that the largest sustained allocation source.
///
/// The queue already accepts only Action&lt;ThreadedTaskQueue&gt; and
/// Func&lt;ThreadedTaskQueue,T&gt;. A DynamicMethod compiled once per closed
/// delegate type casts the callback and calls its Invoke method directly.
/// Callee exceptions are wrapped in TargetInvocationException exactly as
/// DynamicInvoke would wrap them, preserving WorkerThread's completion-task
/// exception contract. Unsupported shapes fall back to DynamicInvoke.
/// </summary>
[HarmonyPatch]
internal static class ThreadedTaskQueueDynamicInvokePatch
{
    private delegate object? QueueInvoker(Delegate callback, object?[]? args);

    private static readonly ConcurrentDictionary<Type, QueueInvoker> Invokers = new();

    internal static bool Applied;
    internal static long Invocations;
    internal static long Compiled;

    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(typeof(ThreadedTaskQueue), "WorkerThread")
        ?? throw new MissingMethodException(typeof(ThreadedTaskQueue).FullName, "WorkerThread");

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var dynamicInvoke = AccessTools.DeclaredMethod(
            typeof(Delegate),
            nameof(Delegate.DynamicInvoke),
            new[] { typeof(object[]) });
        var replacement = AccessTools.DeclaredMethod(
            typeof(ThreadedTaskQueueDynamicInvokePatch),
            nameof(Invoke));
        var replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(dynamicInvoke))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replaced++;
            }
            yield return instruction;
        }

        Applied = replaced == 1;
        if (replaced != 1)
        {
            throw new InvalidOperationException(
                $"Expected one Delegate.DynamicInvoke call in ThreadedTaskQueue.WorkerThread, found {replaced}.");
        }
    }

    private static object? Invoke(Delegate callback, object?[]? args)
    {
        QueueInvoker invoker;
        try
        {
            invoker = Invokers.GetOrAdd(callback.GetType(), Compile);
        }
        catch (Exception)
        {
            return callback.DynamicInvoke(args);
        }

        Interlocked.Increment(ref Invocations);
        try
        {
            return invoker(callback, args);
        }
        catch (Exception ex)
        {
            // DynamicInvoke wraps every callee exception, including a
            // TargetInvocationException thrown by the callee itself.
            throw new TargetInvocationException(ex);
        }
    }

    private static QueueInvoker Compile(Type delegateType)
    {
        var invoke = delegateType.GetMethod(
            "Invoke",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(delegateType.FullName, "Invoke");
        var parameters = invoke.GetParameters();
        if (parameters.Length != 1
            || parameters[0].ParameterType != typeof(ThreadedTaskQueue)
            || invoke.ReturnType.IsByRef
            || invoke.ReturnType.IsByRefLike)
        {
            throw new NotSupportedException(
                $"Unsupported ThreadedTaskQueue callback shape: {delegateType}.");
        }

        var dynamic = new DynamicMethod(
            "ThreadedTaskQueueInvoke_" + delegateType.Name,
            typeof(object),
            new[] { typeof(Delegate), typeof(object[]) },
            typeof(ThreadedTaskQueueDynamicInvokePatch).Module,
            skipVisibility: true);
        var il = dynamic.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, delegateType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Castclass, typeof(ThreadedTaskQueue));
        il.Emit(OpCodes.Callvirt, invoke);
        if (invoke.ReturnType == typeof(void))
        {
            il.Emit(OpCodes.Ldnull);
        }
        else if (invoke.ReturnType.IsValueType)
        {
            il.Emit(OpCodes.Box, invoke.ReturnType);
        }
        il.Emit(OpCodes.Ret);

        Interlocked.Increment(ref Compiled);
        return (QueueInvoker)dynamic.CreateDelegate(typeof(QueueInvoker));
    }

    internal static string Counters() =>
        Volatile.Read(ref Invocations) + "/" + Volatile.Read(ref Compiled);
}
