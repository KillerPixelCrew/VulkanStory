using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using VulkanStory.Settings;

namespace VulkanStory.Game;

internal sealed class RendererSettingsScreen : GuiScreen
{
    private readonly RendererSettingsPanel panel;
    private bool closeRequested;
    internal RendererSettingsScreen(ScreenManager manager, GuiScreen parent, string json, System.Func<string, string?> save) : base(manager, parent)
    {
        ShowMainMenu = parent.ShowMainMenu; RenderBg = parent.RenderBg;
        panel = new RendererSettingsPanel(json, save,
            message => manager.api.Logger.Notification("{0}", message), () => closeRequested = true,
            editingActive: () => IsOpened && !closeRequested);
        ComposePanel();
    }
    public override bool ShouldDisposePreviousScreen => false;
    public override void OnScreenLoaded() { }
    private void ComposePanel()
    {
        ElementComposer?.Dispose();
        var background = ElementStdBounds.DialogBackground().WithFixedPadding(GuiStyle.ElementToDialogPadding, GuiStyle.ElementToDialogPadding);
        GuiComposer? composer = null;
        composer = ScreenManager.GuiComposers.Create("vulkanstory-mainmenu-settings", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background).AddDialogTitleBar("VulkanStory", () =>
            {
                if (IsOpened && ReferenceEquals(ElementComposer, composer)) closeRequested = true;
            }).BeginChildElements(background);
        ElementComposer = panel.Compose(composer, () => IsOpened && !closeRequested && ReferenceEquals(ElementComposer, composer));
    }
    public override void RenderToPrimary(float dt) => ParentScreen.RenderToPrimary(dt);
    public override void RenderAfterPostProcessing(float dt) => ParentScreen.RenderAfterPostProcessing(dt);
    public override void RenderAfterFinalComposition(float dt) => ParentScreen.RenderAfterFinalComposition(dt);
    public override void RenderAfterBlit(float dt) => ParentScreen.RenderAfterBlit(dt);
    public override void RenderToDefaultFramebuffer(float dt)
    {
        ParentScreen.RenderToDefaultFramebuffer(dt);
        if (!IsOpened) return;
        if (closeRequested) { ScreenManager.LoadScreen(ParentScreen); return; }
        if (panel.TakeRefresh()) ComposePanel();
        if (ElementComposer?.Composed != true) return;
        ElementComposer.Render(dt); ElementComposer.PostRender(dt);
    }
    public override void OnMouseDown(MouseEvent e) => ElementComposer.OnMouseDown(e);
    public override void OnMouseUp(MouseEvent e) => ElementComposer.OnMouseUp(e);
    public override void OnMouseMove(MouseEvent e) => ElementComposer.OnMouseMove(e);
    public override void OnMouseWheel(MouseWheelEventArgs e) => ElementComposer.OnMouseWheel(e);
    public override bool OnBackPressed() { closeRequested = true; return true; }
    public override bool OnWindowClosed() => ParentScreen.OnWindowClosed();
    public override void OnWindowResized(int width, int height)
    { ParentScreen.OnWindowResized(width, height); if (IsOpened) ComposePanel(); }
}
