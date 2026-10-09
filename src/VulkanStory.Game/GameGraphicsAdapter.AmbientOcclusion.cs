// Retained AO host and native SSAO/blur/composite; baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
using System;
using System.Collections.Generic;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.AmbientOcclusion;

namespace VulkanStory.Game;

// Vulkan-native render systems (docs/vulkan.md), Phase 3b stage 1c: the
// post chain's ambient-occlusion step drawn natively - the vanilla SSAO pass, its bilateral blur
// ping-pong and the AO composite that multiplies the visibility into the scene before the TAA
// resolve reads it.
//
// The step is the OpenGL body's (ClientPlatformWindows.OptimumPostAmbientOcclusion and the
// private ApplyOptimumSceneSsao it calls), value for value: the same guards, the same order, the
// same textures. Uniform sizes use the allocated render targets, which can differ from
// window size when a vendor upscaler is active. What changes is how each draw reaches the GPU -
// a pipeline built for stated fixed state (per-attachment blend, depth test/write/compare, cull,
// topology and the target's formats) instead of whatever the GL state tracker happens to hold,
// a pass that names its target, its written colour slots and the textures it samples instead of
// a draw-buffer mask, uniforms written by resolved placement instead of by name, and sampled
// textures resolved straight to bindless slots.
//
// Both AO modes run here. Vanilla SSAO renders into frameBuffers[13], is blurred through 15/14
// and composed from 14; Optimum's GTAO (GtaoRenderer, already native and untouched by this file)
// hands the composite its visibility texture instead and the composite takes the OPTIMUMAO branch
// with optimumAoMode = 1. Either way the step ends by recording that AO is in the scene, which is
// what keeps the final composition from applying it a second time.
internal sealed partial class GameGraphicsAdapter
{
    // Vendor upscalers consume the motion attachment but do not allocate TAA history targets.
    // Both routes still need AO in scene color before their temporal reconstruction.
    private bool NativeAoTemporalActive => AoSettings.EffectiveTemporalPipeline &&
        (TaaTargetsReady || (FrameState.MotionAttachment >= 0 &&
            (AoSettings.UpscalerReplacesTaa || AoSettings.Settings.FrameGeneration != "off")));

    private readonly NativeFullscreenPass nativeSsao = new("ssao",
        new[] { "screenSize", "projection", "samples", "temporalFrameIndex" },
        new[] { "gPosition", "gNormal", "texNoise", "revealage" });

    private readonly NativeFullscreenPass nativeBilateralBlur = new("bilateralblur",
        new[] { "frameSize", "isVertical" },
        new[] { "inputTexture", "depthTexture" });

    private readonly NativeFullscreenPass nativeSceneSsao = new("scene-ssao",
        new[] { "invRenderHeight", "optimumAoMode", "optimumAoDebugInScene" },
        new[] { "ssaoScene", "gPositionScene", "revealageScene" });

    /// <summary>The frame buffer indices this step draws into and reads back, as the base indexes them.</summary>
    private const int NativeSsaoTargetIndex = 13;
    private const int NativeSsaoBlurVerticalIndex = 14;
    private const int NativeSsaoBlurHorizontalIndex = 15;

    /// <summary>
    /// The session's native AO step: <see cref="RenderGtao" /> first,
    /// <see cref="NativeVanillaSsaoPass" /> and its blur when GTAO declines, then the composite - under the
    /// vanilla branch while TAA or an upscaler is active, under the GTAO branch always.
    /// </summary>
    /// <param name="projectMatrix">Original scene projection used to reconstruct AO camera coordinates.</param>
    internal void RenderAmbientOcclusionPost(float[] projectMatrix)
    {
        AmbientOcclusionInScene = false;
        AmbientOcclusionTexture = 0;
        if (!GameFrameBindings.RenderSsao(platform!) || projectMatrix == null) return;

        AmbientOcclusionTexture = RenderGtao(projectMatrix);
        if (AmbientOcclusionTexture != 0)
        {
            NativeSceneSsaoPass();
            return;
        }

        if (!NativeVanillaSsaoReady()) return;
        float ssaa = GameFramebufferBindings.SsaaLevel(platform!);
        FrameBufferRef primary = platform!.FrameBuffers[0];

        // Outside every native pass, exactly where the OpenGL body puts them: this is the
        // GL-shaped state the steps after this one inherit.
        ToggleBlend(false, EnumBlendMode.Standard);
        NativeVanillaSsaoPass(projectMatrix, ssaa);
        NativeBilateralBlurPasses();
        // The body's tail: the blur's last target is what it leaves bound, with the viewport
        // back at full render resolution - the Luma step inherits that viewport.
        LoadFramebuffer(EnumFrameBuffer.SSAOBlurVertical);
        ToggleBlend(true, EnumBlendMode.Standard);
        Viewport(primary.Width, primary.Height);
        if (NativeAoTemporalActive)
        {
            NativeSceneSsaoPass();
        }
    }

    private bool nativeVanillaSsaoUnreadyReported;

    /// <summary>
    /// Whether the vanilla SSAO passes have what they index without a check: Primary with the
    /// SSAO G-buffer (slots 2 and 3), Transparent's revealage, the SSAO target and both blur
    /// targets, and the two program objects. The GTAO route checks its own inputs in
    /// <see cref="RenderGtao" />, and the final composition reads the blur target null-safely;
    /// only this route dereferences them unguarded. The old platform fell back to the OpenGL body
    /// here; there is no such body now, so a frame without them goes without vanilla AO instead
    /// of throwing mid-frame. A program that failed to load is not refused here: its pipeline
    /// request already declines and reports that pass, as before.
    /// </summary>
    private bool NativeVanillaSsaoReady()
    {
        List<FrameBufferRef>? buffers = platform!.FrameBuffers;
        bool ready = buffers != null && buffers.Count > NativeSsaoBlurHorizontalIndex &&
            buffers[0]?.ColorTextureIds is { Length: >= 4 } &&
            buffers[1]?.ColorTextureIds is { Length: >= 2 } &&
            buffers[NativeSsaoTargetIndex]?.ColorTextureIds is { Length: >= 2 } &&
            buffers[NativeSsaoBlurVerticalIndex]?.ColorTextureIds is { Length: >= 1 } &&
            buffers[NativeSsaoBlurHorizontalIndex]?.ColorTextureIds is { Length: >= 1 } &&
            ShaderPrograms.Ssao != null && ShaderPrograms.Bilateralblur != null;
        if (ready)
        {
            nativeVanillaSsaoUnreadyReported = false;
        }
        else if (!nativeVanillaSsaoUnreadyReported)
        {
            nativeVanillaSsaoUnreadyReported = true;
            platform!.Logger.Warning("VulkanStory: vanilla SSAO skipped; its targets or programs are not available.");
        }
        return ready;
    }

    // ------------------------------------------------------------------ 1: vanilla SSAO

    /// <summary>
    /// The raw SSAO pass. One colour slot on frameBuffers[13], cleared white at pass entry the
    /// way the body's ClearSsaoTarget clears it, no blend, no depth, and the viewport the body's
    /// LoadFramebuffer(SSAO) case sets - the target's own size. The four samplers and the four
    /// uniform values follow the body's half-resolution screenSize rule using the actual
    /// render targets. The temporal dither index uses the same compile condition.
    /// </summary>
    private void NativeVanillaSsaoPass(float[] projectMatrix, float ssaa)
    {
        using VulkanDevice.GpuSection gpuSection = RequireDevice().BeginGpuSection("ao_vanilla_ssao");
        List<FrameBufferRef> buffers = platform!.FrameBuffers;
        FrameBufferRef primary = buffers[0];
        FrameBufferRef transparent = buffers[1];
        FrameBufferRef target = buffers[NativeSsaoTargetIndex];

        NativePipeline? pipeline = NativePostPipeline(nativeSsao, ShaderPrograms.Ssao, target.FboId, 1u,
            NativeOpaqueSlotZeroBlend(), depthTest: false, depthWrite: false, CompareOp.Less);
        if (pipeline == null) return;

        int gNormal = primary.ColorTextureIds[2];
        int gPosition = primary.ColorTextureIds[3];
        int noise = target.ColorTextureIds[1];
        int revealage = transparent.ColorTextureIds[1];

        if (BeginNativeAoPass("SSAO/" + NativeSsaoTargetIndex, target.FboId, target.Width, target.Height,
                new[] { gPosition, gNormal, noise, revealage }, clearWhite: true))
        {
            // screenSize: the body's num is 0.5 at SSAA 1 and 1 otherwise, so the value is the
            // SSAO target's resolution at SSAA 1 and the full render resolution above it.
            RequireDevice().WriteNative(pipeline, nativeSsao.Uniforms[0],
                ssaa == 1f ? target.Width : primary.Width,
                ssaa == 1f ? target.Height : primary.Height);
            WriteNativeFloats(pipeline, nativeSsao.Uniforms[1], projectMatrix);
            WriteNativeFloats(pipeline, nativeSsao.Uniforms[2], GameFramebufferBindings.SsaoKernel(platform!));
            if (AoSettings.EffectiveTemporalPipeline)
            {
                RequireDevice().WriteNative(pipeline, nativeSsao.Uniforms[3],
                    (float)(AoTemporal.State.FrameIndex & 1023L));
            }
            RequireDevice().DrawNativeFullscreen(pipeline, new[]
            {
                new NativeTexture(nativeSsao.Samplers[0], gPosition),
                new NativeTexture(nativeSsao.Samplers[1], gNormal),
                new NativeTexture(nativeSsao.Samplers[2], noise),
                new NativeTexture(nativeSsao.Samplers[3], revealage),
            });
        }
        RequireDevice().EndNativePass();
    }

    // ------------------------------------------------------------------ 2: bilateral blur

    /// <summary>
    /// The bilateral blur ping-pong: one horizontal half-iteration into frameBuffers[15] and one
    /// vertical into frameBuffers[14], once at SSAO quality 1 and three times otherwise. Each
    /// half-iteration is its own pass, because each writes a target the next one samples.
    ///
    /// frameSize is the body's: frameBuffers[15]'s size, captured once and reused by every
    /// half-iteration including the vertical ones that write frameBuffers[14]. The two targets are
    /// built at the same size, so this is a value, not a bug to fix - and it is reproduced, not
    /// corrected. depthTexture is likewise bound on both halves: the body sets it on the
    /// horizontal half only and the vertical half inherits the same binding from the program.
    /// </summary>
    private void NativeBilateralBlurPasses()
    {
        using VulkanDevice.GpuSection gpuSection = RequireDevice().BeginGpuSection("ao_bilateral_blur");
        List<FrameBufferRef> buffers = platform!.FrameBuffers;
        FrameBufferRef primary = buffers[0];
        FrameBufferRef horizontal = buffers[NativeSsaoBlurHorizontalIndex];
        FrameBufferRef vertical = buffers[NativeSsaoBlurVerticalIndex];

        NativePipeline? pipeline = NativePostPipeline(nativeBilateralBlur, ShaderPrograms.Bilateralblur,
            horizontal.FboId, 1u, NativeOpaqueSlotZeroBlend(), depthTest: false, depthWrite: false, CompareOp.Less);
        if (pipeline == null) return;

        int depth = primary.DepthTextureId;
        int iterations = ClientSettings.SSAOQuality == 1 ? 1 : 3;
        for (int i = 0; i < iterations; i++)
        {
            int source = buffers[i == 0 ? NativeSsaoTargetIndex : NativeSsaoBlurVerticalIndex].ColorTextureIds[0];
            NativeBilateralBlurHalf(pipeline, horizontal, source, depth, horizontal, isVertical: 0);
            NativeBilateralBlurHalf(pipeline, vertical, horizontal.ColorTextureIds[0], depth, horizontal, isVertical: 1);
        }
    }

    /// <summary>One half-iteration of the blur: one target, one input, the shared frameSize.</summary>
    private void NativeBilateralBlurHalf(NativePipeline pipeline, FrameBufferRef target, int source, int depth,
        FrameBufferRef frameSizeSource, int isVertical)
    {
        if (!BeginNativeAoPass("SSAOBlur/" + target.FboId, target.FboId, target.Width, target.Height,
                new[] { source, depth }, clearWhite: false))
        {
            RequireDevice().EndNativePass();
            return;
        }

        RequireDevice().WriteNative(pipeline, nativeBilateralBlur.Uniforms[0],
            (float)frameSizeSource.Width, (float)frameSizeSource.Height);
        RequireDevice().WriteNative(pipeline, nativeBilateralBlur.Uniforms[1], isVertical);
        RequireDevice().DrawNativeFullscreen(pipeline, new[]
        {
            new NativeTexture(nativeBilateralBlur.Samplers[0], source),
            new NativeTexture(nativeBilateralBlur.Samplers[1], depth),
        });
        RequireDevice().EndNativePass();
    }

    // ------------------------------------------------------------------ 3: the AO composite

    /// <summary>
    /// The AO composite, natively: the visibility term multiplied into Primary colour 0 and
    /// nothing else, before the TAA resolve reads that colour.
    ///
    /// The Multiply blend is the pipeline's - dst * (1 - srcAlpha), which is what
    /// GlToggleBlend(true, EnumBlendMode.Multiply) sets - and the single written colour slot is
    /// the pass's, not a draw-buffer mask. That is also what lets the pass sample Primary's
    /// G-buffer position attachment: a slot the pass leaves out is not part of its scope, so it
    /// is read as a texture rather than being feedback.
    /// </summary>
    private void NativeSceneSsaoPass()
    {
        using VulkanDevice.GpuSection gpuSection = RequireDevice().BeginGpuSection("ao_scene_composite");
        int composite = SceneSsaoProgram();
        if (composite <= 0) return;

        List<FrameBufferRef> buffers = platform!.FrameBuffers;
        FrameBufferRef primary = buffers[0];
        FrameBufferRef transparent = buffers[1];

        int aoTexture = AmbientOcclusionTexture;
        if (aoTexture == 0)
        {
            FrameBufferRef blurred = buffers[NativeSsaoBlurVerticalIndex];
            if (blurred?.ColorTextureIds == null || blurred.ColorTextureIds.Length == 0) return;
            aoTexture = blurred.ColorTextureIds[0];
        }

        bool gtao = AmbientOcclusionShadersUseGtao;
        int gPosition = gtao ? primary.ColorTextureIds[3] : 0;
        int revealage = gtao ? transparent.ColorTextureIds[1] : 0;

        bool debugInScene = AoSettings.Settings.AmbientOcclusionDebugView;
        NativePipeline? pipeline = NativePostPipeline(nativeSceneSsao, composite, primary.FboId, 1u,
            debugInScene ? NativeOpaqueSlotZeroBlend() : NativeMultiplySlotZeroBlend(),
            depthTest: false, depthWrite: false, CompareOp.Less);
        if (pipeline == null) return;

        // The body binds Primary through LoadFrameBuffer here, which is also what puts the
        // viewport back at full render resolution for the steps that follow.
        LoadFramebuffer(EnumFrameBuffer.Primary);

        var reads = new List<int> { aoTexture };
        if (gtao)
        {
            reads.Add(gPosition);
            reads.Add(revealage);
        }

        bool drawn = false;
        if (BeginNativeAoPass("SceneSsao/" + primary.FboId, primary.FboId, primary.Width, primary.Height,
                reads.ToArray(), clearWhite: false))
        {
            var textures = new List<NativeTexture> { new(nativeSceneSsao.Samplers[0], aoTexture) };
            if (gtao)
            {
                textures.Add(new NativeTexture(nativeSceneSsao.Samplers[1], gPosition));
                textures.Add(new NativeTexture(nativeSceneSsao.Samplers[2], revealage));
                RequireDevice().WriteNative(pipeline, nativeSceneSsao.Uniforms[1],
                    AmbientOcclusionTexture != 0 ? 1 : 0);
            }
            RequireDevice().WriteNative(pipeline, nativeSceneSsao.Uniforms[0], 1f / primary.Height);
            RequireDevice().WriteNative(pipeline, nativeSceneSsao.Uniforms[2], debugInScene ? 1 : 0);
            drawn = RequireDevice().DrawNativeFullscreen(pipeline, textures.ToArray());
        }
        RequireDevice().EndNativePass();

        // The net GL-shaped state the body's composite leaves: blending back on in the standard
        // mode and the depth test back on. The draw-buffer mask is not restored because it was
        // never narrowed - the written slot is the pass's, so the world mask never moved.
        ToggleBlend(true, EnumBlendMode.Standard);
        Stated.DepthTest = true;
        AmbientOcclusionInScene = drawn;
    }

    // ------------------------------------------------------------------ shared plumbing

    /// <summary>Colour slot 0 alone, opaque: no blend, every channel written, every other slot masked out.</summary>
    private static AttachmentBlend[] NativeOpaqueSlotZeroBlend() => new[] { AttachmentBlend.Default };

    /// <summary>
    /// Colour slot 0 alone with EnumBlendMode.Multiply: glBlendFuncSeparate(ZERO,
    /// ONE_MINUS_SRC_ALPHA, ONE, ONE_MINUS_SRC_ALPHA) over glBlendEquation(FUNC_ADD).
    /// </summary>
    private static AttachmentBlend[] NativeMultiplySlotZeroBlend()
    {
        AttachmentBlend blend = AttachmentBlend.Default;
        blend.Enabled = true;
        blend.SrcColor = BlendFactor.Zero;
        blend.DstColor = BlendFactor.OneMinusSrcAlpha;
        blend.ColorOp = BlendOp.Add;
        blend.SrcAlpha = BlendFactor.One;
        blend.DstAlpha = BlendFactor.OneMinusSrcAlpha;
        blend.AlphaOp = BlendOp.Add;
        return new[] { blend };
    }

    /// <summary>
    /// A pass of this step: one written colour slot, its own viewport (every target here is drawn
    /// at its own size, which is what the body's LoadFrameBuffer cases set), its reads, and the
    /// white clear the SSAO target starts from.
    /// </summary>
    private bool BeginNativeAoPass(string name, int framebufferId, int width, int height, int[] reads, bool clearWhite) =>
        RequireDevice().BeginNativePass(new NativePassDescription
        {
            Name = name,
            FramebufferId = framebufferId,
            ColorSlots = 1u,
            Reads = reads,
            Flags = PassFlags.None,
            ClearSlots = clearWhite ? 1u : 0u,
            ClearValue = new[] { 1f, 1f, 1f, 1f },
            ViewportWidth = width,
            ViewportHeight = height,
        });

}

// Optimum AO (docs/vulkan.md#ambient-occlusion section C): the GTAO visibility-bitmask pass
// on the RequireDevice(). The base's RenderPostprocessingEffects asks for it where vanilla SSAO would run
// and composes the returned texture through ApplyOptimumSceneSsao before the TAA resolve.
internal sealed partial class GameGraphicsAdapter
{
    private GtaoRenderer? ambientOcclusion;

    /// <summary>This frame's output, or 0 when GTAO did not run (the debug outputs and the compose pass's reads key off it).</summary>
    private int ambientOcclusionOutput;

    /// <summary>The output texture whose sampler state was last set up.</summary>
    private int ambientOcclusionSampledTexture;

    /// <summary>Set when the passes cannot run on this device; vanilla SSAO then runs for the session.</summary>
    private string? ambientOcclusionFailure;

    private bool ambientOcclusionToneRefusalLogged;

    private (string Preset, bool Temporal, bool Upscaler, GtaoSettings Settings)? ambientOcclusionSettingsCache;

    /// <summary>
    /// Runs GTAO when the live shaders were built for it (OPTIMUMAO, stamped from
    /// <see cref="AmbientOcclusionShadersUseGtao" /> and the SSAO G-buffer's condition) and returns
    /// the denoised visibility; 0 hands the frame to vanilla SSAO.
    /// </summary>
    private int RenderGtao(float[] projectMatrix)
    {
        ambientOcclusionOutput = 0;
        if (device == null || projectMatrix == null || ambientOcclusionFailure != null) return 0;
        if (!AmbientOcclusionShadersUseGtao) return 0;
        FrameBufferRef? primary = platform!.FrameBuffers is { Count: > 0 } buffers ? buffers[0] : null;
        if (primary?.ColorTextureIds == null || primary.ColorTextureIds.Length < 4 || primary.DepthTextureId == 0) return 0;

        // Vendor upscalers receive AO in scene color, not as a denoising input.
        // Use spatially stable AO there; the game's own TAA can accumulate
        // XeGTAO's rotating noise instead.
        bool temporal = AoSettings.EffectiveTaa && TaaTargetsReady;
        GtaoSettings settings = AmbientOcclusionSettings(temporal, AoSettings.UpscalerReplacesTaa);
        if (!ambientOcclusionToneRefusalLogged && settings.EffectiveTone(0, out string? refusal) != settings.Tone)
        {
            ambientOcclusionToneRefusalLogged = true;
            platform!.Logger.Warning("[VulkanStory] AO: " + refusal);
        }
        uint noiseIndex = temporal ? (uint)(AoTemporal.State.FrameIndex & 0xFFFFFFFFL) : 0u;

        ambientOcclusion ??= new GtaoRenderer(RequireDevice());
        int output = ambientOcclusion.Render(primary.DepthTextureId, primary.ColorTextureIds[2], projectMatrix, settings, noiseIndex);
        if (output == 0)
        {
            if (ambientOcclusion.ProgramsFailed)
            {
                ambientOcclusionFailure = ambientOcclusion.LastError ?? "unknown";
                platform!.Logger.Error("[VulkanStory] AO: GTAO is unavailable on this device, vanilla SSAO runs instead: " + ambientOcclusionFailure);
            }
            return 0;
        }
        if (output != ambientOcclusionSampledTexture)
        {
            // Composed with texelFetch at the same resolution; nearest and clamp keep any sampling exact.
            SetupTextureSampler(output, 9728, 33071);
            ambientOcclusionSampledTexture = output;
        }
        ambientOcclusionOutput = output;
        return output;
    }

    /// <summary>The debug outputs of this frame in the base's index order (working term, edges, depth level 0, output).</summary>
    internal int AmbientOcclusionDebugTexture(int index)
    {
        if (ambientOcclusionOutput == 0 || ambientOcclusion == null) return 0;
        return index switch
        {
            0 => ambientOcclusion.WorkingTermTexture,
            1 => ambientOcclusion.EdgesTexture,
            2 => ambientOcclusion.WorkingDepthTexture,
            3 => ambientOcclusion.OutputTexture,
            _ => 0,
        };
    }

    /// <summary>The preset with measurement overrides, rebuilt when the temporal path changes.</summary>
    private GtaoSettings AmbientOcclusionSettings(bool temporal, bool upscaler)
    {
        string preset = AoSettings.Settings.AmbientOcclusionPreset ?? "";
        if (ambientOcclusionSettingsCache is { } cached && cached.Preset == preset &&
            cached.Temporal == temporal && cached.Upscaler == upscaler)
        {
            return cached.Settings;
        }
        GtaoPreset parsed = GtaoSettings.ParsePreset(preset);
        GtaoSettings settings = (upscaler
                ? GtaoSettings.ForUpscaler(parsed)
                : GtaoSettings.ForPreset(parsed, temporal))
            .WithEnvironment(Environment.GetEnvironmentVariable);
        ambientOcclusionSettingsCache = (preset, temporal, upscaler, settings);
        return settings;
    }

    /// <summary>The size-dependent targets go with the framebuffers; the next frame recreates them.</summary>
    internal void ReleaseAmbientOcclusionTargets()
    {
        ambientOcclusion?.ReleaseTargets();
        ambientOcclusionOutput = 0;
        ambientOcclusionSampledTexture = 0;
    }

    internal void ReleaseAmbientOcclusion()
    {
        ambientOcclusion?.Dispose();
        ambientOcclusion = null;
        ambientOcclusionOutput = 0;
        ambientOcclusionSampledTexture = 0;
    }
}
