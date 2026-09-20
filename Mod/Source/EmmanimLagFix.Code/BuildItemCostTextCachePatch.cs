using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Gui.Build;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Resources;
using Cosmoteer.Simulation;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Every part and door button in the build toolbox re-runs GetItemCostText on
/// every frame via BeforeDrawWhileActive. Each call allocates a pooled
/// ShipCost, walks the resource dictionaries into SortedDictionary-backed
/// cost structures, formats strings through a StringBuilder, and when the
/// text changes the WidgetTextRenderer rebuilds its XML layout (including a
/// fresh XmlTextReader). A live allocation trace attributed ~400 MiB per
/// minute to this path. The output only depends on the item's cost inputs and
/// a handful of mutable pieces of toolbox state, so cache the result keyed on
/// (credits, resource array identity) guarded by a content fingerprint of
/// everything the vanilla method reads: editing mode, spendable money, the
/// bound ship, its construction mode, and the contents of the available /
/// buyable / refundable resource dictionaries. On a fingerprint match the
/// cached string and affordability are returned verbatim; on any change the
/// vanilla method runs and the result is stored. Display strings and XML
/// formatting are unchanged because the returned text itself is cached.
/// </summary>
[HarmonyPatch]
internal static class BuildItemCostTextCachePatch
{
    internal readonly struct CostTextKey : IEquatable<CostTextKey>
    {
        internal readonly int Credits;
        internal readonly object? Resources;

        internal CostTextKey(int credits, object? resources)
        {
            Credits = credits;
            Resources = resources;
        }

        public bool Equals(CostTextKey other) =>
            Credits == other.Credits && ReferenceEquals(Resources, other.Resources);

        public override bool Equals(object? obj) => obj is CostTextKey other && Equals(other);

        public override int GetHashCode() =>
            Credits ^ (Resources == null ? 0 : RuntimeHelpers.GetHashCode(Resources));
    }

    private struct CostTextResult
    {
        internal long Fingerprint;
        internal string? Text;
        internal bool CanAfford;
    }

    // Extracted so the smoke test can exercise hit/miss behaviour without a
    // live BuildToolbox (its static constructor needs a running game).
    internal sealed class CostTextCache
    {
        private readonly Dictionary<CostTextKey, CostTextResult> _entries = new();

        internal bool TryGet(CostTextKey key, long fingerprint, out string? text, out bool canAfford)
        {
            if (_entries.TryGetValue(key, out var result) && result.Fingerprint == fingerprint)
            {
                text = result.Text;
                canAfford = result.CanAfford;
                return true;
            }
            text = null;
            canAfford = false;
            return false;
        }

        internal void Store(CostTextKey key, long fingerprint, string text, bool canAfford)
        {
            _entries[key] = new CostTextResult
            {
                Fingerprint = fingerprint,
                Text = text,
                CanAfford = canAfford,
            };
        }
    }

    internal static long CacheHits;
    internal static long CacheMisses;

    private static readonly ConditionalWeakTable<BuildToolbox, CostTextCache> Caches = new();

    private static bool Prepare()
    {
        var toolbox = typeof(BuildToolbox);
        var target = AccessTools.Method(toolbox, nameof(BuildToolbox.GetItemCostText));
        var targetParams = target?.GetParameters();
        var modeType = typeof(SimRoot).GetProperty("Mode")?.PropertyType;
        var ok =
            targetParams is { Length: 3 }
            && targetParams[0].ParameterType == typeof(int)
            && AccessTools.Field(toolbox, "_ship") != null
            && AccessTools.Field(toolbox, "_availableResources") != null
            && AccessTools.Field(toolbox, "_buyableResources") != null
            && AccessTools.Field(toolbox, "_refundableResources") != null
            && AccessTools.Property(toolbox, nameof(BuildToolbox.IsEditingBlueprints)) != null
            && AccessTools.Property(toolbox, nameof(BuildToolbox.Sim)) != null
            && AccessTools.Property(toolbox, "Game") != null
            && AccessTools.Property(typeof(GameRoot), nameof(GameRoot.LocalPlayerSpendableMoney)) != null
            && AccessTools.Property(typeof(GameRoot), "Mode") != null
            && modeType?.GetMethod("GetConstructionMode") != null
            && AccessTools.Property(typeof(CostedQuantities), nameof(CostedQuantities.Count)) != null;
        if (!ok)
        {
            Halfling.Logging.Logger.Log(
                "[EmmanimLagFix] BuildToolbox item-cost internals changed; leaving vanilla behaviour.");
        }
        return ok;
    }

    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(BuildToolbox), nameof(BuildToolbox.GetItemCostText))
        ?? throw new MissingMethodException(nameof(BuildToolbox), nameof(BuildToolbox.GetItemCostText));

    private static bool Prefix(
        BuildToolbox __instance,
        int credits,
        (ID<ResourceRules> Type, int Quantity)[] resources,
        ref bool canAfford,
        ref string __result,
        out object? __state)
    {
        canAfford = false;
        var fingerprint = ComputeFingerprint(__instance, credits, resources);
        var cache = Caches.GetOrCreateValue(__instance);
        var key = new CostTextKey(credits, resources);
        if (cache.TryGet(key, fingerprint, out var text, out canAfford))
        {
            __result = text!;
            __state = null;
            CacheHits++;
            return false;
        }
        __state = new KeyValuePair<CostTextKey, long>(key, fingerprint);
        __result = null!;
        return true;
    }

    private static void Postfix(
        BuildToolbox __instance,
        string __result,
        bool canAfford,
        object? __state)
    {
        if (__state is KeyValuePair<CostTextKey, long> pending && __result != null)
        {
            CacheMisses++;
            Caches.GetOrCreateValue(__instance).Store(pending.Key, pending.Value, __result, canAfford);
        }
    }

    private static long ComputeFingerprint(
        BuildToolbox toolbox,
        int credits,
        (ID<ResourceRules> Type, int Quantity)[]? resources)
    {
        var ship = toolbox._ship;
        var game = toolbox.Game;
        var sim = game?.Sim;
        return ComputeFingerprint(
            credits,
            resources,
            toolbox.IsEditingBlueprints,
            ship,
            sim,
            game?.Mode == null ? 0 : game.LocalPlayerSpendableMoney,
            ship != null && sim?.Mode != null ? (int)sim.Mode.GetConstructionMode(ship) : -1,
            toolbox._availableResources,
            toolbox._buyableResources,
            toolbox._refundableResources);
    }

    // Folds every input the vanilla method reads into a single 64-bit value.
    // A collision would only show a stale cost label; the fingerprint covers
    // the full input set so a genuine state change always misses.
    //
    // The three dictionaries are typed concretely on purpose. They are
    // Dictionary<,> fields on BuildToolbox, and this runs once per toolbox
    // button per frame -- the same call volume as the path being optimised --
    // so declaring them as IEnumerable<KeyValuePair<,>> would box a
    // Dictionary struct enumerator on each of the three foreach loops and
    // hand back a slice of the allocations the cache exists to remove.
    internal static long ComputeFingerprint(
        int credits,
        (ID<ResourceRules> Type, int Quantity)[]? resources,
        bool editingBlueprints,
        object? ship,
        object? sim,
        int spendableMoney,
        int constructionMode,
        Dictionary<ID<ResourceRules>, int>? availableResources,
        Dictionary<ID<ResourceRules>, CostedQuantities>? buyableResources,
        Dictionary<ID<ResourceRules>, CostedQuantities>? refundableResources)
    {
        unchecked
        {
            var hash = (long)credits;
            if (resources != null)
            {
                for (var i = 0; i < resources.Length; i++)
                {
                    hash = hash * 31 + resources[i].Type.GetHashCode();
                    hash = hash * 31 + resources[i].Quantity;
                }
            }
            hash = hash * 31 + (editingBlueprints ? 1 : 0);
            hash = hash * 31 + (ship == null ? 0 : RuntimeHelpers.GetHashCode(ship));
            hash = hash * 31 + (sim == null ? 0 : RuntimeHelpers.GetHashCode(sim));
            hash = hash * 31 + spendableMoney;
            hash = hash * 31 + constructionMode;
            if (availableResources != null)
            {
                foreach (var pair in availableResources)
                {
                    hash = hash * 31 + pair.Key.GetHashCode();
                    hash = hash * 31 + pair.Value;
                }
            }
            if (buyableResources != null)
            {
                foreach (var pair in buyableResources)
                {
                    hash = hash * 31 + pair.Key.GetHashCode();
                    hash = FoldCostedQuantities(hash, pair.Value);
                }
            }
            if (refundableResources != null)
            {
                foreach (var pair in refundableResources)
                {
                    hash = hash * 31 + pair.Key.GetHashCode();
                    hash = FoldCostedQuantities(hash, pair.Value);
                }
            }
            return hash;
        }
    }

    private static long FoldCostedQuantities(long hash, CostedQuantities? quantities)
    {
        if (quantities == null)
            return hash * 31;
        hash = hash * 31 + quantities.Count;
        for (var i = 0; i < quantities.Count; i++)
        {
            var quantity = quantities[i];
            hash = hash * 31 + quantity.Quantity;
            hash = hash * 31 + quantity.PricePerUnit;
        }
        return hash;
    }
}
