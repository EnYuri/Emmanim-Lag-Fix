using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Game.Gui;
using Cosmoteer.Gui;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Paint;
using Halfling.Geometry;
using Halfling.Gui;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// <c>PaintToolbox</c>'s constructor eagerly builds a full decal-tab tree and a
/// base-roof-texture picker for every <c>ShipRules</c> in <c>GameApp.Rules.Ships</c>
/// — every faction/mod's ship classes, not just the ones the player will ever
/// paint. On a save with several installed ship-adding mods this measured out to
/// one stable but very large eager tree (11,475 decal <c>TexturePicker.TextureItem</c>
/// widgets in this installation; see Dev/emmanim_lag_fix_code/MEMORY_DIAGNOSTICS.md).
/// It is also a leading contributor to the multi-second stall building a new
/// <c>GameRoot</c> can hit while constructing <c>PaintToolbox</c>.
///
/// <c>AddDecalPicker</c>/<c>AddBaseTexturePicker</c> each take the exact same
/// per-instance construction context (the toolbox itself, the shared GameGui,
/// the shared layout box that receives the per-ship widget subtree, and — for
/// decals — the shared <c>getLayer</c> delegate) on every call regardless of
/// which ShipRules is being built. Two narrowly-scoped transpilers redirect the
/// single call site inside <c>AddDecalsLayers</c>/<c>AddBasePaintLayer</c> from
/// the (expensive) builder method to a (cheap) capture method that only records
/// that context — the ships-array loop still runs once per ShipRules, but each
/// iteration now only stores a few references instead of building a widget
/// subtree. A postfix on <c>OnSelfActivated</c> — the only place <c>_ship</c> is
/// ever assigned a non-null value — then lazily builds the real picker for that
/// one ShipRules on first use, by invoking the original, untouched
/// <c>AddDecalPicker</c>/<c>AddBaseTexturePicker</c> methods.
///
/// A second layer leaves every normal group tab/page in place but passes a null
/// item list through <c>AddDecalsGroup</c>, then runs the untouched
/// <c>AddDecalButton</c> builder only when that tab opens. Favorite groups already
/// use a null list and retain their original dynamic path. A prefix on
/// <c>SelectDecalType</c> materializes the matching pending group before vanilla
/// searches it, preserving grab-decal behavior. Built ship pickers and groups
/// remain resident for the lifetime of the toolbox; this patch performs no
/// unsafe widget teardown.
/// </summary>
internal static class PaintToolboxLazyPickerPatch
{
    private const int DecalItemsPerFrame = 128;

    private static readonly Type PaintToolboxType = AccessTools.TypeByName(
        "Cosmoteer.Game.Gui.Paint.PaintToolbox")
        ?? throw new TypeLoadException("PaintToolbox was not found.");

    private static readonly MethodInfo AddDecalPickerMethod = AccessTools.Method(
        PaintToolboxType, "AddDecalPicker")
        ?? throw new MissingMethodException(PaintToolboxType.FullName, "AddDecalPicker");

    private static readonly MethodInfo AddBaseTexturePickerMethod = AccessTools.Method(
        PaintToolboxType, "AddBaseTexturePicker")
        ?? throw new MissingMethodException(PaintToolboxType.FullName, "AddBaseTexturePicker");

    private static readonly MethodInfo AddDecalButtonMethod = AccessTools.Method(
        PaintToolboxType, "AddDecalButton")
        ?? throw new MissingMethodException(PaintToolboxType.FullName, "AddDecalButton");

    private static readonly FieldInfo ShipField = AccessTools.Field(PaintToolboxType, "_ship")
        ?? throw new MissingFieldException(PaintToolboxType.FullName, "_ship");

    private sealed class Context
    {
        public object? DecalGameGui;
        public LayoutBox? DecalBox;
        public Func<int>? GetLayer;
        public LayoutBox? BaseTextureBox;
        public readonly HashSet<ShipRules> BuiltDecalPickers = new();
        public readonly HashSet<ShipRules> BuiltBaseTexturePickers = new();
        public readonly List<PendingDecalGroup> PendingDecalGroups = new();
    }

    private sealed class PendingDecalGroup
    {
        private readonly Context _context;
        private readonly object _toolbox;
        private readonly ShipRules _shipRules;
        private readonly GameGui _gameGui;
        private readonly Func<int> _getLayer;
        private readonly ScrollBox<TexturePicker.TextureItem> _decalsBox;
        private readonly ImageListItem _groupButton;
        private List<ID<Decal>>? _decals;
        private int _nextDecalIndex;
        private bool _building;
        private bool _batchScheduled;

        public ShipRules ShipRules => _shipRules;

        public PendingDecalGroup(
            Context context,
            object toolbox,
            ShipRules shipRules,
            GameGui gameGui,
            Func<int> getLayer,
            ScrollBox<TexturePicker.TextureItem> decalsBox,
            ImageListItem groupButton,
            List<ID<Decal>> decals)
        {
            _context = context;
            _toolbox = toolbox;
            _shipRules = shipRules;
            _gameGui = gameGui;
            _getLayer = getLayer;
            _decalsBox = decalsBox;
            _groupButton = groupButton;
            _decals = decals;

            // Selecting the tab is the normal path. Activated is a defensive
            // fallback for GUI-state restoration paths that activate a page
            // without producing a fresh button-selection transition.
            _groupButton.Selected += OnGroupOpened;
            _decalsBox.Activated += OnGroupOpened;
        }

        public bool ContainsPending(ID<Decal> id)
        {
            if (_decals == null)
            {
                return false;
            }

            for (var i = _nextDecalIndex; i < _decals.Count; i++)
            {
                if (_decals[i] == id)
                {
                    return true;
                }
            }
            return false;
        }

        public void BuildIfAlreadyOpen()
        {
            if (_groupButton.IsSelected || _decalsBox.IsActive)
            {
                StartIncrementalBuild();
            }
        }

        private void OnGroupOpened(object? sender, EventArgs e) => StartIncrementalBuild();

        public void StartIncrementalBuild()
        {
            if (_decals == null || _building || _batchScheduled)
            {
                return;
            }

            var root = _decalsBox.Root;
            if (root == null)
            {
                // This should only be reachable from an unusual programmatic
                // selection before the picker is attached to a GUI root.
                BuildImmediately();
                return;
            }

            _batchScheduled = true;
            root.AddOneTimePreDrawCallback(_ =>
            {
                _batchScheduled = false;
                if (_groupButton.IsSelected || _decalsBox.IsActive)
                {
                    BuildBatch(DecalItemsPerFrame);
                }
            }, int.MaxValue);
        }

        public void BuildImmediately()
        {
            if (_decals != null)
            {
                BuildBatch(int.MaxValue);
            }
        }

        private void BuildBatch(int maximumItems)
        {
            if (_decals == null || _building)
            {
                return;
            }

            _building = true;
            var shouldScheduleAnotherBatch = false;
            try
            {
                var count = Math.Min(maximumItems, _decals.Count - _nextDecalIndex);
                var end = _nextDecalIndex + count;
                while (_nextDecalIndex < end)
                {
                    var decal = _decals[_nextDecalIndex++];
                    var item = (TexturePicker.TextureItem?)AddDecalButtonMethod.Invoke(
                        _toolbox,
                        new object?[]
                        {
                            _shipRules,
                            _gameGui,
                            _getLayer,
                            decal,
                            null,
                            null,
                            _decalsBox,
                            _groupButton,
                            true,
                            null,
                        });

                    // When an item is added to an already-active lazy page,
                    // AddChild activates it before AddDecalButton attaches its
                    // favorite-star Activated handler. Re-activate only the new
                    // item after construction so the handler initializes the
                    // star without toggling the whole page every frame.
                    if (item?.IsActive == true)
                    {
                        item.SelfActive = false;
                        item.SelfActive = true;
                    }
                }

                if (_nextDecalIndex >= _decals.Count)
                {
                    CompleteBuild();
                }
                else
                {
                    shouldScheduleAnotherBatch = _groupButton.IsSelected || _decalsBox.IsActive;
                }
            }
            finally
            {
                _building = false;
            }

            if (shouldScheduleAnotherBatch)
            {
                StartIncrementalBuild();
            }
        }

        private void CompleteBuild()
        {
            _decals = null;
            _groupButton.Selected -= OnGroupOpened;
            _decalsBox.Activated -= OnGroupOpened;
            _context.PendingDecalGroups.Remove(this);
        }
    }

    private static readonly ConditionalWeakTable<object, Context> Contexts = new();

    private static Context GetContext(object toolbox) => Contexts.GetOrCreateValue(toolbox);

    /// <summary>Replacement call target for the per-ship <c>AddDecalPicker</c> call.</summary>
    private static void CaptureDecalPickerContext(object toolbox, ShipRules shipRules, object gameGui, LayoutBox box, Func<int> getLayer)
    {
        var ctx = GetContext(toolbox);
        ctx.DecalGameGui = gameGui;
        ctx.DecalBox = box;
        ctx.GetLayer = getLayer;
    }

    /// <summary>Replacement call target for the per-ship <c>AddBaseTexturePicker</c> call.</summary>
    private static void CaptureBaseTexturePickerContext(object toolbox, ShipRules shipRules, LayoutBox box)
    {
        var ctx = GetContext(toolbox);
        ctx.BaseTextureBox = box;
    }

    /// <summary>
    /// Builds the real decal/base-texture pickers for <paramref name="shipRules"/> on
    /// <paramref name="toolbox"/>, if they have not been built yet and the construction
    /// context has already been captured (i.e. the constructor has run).
    /// </summary>
    internal static void EnsureBuilt(object toolbox, ShipRules shipRules)
    {
        var ctx = GetContext(toolbox);
        if (ctx.DecalGameGui != null && ctx.DecalBox != null && ctx.GetLayer != null
            && ctx.BuiltDecalPickers.Add(shipRules))
        {
            AddDecalPickerMethod.Invoke(toolbox, new object[] { shipRules, ctx.DecalGameGui, ctx.DecalBox, ctx.GetLayer });
        }
        if (ctx.BaseTextureBox != null && ctx.BuiltBaseTexturePickers.Add(shipRules))
        {
            AddBaseTexturePickerMethod.Invoke(toolbox, new object[] { shipRules, ctx.BaseTextureBox });
        }
    }

    /// <summary>
    /// Called around <c>AddDecalsGroup</c>. Passing a null decal list through the
    /// original method preserves all vanilla tab/page/event construction while
    /// skipping only its eager item loop. Favorite groups already pass null and
    /// therefore remain on their original dynamic add/remove path.
    /// </summary>
    internal static void DeferDecalGroup(ref List<ID<Decal>>? decals, out List<ID<Decal>>? state)
    {
        state = decals;
        if (decals != null)
        {
            decals = null;
        }
    }

    internal static void RegisterDeferredDecalGroup(
        object toolbox,
        ShipRules shipRules,
        GameGui gameGui,
        Func<int> getLayer,
        ScrollBox<TexturePicker.TextureItem> decalsBox,
        ImageListItem groupButton,
        List<ID<Decal>>? state)
    {
        if (state == null)
        {
            return;
        }

        var ctx = GetContext(toolbox);
        var pending = new PendingDecalGroup(
            ctx, toolbox, shipRules, gameGui, getLayer, decalsBox, groupButton, state);
        ctx.PendingDecalGroups.Add(pending);

        // TabBox may have selected/activated its first page inside AddTab, before
        // this postfix could subscribe. Materialize that already-visible group
        // immediately so the first paint view is never an empty page.
        pending.BuildIfAlreadyOpen();
    }

    /// <summary>
    /// Vanilla SelectDecalType searches only materialized item widgets. Build the
    /// one deferred normal group containing the requested decal before that search,
    /// preserving grab-decal and programmatic selection behavior.
    /// </summary>
    internal static void EnsureGroupContaining(object toolbox, ShipRules shipRules, ID<Decal> id)
    {
        var ctx = GetContext(toolbox);
        var pending = ctx.PendingDecalGroups.FirstOrDefault(
            group => ReferenceEquals(group.ShipRules, shipRules) && group.ContainsPending(id));
        pending?.BuildImmediately();
    }

    /// <summary>
    /// Finds the single <c>call</c>/<c>callvirt</c> to <paramref name="originalTarget"/>
    /// in <paramref name="instructions"/> and redirects it to
    /// <paramref name="replacementTarget"/> in place, preserving labels/blocks. Throws if
    /// the call does not appear exactly once, so a future game update that changes this
    /// code shape disables the optimization instead of silently doing nothing or patching
    /// the wrong site.
    /// </summary>
    internal static IEnumerable<CodeInstruction> RedirectSingleCall(
        IEnumerable<CodeInstruction> instructions,
        MethodInfo originalTarget,
        MethodInfo replacementTarget)
    {
        var codes = new List<CodeInstruction>(instructions);
        var replaced = 0;

        foreach (var instruction in codes)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && instruction.operand is MethodInfo mi
                && mi == originalTarget)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacementTarget;
                replaced++;
            }
        }

        if (replaced != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one call to {originalTarget.DeclaringType}.{originalTarget.Name}, " +
                $"found {replaced}. The game code shape has changed; skipping the lazy picker patch.");
        }

        return codes;
    }

    internal static Type ToolboxType => PaintToolboxType;
    internal static FieldInfo ShipFieldInfo => ShipField;
}

/// <summary>Redirects the eager per-ship decal-picker build to a cheap context capture.</summary>
[HarmonyPatch]
internal static class PaintToolboxAddDecalsLayersLazyPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(PaintToolboxLazyPickerPatch.ToolboxType, "AddDecalsLayers")
        ?? throw new MissingMethodException(PaintToolboxLazyPickerPatch.ToolboxType.FullName, "AddDecalsLayers");

    private static readonly MethodInfo OriginalTarget = AccessTools.Method(
        PaintToolboxLazyPickerPatch.ToolboxType, "AddDecalPicker")
        ?? throw new MissingMethodException(PaintToolboxLazyPickerPatch.ToolboxType.FullName, "AddDecalPicker");

    private static readonly MethodInfo ReplacementTarget = AccessTools.Method(
        typeof(PaintToolboxLazyPickerPatch), "CaptureDecalPickerContext")
        ?? throw new MissingMethodException(nameof(PaintToolboxLazyPickerPatch), "CaptureDecalPickerContext");

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        PaintToolboxLazyPickerPatch.RedirectSingleCall(instructions, OriginalTarget, ReplacementTarget);
}

/// <summary>Redirects the eager per-ship base-texture-picker build to a cheap context capture.</summary>
[HarmonyPatch]
internal static class PaintToolboxAddBasePaintLayerLazyPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(PaintToolboxLazyPickerPatch.ToolboxType, "AddBasePaintLayer")
        ?? throw new MissingMethodException(PaintToolboxLazyPickerPatch.ToolboxType.FullName, "AddBasePaintLayer");

    private static readonly MethodInfo OriginalTarget = AccessTools.Method(
        PaintToolboxLazyPickerPatch.ToolboxType, "AddBaseTexturePicker")
        ?? throw new MissingMethodException(PaintToolboxLazyPickerPatch.ToolboxType.FullName, "AddBaseTexturePicker");

    private static readonly MethodInfo ReplacementTarget = AccessTools.Method(
        typeof(PaintToolboxLazyPickerPatch), "CaptureBaseTexturePickerContext")
        ?? throw new MissingMethodException(nameof(PaintToolboxLazyPickerPatch), "CaptureBaseTexturePickerContext");

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        PaintToolboxLazyPickerPatch.RedirectSingleCall(instructions, OriginalTarget, ReplacementTarget);
}

/// <summary>
/// <c>_ship</c> is assigned a non-null value only here (paint mode just opened for
/// the single selected ship), and cleared to null only in <c>OnSelfDeactivated</c> —
/// there is no code path that changes which ship is being painted without the
/// toolbox deactivating and reactivating. So this is the single correct point to
/// lazily build the picker for that ship's ShipRules before any input (including
/// <c>SelectDecalType</c>/grab-decal) can run against it.
/// </summary>
[HarmonyPatch]
internal static class PaintToolboxOnSelfActivatedLazyPickerPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(PaintToolboxLazyPickerPatch.ToolboxType, "OnSelfActivated")
        ?? throw new MissingMethodException(PaintToolboxLazyPickerPatch.ToolboxType.FullName, "OnSelfActivated");

    private static void Postfix(object __instance)
    {
        if (PaintToolboxLazyPickerPatch.ShipFieldInfo.GetValue(__instance) is Ship ship)
        {
            PaintToolboxLazyPickerPatch.EnsureBuilt(__instance, ship.Rules);
        }
    }
}


/// <summary>
/// Leaves every decal-group tab and page intact but defers the expensive normal
/// decal item loop until that group is actually opened. Favorite groups pass a
/// null decal list in vanilla and are deliberately unaffected.
/// </summary>
[HarmonyPatch]
internal static class PaintToolboxAddDecalsGroupLazyItemsPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(PaintToolboxLazyPickerPatch.ToolboxType, "AddDecalsGroup")
        ?? throw new MissingMethodException(PaintToolboxLazyPickerPatch.ToolboxType.FullName, "AddDecalsGroup");

    private static void Prefix(ref List<ID<Decal>>? decals, out List<ID<Decal>>? __state) =>
        PaintToolboxLazyPickerPatch.DeferDecalGroup(ref decals, out __state);

    private static void Postfix(
        object __instance,
        ShipRules shipRules,
        GameGui gameGui,
        Func<int> getLayer,
        ImageListItem groupButton,
        ScrollBox<TexturePicker.TextureItem> __result,
        List<ID<Decal>>? __state) =>
        PaintToolboxLazyPickerPatch.RegisterDeferredDecalGroup(
            __instance, shipRules, gameGui, getLayer, __result, groupButton, __state);
}


/// <summary>Materializes the matching deferred group before vanilla searches it.</summary>
[HarmonyPatch]
internal static class PaintToolboxSelectDecalTypeLazyItemsPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(PaintToolboxLazyPickerPatch.ToolboxType, "SelectDecalType")
        ?? throw new MissingMethodException(PaintToolboxLazyPickerPatch.ToolboxType.FullName, "SelectDecalType");

    private static void Prefix(object __instance, ShipRules shipRules, ID<Decal> id)
    {
        PaintToolboxLazyPickerPatch.EnsureBuilt(__instance, shipRules);
        PaintToolboxLazyPickerPatch.EnsureGroupContaining(__instance, shipRules, id);
    }
}
