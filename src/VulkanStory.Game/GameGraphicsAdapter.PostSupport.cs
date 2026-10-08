using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private const int PrimaryIndex = (int)EnumFrameBuffer.Primary, FindBrightIndex = (int)EnumFrameBuffer.FindBright;
    private const int BlurHorizontalMedResIndex = (int)EnumFrameBuffer.BlurHorizontalMedRes;
    private const int BlurVerticalMedResIndex = (int)EnumFrameBuffer.BlurVerticalMedRes;
    private const int BlurHorizontalLowResIndex = (int)EnumFrameBuffer.BlurHorizontalLowRes;
    private const int BlurVerticalLowResIndex = (int)EnumFrameBuffer.BlurVerticalLowRes;
    private const int GodRaysIndex = (int)EnumFrameBuffer.GodRays, LumaIndex = (int)EnumFrameBuffer.Luma;
    private RendererSettingsState? postSettings;
    internal bool TaaResolvedThisFrame { get; set; }
    private DefaultShaderUniforms PostUniforms => platform!.ShaderUniforms;
    private int GodRaysSampleLimit => (postSettings ?? throw new InvalidOperationException("Post settings are not attached."))
        .Settings.GodRaysSampleCap ? 100 : 180;
    internal void ConfigurePostSettings(RendererSettingsState settings)
    {
        if (Environment.CurrentManagedThreadId != ownerThread || device == null)
            throw new InvalidOperationException("Post configuration requires the live session owner thread.");
        ArgumentNullException.ThrowIfNull(settings);
        if (postSettings != null) throw new InvalidOperationException("Post settings already have an owner.");
        postSettings = settings;
    }
    private float NativeAmbientBloomLevel() => ClientSettings.AmbientBloomLevel / 100f +
        PostUniforms.AmbientBloomLevelAdd[0] + PostUniforms.AmbientBloomLevelAdd[1] +
        PostUniforms.AmbientBloomLevelAdd[2] + PostUniforms.AmbientBloomLevelAdd[3];
    private static FrameBufferRef? NativePostTarget(IReadOnlyList<FrameBufferRef>? buffers, int index) =>
        buffers != null && index >= 0 && index < buffers.Count && buffers[index] is
            { Disposed: false, FboId: not 0, ColorTextureIds.Length: > 0 } target ? target : null;
    private static bool NativeProgramUsable(ShaderProgramBase? program) =>
        program is { LoadError: false, Disposed: false, ProgramId: > 0 };
    private bool BeginNativePostPass(string name, int framebuffer, int[] reads, bool transient) =>
        RequireDevice().BeginNativePass(new NativePassDescription
        {
            Name = name, FramebufferId = framebuffer, ColorSlots = 1u,
            Reads = reads, TransientSlots = transient ? 1u : 0u, Flags = PassFlags.None,
        });
    private static AttachmentBlend[] NativeStandardBlend()
    {
        AttachmentBlend blend = AttachmentBlend.Default; blend.Enabled = true;
        return [blend];
    }
    private void WriteNativeVec3(NativePipeline pipeline, NativeUniform uniform, Vec3f value) =>
        RequireDevice().WriteNative(pipeline, uniform, value.X, value.Y, value.Z);
    private NativePipeline? NativePostPipeline(NativeFullscreenPass pass, ShaderProgramBase program,
        int framebuffer, uint slots, AttachmentBlend[] blend, bool depthTest, bool depthWrite, CompareOp depthCompare) =>
        NativePostPipeline(pass, program.ProgramId, framebuffer, slots, blend, depthTest, depthWrite, depthCompare);
    private NativePipeline? NativePostPipeline(NativeFullscreenPass pass, int program,
        int framebuffer, uint slots, AttachmentBlend[] blend, bool depthTest, bool depthWrite, CompareOp depthCompare)
    {
        var renderer = RequireDevice();
        RenderTargetFormats? formats = renderer.NativeTargetFormats(framebuffer, slots);
        if (formats == null) return null;
        NativePipeline? pipeline = renderer.RequestNativePipeline(new NativePipelineDescription
        {
            ProgramId = program, PassName = pass.PassName, Blend = blend,
            DepthTest = depthTest, DepthWrite = depthWrite, DepthCompare = depthCompare,
            Cull = CullModeFlags.None, Topology = PrimitiveTopology.TriangleList, Targets = formats,
        }, out string reason);
        if (pipeline == null)
        {
            if (!pass.Reported) platform!.Logger.Warning("VulkanStory: no native pipeline for '{0}': {1}", pass.PassName, reason);
            pass.Reported = true; pass.Pipeline = null; return null;
        }
        pass.Reported = false;
        if (!ReferenceEquals(pass.Pipeline, pipeline)) pass.Adopt(pipeline, formats);
        return pipeline;
    }
    private void DrawPostTriangle(bool requirePipeline = false)
    {
        PassDeclaration? outer = StatedPass;
        int target = PostTargetIndex(CurrentTargetId);
        StatedPass = new PassDeclaration { Name = "Post/" + CurrentTargetId,
            FramebufferId = CurrentTargetId, ColorSlots = 1u, Flags = PassFlags.None,
            Reads = PostReadTextures(target), TransientSlots = PostTransientSlots(target) };
        try
        {
            if (!RecordStatedDraw(null, 1, null, null, 0, requirePipeline) && requirePipeline)
                throw new InvalidOperationException("Required post draw failed: " + lastStatedDrawRefusal);
        }
        finally { StatedPass = outer; }
    }

    // Retained fallback bodies from the platform post split. These use the
    // existing patched shader/uniform/sampler routes and the same stated draw.
    private void LegacyBloom(int scene, int glow)
    {
        if (!GameFrameBindings.RenderBloom(platform!)) return;
        var display = RequireFramebufferHost().PixelSize();
        float scale = GameFramebufferBindings.SsaaLevel(platform!);
        ToggleBlend(false, EnumBlendMode.Standard);
        LoadFramebuffer(EnumFrameBuffer.FindBright);
        ShaderProgramFindbright bright = ShaderPrograms.Findbright;
        bright.Use();
        bright.ColorTex2D = scene; bright.GlowTex2D = glow;
        bright.AmbientBloomLevel = NativeAmbientBloomLevel(); bright.ExtraBloom = PostUniforms.ExtraBloom;
        DrawPostTriangle(); bright.Stop();
        ShaderProgramBlur blur = ShaderPrograms.Blur;
        blur.Use(); blur.Uniform("frameSize", display.Width * scale, display.Height * scale);
        LoadFramebuffer(EnumFrameBuffer.BlurHorizontalMedRes);
        blur.IsVertical = 0; blur.InputTexture2D = platform!.FrameBuffers[FindBrightIndex].ColorTextureIds[0];
        DrawPostTriangle();
        LoadFramebuffer(EnumFrameBuffer.BlurVerticalMedRes);
        blur.IsVertical = 1; blur.InputTexture2D = platform.FrameBuffers[BlurHorizontalMedResIndex].ColorTextureIds[0];
        DrawPostTriangle();
        Viewport((int)(scale * display.Width / 4f), (int)(scale * display.Height / 4f));
        LoadFramebuffer(EnumFrameBuffer.BlurHorizontalLowRes);
        blur.IsVertical = 0; blur.InputTexture2D = platform.FrameBuffers[BlurVerticalMedResIndex].ColorTextureIds[0];
        DrawPostTriangle();
        LoadFramebuffer(EnumFrameBuffer.BlurVerticalLowRes);
        blur.IsVertical = 1; blur.InputTexture2D = platform.FrameBuffers[BlurHorizontalLowResIndex].ColorTextureIds[0];
        DrawPostTriangle(); blur.Stop();
        Viewport((int)(scale * display.Width), (int)(scale * display.Height));
        ToggleBlend(true, EnumBlendMode.Standard);
    }
    private void LegacyGodRays(int scene, int glow)
    {
        if (!GameFrameBindings.RenderGodRays(platform!)) return;
        var display = RequireFramebufferHost().PixelSize();
        float scale = GameFramebufferBindings.SsaaLevel(platform!);
        LoadFramebuffer(EnumFrameBuffer.GodRays);
        ShaderProgramGodrays rays = ShaderPrograms.Godrays;
        rays.Use(); rays.Uniform("invFrameSizeIn", 1f / (display.Width * scale), 1f / (display.Height * scale));
        if (rays.HasUniform("maxGodRaySamples")) rays.Uniform("maxGodRaySamples", GodRaysSampleLimit);
        rays.SunPosScreenIn = PostUniforms.SunPositionScreen; rays.SunPos3dIn = PostUniforms.LightPosition3D;
        rays.PlayerViewVector = PostUniforms.PlayerViewVector; rays.Dusk = PostUniforms.Dusk;
        rays.IGlobalTimeIn = (float)platform!.EllapsedMs / 1000f;
        rays.InputTexture2D = scene; rays.GlowParts2D = glow;
        DrawPostTriangle(); rays.Stop();
        Viewport((int)(scale * display.Width), (int)(scale * display.Height));
    }
    private void LegacyPostLuma(int scene)
    {
        LoadFramebuffer(EnumFrameBuffer.Luma);
        if (GameFrameBindings.RenderFxaa(platform!) && !TaaResolvedThisFrame && !UpscaledThisFrame)
        {
            ShaderProgramLuma luma = ShaderPrograms.Luma; luma.Use();
            luma.Scene2D = platform!.FrameBuffers[PrimaryIndex].ColorTextureIds[0];
            DrawPostTriangle(requirePipeline: true); luma.Stop();
        }
        else
        {
            ShaderProgramBlit blit = ShaderPrograms.Blit; blit.Use(); blit.Scene2D = scene;
            DrawPostTriangle(requirePipeline: true); blit.Stop();
        }
    }
    internal void FinishPostStages()
    {
        RequireDevice(); Stated.SetBlendEnabled(true);
        LoadFramebuffer(EnumFrameBuffer.Primary);
        CheckGraphicsError("post");
    }
    /// <summary>Runs display-resolution bloom, god rays and luma after scene reconstruction, preserving the selected scene/glow identities.</summary>
    /// <param name="scene">Scene texture after SR/TAA reconstruction.</param>
    /// <param name="glow">Glow texture matched to the selected scene.</param>
    internal void RenderPostTail(int scene, int glow)
    {
        RequireDevice();
        RenderBloomPost(scene, glow);
        RenderGodRaysPost(scene, glow);
        RenderLumaPost(scene);
        FinishPostStages();
    }
}
