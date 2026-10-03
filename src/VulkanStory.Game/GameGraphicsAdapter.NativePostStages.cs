using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

// Direct bloom/god-ray/luma migration from VulkanClientPlatform.NativePostFinal.cs.
// Baseline 386e0d05386d0b228b439d09aeca851428f7bbf3; shader/resource layouts retained.
internal sealed partial class GameGraphicsAdapter
{
    // ------------------------------------------------------------------ pass 6: bloom

    private readonly NativeFullscreenPass nativeFindBright =
        new("findbright", new[] { "ambientBloomLevel", "extraBloom" }, new[] { "colorTex", "glowTex" });

    // One instance per blur target: the program is the same, but a pipeline is per target
    // formats, and each instance keeps its own resolved placements.
    private readonly NativeFullscreenPass nativeBlurMedHorizontal =
        new("blur", new[] { "frameSize", "isVertical" }, new[] { "inputTexture" });

    private readonly NativeFullscreenPass nativeBlurMedVertical =
        new("blur", new[] { "frameSize", "isVertical" }, new[] { "inputTexture" });

    private readonly NativeFullscreenPass nativeBlurLowHorizontal =
        new("blur", new[] { "frameSize", "isVertical" }, new[] { "inputTexture" });

    private readonly NativeFullscreenPass nativeBlurLowVertical =
        new("blur", new[] { "frameSize", "isVertical" }, new[] { "inputTexture" });

    /// <summary>
    /// The bloom chain, drawn natively: find-bright into the full-resolution target, then the
    /// two blur ping-pongs at half and quarter resolution. The OpenGL body
    /// (ClientPlatformWindows.OptimumPostBloom) turns blending off for the whole block, sets the
    /// blur's <c>frameSize</c> exactly once - at the full resolution, before the half-resolution
    /// pair, and never again for the quarter-resolution pair - and puts the viewport and
    /// blending back at the end. All of that is reproduced here, the stale <c>frameSize</c>
    /// included: it is what the vanilla image is made of, not a bug to fix.
    /// </summary>
    internal void RenderBloomPost(int scene, int glow)
    {
        if (!GameFrameBindings.RenderBloom(platform!)) return;

        List<FrameBufferRef> buffers = platform!.FrameBuffers;
        ShaderProgramFindbright findbright = ShaderPrograms.Findbright;
        ShaderProgramBlur blur = ShaderPrograms.Blur;
        FrameBufferRef? findBrightTarget = NativePostTarget(buffers, FindBrightIndex);
        FrameBufferRef? medHorizontal = NativePostTarget(buffers, BlurHorizontalMedResIndex);
        FrameBufferRef? medVertical = NativePostTarget(buffers, BlurVerticalMedResIndex);
        FrameBufferRef? lowHorizontal = NativePostTarget(buffers, BlurHorizontalLowResIndex);
        FrameBufferRef? lowVertical = NativePostTarget(buffers, BlurVerticalLowResIndex);

        if (!NativeProgramUsable(findbright) || !NativeProgramUsable(blur) ||
            findBrightTarget == null || medHorizontal == null || medVertical == null ||
            lowHorizontal == null || lowVertical == null)
        {
            LegacyBloom(scene, glow);
            return;
        }

        NativePipeline? bright = NativePipelineFor(nativeFindBright, findbright, findBrightTarget.FboId);
        NativePipeline? medH = NativePipelineFor(nativeBlurMedHorizontal, blur, medHorizontal.FboId);
        NativePipeline? medV = NativePipelineFor(nativeBlurMedVertical, blur, medVertical.FboId);
        NativePipeline? lowH = NativePipelineFor(nativeBlurLowHorizontal, blur, lowHorizontal.FboId);
        NativePipeline? lowV = NativePipelineFor(nativeBlurLowVertical, blur, lowVertical.FboId);
        if (bright == null || medH == null || medV == null || lowH == null || lowV == null)
        {
            LegacyBloom(scene, glow);
            return;
        }

        int postWidth = findBrightTarget.Width;
        int postHeight = findBrightTarget.Height;

        // The block's blend state, left where the OpenGL body leaves it, outside the passes.
        ToggleBlend(false, EnumBlendMode.Standard);

        if (BeginNativePostPass("Post/" + FindBrightIndex, findBrightTarget.FboId, new[] { scene, glow },
                transient: true))
        {
            RequireDevice().WriteNative(bright, nativeFindBright.Uniforms[0], NativeAmbientBloomLevel());
            RequireDevice().WriteNative(bright, nativeFindBright.Uniforms[1], PostUniforms.ExtraBloom);
            RequireDevice().DrawNativeFullscreen(bright, new[]
            {
                new NativeTexture(nativeFindBright.Samplers[0], scene),
                new NativeTexture(nativeFindBright.Samplers[1], glow),
            });
        }
        RequireDevice().EndNativePass();

        // frameSize is the full-resolution value the OpenGL body sets once here and reuses for
        // all four blur draws, including the two that write a quarter-resolution target.
        float blurWidth = postWidth;
        float blurHeight = postHeight;

        NativeBlurStep(medH, nativeBlurMedHorizontal, "Post/" + BlurHorizontalMedResIndex,
            medHorizontal.FboId, findBrightTarget.ColorTextureIds[0], vertical: 0, blurWidth, blurHeight);
        NativeBlurStep(medV, nativeBlurMedVertical, "Post/" + BlurVerticalMedResIndex,
            medVertical.FboId, medHorizontal.ColorTextureIds[0], vertical: 1, blurWidth, blurHeight);
        NativeBlurStep(lowH, nativeBlurLowHorizontal, "Post/" + BlurHorizontalLowResIndex,
            lowHorizontal.FboId, medVertical.ColorTextureIds[0], vertical: 0, blurWidth, blurHeight);
        NativeBlurStep(lowV, nativeBlurLowVertical, "Post/" + BlurVerticalLowResIndex,
            lowVertical.FboId, lowHorizontal.ColorTextureIds[0], vertical: 1, blurWidth, blurHeight);

        // What the rest of the frame inherits from this block on the OpenGL body.
        Viewport(postWidth, postHeight);
        ToggleBlend(true, EnumBlendMode.Standard);
    }

    private void NativeBlurStep(NativePipeline pipeline, NativeFullscreenPass pass, string name,
        int framebufferId, int input, int vertical, float frameWidth, float frameHeight)
    {
        if (BeginNativePostPass(name, framebufferId, new[] { input }, transient: true))
        {
            RequireDevice().WriteNative(pipeline, pass.Uniforms[0], frameWidth, frameHeight);
            RequireDevice().WriteNative(pipeline, pass.Uniforms[1], vertical);
            RequireDevice().DrawNativeFullscreen(pipeline, new[] { new NativeTexture(pass.Samplers[0], input) });
        }
        RequireDevice().EndNativePass();
    }

    // --------------------------------------------------------------- pass 7: god rays

    private readonly NativeFullscreenPass nativeGodRays = new("godrays",
        new[]
        {
            "invFrameSizeIn", "maxGodRaySamples", "sunPosScreenIn", "sunPos3dIn",
            "playerViewVector", "dusk", "iGlobalTimeIn",
        },
        new[] { "inputTexture", "glowParts" });

    /// <summary>
    /// God rays, drawn natively into the half-resolution target. The OpenGL body
    /// (ClientPlatformWindows.OptimumPostGodRays) toggles no blend of its own, so the draw runs
    /// with whatever the steps before it left - blending on in the source-alpha mode, which the
    /// shader's <c>outColor.a = 1</c> makes indistinguishable from blending off. The pipeline
    /// states that mode outright rather than inheriting a tracked one, and the viewport reset the
    /// body ends with stays, outside the pass.
    ///
    /// <c>sunPos3dIn</c> comes from <c>PostUniforms.LightPosition3D</c> here and from
    /// <c>SunPosition3D</c> in the final composition: two different fields behind one uniform
    /// name, as on the OpenGL body.
    /// </summary>
    internal void RenderGodRaysPost(int scene, int glow)
    {
        if (!GameFrameBindings.RenderGodRays(platform!)) return;

        List<FrameBufferRef> buffers = platform!.FrameBuffers;
        ShaderProgramGodrays godrays = ShaderPrograms.Godrays;
        FrameBufferRef? target = NativePostTarget(buffers, GodRaysIndex);
        if (!NativeProgramUsable(godrays) || target == null)
        {
            LegacyGodRays(scene, glow);
            return;
        }

        NativePipeline? pipeline = NativePostPipeline(nativeGodRays, godrays, target.FboId, 1u,
            NativeStandardBlend(), depthTest: false, depthWrite: false, CompareOp.Less);
        if (pipeline == null)
        {
            LegacyGodRays(scene, glow);
            return;
        }

        FrameBufferRef postTarget = buffers[FindBrightIndex];

        if (BeginNativePostPass("Post/" + GodRaysIndex, target.FboId, new[] { scene, glow }, transient: false))
        {
            // The input texel size is the full-resolution one, describing the texture the pass
            // samples and not the half-resolution target it writes.
            RequireDevice().WriteNative(pipeline, nativeGodRays.Uniforms[0],
                1f / postTarget.Width, 1f / postTarget.Height);
            RequireDevice().WriteNative(pipeline, nativeGodRays.Uniforms[1], GodRaysSampleLimit);
            WriteNativeVec3(pipeline, nativeGodRays.Uniforms[2], PostUniforms.SunPositionScreen);
            WriteNativeVec3(pipeline, nativeGodRays.Uniforms[3], PostUniforms.LightPosition3D);
            WriteNativeVec3(pipeline, nativeGodRays.Uniforms[4], PostUniforms.PlayerViewVector);
            RequireDevice().WriteNative(pipeline, nativeGodRays.Uniforms[5], PostUniforms.Dusk);
            RequireDevice().WriteNative(pipeline, nativeGodRays.Uniforms[6], (float)platform!.EllapsedMs / 1000f);
            RequireDevice().DrawNativeFullscreen(pipeline, new[]
            {
                new NativeTexture(nativeGodRays.Samplers[0], scene),
                new NativeTexture(nativeGodRays.Samplers[1], glow),
            });
        }
        RequireDevice().EndNativePass();

        Viewport(postTarget.Width, postTarget.Height);
    }

    // -------------------------------------------------------- pass 8: FXAA luma or blit

    private readonly NativeFullscreenPass nativeLuma =
        new("luma", Array.Empty<string>(), new[] { "scene" });

    private readonly NativeFullscreenPass nativeLumaBlit =
        new("blit", Array.Empty<string>(), new[] { "scene" });

    /// <summary>
    /// The Luma step, drawn natively. The OpenGL body (ClientPlatformWindows.OptimumPostLuma)
    /// picks the branch on <c>RenderFXAA &amp;&amp; !TaaResolvedThisFrame</c>: the FXAA luma
    /// prepass over the raw jittered Primary colour - bypassing the whole resolved chain - or a
    /// pass-through blit of the chain's scene. Both go into the Luma target, and the Luma case of
    /// <c>LoadFrameBuffer</c> turns blending off without putting it back; the chain's epilogue
    /// does that.
    /// </summary>
    internal void RenderLumaPost(int scene)
    {
        List<FrameBufferRef> buffers = platform!.FrameBuffers;
        FrameBufferRef? target = NativePostTarget(buffers, LumaIndex);
        FrameBufferRef? primary = NativePostTarget(buffers, PrimaryIndex);
        bool fxaa = GameFrameBindings.RenderFxaa(platform!) && !TaaResolvedThisFrame && !UpscaledThisFrame;
        if (fxaa && (primary == null || primary.ColorTextureIds == null || primary.ColorTextureIds.Length < 1))
        {
            LegacyPostLuma(scene);
            return;
        }

        ShaderProgramBase program = fxaa ? ShaderPrograms.Luma : ShaderPrograms.Blit;
        NativeFullscreenPass pass = fxaa ? nativeLuma : nativeLumaBlit;
        if (!NativeProgramUsable(program) || target == null)
        {
            LegacyPostLuma(scene);
            return;
        }

        NativePipeline? pipeline = NativePipelineFor(pass, program, target.FboId);
        if (pipeline == null)
        {
            LegacyPostLuma(scene);
            return;
        }

        int source = fxaa ? primary!.ColorTextureIds[0] : scene;

        // The Luma case of LoadFrameBuffer turns blending off and leaves it off.
        Stated.SetBlendEnabled(false);
        if (BeginNativePostPass("Post/" + LumaIndex, target.FboId, new[] { source }, transient: false))
        {
            RequireDevice().DrawNativeFullscreen(pipeline, new[] { new NativeTexture(pass.Samplers[0], source) }, requirePipeline: true);
        }
        RequireDevice().EndNativePass();
    }

}
