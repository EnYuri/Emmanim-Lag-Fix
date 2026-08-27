using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Game.Gui.Build;
using Cosmoteer.Game.Gui.Crew;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Roles;
using Halfling.Geometry;
using Halfling.Graphics;
using Halfling.Gui;
using Halfling.Gui.Components.AutoSizing;
using Halfling.Gui.Components.Graphics;
using Halfling.Gui.Components.Rects;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Vanilla already creates priority categories on demand, but opening a
/// category immediately creates eleven buttons for every priority of every
/// part in that category. Large content packs can therefore allocate thousands
/// of widgets in one frame. Replace each part with a cheap collapsed header and
/// create its priority buttons only when the player expands that part.
/// </summary>
[HarmonyPatch]
internal static class RolePriorityLazyPartPatch
{
    private static readonly FieldInfo WindowField;

    static RolePriorityLazyPartPatch()
    {
        var displayType = GetDisplayType();
        WindowField = AccessTools.Field(displayType, "<>4__this")
            ?? throw new MissingFieldException(displayType.FullName, "<>4__this");
    }

    private static Type GetDisplayType()
    {
        return AccessTools.TypeByName("Cosmoteer.Game.Gui.Crew.RoleEditWindow+<>c__DisplayClass19_0")
            ?? throw new TypeLoadException("RoleEditWindow priority display class was not found.");
    }

    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(GetDisplayType(), "<CreatePrioritiesTab>g___AddPart|1")
            ?? throw new MissingMethodException(GetDisplayType().FullName, "<CreatePrioritiesTab>g___AddPart|1");
    }

    private static bool Prefix(
        object __instance,
        PartRules __0,
        ScrollBox __1,
        ID<EditorGroupRules>? __2)
    {
        var window = (RoleEditWindow)(WindowField.GetValue(__instance)
            ?? throw new InvalidOperationException("RoleEditWindow display class has no owner."));

        AddCollapsedPart(window, __0, __1, __2);
        return false;
    }

    private static void AddCollapsedPart(
        RoleEditWindow window,
        PartRules part,
        ScrollBox groupBox,
        ID<EditorGroupRules>? groupId)
    {
        var partBox = new LayoutBox();
        partBox.NineSlice.Flags = NineSliceFlags.None;
        partBox.AutoSize.AutoHeightMode = AutoSizeMode.Enable;
        partBox.AutoSize.MinHeight = 42f;
        partBox.Children.LayoutAlgorithm = LayoutAlgorithms.StretchTopToBottom;
        groupBox.AddChild(partBox);

        var header = new ImageListItem(part.JobsIcon ?? part.EditorIcon);
        header.Height = 42f;
        header.TextProvider = part.JobsNameKey ?? part.NameKey;
        header.SelectionController.EnableSelect = false;
        header.SelectionController.EnableDeselect = false;
        header.ImageRectController.InnerRectMode = InnerRectMode.Fill;
        header.ImageSprite.ScaleMode = SpriteScaleMode.ShrinkFitMaintainAspect;
        header.StateNormalTextRenderer.Padding = new Borders(8f, 0f, 0f, 0f);
        header.StateHighlightedTextRenderer.Padding = new Borders(8f, 0f, 0f, 0f);
        header.StatePressedTextRenderer.Padding = new Borders(8f, 0f, 0f, 0f);
        header.SelectedTextRenderer.Padding = new Borders(8f, 0f, 0f, 0f);
        partBox.AddChild(header);

        var priorities = new LayoutBox();
        priorities.NineSlice.Flags = NineSliceFlags.None;
        priorities.AutoSize.AutoHeightMode = AutoSizeMode.Enable;
        priorities.Children.LayoutAlgorithm = LayoutAlgorithms.StretchTopToBottom;
        priorities.Children.BorderPadding = new Borders(10f, 0f, 0f, 0f);
        priorities.SelfActive = false;
        partBox.AddChild(priorities);

        var populated = false;
        header.Clicked += delegate
        {
            if (!populated)
            {
                foreach (PriorityInfo info in part.PriorityInfos)
                {
                    priorities.AddChild(new RoleEditWindow.JobPriorityWidget(window, info, groupId));
                }

                populated = true;
            }

            priorities.SelfActive = !priorities.SelfActive;
            header.IsSelected = priorities.SelfActive;
        };

        // Preserve vanilla's editor-parent ordering, but keep every child
        // collapsed as well so it contributes only one inexpensive header.
        foreach (PartRules child in part.EditorChildParts)
        {
            if (child.PriorityInfos.Count > 0)
            {
                AddCollapsedPart(window, child, groupBox, groupId);
            }
        }
    }
}

/// <summary>
/// Active priority rows otherwise rewrite all eleven button states every
/// rendered frame. Ten refreshes per second keeps selection feedback responsive
/// while eliminating most redundant UI work. Click handlers and multiplayer
/// role updates remain untouched.
/// </summary>
[HarmonyPatch]
internal static class RolePriorityStateThrottlePatch
{
    private const int RefreshesPerSecond = 10;
    private static readonly long RefreshIntervalTicks = Stopwatch.Frequency / RefreshesPerSecond;
    private static readonly ConditionalWeakTable<object, Gate> Gates = new();

    private sealed class Gate
    {
        public long NextRefresh;
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var windowType = AccessTools.TypeByName("Cosmoteer.Game.Gui.Crew.RoleEditWindow")
            ?? throw new TypeLoadException("RoleEditWindow was not found.");

        foreach (var nestedName in new[] { "JobPriorityWidget", "AssignmentPriorityWidget" })
        {
            var nestedType = windowType.GetNestedType(nestedName, BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new TypeLoadException($"RoleEditWindow.{nestedName} was not found.");
            yield return AccessTools.Method(nestedType, "OnUpdatePriorityState")
                ?? throw new MissingMethodException(nestedType.FullName, "OnUpdatePriorityState");
        }
    }

    private static bool Prefix(object __instance)
    {
        var gate = Gates.GetOrCreateValue(__instance);
        var now = Stopwatch.GetTimestamp();
        if (now < gate.NextRefresh)
        {
            return false;
        }

        gate.NextRefresh = now + RefreshIntervalTicks;
        return true;
    }
}
