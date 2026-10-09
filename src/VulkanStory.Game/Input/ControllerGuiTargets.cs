using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Input;

/// <summary>
/// Reads active composer geometry for controller cursor snapping. Screen and
/// client fields use the validated <see cref="GameGuiBindings"/> accessors.
/// The composer element map belongs to the pinned game version; if it changes,
/// D-pad movement falls back to a fixed cursor step.
/// </summary>
/// <remarks>Runs in the controller poll, so lookups use cached field accessors and avoid LINQ.</remarks>
internal static class ControllerGuiTargets
{
    private static GameGuiBindings? gui;
    private static readonly AccessTools.FieldRef<GuiComposer, Dictionary<string, GuiElement>>? InteractiveElements =
        CreateInteractiveElements();

    // The session host already constructs these bindings, so a changed field
    // fails at session startup rather than in the first controller poll.
    private static GameGuiBindings Gui => gui ??= new GameGuiBindings();

    /// <summary>One GUI discovery snapshot shared by context ownership and navigation in a controller poll.</summary>
    /// <param name="Owner">Unfocused host or foreground dialog/screen used by input-context release guards.</param>
    /// <param name="Composers">Enabled foreground composers and eligible inventory HUD composers discovered by this snapshot.</param>
    internal sealed record GuiSnapshot(object? Owner, List<GuiComposer> Composers);

    /// <summary>Collects enabled screen/front-dialog composers, including inventory HUD grids when foreground inventory is active.</summary>
    /// <remarks>An absent composer element map reduces the available navigation targets.</remarks>
    public static List<GuiComposer> ActiveComposers(IControllerPlatformHost platform) => Snapshot(platform).Composers;

    /// <summary>Collects the last foreground dialog at the lowest input order and its enabled composers.</summary>
    internal static GuiSnapshot Snapshot(IControllerPlatformHost platform)
    {
        var composers = new List<GuiComposer>();
        GuiScreen? screen = CurrentScreen(platform);
        if (screen != null &&
            screen.IsOpened && screen.ElementComposer is { Enabled: true } screenComposer)
            composers.Add(screenComposer);

        ClientMain? game = RunningGame(screen);
        GuiDialog? foreground = null;
        if (game?.api?.OpenedGuis is { } opened)
        {
            foreground = ForegroundDialog(game);
            if (foreground != null)
            {
                composers.Clear();
                foreach (GuiComposer composer in foreground.Composers.Values)
                    if (composer.Enabled) composers.Add(composer);
            }
            // The hotbar remains a HUD dialog while inventory is open. Include
            // its grids only when the foreground UI is itself an inventory,
            // so a modal settings page cannot operate on slots behind it.
            bool inventory = false;
            foreach (GuiComposer composer in composers)
                if (HasSlotGrid(composer)) { inventory = true; break; }
            if (inventory)
                foreach (object item in opened)
                    if (item is GuiDialog dialog && dialog.IsOpened() && dialog.DialogType == EnumDialogType.HUD)
                        foreach (GuiComposer composer in dialog.Composers.Values)
                            if (composer.Enabled && HasSlotGrid(composer)) composers.Add(composer);
        }
        return new GuiSnapshot(Owner(platform, screen, foreground), composers);
    }

    /// <summary>Returns the client owned by the current running-game screen, or null outside a loaded game.</summary>
    public static ClientMain? ActiveGame(IControllerPlatformHost platform) => RunningGame(CurrentScreen(platform));

    /// <summary>Returns the input-context owner used for release guards: unfocused host, front dialog, or current screen.</summary>
    /// <remarks>For equal dialog input orders, the last encountered dialog owns the context. Recomputes only the
    /// owner, without composer discovery, so a guard after key dispatch still observes a synchronously opened dialog.</remarks>
    internal static object? ForegroundOwner(IControllerPlatformHost platform)
    {
        GuiScreen? screen = CurrentScreen(platform);
        return Owner(platform, screen, ForegroundDialog(RunningGame(screen)));
    }

    private static object? Owner(IControllerPlatformHost platform, GuiScreen? screen, GuiDialog? foreground) =>
        !platform.IsFocused ? platform : foreground ?? (object?)screen;

    /// <summary>Current screen of the screen manager registered as a platform key handler.</summary>
    private static GuiScreen? CurrentScreen(IControllerPlatformHost platform)
    {
        foreach (object handler in platform.Original.keyEventHandlers)
            if (handler is ScreenManager manager) return Gui.CurrentScreenOf(manager);
        return null;
    }

    private static ClientMain? RunningGame(GuiScreen? screen) =>
        screen is GuiScreenRunningGame running ? Gui.RunningGame(running) : null;

    /// <summary>Last open regular dialog with the lowest input order.</summary>
    private static GuiDialog? ForegroundDialog(ClientMain? game)
    {
        GuiDialog? foreground = null;
        if (game?.api?.OpenedGuis is not { } opened) return null;
        foreach (object item in opened)
            if (item is GuiDialog dialog && dialog.IsOpened() && dialog.DialogType == EnumDialogType.Dialog &&
                (foreground == null || dialog.InputOrder <= foreground.InputOrder)) foreground = dialog;
        return foreground;
    }

    private static bool HasSlotGrid(GuiComposer composer)
    {
        if (Elements(composer) is not { } elements) return false;
        foreach (GuiElement element in elements.Values)
            if (element is GuiElementItemSlotGridBase) return true;
        return false;
    }

    private static Dictionary<string, GuiElement>? Elements(GuiComposer composer) =>
        InteractiveElements is { } accessor ? accessor(composer) : null;

    private static AccessTools.FieldRef<GuiComposer, Dictionary<string, GuiElement>>? CreateInteractiveElements()
    {
        FieldInfo? field = typeof(GuiComposer).GetField("interactiveElements", BindingFlags.Instance | BindingFlags.NonPublic);
        return field != null && field.FieldType == typeof(Dictionary<string, GuiElement>)
            ? AccessTools.FieldRefAccess<GuiComposer, Dictionary<string, GuiElement>>(field) : null;
    }

    /// <summary>Extracts visible grid-slot geometry in reverse composer order for semantic inventory operations.</summary>
    internal static List<ControllerSlotTarget> SlotTargets(IReadOnlyList<GuiComposer> composers)
    {
        var targets = new List<ControllerSlotTarget>();
        if (InteractiveElements == null) return targets;
        for (int c = composers.Count - 1; c >= 0; c--)
        {
            GuiComposer composer = composers[c];
            if (!composer.Enabled || !composer.Composed || Elements(composer) is not { } elements) continue;
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
        if (InteractiveElements == null || !platform.HasControllerWindow) return targets;
        (int width, int height) = platform.ControllerWindowSize;
        foreach (GuiComposer composer in composers)
        {
            if (!composer.Composed || Elements(composer) is not { } elements) continue;
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
