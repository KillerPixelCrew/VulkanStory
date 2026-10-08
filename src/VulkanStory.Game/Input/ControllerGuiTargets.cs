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

    /// <summary>One GUI discovery snapshot shared by context ownership and navigation in a controller poll.</summary>
    /// <param name="Owner">Unfocused host or foreground dialog/screen used by input-context release guards.</param>
    /// <param name="Composers">Enabled foreground composers and eligible inventory HUD composers discovered by this snapshot.</param>
    internal sealed record GuiSnapshot(object? Owner, List<GuiComposer> Composers);

    /// <summary>Collects enabled screen/front-dialog composers, including inventory HUD grids when foreground inventory is active.</summary>
    /// <remarks>Uses the pinned game's private fields; absent fields reduce the available navigation targets.</remarks>
    public static List<GuiComposer> ActiveComposers(IControllerPlatformHost platform) => Snapshot(platform).Composers;

    /// <summary>Collects the last foreground dialog at the lowest input order and its enabled composers.</summary>
    internal static GuiSnapshot Snapshot(IControllerPlatformHost platform)
    {
        var composers = new List<GuiComposer>();
        ScreenManager? manager = platform.Original.keyEventHandlers.OfType<ScreenManager>().FirstOrDefault();
        GuiScreen? screen = manager != null ? CurrentScreenField?.GetValue(manager) as GuiScreen : null;
        if (screen != null &&
            screen.IsOpened && screen.ElementComposer is { Enabled: true } screenComposer)
            composers.Add(screenComposer);

        ClientMain? game = screen is GuiScreenRunningGame ? RunningGameField?.GetValue(screen) as ClientMain : null;
        GuiDialog? foreground = null;
        if (game?.api?.OpenedGuis != null)
        {
            GuiDialog[] dialogs = game.api.OpenedGuis.OfType<GuiDialog>().Where(dialog => dialog.IsOpened()).ToArray();
            foreach (GuiDialog dialog in dialogs)
                if (dialog.DialogType == EnumDialogType.Dialog &&
                    (foreground == null || dialog.InputOrder <= foreground.InputOrder)) foreground = dialog;
            if (foreground != null)
            {
                composers.Clear();
                foreach (GuiComposer composer in foreground.Composers.Values)
                    if (composer.Enabled) composers.Add(composer);
            }
            // The hotbar remains a HUD dialog while inventory is open. Include
            // its grids only when the foreground UI is itself an inventory,
            // so a modal settings page cannot operate on slots behind it.
            bool inventory = composers.Any(composer => InteractiveElementsField?.GetValue(composer) is
                Dictionary<string, GuiElement> elements && elements.Values.Any(element => element is GuiElementItemSlotGridBase));
            if (inventory)
                foreach (GuiDialog dialog in dialogs)
                    if (dialog.DialogType == EnumDialogType.HUD)
                        foreach (GuiComposer composer in dialog.Composers.Values)
                            if (composer.Enabled && InteractiveElementsField?.GetValue(composer) is
                                Dictionary<string, GuiElement> elements && elements.Values.Any(element => element is GuiElementItemSlotGridBase)) composers.Add(composer);
        }
        return new GuiSnapshot(!platform.IsFocused ? platform : foreground ?? (object?)screen, composers);
    }

    /// <summary>Returns the client owned by the current running-game screen, or null outside a loaded game.</summary>
    public static ClientMain? ActiveGame(IControllerPlatformHost platform)
    {
        ScreenManager? manager = platform.Original.keyEventHandlers.OfType<ScreenManager>().FirstOrDefault();
        GuiScreen? screen = manager != null ? CurrentScreenField?.GetValue(manager) as GuiScreen : null;
        return screen is GuiScreenRunningGame ? RunningGameField?.GetValue(screen) as ClientMain : null;
    }

    /// <summary>Returns the input-context owner used for release guards: unfocused host, front dialog, or current screen.</summary>
    /// <remarks>For equal dialog input orders, the last encountered dialog owns the context.</remarks>
    internal static object? ForegroundOwner(IControllerPlatformHost platform)
    {
        return Snapshot(platform).Owner;
    }

    /// <summary>Extracts visible grid-slot geometry in reverse composer order for semantic inventory operations.</summary>
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
                targets.AddRange(VisibleSlots(grid));
            }
        }
        return targets;
    }

    /// <summary>Collects window-contained, unclipped centers of slots, list cells, and focusable GUI elements.</summary>
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
                    foreach (var slot in VisibleSlots(grid)) Add(slot.Bounds, grid.InsideClipBounds, width, height, targets);
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

    /// <summary>Uses one slot geometry/clip rule for cursor and semantic inventory navigation.</summary>
    private static IEnumerable<ControllerSlotTarget> VisibleSlots(GuiElementItemSlotGridBase grid)
    {
        if (grid.SlotBounds == null) yield break;
        int count = Math.Min(grid.SlotBounds.Length, grid.renderedSlots.Count);
        for (int i = 0; i < count; i++)
        {
            ElementBounds bounds = grid.SlotBounds[i];
            var center = new Vector2((float)(bounds.absX + bounds.OuterWidth / 2), (float)(bounds.absY + bounds.OuterHeight / 2));
            if (bounds.OuterWidth <= 0 || bounds.OuterHeight <= 0 || grid.InsideClipBounds?.PointInside(center.X, center.Y) == false) continue;
            yield return new ControllerSlotTarget(grid, i, grid.renderedSlots.GetKeyAtIndex(i), bounds, center);
        }
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

/// <summary>Rendered inventory-slot geometry plus the grid's filtered index and underlying inventory slot identity.</summary>
/// <param name="Grid">Official GUI grid responsible for permissions and slot operations.</param>
/// <param name="Index">Index in the grid's rendered/filtered slot collection.</param>
/// <param name="SlotId">Inventory slot ID mapped from the rendered collection.</param>
/// <param name="Bounds">Live rendered slot bounds used for cursor containment.</param>
/// <param name="Center">Drawable-coordinate center used for directional navigation.</param>
internal readonly record struct ControllerSlotTarget(GuiElementItemSlotGridBase Grid, int Index,
    int SlotId, ElementBounds Bounds, Vector2 Center);
