using Vintagestory.API.Client;

namespace VulkanStory.Settings;

/// <summary>Owns a settings body's texture and widgets inside a fixed scroll viewport.</summary>
internal sealed class SettingsScrollContent : GuiElement
{
    internal GuiComposer Content { get; }
    private readonly ElementBounds body;
    private bool disposed;

    internal SettingsScrollContent(ICoreClientAPI api, ElementBounds viewport, double width) : base(api, viewport)
    {
        body = ElementBounds.Fixed(0, 0, width, 1);
        viewport.WithChild(body);
        Content = api.Gui.CreateCompo("vulkanstory-settings-content", body);
    }

    internal void SetHeight(double height) => body.fixedHeight = Math.Max(1, height);

    internal void ScrollTo(float value)
    {
        body.fixedY = -value;
        body.MarkDirtyRecursive();
        body.CalcWorldBounds();
    }

    public override bool Focusable => true;
    private bool PopupContains(int x, int y)
    {
        foreach (string key in new[] { "Upscaler", "UpscalerQuality", "DlssPreset", "FrameGeneration",
            "FrameGenerationMultiplier", "LowLatencyMode", "AmbientOcclusion", "AmbientOcclusionPreset", "WorldgenWorkers" })
            if (Content.GetElement(key) is GuiElementDropDown dropdown && dropdown.listMenu.IsOpened &&
                dropdown.IsPositionInside(x, y)) return true;
        return false;
    }

    public override bool IsPositionInside(int x, int y) => base.IsPositionInside(x, y) || PopupContains(x, y);

    public override void BeforeCalcBounds()
    {
        // Static switch/slider backgrounds must belong to the moving body,
        // rather than the outer dialog's stationary texture.
        Content.ReCompose();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        api.Render.PushScissor(Bounds);
        try { Content.Render(deltaTime); }
        finally { api.Render.PopScissor(); }
        MouseOverCursor = Content.MouseOverCursor;
    }

    public override void PostRenderInteractiveElements(float deltaTime) => Content.PostRender(deltaTime);

    public override void OnMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        if (IsPositionInside(args.X, args.Y)) Content.OnMouseDown(args);
    }

    // Continue forwarding release/move events outside the viewport while dragging.
    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args) => Content.OnMouseUp(args);
    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args) => Content.OnMouseMove(args);
    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (IsPositionInside(api.Input.MouseX, api.Input.MouseY)) Content.OnMouseWheel(args);
    }

    public override void OnFocusGained()
    {
        base.OnFocusGained();
        if (Content.CurrentTabIndexElement == null && Content.FirstTabbableElement is { } first)
            Content.FocusElement(first.TabIndex);
    }

    public override void OnFocusLost()
    {
        Content.UnfocusOwnElements();
        base.OnFocusLost();
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        var focused = Content.CurrentTabIndexElement;
        bool leaving = args.KeyCode == (int)GlKeys.Tab && focused != null &&
            (args.ShiftPressed ? focused.TabIndex == 0 : focused.TabIndex == Content.MaxTabIndex);
        // Let the outer composer move to navigation/footer controls at either end.
        Content.OnKeyDown(args, HasFocus && !leaving);
    }
    public override void OnKeyUp(ICoreClientAPI api, KeyEvent args) => Content.OnKeyUp(args);
    public override void OnKeyPress(ICoreClientAPI api, KeyEvent args) => Content.OnKeyPress(args);

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Content.Dispose();
        base.Dispose();
    }
}
