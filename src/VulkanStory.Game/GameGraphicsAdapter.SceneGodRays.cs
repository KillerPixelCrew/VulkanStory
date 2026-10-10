using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeFullscreenPass nativeSceneGodRays = new("scene-godrays", [], ["godrayParts"]);
    private int sceneGodRaysProgram;
    private bool sceneGodRaysFailed;
    internal bool GodRaysInScene { get; set; }

    /// <summary>Reconstructs sky lighting with the selected upscaler's scene, using matching raw colour and glow inputs.</summary>
    internal void RenderSceneGodRays()
    {
        if (!AoSettings.UpscalerReplacesTaa || !GameFrameBindings.RenderGodRays(platform!)) return;
        var renderer = RequireDevice();
        var buffers = platform!.FrameBuffers;
        FrameBufferRef primary = NativePostTarget(buffers, PrimaryIndex) ??
            throw new InvalidOperationException("Required scene god-ray primary target is missing.");
        if (primary.ColorTextureIds.Length < 2)
            throw new InvalidOperationException("Required scene god-ray glow input is missing.");
        FrameBufferRef rays = NativePostTarget(buffers, GodRaysIndex) ??
            throw new InvalidOperationException("Required scene god-ray output target is missing.");
        int program = OwnedProgram("scene-godrays", ref sceneGodRaysProgram, ref sceneGodRaysFailed);
        if (program <= 0)
            throw new InvalidOperationException("Required scene god-ray composition shader is unavailable.");
        NativePipeline pipeline = NativePostPipeline(nativeSceneGodRays, program, primary.FboId, 1u,
            NativeSceneGodRaysBlend(), false, false, CompareOp.Less) ??
            throw new InvalidOperationException("Required scene god-ray composition pipeline is unavailable.");

        // Both sampled inputs still carry the same raster jitter. Once their light is in colour
        // zero, the SDK reconstructs it along with the scene rather than adding raw glow later.
        RenderGodRaysPost(primary.ColorTextureIds[0], primary.ColorTextureIds[1], requirePipeline: true);
        LoadFramebuffer(EnumFrameBuffer.Primary);
        bool drawn = false;
        try
        {
            if (BeginNativeBlitPass("SceneGodRays/" + primary.FboId, primary.FboId,
                primary.Width, primary.Height, [rays.ColorTextureIds[0]]))
                drawn = renderer.DrawNativeFullscreen(pipeline,
                    [new NativeTexture(nativeSceneGodRays.Samplers[0], rays.ColorTextureIds[0])], requirePipeline: true);
        }
        finally { renderer.EndNativePass(); }
        if (!drawn)
            throw new InvalidOperationException("Required scene god-ray composition did not draw: " + renderer.NativeDrawRefusal);
        GodRaysInScene = true;
    }

    private static AttachmentBlend[] NativeSceneGodRaysBlend()
    {
        AttachmentBlend blend = AttachmentBlend.Default;
        blend.Enabled = true;
        blend.SrcColor = BlendFactor.One;
        blend.DstColor = BlendFactor.One;
        blend.ColorOp = BlendOp.Add;
        blend.SrcAlpha = BlendFactor.Zero;
        blend.DstAlpha = BlendFactor.One;
        blend.AlphaOp = BlendOp.Add;
        return [blend];
    }

    internal void ReloadSceneGodRaysProgram()
    {
        var renderer = RequireLifecycleDevice();
        if (sceneGodRaysProgram > 0) renderer.DeleteProgram(sceneGodRaysProgram);
        sceneGodRaysProgram = 0;
        sceneGodRaysFailed = false;
        nativeSceneGodRays.Pipeline = null;
        GodRaysInScene = false;
    }
}
