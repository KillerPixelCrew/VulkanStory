using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Input;

/// <summary>
/// Reads active composer geometry for controller cursor snapping. The two
/// non-public fields belong to the pinned game version; if either changes,
/// D-pad movement falls back to a fixed cursor step.
/// </summary>
internal static class ControllerGuiTargets
{
    private static readonly FieldInfo? CurrentScreenField = typeof(ScreenManager).GetField(
        "CurrentScreen", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? RunningGameField = typeof(GuiScreenRunningGame).GetField(
        "runningGame", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? InteractiveElementsField = typeof(GuiComposer).GetField(
        "interactiveElements", BindingFlags.Instance | BindingFlags.NonPublic);

    public static List<GuiComposer> ActiveComposers(IControllerPlatformHost platform)
    {
        var composers = new List<GuiComposer>();
        ScreenManager? manager = platform.Original.keyEventHandlers.OfType<ScreenManager>().FirstOrDefault();
        GuiScreen? screen = manager != null ? CurrentScreenField?.GetValue(manager) as GuiScreen : null;
        if (screen != null &&
            screen.IsOpened && screen.ElementComposer is { Enabled: true } screenComposer)
            composers.Add(screenComposer);

        ClientMain? game = screen is GuiScreenRunningGame ? RunningGameField?.GetValue(screen) as ClientMain : null;
        if (game?.api?.OpenedGuis != null)
        {
            double frontOrder = double.PositiveInfinity;
            foreach (object item in game.api.OpenedGuis)
                if (item is GuiDialog dialog && dialog.IsOpened() && dialog.DialogType == EnumDialogType.Dialog) frontOrder = Math.Min(frontOrder, dialog.InputOrder);
            foreach (object item in game.api.OpenedGuis)
            {
                if (item is not GuiDialog dialog || !dialog.IsOpened() ||
                    dialog.DialogType != EnumDialogType.Dialog || dialog.InputOrder > frontOrder) continue;
                foreach (GuiComposer composer in dialog.Composers.Values)
                    if (composer.Enabled) composers.Add(composer);
            }
            // The hotbar remains a HUD dialog while inventory is open. Include
            // its grids only when the foreground UI is itself an inventory,
            // so a modal settings page cannot operate on slots behind it.
            bool inventory = composers.Any(composer => InteractiveElementsField?.GetValue(composer) is
                Dictionary<string, GuiElement> elements && elements.Values.Any(element => element is GuiElementItemSlotGridBase));
            if (inventory)
                foreach (object item in game.api.OpenedGuis)
                    if (item is GuiDialog dialog && dialog.IsOpened() && dialog.DialogType == EnumDialogType.HUD)
                        foreach (GuiComposer composer in dialog.Composers.Values)
                            if (composer.Enabled && InteractiveElementsField?.GetValue(composer) is
                                Dictionary<string, GuiElement> elements && elements.Values.Any(element => element is GuiElementItemSlotGridBase)) composers.Add(composer);
        }
        return composers;
    }

    public static ClientMain? ActiveGame(IControllerPlatformHost platform)
    {
        ScreenManager? manager = platform.Original.keyEventHandlers.OfType<ScreenManager>().FirstOrDefault();
        GuiScreen? screen = manager != null ? CurrentScreenField?.GetValue(manager) as GuiScreen : null;
        return screen is GuiScreenRunningGame ? RunningGameField?.GetValue(screen) as ClientMain : null;
    }

    internal static object? ForegroundOwner(IControllerPlatformHost platform)
    {
        if (!platform.IsFocused) return platform;
        if (ActiveGame(platform)?.api?.OpenedGuis is { } guis)
        {
            GuiDialog? foreground = null;
            foreach (object item in guis)
                if (item is GuiDialog dialog && dialog.IsOpened() && dialog.DialogType == EnumDialogType.Dialog &&
                    (foreground == null || dialog.InputOrder <= foreground.InputOrder)) foreground = dialog;
            if (foreground != null) return foreground;
        }
        return platform.ControllerCurrentScreen();
    }

    internal static List<ControllerSlotTarget> SlotTargets(IReadOnlyList<GuiComposer> composers)
    {
        var targets = new List<ControllerSlotTarget>();
        if (InteractiveElementsField == null) return targets;
        for (int c = composers.Count - 1; c >= 0; c--)
        {
            GuiComposer composer = composers[c];
            if (!composer.Enabled || !composer.Composed ||
                InteractiveElementsField.GetValue(composer) is not Dictionary<string, GuiElement> elements) continue;
            foreach (GuiElement element in elements.Values)
            {
                if (element is not GuiElementItemSlotGridBase grid || grid.SlotBounds == null) continue;
                int count = Math.Min(grid.SlotBounds.Length, grid.renderedSlots.Count);
                for (int i = 0; i < count; i++)
                {
                    ElementBounds bounds = grid.SlotBounds[i];
                    var center = new Vector2((float)(bounds.absX + bounds.OuterWidth / 2),
                        (float)(bounds.absY + bounds.OuterHeight / 2));
                    if (bounds.OuterWidth <= 0 || bounds.OuterHeight <= 0 ||
                        grid.InsideClipBounds?.PointInside(center.X, center.Y) == false) continue;
                    targets.Add(new ControllerSlotTarget(grid, i, grid.renderedSlots.GetKeyAtIndex(i), bounds, center));
                }
            }
        }
        return targets;
    }

    public static List<Vector2> Collect(IControllerPlatformHost platform, IReadOnlyList<GuiComposer> composers)
    {
        var targets = new List<Vector2>();
        if (InteractiveElementsField == null || !platform.HasControllerWindow) return targets;
        (int width, int height) = platform.ControllerWindowSize;
        foreach (GuiComposer composer in composers)
        {
            if (!composer.Composed || InteractiveElementsField.GetValue(composer) is not Dictionary<string, GuiElement> elements)
                continue;
            foreach (GuiElement element in elements.Values)
            {
                if (element is GuiElementItemSlotGridBase grid && grid.SlotBounds != null)
                {
                    int count = Math.Min(grid.SlotBounds.Length, grid.renderedSlots.Count);
                    for (int i = 0; i < count; i++) Add(grid.SlotBounds[i], grid.InsideClipBounds, width, height, targets);
                }
                else if (element.GetType().GetField("elementCells")?.GetValue(element) is IEnumerable<IGuiElementCell> cells)
                {
                    foreach (IGuiElementCell cell in cells) Add(cell.Bounds, cell.InsideClipBounds, width, height, targets);
                }
                else if (element.Focusable)
                {
                    Add(element.Bounds, element.InsideClipBounds, width, height, targets);
                }
            }
        }
        return targets;
    }

    private static void Add(ElementBounds bounds, ElementBounds? clip, int width, int height, List<Vector2> targets)
    {
        if (bounds.OuterWidth <= 4 || bounds.OuterHeight <= 4) return;
        double x = bounds.absX + bounds.OuterWidth / 2;
        double y = bounds.absY + bounds.OuterHeight / 2;
        if (x < 0 || x >= width || y < 0 || y >= height || clip?.PointInside(x, y) == false) return;
        targets.Add(new Vector2((float)x, (float)y));
    }
}

internal readonly record struct ControllerSlotTarget(GuiElementItemSlotGridBase Grid, int Index,
    int SlotId, ElementBounds Bounds, Vector2 Center);
