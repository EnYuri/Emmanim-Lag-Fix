using System.Runtime.CompilerServices;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Resources;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Replaces PerShipCount's single exclusive List lock with copy-on-write
/// snapshots. Resource search and sink-job workers only read snapshots; count
/// mutations publish a complete replacement with CompareExchange. This keeps
/// the vanilla entry order, allied-ship sum, and dead-reference cleanup without
/// serializing parallel readers or retaining any path/resource-location data.
/// </summary>
internal static class PerShipCountLockFreeStorage
{
    internal readonly record struct Entry(WeakReference<Ship> Ship, MPValue<int> Count);

    internal sealed class State
    {
        public Entry[] Entries;

        public State(ResourceManager.PerShipCount original)
        {
            lock (original._countsForShips)
            {
                Entries = new Entry[original._countsForShips.Count];
                for (var i = 0; i < Entries.Length; i++)
                {
                    var entry = original._countsForShips[i];
                    Entries[i] = new Entry(entry.Ship, entry.Count);
                }
            }
        }
    }

    private static readonly ConditionalWeakTable<ResourceManager.PerShipCount, State> States = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static State GetState(ResourceManager.PerShipCount count) =>
        States.GetValue(count, static key => new State(key));

    internal static int GetCount(
        ResourceManager.PerShipCount count,
        Ship perspectiveShip,
        MPValueType valueType)
    {
        var state = GetState(count);
        var snapshot = Volatile.Read(ref state.Entries);
        var total = 0;
        var foundDead = false;

        for (var i = 0; i < snapshot.Length; i++)
        {
            var entry = snapshot[i];
            if (entry.Ship.TryGetTarget(out var target))
            {
                if (!target.IsEnemiesWith(perspectiveShip))
                {
                    total += entry.Count.GetValue(valueType);
                }
            }
            else
            {
                foundDead = true;
            }
        }

        if (foundDead)
        {
            RemoveDeadEntries(state, snapshot);
        }

        return total;
    }

    internal static MPValue<int> AddCount(
        ResourceManager.PerShipCount count,
        Ship perspectiveShip,
        MPValue<int> amount)
    {
        var state = GetState(count);
        while (true)
        {
            var snapshot = Volatile.Read(ref state.Entries);
            var replacement = new Entry[snapshot.Length + 1];
            var replacementIndex = -1;
            var result = amount;

            for (var i = 0; i < snapshot.Length; i++)
            {
                var entry = snapshot[i];
                replacement[i] = entry;
                if (entry.Ship.TryGetTarget(out var target))
                {
                    if (ReferenceEquals(target, perspectiveShip))
                    {
                        replacementIndex = i;
                        result = new MPValue<int>(
                            entry.Count.Displayed + amount.Displayed,
                            entry.Count.Confirmed + amount.Confirmed);
                        replacement[i] = new Entry(entry.Ship, result);
                        break;
                    }
                }
                else
                {
                    replacementIndex = i;
                    replacement[i] = new Entry(new WeakReference<Ship>(perspectiveShip), amount);
                    break;
                }
            }

            if (replacementIndex >= 0)
            {
                Array.Copy(snapshot, replacementIndex + 1, replacement, replacementIndex + 1, snapshot.Length - replacementIndex - 1);
                Array.Resize(ref replacement, snapshot.Length);
            }
            else
            {
                replacement[^1] = new Entry(new WeakReference<Ship>(perspectiveShip), amount);
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref state.Entries, replacement, snapshot), snapshot))
            {
                return result;
            }
        }
    }

    private static void RemoveDeadEntries(State state, Entry[] observed)
    {
        var liveCount = 0;
        for (var i = 0; i < observed.Length; i++)
        {
            if (observed[i].Ship.TryGetTarget(out _))
            {
                liveCount++;
            }
        }

        if (liveCount == observed.Length)
        {
            return;
        }

        var replacement = new Entry[liveCount];
        var targetIndex = 0;
        for (var i = 0; i < observed.Length; i++)
        {
            var entry = observed[i];
            if (entry.Ship.TryGetTarget(out _))
            {
                replacement[targetIndex++] = entry;
            }
        }

        Interlocked.CompareExchange(ref state.Entries, replacement, observed);
    }
}

[HarmonyPatch(typeof(ResourceManager.PerShipCount), nameof(ResourceManager.PerShipCount.GetCount))]
internal static class PerShipCountLockFreeGetPatch
{
    private static bool Prefix(
        ResourceManager.PerShipCount __instance,
        Ship perspectiveShip,
        MPValueType valueType,
        ref int __result)
    {
        __result = PerShipCountLockFreeStorage.GetCount(__instance, perspectiveShip, valueType);
        return false;
    }
}

[HarmonyPatch(typeof(ResourceManager.PerShipCount), nameof(ResourceManager.PerShipCount.AddCount))]
internal static class PerShipCountLockFreeAddPatch
{
    private static bool Prefix(
        ResourceManager.PerShipCount __instance,
        Ship perspectiveShip,
        MPValue<int> amount,
        ref MPValue<int> __result)
    {
        __result = PerShipCountLockFreeStorage.AddCount(__instance, perspectiveShip, amount);
        return false;
    }
}
