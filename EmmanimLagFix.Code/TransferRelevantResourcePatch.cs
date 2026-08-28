using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Game.Gui.Resources;
using Cosmoteer.Modes.Career.Comms;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Vanilla creates one transfer widget for every stackable resource defined by
/// every loaded mod. Most of those rows are immediately hidden because neither
/// ship owns that resource. Snapshot the resource types that can actually be
/// transferred when the window opens, then let the worker build only those
/// rows. Transfer execution and resource accounting remain vanilla.
/// </summary>
[HarmonyPatch]
internal static class TransferRelevantResourceSnapshotPatch
{
    private static readonly ConditionalWeakTable<object, Snapshot> Snapshots = new();

    private sealed class Snapshot
    {
        public readonly HashSet<ID<ResourceRules>> ResourceTypes = new();
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return typeof(CrewAndResourceTransferWindow)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        yield return typeof(CommTradeTab)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
    }

    private static void Prefix(object __instance, object[] __args)
    {
        var snapshot = new Snapshot();
        foreach (var ship in __args.OfType<Ship>().Distinct())
        {
            foreach (var source in ship.Resources.Sources)
            {
                if (source.Resources > 0)
                {
                    snapshot.ResourceTypes.Add(source.ResourceType);
                }
            }

            foreach (var job in ship.Resources.GetTransferJobs())
            {
                snapshot.ResourceTypes.Add(job.ResourceType);
            }
        }

        Snapshots.Remove(__instance);
        Snapshots.Add(__instance, snapshot);
    }

    internal static ResourceRules[] Filter(object owner, ResourceRules[] resources)
    {
        if (!Snapshots.TryGetValue(owner, out var snapshot))
        {
            return resources;
        }

        var filtered = new List<ResourceRules>(Math.Min(resources.Length, snapshot.ResourceTypes.Count));
        foreach (var resource in resources)
        {
            if (resource.IsStackable && snapshot.ResourceTypes.Contains(resource.ID))
            {
                filtered.Add(resource);
            }
        }
        return filtered.ToArray();
    }
}

[HarmonyPatch]
internal static class TransferRelevantResourceWorkerPatch
{
    private static readonly Type CrewClosureType = RequireType(
        "Cosmoteer.Game.Gui.Resources.CrewAndResourceTransferWindow+<>c__DisplayClass21_0");
    private static readonly Type TradeClosureType = RequireType(
        "Cosmoteer.Modes.Career.Comms.CommTradeTab+<>c__DisplayClass23_0");
    private static readonly Dictionary<Type, FieldInfo> OwnerFields = new()
    {
        [CrewClosureType] = RequireField(CrewClosureType, "<>4__this"),
        [TradeClosureType] = RequireField(TradeClosureType, "<>4__this")
    };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(CrewClosureType, "<.ctor>b__8")
            ?? throw new MissingMethodException(CrewClosureType.FullName, "<.ctor>b__8");
        yield return AccessTools.Method(TradeClosureType, "<.ctor>b__6")
            ?? throw new MissingMethodException(TradeClosureType.FullName, "<.ctor>b__6");
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        var filter = AccessTools.Method(typeof(TransferRelevantResourceWorkerPatch), nameof(FilterForClosure))
            ?? throw new MissingMethodException(typeof(TransferRelevantResourceWorkerPatch).FullName, nameof(FilterForClosure));
        var replacements = 0;

        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.operand is FieldInfo field &&
                field.Name == "Resources" &&
                field.FieldType == typeof(ResourceRules[]))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, filter);
                replacements++;
            }
        }

        if (replacements != 1)
        {
            throw new InvalidOperationException(
                $"Expected one global resource-array load in {__originalMethod}, found {replacements}.");
        }
    }

    private static ResourceRules[] FilterForClosure(ResourceRules[] resources, object closure)
    {
        var owner = OwnerFields[closure.GetType()].GetValue(closure)
            ?? throw new InvalidOperationException("Transfer row worker closure has no owner.");
        return TransferRelevantResourceSnapshotPatch.Filter(owner, resources);
    }

    private static Type RequireType(string name) =>
        AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{name} was not found.");

    private static FieldInfo RequireField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(type.FullName, name);
}
