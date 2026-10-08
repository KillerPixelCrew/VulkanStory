using Vintagestory.API.Client;

namespace VulkanStory.Game.Input;

/// <summary>Controller-driven text entry for an already focused in-game text field.</summary>
internal sealed class ControllerKeyboardDialog : GuiDialog
{
    private readonly ControllerKeyboardContent content;
    /// <summary>Original focused text field receiving keyboard edits.</summary>
    internal GuiElementEditableTextBase Target { get; }

    /// <summary>Creates and composes an in-game keyboard for the supplied editable target.</summary>
    public ControllerKeyboardDialog(ICoreClientAPI api, GuiElementEditableTextBase target) : base(api)
    {
        Target = target;
        content = new ControllerKeyboardContent(api, target, () => TryClose());
        ComposeKeyboard();
    }

    /// <inheritdoc />
    public override bool DisableMouseGrab => true;
    /// <inheritdoc />
    public override string ToggleKeyCombinationCode => null!;
    /// <inheritdoc />
    public override double DrawOrder => 0.98;
    /// <inheritdoc />
    public override double InputOrder => 0.01;

    /// <summary>Recomposes changed key labels only while this dialog remains open.</summary>
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
