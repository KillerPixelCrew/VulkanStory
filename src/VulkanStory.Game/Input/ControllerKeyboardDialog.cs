using Vintagestory.API.Client;

namespace VulkanStory.Game.Input;

/// <summary>Controller-driven text entry for an already focused in-game text field.</summary>
internal sealed class ControllerKeyboardDialog : GuiDialog
{
    private readonly ControllerKeyboardContent content;
    internal GuiElementEditableTextBase Target { get; }

    public ControllerKeyboardDialog(ICoreClientAPI api, GuiElementEditableTextBase target) : base(api)
    {
        Target = target;
        content = new ControllerKeyboardContent(api, target, () => TryClose());
        ComposeKeyboard();
    }

    public override bool DisableMouseGrab => true;
    public override string ToggleKeyCombinationCode => null!;
    public override double DrawOrder => 0.98;
    public override double InputOrder => 0.01;

    public void ApplyPendingRefresh()
    {
        if (!IsOpened() || !content.TakeRefreshRequest()) return;
        ComposeKeyboard();
    }

    private void ComposeKeyboard()
    {
        ClearComposers();
        ElementBounds background = ElementStdBounds.DialogBackground()
            .WithFixedPadding(GuiStyle.ElementToDialogPadding, GuiStyle.ElementToDialogPadding);
        GuiComposer composer = capi.Gui.CreateCompo("vulkanstory-controller-keyboard", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background)
            .AddDialogTitleBar("Controller keyboard", () => TryClose())
            .BeginChildElements(background);

        content.AddKeys(composer);
        SingleComposer = composer.EndChildElements().Compose();
    }
}
