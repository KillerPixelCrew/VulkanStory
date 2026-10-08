using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VulkanStory.Mod;

/// <summary>Nonfocusable HUD displaying the runtime's real-frame/generated-output text with viewport-aware wrapping.</summary>
internal sealed class RendererFpsHud : HudElement
{
    private readonly GuiComposer counter;
    private readonly GuiElementDynamicText text;
    private readonly CairoFont font;
    private readonly ElementBounds bounds;
    private string lastText = "";
    private int lastWidth;
    private float lastScale;
    /// <summary>Creates the retained dynamic text composer; visibility remains controlled by the runtime setting callback.</summary>
    internal RendererFpsHud(ICoreClientAPI api) : base(api)
    {
        font = CairoFont.WhiteSmallishText().WithStroke(ColorUtil.BlackArgbDouble, 1.5);
        bounds = ElementBounds.Fixed(8, 8, 360, 28);
        counter = api.Gui.CreateCompo("vulkanstory-fps", bounds)
            .AddDynamicText("Real: -- FPS | FG output: -- FPS", font, ElementBounds.Fixed(0, 0, 360, 28), "fps").OnlyDynamic().Compose();
        text = counter.GetDynamicText("fps");
    }
    /// <inheritdoc />
    public override bool Focusable => false;
    /// <inheritdoc />
    public override string ToggleKeyCombinationCode => null!;
    private static bool Enabled => AppContext.GetData("VulkanStory.Runtime.ShowFpsCounter") is Func<bool> show && show();
    /// <inheritdoc />
    /// <remarks>Updates text synchronously on the GUI render thread only when text, width, or GUI scale changes.</remarks>
    public override void OnRenderGUI(float deltaTime)
    {
        if (!Enabled) return;
        string current = AppContext.GetData("VulkanStory.Runtime.FpsText") is Func<string> read ? read() : "Renderer inactive";
        int viewportWidth = capi.Render.FrameWidth;
        float scale = RuntimeEnv.GUIScale;
        if (viewportWidth <= 0 || !float.IsFinite(scale) || scale <= 0) return;
        if (current != lastText || viewportWidth != lastWidth || scale != lastScale)
        {
            var measured = ElementBounds.Fixed(0, 0, 1, 1);
            font.AutoBoxSize(current, measured);
            double available = Math.Max(1, viewportWidth / (double)scale - 16);
            bounds.fixedWidth = text.Bounds.fixedWidth = Math.Min(measured.fixedWidth + 4, available);
            bounds.MarkDirtyRecursive(); bounds.CalcWorldBounds();
            // Fixed text bounds allow AutoHeight to grow for wrapped lines.
            // Compose synchronously on the GUI render thread; no queued update
            // may race a later value or the HUD's disposal.
            text.SetNewText(current, autoHeight: true, forceRedraw: true);
            bounds.fixedHeight = text.Bounds.fixedHeight;
            bounds.MarkDirtyRecursive(); bounds.CalcWorldBounds();
            lastText = current; lastWidth = viewportWidth; lastScale = scale;
        }
        counter.Render(deltaTime);
    }
    /// <inheritdoc />
    public override void OnFinalizeFrame(float dt) { if (Enabled) counter.PostRender(dt); }
    /// <inheritdoc />
    public override void Dispose() { counter.Dispose(); base.Dispose(); }
}
