using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace VulkanStory.Mod;

internal sealed class RendererFpsHud : HudElement
{
    private readonly GuiComposer counter;
    private readonly GuiElementDynamicText text;
    private string lastText = "";
    internal RendererFpsHud(ICoreClientAPI api) : base(api)
    {
        var font = CairoFont.WhiteSmallishText().WithStroke(ColorUtil.BlackArgbDouble, 1.5);
        counter = api.Gui.CreateCompo("vulkanstory-fps", ElementBounds.Fixed(8, 8, 360, 28))
            .AddDynamicText("Real: -- FPS | FG output: -- FPS", font, ElementBounds.Fill, "fps").OnlyDynamic().Compose();
        text = counter.GetDynamicText("fps");
    }
    public override bool Focusable => false;
    public override string ToggleKeyCombinationCode => null!;
    private static bool Enabled => AppContext.GetData("VulkanStory.Runtime.ShowFpsCounter") is Func<bool> show && show();
    public override void OnRenderGUI(float deltaTime)
    {
        if (!Enabled) return;
        string current = AppContext.GetData("VulkanStory.Runtime.FpsText") is Func<string> read ? read() : "Renderer inactive";
        if (current != lastText) { lastText = current; text.SetNewTextAsync(current); }
        counter.Render(deltaTime);
    }
    public override void OnFinalizeFrame(float dt) { if (Enabled) counter.PostRender(dt); }
    public override void Dispose() { counter.Dispose(); base.Dispose(); }
}
