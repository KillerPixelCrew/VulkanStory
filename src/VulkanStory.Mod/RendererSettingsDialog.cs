using Vintagestory.API.Client;
using VulkanStory.Settings;

namespace VulkanStory.Mod;

internal sealed class RendererSettingsDialog : GuiDialog
{
    private readonly RendererSettingsPanel panel;
    internal RendererSettingsDialog(ICoreClientAPI api, string json, Func<string, string?> apply) : base(api)
    {
        panel = new RendererSettingsPanel(json, apply, api.ShowChatMessage, CloseTitle, editingActive: IsOpened);
        ComposePanel();
    }
    public override bool DisableMouseGrab => true;
    public override string ToggleKeyCombinationCode => null!;
    public override void OnRenderGUI(float deltaTime)
    {
        if (panel.TakeRefresh()) ComposePanel();
        base.OnRenderGUI(deltaTime);
    }
    private void CloseTitle() => TryClose();
    private void ComposePanel()
    {
        ClearComposers();
        var background = ElementStdBounds.DialogBackground().WithFixedPadding(GuiStyle.ElementToDialogPadding, GuiStyle.ElementToDialogPadding);
        GuiComposer? composer = null;
        composer = capi.Gui.CreateCompo("vulkanstory-renderer-settings", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background).AddDialogTitleBar("VulkanStory", () =>
            {
                if (IsOpened() && ReferenceEquals(SingleComposer, composer)) CloseTitle();
            }).BeginChildElements(background);
        SingleComposer = panel.Compose(composer, () => IsOpened() && ReferenceEquals(SingleComposer, composer));
    }
}
