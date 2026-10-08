using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VulkanStory.Settings;

namespace VulkanStory.Mod;

/// <summary>Ordinary mod/chat dialog hosting the shared renderer settings panel and preview restoration.</summary>
internal sealed class RendererSettingsDialog : GuiDialog
{
    private readonly RendererSettingsPanel panel;
    private float scrollOffset;
    private (int Width, int Height, float Scale) geometry;
    private string composedPage = "";
    /// <summary>Creates the panel from current settings JSON using the supplied persist/apply callback.</summary>
    internal RendererSettingsDialog(ICoreClientAPI api, string json, Func<string, string?> apply) : base(api)
    {
        panel = new RendererSettingsPanel(json, apply, api.ShowChatMessage, CloseTitle, editingActive: IsOpened);
        ComposePanel();
    }
    /// <inheritdoc />
    public override bool DisableMouseGrab => true;
    /// <inheritdoc />
    /// <remarks>Restores any unsaved live preview and reports restoration failures through chat.</remarks>
    public override void OnGuiClosed()
    {
        if (panel.EndPreview() is string error) capi.ShowChatMessage("Could not restore settings: " + error);
        base.OnGuiClosed();
    }
    /// <inheritdoc />
    public override string ToggleKeyCombinationCode => null!;
    /// <inheritdoc />
    /// <remarks>Recomposes the panel when it requests a page, error, or periodic status refresh.</remarks>
    public override void OnRenderGUI(float deltaTime)
    {
        if (panel.TakeRefresh() || geometry != (capi.Render.FrameWidth, capi.Render.FrameHeight, RuntimeEnv.GUIScale)) ComposePanel();
        base.OnRenderGUI(deltaTime);
    }
    private void CloseTitle() => TryClose();
    private void ComposePanel()
    {
        ClearComposers();
        geometry = (capi.Render.FrameWidth, capi.Render.FrameHeight, RuntimeEnv.GUIScale);
        double scale = Math.Max(.1, RuntimeEnv.GUIScale), padding = GuiStyle.ElementToDialogPadding;
        double width = Math.Max(180, Math.Min(580, capi.Render.FrameWidth / scale - 2 * padding - 20));
        double height = Math.Max(108, Math.Min(620, capi.Render.FrameHeight / scale - 2 * padding - 30));
        double contentWidth = width - 25, visibleHeight = height - 82;
        if (composedPage != panel.CurrentPageName) scrollOffset = 0;
        composedPage = panel.CurrentPageName;
        var background = ElementBounds.Fixed(0, 0, width, height).WithFixedPadding(padding);
        GuiComposer? composer = null;
        composer = capi.Gui.CreateCompo("vulkanstory-renderer-settings", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background).AddDialogTitleBar("VulkanStory", () =>
            {
                if (IsOpened() && ReferenceEquals(SingleComposer, composer)) CloseTitle();
            }).BeginChildElements(background);
        bool Live() => IsOpened() && ReferenceEquals(SingleComposer, composer);
        panel.AddCompactPageNavigation(composer, contentWidth, Live);
        var clip = ElementBounds.Fixed(0, 42, contentWidth, visibleHeight);
        var body = ElementBounds.Fixed(0, 0, contentWidth, 1).WithParent(clip);
        composer.AddVerticalScrollbar(value =>
        {
            if (!ReferenceEquals(SingleComposer, composer)) return;
            scrollOffset = value; body.fixedY = -value;
            body.MarkDirtyRecursive(); body.CalcWorldBounds();
        }, ElementBounds.Fixed(width - 20, 42, 20, visibleHeight), "vulkanstory-scroll")
            .BeginClip(clip).BeginChildElements(body);
        Action initialize = panel.AddContent(composer, contentWidth, out double contentHeight,
            canInteract: Live, includeFooter: false, includePages: false);
        body.fixedHeight = contentHeight;
        composer.EndChildElements().EndClip();
        panel.AddFooter(composer, contentWidth, height - 30, Live);
        SingleComposer = composer.EndChildElements().Compose();
        initialize();
        var scrollbar = composer.GetScrollbar("vulkanstory-scroll");
        scrollbar.SetHeights((float)visibleHeight, (float)Math.Max(visibleHeight, contentHeight));
        scrollbar.CurrentYPosition = Math.Clamp(scrollOffset, 0, (float)Math.Max(0, contentHeight - visibleHeight));
        scrollbar.TriggerChanged();
    }
}
