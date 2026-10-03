using Vintagestory.API.Client;
using Vintagestory.Client;
using VulkanStory.Platform.Sdl;

namespace VulkanStory.Game.Input;

// Controller keyboard overlays supply their original editable target here.
internal sealed record ControllerTextTargets(
    Func<GuiScreen, GuiElementEditableTextBase?> Screen,
    Func<GuiDialog, GuiElementEditableTextBase?> Dialog);

/// <summary>Retained GUI focus and native IME-area coordination for the SDL host.</summary>
internal sealed class SdlGuiTextInput
{
    private readonly SdlWindowHost window;
    private readonly GameGuiBindings bindings;
    private readonly ControllerTextTargets controllerTargets;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private GuiElementEditableTextBase? target;
    private bool active;
    private (int X, int Y, int Width, int Height, int Cursor)? area;
    internal long TargetRevision { get; private set; }

    internal SdlGuiTextInput(SdlWindowHost window, ControllerTextTargets controllerTargets)
    {
        this.window = window;
        this.controllerTargets = controllerTargets;
        bindings = new GameGuiBindings();
    }

    internal bool HasCurrentTarget()
    {
        RequireOwner();
        return active && window.IsFocused && target != null && ReferenceEquals(target, FocusedEditableText());
    }

    internal void Sync()
    {
        RequireOwner();
        GuiElementEditableTextBase? focused = window.IsFocused ? FocusedEditableText() : null;
        if (focused is null) { Stop(); return; }
        if (!ReferenceEquals(focused, target))
        {
            if (active) window.ClearComposition();
            target = focused;
            TargetRevision++;
        }

        var logical = window.WindowSize;
        if (logical.Width <= 0 || logical.Height <= 0) return;
        var pixels = window.PixelSize;
        double scaleX = (double)logical.Width / Math.Max(1, pixels.Width);
        double scaleY = (double)logical.Height / Math.Max(1, pixels.Height);
        var bounds = focused.Bounds;
        int x = Math.Clamp((int)Math.Round(bounds.absX * scaleX), 0, logical.Width - 1);
        int y = Math.Clamp((int)Math.Round(bounds.absY * scaleY), 0, logical.Height - 1);
        int width = Math.Clamp((int)Math.Round(bounds.OuterWidth * scaleX), 1, logical.Width - x);
        int height = Math.Clamp((int)Math.Round(bounds.OuterHeight * scaleY), 1, logical.Height - y);
        double caretPixelX = bounds.renderX + bindings.CaretX(focused) - bindings.LeftOffset(focused);
        int cursor = SdlWindowCoordinates.TextInputCursorOffset(
            caretPixelX, bounds.absX, width, logical.Width, pixels.Width);
        var nextArea = (x, y, width, height, cursor);
        if (area != nextArea)
        {
            window.SetTextInputArea(x, y, width, height, cursor);
            area = nextArea;
        }
        if (!active)
        {
            window.SetTextInputActive(true);
            active = true;
        }
    }

    internal GuiElementEditableTextBase? FocusedEditableText()
    {
        RequireOwner();
        if (bindings.CurrentScreen is not { } screen) return null;
        if (controllerTargets.Screen(screen) is { } keyboardTarget) return keyboardTarget;
        if (screen is GuiScreenRunningGame running)
        {
            if (bindings.RunningGame(running) is not { } game || bindings.LoadedGuis(game) is not { } dialogs)
                return null;
            foreach (GuiDialog dialog in dialogs)
            {
                if (!dialog.ShouldReceiveKeyboardEvents()) continue;
                if (dialog.IsOpened() && controllerTargets.Dialog(dialog) is { } dialogTarget)
                    return dialogTarget;
                foreach (GuiComposer composer in dialog.Composers.Values)
                    if (FocusedEditableText(composer) is { } text) return text;
            }
            return null;
        }
        return FocusedEditableText(screen.ElementComposer);
    }

    private static GuiElementEditableTextBase? FocusedEditableText(GuiComposer? composer)
    {
        if (composer?.Enabled != true || !composer.Composed) return null;
        GuiElement? focused = composer.CurrentTabIndexElement;
        while (focused is GuiElementContainer container) focused = container.CurrentTabIndexElement;
        return focused is GuiElementEditableTextBase text && text.HasFocus ? text : null;
    }

    internal void Stop()
    {
        RequireOwner();
        if (active)
        {
            window.ClearComposition();
            window.SetTextInputActive(false);
        }
        if (active || target != null) TargetRevision++;
        active = false;
        target = null;
        area = null;
    }

    private void RequireOwner()
    {
        if (ownerThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("GUI/IME coordination must stay on the SDL owner thread.");
    }
}
