using VulkanStory.Render.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VulkanStory.Game;

// Retained VulkanClientPlatform.UiSeparation bodies and shader sources.
// Baseline 386e0d05386d0b228b439d09aeca851428f7bbf3; state belongs to this adapter.
internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeFullscreenPass nativeSceneNoHudCopy = new("ui-compose", [], ["uiTex"]);
    private readonly NativeFullscreenPass nativeUiCompose = new("ui-compose", [], ["uiTex"]);
    private readonly NativeFullscreenPass nativeGeneratedUiCompose = new("ui-compose", [], ["uiTex"]);
    private int uiComposeProgram;
    private bool uiComposeFailed;
    internal bool UiScopeOpen => Stated.UiImageFramebuffer > 0;

    private string OwnedShaderSource(string pass, string stage)
    {
        // This renderer also owns the early menu, before the mod AssetsLoaded
        // stage. Read the game's selected base-asset map, as ShaderRegistry does.
        // The same map contains selected mod overrides after their normal load.
        IAsset? replacement = platform!.AssetManager?.TryGet_BaseAssets(new AssetLocation("shaders/" + pass + "." + stage));
        if (replacement != null) return replacement.ToText();
        using Stream stream = typeof(GameGraphicsAdapter).Assembly.GetManifestResourceStream(
            "VulkanStory.Game.Shaders." + pass + "." + stage) ?? throw new InvalidOperationException("Owned shader is missing: " + pass);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private int UiComposeProgram() => OwnedProgram("ui-compose", ref uiComposeProgram, ref uiComposeFailed);
    private int OwnedProgram(string pass, ref int program, ref bool failed, string prefix = "")
    {
        var renderer = RequireDevice();
        if (program > 0 || failed) return program;
        var vertex = new ShaderStageDefinition { Type = ShaderStageType.VertexShader, Code = OwnedShaderSource(pass, "vsh"), PrefixCode = prefix };
        var fragment = new ShaderStageDefinition { Type = ShaderStageType.FragmentShader, Code = OwnedShaderSource(pass, "fsh"), PrefixCode = prefix };
        if (renderer.CompileShader(vertex) && renderer.CompileShader(fragment))
            program = renderer.LinkProgram(new ShaderProgramDefinition(pass, vertex, fragment, null, null));
        if (program <= 0)
        {
            failed = true;
            platform!.Logger.Error("VulkanStory: owned shader '{0}' failed: {1}", pass, renderer.GetError());
        }
        return program;
    }
    private Vintagestory.API.Client.FrameBufferRef? UiTarget(int index)
    {
        var targets = platform!.FrameBuffers;
        return index >= 0 && targets != null && index < targets.Count &&
            targets[index] is { Disposed: false, ColorTextureIds.Length: > 0 } target ? target : null;
    }
    internal void CaptureSceneNoHud()
    {
        var renderer = RequireDevice();
        SceneNoHudCaptured = false;
        var snapshot = UiTarget(sceneNoHudIndex);
        if (snapshot == null || renderer.DefaultColorTextureId <= 0) return;
        int program = UiComposeProgram();
        if (program <= 0) return;
        int scene = renderer.DefaultColorTextureId;
        var pipeline = NativePipelineFor(nativeSceneNoHudCopy, program, snapshot.FboId);
        try
        {
            if (pipeline != null && BeginNativeBlitPass("SceneNoHud/" + SceneNoHudIndex,
                snapshot.FboId, snapshot.Width, snapshot.Height, [scene]))
                SceneNoHudCaptured = renderer.DrawNativeFullscreen(pipeline,
                    [new NativeTexture(nativeSceneNoHudCopy.Samplers[0], scene)], requirePipeline: true);
        }
        finally { renderer.EndNativePass(); }
    }
    internal void OpenUiScope()
    {
        var renderer = RequireDevice();
        CloseUiScope();
        var ui = UiTarget(uiTargetIndex);
        if (ui == null || UiComposeProgram() <= 0) return;
        renderer.ClearNativeColor(ui.FboId, 0, 0f, 0f, 0f, 0f);
        renderer.ClearNativeDepth(ui.FboId, 1f);
        renderer.RedirectDefaultFramebuffer(ui.FboId);
        Stated.UiImageFramebuffer = ui.FboId;
    }
    internal void ComposeUiTarget()
    {
        var renderer = RequireDevice();
        if (!UiScopeOpen) return;
        var ui = UiTarget(uiTargetIndex);
        CloseUiScope();
        LoadFramebuffer(EnumFrameBuffer.Default);
        renderer.ClearNativeDepth(NativeDefaultTarget, 1f);
        ToggleBlend(true, EnumBlendMode.Standard);
        if (ui == null || uiComposeProgram <= 0) return;
        int color = ui.ColorTextureIds[0];
        var display = RequireFramebufferHost().PixelSize();
        AttachmentBlend[] blend = [AttachmentBlend.For(true, RenderBlendMode.PremultipliedAlpha)];
        var pipeline = NativePipelineFor(nativeUiCompose, uiComposeProgram, NativeDefaultTarget, blend);
        try
        {
            if (pipeline != null && BeginNativeBlitPass("UiCompose/Default", NativeDefaultTarget,
                display.Width, display.Height, [color]))
                renderer.DrawNativeFullscreen(pipeline, [new NativeTexture(nativeUiCompose.Samplers[0], color)], requirePipeline: true);
        }
        finally { renderer.EndNativePass(); }
    }
    internal bool ComposeGeneratedFrameUi(FrameBufferRef output, FrameBufferRef ui)
    {
        var renderer = RequireDevice();
        int program = UiComposeProgram();
        if (program <= 0) return false;
        AttachmentBlend[] blend = [AttachmentBlend.For(true, RenderBlendMode.PremultipliedAlpha)];
        var pipeline = NativePipelineFor(nativeGeneratedUiCompose, program, output.FboId, blend);
        try
        {
            return pipeline != null && BeginNativeBlitPass("UiCompose/Generated", output.FboId,
                output.Width, output.Height, [ui.ColorTextureIds[0]]) &&
                renderer.DrawNativeFullscreen(pipeline,
                    [new NativeTexture(nativeGeneratedUiCompose.Samplers[0], ui.ColorTextureIds[0])], requirePipeline: true);
        }
        finally { renderer.EndNativePass(); }
    }
    internal void ClearUiOrDefaultDepth(float depth) =>
        RequireDevice().ClearNativeDepth(NativeDefaultTarget, depth);
    internal void AbortUiScope() { RequireDevice(); CloseUiScope(); }
    internal void ReloadUiProgram()
    {
        var renderer = RequireDevice();
        CloseUiScope();
        if (uiComposeProgram > 0) renderer.DeleteProgram(uiComposeProgram);
        uiComposeProgram = 0; uiComposeFailed = false;
        nativeSceneNoHudCopy.Pipeline = nativeUiCompose.Pipeline = nativeGeneratedUiCompose.Pipeline = null;
    }
}
