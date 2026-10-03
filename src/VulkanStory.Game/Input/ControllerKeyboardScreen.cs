using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;

namespace VulkanStory.Game.Input;

/// <summary>Main-menu keyboard screen; preserves the underlying screen and text field.</summary>
internal sealed class ControllerKeyboardScreen : GuiScreen
{
    private readonly ControllerKeyboardContent content;
    private bool closeRequested;
    internal GuiElementEditableTextBase Target { get; }

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

    public override bool ShouldDisposePreviousScreen => false;
    public override void OnScreenLoaded() { }

    public void ApplyPendingRefresh()
    {
        if (closeRequested) { Close(); return; }
        if (IsOpened && content.TakeRefreshRequest()) ComposeKeyboard();
    }

    private void RequestClose() => closeRequested = true;

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

    public override void RenderToPrimary(float dt) => ParentScreen.RenderToPrimary(dt);
    public override void RenderAfterPostProcessing(float dt) => ParentScreen.RenderAfterPostProcessing(dt);
    public override void RenderAfterFinalComposition(float dt) => ParentScreen.RenderAfterFinalComposition(dt);
    public override void RenderAfterBlit(float dt) => ParentScreen.RenderAfterBlit(dt);
    public override void RenderToDefaultFramebuffer(float dt)
    {
        ParentScreen.RenderToDefaultFramebuffer(dt);
        if (!IsOpened || ElementComposer?.Composed != true) return;
        ElementComposer.Render(dt);
        ElementComposer.PostRender(dt);
    }

    public override void OnMouseDown(MouseEvent e) => ElementComposer.OnMouseDown(e);
    public override void OnMouseUp(MouseEvent e) => ElementComposer.OnMouseUp(e);
    public override void OnMouseMove(MouseEvent e) => ElementComposer.OnMouseMove(e);
    public override void OnMouseWheel(MouseWheelEventArgs e) => ElementComposer.OnMouseWheel(e);
    public override void OnKeyPress(KeyEvent e) => content.PhysicalKeyPress(e);
    public override void OnKeyDown(KeyEvent e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.KeyCode is (int)GlKeys.BackSpace or (int)GlKeys.Delete or
            (int)GlKeys.Left or (int)GlKeys.Right or (int)GlKeys.Home or (int)GlKeys.End)
            content.PhysicalKeyDown(e);
    }
    public override bool OnBackPressed() { RequestClose(); return true; }
    public override bool OnWindowClosed() => ParentScreen.OnWindowClosed();
    public override void OnWindowResized(int width, int height)
    {
        Close();
        ParentScreen.OnWindowResized(width, height);
    }
}
