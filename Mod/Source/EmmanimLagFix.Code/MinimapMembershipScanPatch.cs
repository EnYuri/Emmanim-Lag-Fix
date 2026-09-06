using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Game.Gui;
using Cosmoteer.Simulation;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Stops the minimap from asking every object in the sector whether it is
/// visible, every frame.
///
/// <c>Minimap.OnUpdateMinimap</c> is wired to <c>BeforeDrawWhileActive</c>, so
/// it runs once per drawn frame, and it begins with a full scan:
///
/// <code>
/// var allInScene = ObjectIndicatorSource.GetAllInScene(Sim);
/// for (int i = 0; i &lt; allInScene.Count; i++)
///     if (allInScene[i].ShowOnMinimap) objects.Add(allInScene[i]);
/// </code>
///
/// For a ship that reduces to <c>Ship.IsWithinRadarOfLocalPlayer</c>, which
/// intersects the ship's bounding circle against the team's radar circles. A
/// 20-second host trace spent 220.4 ms inside that scan - 105.3 ms in
/// <c>IsWithinRadarOfTeam</c>, 50.4 ms in <c>ShipIndicatorSource.ShowOnMinimap</c>
/// and 64.7 ms in the loop itself - which is 1.5% of the main thread and the
/// largest identifiable non-rendering cost in the minimap.
///
/// A per-frame memo would save nothing: each source is already asked exactly
/// once per frame, so there is no repeat to collapse. What is actually
/// redundant is re-deciding, sixty times a second, a membership that changes
/// when a ship crosses a radar boundary. So this rescans the whole scene at
/// 10 Hz and, in between, re-tests only the sources that were visible last
/// time - a far smaller set, since most of a populated sector is out of radar
/// range.
///
/// Disappearance stays immediate, which is the half that matters for
/// correctness: a cached source is dropped as soon as its own
/// <c>ShowOnMinimap</c> goes false, and also as soon as it leaves the scene,
/// which shows up as a null <c>Sim</c>. A change in the scene population count
/// forces a full rescan on the spot, so a ship that is destroyed or spawned is
/// never held past the frame it happened in. Only an appearance with no
/// population change - a ship already in the sector drifting into radar range -
/// can be up to 100 ms late.
///
/// Nothing here is simulation state. <c>GetMinimapObjects</c> is private, has
/// one caller, and its result feeds the minimap's widget list and displayed
/// bounds; no value derived from it reaches a tick.
/// </summary>
[HarmonyPatch]
internal static class MinimapMembershipScanPatch
{
    /// <summary>
    /// Long enough to remove most of the scan, short enough that an appearance
    /// still reads as immediate. The minimap redraws every frame regardless -
    /// only membership is throttled, never a blip's position.
    /// </summary>
    private const long RescanIntervalMs = 100;

    /// <summary>Counts full scans skipped, for the smoke test and for diagnostics.</summary>
    internal static long Reused { get; private set; }

    private sealed class State
    {
        /// <summary>The sources the last full scan found, kept pruned in between.</summary>
        internal readonly List<ObjectIndicatorSource> Visible = [];

        internal long LastScanMs = long.MinValue;

        /// <summary>Scene population at the last full scan. Any change forces another.</summary>
        internal int LastSceneCount = -1;
    }

    private static readonly ConditionalWeakTable<Minimap, State> States = new();

    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(typeof(Minimap), "GetMinimapObjects")
        ?? throw new MissingMethodException(typeof(Minimap).FullName, "GetMinimapObjects");

    /// <summary>
    /// Returns false to skip vanilla's full scan and answer from the retained
    /// set instead.
    /// </summary>
    private static bool Prefix(Minimap __instance, List<ObjectIndicatorSource> objects)
    {
        var state = States.GetValue(__instance, static _ => new State());
        var now = Environment.TickCount64;

        if (now - state.LastScanMs < RescanIntervalMs
            && ObjectIndicatorSource.GetAllInScene(__instance.Sim).Count == state.LastSceneCount)
        {
            var kept = 0;
            for (var i = 0; i < state.Visible.Count; i++)
            {
                var source = state.Visible[i];

                // A source removed from the scene has no root node and so no
                // Sim. Testing that first keeps ShowOnMinimap off a detached
                // object.
                if (source.Sim is null || !source.ShowOnMinimap)
                {
                    continue;
                }

                objects.Add(source);
                state.Visible[kept++] = source;
            }

            state.Visible.RemoveRange(kept, state.Visible.Count - kept);
            Reused++;
            return false;
        }

        return true;
    }

    /// <summary>Retains what the full scan found, so the next frames can prune it.</summary>
    private static void Postfix(Minimap __instance, List<ObjectIndicatorSource> objects, bool __runOriginal)
    {
        if (!__runOriginal)
        {
            return;
        }

        var state = States.GetValue(__instance, static _ => new State());
        state.Visible.Clear();
        state.Visible.AddRange(objects);
        state.LastScanMs = Environment.TickCount64;
        state.LastSceneCount = ObjectIndicatorSource.GetAllInScene(__instance.Sim).Count;
    }
}
