using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;

namespace VulkanStory.Game.Input;

/// <summary>Main-menu keyboard screen; preserves the underlying screen and text field.</summary>
internal sealed class ControllerKeyboardScreen : GuiScreen
{
    private readonly ControllerKeyboardContent content;
    private bool closeRequested;
    /// <summary>Editable field retained on the parent screen and receiving keyboard edits.</summary>
    internal GuiElementEditableTextBase Target { get; }

    /// <summary>Creates a keyboard overlay while retaining the parent's screen and rendering behavior.</summary>
    public ControllerKeyboardScreen(ScreenManager manager, GuiScreen parent,
        GuiElementEditableTextBase target) : base(manager, parent)
    {
        ShowMainMenu = parent.ShowMainMenu;
        RenderBg = parent.RenderBg;
        Target = target;
        content = new ControllerKeyboardContent(manager.api, target, RequestClose,
            () => ParentScreen.OnKeyDown(new KeyEvent { KeyCode = (int)GlKeys.Enter }));
        ComposeKeyboard();
    }

    /// <inheritdoc />
    public override bool ShouldDisposePreviousScreen => false;
    /// <inheritdoc />
    public override void OnScreenLoaded() { }

    /// <summary>Processes deferred dismissal or refreshes the key grid while this screen is open.</summary>
    public void ApplyPendingRefresh()
    {
        if (closeRequested) { Close(); return; }
        if (IsOpened && content.TakeRefreshRequest()) ComposeKeyboard();
    }

    private void RequestClose() => closeRequested = true;

    /// <summary>Returns the screen manager to the retained parent if this keyboard is still open.</summary>
    public void Close()
    {
        if (IsOpened) ScreenManager.LoadScreen(ParentScreen);
    }

    private void ComposeKeyboard()
    {
        ElementBounds background = ElementStdBounds.DialogBackground()
            .WithFixedPadding(GuiStyle.ElementToDialogPadding, GuiStyle.ElementToDialogPadding);
        GuiComposer composer = ScreenManager.GuiComposers.Create(
                "optimum-mainmenu-controller-keyboard", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background)
            .BeginChildElements(background);
        content.AddKeys(composer);
        ElementComposer = composer.EndChildElements().Compose();
    }

    /// <inheritdoc />
    public override void RenderToPrimary(float dt) => ParentScreen.RenderToPrimary(dt);
    /// <inheritdoc />
    public override void RenderAfterPostProcessing(float dt) => ParentScreen.RenderAfterPostProcessing(dt);
    /// <inheritdoc />
    public override void RenderAfterFinalComposition(float dt) => ParentScreen.RenderAfterFinalComposition(dt);
    /// <inheritdoc />
    public override void RenderAfterBlit(float dt) => ParentScreen.RenderAfterBlit(dt);
    /// <inheritdoc />
    /// <remarks>Renders the retained parent before this keyboard's composed elements.</remarks>
    public override void RenderToDefaultFramebuffer(float dt)
    {
        ParentScreen.RenderToDefaultFramebuffer(dt);
        if (!IsOpened || ElementComposer?.Composed != true) return;
        ElementComposer.Render(dt);
        ElementComposer.PostRender(dt);
    }

    /// <inheritdoc />
    public override void OnMouseDown(MouseEvent e) => ElementComposer.OnMouseDown(e);
    /// <inheritdoc />
    public override void OnMouseUp(MouseEvent e) => ElementComposer.OnMouseUp(e);
    /// <inheritdoc />
    public override void OnMouseMove(MouseEvent e) => ElementComposer.OnMouseMove(e);
    /// <inheritdoc />
    public override void OnMouseWheel(MouseWheelEventArgs e) => ElementComposer.OnMouseWheel(e);
    /// <inheritdoc />
    public override void OnKeyPress(KeyEvent e) => content.PhysicalKeyPress(e);
    /// <inheritdoc />
    /// <remarks>Forwards unhandled editing/navigation keys to the retained editable target.</remarks>
    public override void OnKeyDown(KeyEvent e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.KeyCode is (int)GlKeys.BackSpace or (int)GlKeys.Delete or
            (int)GlKeys.Left or (int)GlKeys.Right or (int)GlKeys.Home or (int)GlKeys.End)
            content.PhysicalKeyDown(e);
    }
    /// <inheritdoc />
    public override bool OnBackPressed() { RequestClose(); return true; }
    /// <inheritdoc />
    public override bool OnWindowClosed() => ParentScreen.OnWindowClosed();
    /// <inheritdoc />
    /// <remarks>Closes the keyboard before the parent's resize handling runs.</remarks>
    public override void OnWindowResized(int width, int height)
    {
        Close();
        ParentScreen.OnWindowResized(width, height);
    }
}
