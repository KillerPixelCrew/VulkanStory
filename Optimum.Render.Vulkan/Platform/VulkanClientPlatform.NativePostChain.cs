using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native render systems (docs/vulkan-native-render-systems.md), stage 1: Optimum owns
// the post and TAA chain end to end. This file holds the chain - its ORDER, and one helper per
// pass with a stable signature.
//
// The order is section 3's and the OpenGL body's: OIT merge, sky motion, SSAO and blur then the
// AO composite, TAA resolve and sharpen, bloom, god rays, FXAA luma or blit, final composition,
// and last the blit/FSR/debug step that is already native (VulkanClientPlatform.NativeBlit.cs).
// RenderPostprocessingEffects' override runs the steps that live inside it and never calls base.
//
// Two helpers draw natively in this file - the OIT merge and sky motion - through RequestNativePipeline,
// BeginNativePass, WriteNative and DrawNativeFullscreen, exactly as the blit does. Every other
// helper is LEGACY: the same work through the GL-shaped platform calls, which after the split in
// ClientPlatformWindows is one lib virtual per pass, so the chain is complete and correct at
// every commit and a later stage replaces one helper at a time. The bloom chain, god rays, the
// Luma step and the final composition are native in VulkanClientPlatform.NativePostFinal.cs;
// their LEGACY helpers stay as the old route the differential tests compare against.
public partial class VulkanClientPlatform
{
    /// <summary>The chain's passes, in the order the frame runs them (section 3).</summary>
    internal enum NativePostStep
    {
        OitMerge = 0,
        SkyMotion = 1,
        SsaoAndAmbientOcclusion = 2,
        TaaResolve = 3,
        TaaSharpen = 4,
        Bloom = 5,
        GodRays = 6,
        FxaaOrBlit = 7,
        FinalComposition = 8,
        Blit = 9,
    }

    /// <summary>
    /// The chain order, declared once. The steps the chain itself sequences are the ones inside
    /// <see cref="RenderPostprocessingEffects" />; the others are separate virtuals the client
    /// calls in this order, and every helper records its step, so a test can read the order back
    /// off a real frame.
    /// </summary>
    internal static readonly NativePostStep[] NativePostChainOrder =
    {
        NativePostStep.OitMerge,
        NativePostStep.SkyMotion,
        NativePostStep.SsaoAndAmbientOcclusion,
        NativePostStep.TaaResolve,
        NativePostStep.TaaSharpen,
        NativePostStep.Bloom,
        NativePostStep.GodRays,
        NativePostStep.FxaaOrBlit,
        NativePostStep.FinalComposition,
        NativePostStep.Blit,
    };

    /// <summary>
    /// False runs the whole chain on the OpenGL body instead - every pass, the blit included:
    /// the old route the differential tests compare the native one against. The blit keeps its
    /// own <see cref="NativeBlitEnabled" /> switch for the tests written around it.
    /// </summary>
    internal bool NativePostChainEnabled { get; set; } = true;

    /// <summary>Test seam: when set, every chain step appends itself here as it runs.</summary>
    internal List<NativePostStep>? NativePostStepLog { get; set; }

    private bool UseNativePostChain => NativePostChainEnabled && device != null;

    private void NotePostStep(NativePostStep step) => NativePostStepLog?.Add(step);

    // ------------------------------------------------------------------ the chain

    /// <summary>
    /// The chain's own steps, in order, with no call to the base body: the AO step, the TAA
    /// resolve and sharpen, bloom, god rays, the Luma step and the epilogue. The order and the
    /// per-step conditions are the OpenGL body's
    /// (ClientPlatformWindows.RenderPostprocessingEffects).
    /// </summary>
    private void RunNativePostChain(float[] projectMatrix)
    {
        if (!offscreenBufferActive) return;

        SetPassContext("Post", PassFlags.None);
        NotePostStep(NativePostStep.SsaoAndAmbientOcclusion);
        PostStepAmbientOcclusion(projectMatrix);

        NotePostStep(NativePostStep.TaaResolve);
        PostStepTaaResolve();

        int scene = OptimumPostSceneTexture();
        int glow = OptimumPostGlowTexture();
        NotePostStep(NativePostStep.TaaSharpen);
        scene = PostStepTaaSharpen(scene);

        NotePostStep(NativePostStep.Bloom);
        PostStepBloom(scene, glow);
        NotePostStep(NativePostStep.GodRays);
        PostStepGodRays(scene, glow);
        NotePostStep(NativePostStep.FxaaOrBlit);
        PostStepFxaaOrBlit(scene);
        PostStepFinish();
        SetPassContext("Frame", PassFlags.AllowSplit);
    }

    // ----------------------------------------------------------- native pass 1: OIT merge

    private readonly NativeFullscreenPass nativeOitMerge = new("transparentcompose",
        Array.Empty<string>(),
        new[] { "accumulation", "revealage", "inGlow", "OITreveal", "OITaccumulation" });

    /// <summary>
    /// The OIT merge, drawn natively. The OpenGL body binds Primary without touching the
    /// viewport, turns the depth test off and blending on with the global source-alpha mode,
    /// opens the motion window when TAA is running, and composes the Transparent target's three
    /// attachments together with the OIT reveal and accumulation targets.
    ///
    /// Here the pass states its target, its colour slots and its reads, and the blend is the
    /// pipeline's: source-alpha on every slot, and additive (ONE, ONE) on the motion attachment
    /// alone, which is what lets the merge add the transparent layer's coverage into the
    /// reactive channel without touching the vector or the writer depth under it. The GL-shaped
    /// state calls stay, outside the pass: they are what every stage AFTER the merge inherits,
    /// exactly as on the OpenGL body.
    /// </summary>
    private void NativeOitMerge()
    {
        List<FrameBufferRef> buffers = FrameBuffers;
        FrameBufferRef primary = buffers != null && buffers.Count > 0 ? buffers[0] : null!;
        FrameBufferRef transparent = buffers != null && buffers.Count > 1 ? buffers[1] : null!;
        ShaderProgramTransparentcompose compose = ShaderPrograms.Transparentcompose;
        if (!offscreenBufferActive || primary == null || transparent == null ||
            transparent.ColorTextureIds == null || transparent.ColorTextureIds.Length < 3 ||
            compose == null || compose.LoadError || compose.Disposed)
        {
            LegacyOitMerge();
            return;
        }

        OptimumBindKeepViewport(primary);
        ApplyTransparentMergeBlendState();

        bool motion = NativeMotionAttachmentWritable(primary);
        uint slots = NativeWorldColorSlots();
        if (motion) slots |= 1u << MotionAttachmentIndex;

        NativePipeline? pipeline = NativePostPipeline(nativeOitMerge, compose, primary.FboId, slots,
            NativeMergeBlend(slots, motion), depthTest: false, depthWrite: false, CompareOp.Less);
        if (pipeline == null)
        {
            LegacyOitMerge();
            return;
        }

        int accumulation = transparent.ColorTextureIds[0];
        int revealage = transparent.ColorTextureIds[1];
        int inGlow = transparent.ColorTextureIds[2];
        int oitReveal = SystemRenderOITLayers.OptimumOitRevealTexture;
        int oitAccumulation = SystemRenderOITLayers.OptimumOitAccumTexture;

        var reads = new List<int> { accumulation, revealage, inGlow };
        if (oitReveal > 0) reads.Add(oitReveal);
        if (oitAccumulation > 0) reads.Add(oitAccumulation);

        if (BeginNativeKeepViewportPass("MergeTransparent/0", primary.FboId, slots, reads.ToArray()))
        {
            device.DrawNativeFullscreen(pipeline, new[]
            {
                new NativeTexture(nativeOitMerge.Samplers[0], accumulation),
                new NativeTexture(nativeOitMerge.Samplers[1], revealage),
                new NativeTexture(nativeOitMerge.Samplers[2], inGlow),
                new NativeTexture(nativeOitMerge.Samplers[3], oitReveal),
                new NativeTexture(nativeOitMerge.Samplers[4], oitAccumulation),
            });
        }
        device.EndNativePass();
        SetPassContext("Frame", PassFlags.AllowSplit);
    }

    /// <summary>
    /// The merge's blend: the global source-alpha mode on every slot the pass writes, with
    /// FUNC_ADD and (ONE, ONE) on the motion attachment while the window is open
    /// (ClientPlatformWindows.ApplyOptimumMotionAccumulateBlendState).
    /// </summary>
    private AttachmentBlend[] NativeMergeBlend(uint slots, bool motion)
    {
        var blend = new AttachmentBlend[NativeSlotCount(slots)];
        for (int i = 0; i < blend.Length; i++)
        {
            if (((slots >> i) & 1) == 0)
            {
                blend[i].WriteMask = 0;
                continue;
            }
            AttachmentBlend attachment = AttachmentBlend.Default;
            attachment.Enabled = true;
            if (motion && i == MotionAttachmentIndex)
            {
                attachment.SrcColor = BlendFactor.One;
                attachment.DstColor = BlendFactor.One;
                attachment.SrcAlpha = BlendFactor.One;
                attachment.DstAlpha = BlendFactor.One;
            }
            blend[i] = attachment;
        }
        return blend;
    }

    // ---------------------------------------------------------- native pass 2: sky motion

    private readonly NativeFullscreenPass nativeSkyMotion = new("taa-skymotion",
        new[] { "taaRenderSize", "taaJitterPx", "taaInvViewProjJittered", "taaPrevViewProj", "taaCloudReactive" },
        new[] { "transparentRevealTex" });

    /// <summary>
    /// The sky / volumetric-cloud motion and reactive pass, drawn natively: a fullscreen
    /// triangle at window depth 1.0 with the depth test on, GL_LEQUAL and depth writes off,
    /// writing the motion attachment and nothing else. The guards, the two matrices and the
    /// uniform values are the OpenGL body's (ClientPlatformWindows.RenderOptimumSkyMotion); the
    /// motion-only window is the pass's colour slot, not a draw-buffer mask.
    /// </summary>
    private bool NativeSkyMotion()
    {
        if (!OptimumConfig.EffectiveTaa) return false;
        if (!TaaTargetsReady || MotionAttachmentIndex < 0) return false;
        ShaderProgram skyMotion = ShaderPrograms.TaaSkyMotion;
        if (skyMotion == null || skyMotion.LoadError || skyMotion.Disposed) return false;
        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || buffers.Count <= 1) return false;
        FrameBufferRef primary = buffers[0];
        FrameBufferRef transparent = buffers[1];
        if (primary == null || primary.Disposed || transparent == null || transparent.Disposed) return false;
        if (transparent.ColorTextureIds == null || transparent.ColorTextureIds.Length < 2) return false;

        OptimumTemporalFrame frame = OptimumTemporal.Frame;
        if (!frame.WasViewCaptured(EnumTemporalView.World)) return false;

        // The same two matrices the resolve builds for its camera fallback: this frame's
        // jittered view-projection inverted, and the previous frame's unjittered one. Built
        // here rather than reused, exactly as the OpenGL body builds them.
        float[] projection = frame.GetProjection(EnumTemporalView.World);
        var jittered = new double[16];
        for (int i = 0; i < 16; i++) jittered[i] = projection[i];
        OptimumTemporalMath.ApplyProjectionJitter(jittered, frame.JitterPx.X, frame.JitterPx.Y,
            primary.Width, primary.Height);
        var projectionJittered = new float[16];
        for (int i = 0; i < 16; i++) projectionJittered[i] = (float)jittered[i];
        float[] viewProj = Mat4f.Mul(new float[16], projectionJittered, frame.CameraMatrixOrigin);
        float[] invViewProj = Mat4f.Invert(new float[16], viewProj);
        // A failed invert bails before any state is touched, as it does on the OpenGL body.
        if (invViewProj == null) return false;
        float[] prevViewProj = Mat4f.Mul(new float[16],
            frame.GetPrevProjection(EnumTemporalView.World), frame.PrevCameraMatrixOrigin);

        bool opened = NativeMotionAttachmentWritable(primary);
        if (opened)
        {
            uint slots = 1u << MotionAttachmentIndex;
            int reveal = transparent.ColorTextureIds[1];
            NativePipeline? pipeline = NativePostPipeline(nativeSkyMotion, skyMotion, primary.FboId, slots,
                NativeMotionOnlyBlend(slots), depthTest: true, depthWrite: false, CompareOp.LessOrEqual);
            if (pipeline == null)
            {
                opened = false;
            }
            else
            {
                SetPassContext("SkyMotion", PassFlags.None);
                if (BeginNativeKeepViewportPass("SkyMotion/0", primary.FboId, slots, new[] { reveal }))
                {
                    device.WriteNative(pipeline, nativeSkyMotion.Uniforms[0], primary.Width, primary.Height);
                    device.WriteNative(pipeline, nativeSkyMotion.Uniforms[1], frame.JitterPx.X, frame.JitterPx.Y);
                    WriteNativeMatrix(pipeline, nativeSkyMotion.Uniforms[2], invViewProj);
                    WriteNativeMatrix(pipeline, nativeSkyMotion.Uniforms[3], prevViewProj);
                    device.WriteNative(pipeline, nativeSkyMotion.Uniforms[4], OptimumCloudReactive);
                    device.DrawNativeFullscreen(pipeline, new[]
                    {
                        new NativeTexture(nativeSkyMotion.Samplers[0], reveal),
                    });
                }
                device.EndNativePass();
                SetPassContext("Frame", PassFlags.AllowSplit);
            }
        }

        // Everything the OpenGL body's state block and its finally leave for the AfterOIT
        // stages and the post chain: depth writes on, GL_LESS, blending on, culling on. The
        // body normalises them whether or not the window opened, so this runs on both paths.
        GlDepthFunc(EnumDepthFunction.Less);
        GlDepthMask(flag: true);
        GlToggleBlend(on: true);
        GlEnableCullFace();

        if (!opened) return false;
        ScreenManager.FrameProfiler.Mark("rend3D-ret-skymv");
        return true;
    }

    /// <summary>The motion attachment alone, unblended; every other slot masked out of the pass.</summary>
    private AttachmentBlend[] NativeMotionOnlyBlend(uint slots)
    {
        var blend = new AttachmentBlend[NativeSlotCount(slots)];
        for (int i = 0; i < blend.Length; i++)
        {
            if (((slots >> i) & 1) == 0) blend[i].WriteMask = 0;
            else blend[i] = AttachmentBlend.Default;
        }
        return blend;
    }

    // ------------------------------------------------------------------ legacy helpers

    /// <summary>LEGACY - pass 1 on the OpenGL body. <see cref="NativeOitMerge" /> replaces it; kept as the old route.</summary>
    private void LegacyOitMerge()
    {
        SetPassContext("MergeTransparent", PassFlags.None);
        base.MergeTransparentRenderPass();
        SetPassContext("Frame", PassFlags.AllowSplit);
    }

    /// <summary>LEGACY - pass 2 on the OpenGL body. <see cref="NativeSkyMotion" /> replaces it; kept as the old route.</summary>
    private bool LegacySkyMotion()
    {
        SetPassContext("SkyMotion", PassFlags.None);
        bool drawn = base.RenderOptimumSkyMotion();
        SetPassContext("Frame", PassFlags.AllowSplit);
        return drawn;
    }

    /// <summary>
    /// LEGACY - pass 3, the SSAO pass, its bilateral blur and the AO composite, through the lib
    /// virtual that now holds the OpenGL body's inline code. Stage 1c makes it native.
    /// </summary>
    private void PostStepAmbientOcclusion(float[] projectMatrix) => OptimumPostAmbientOcclusion(projectMatrix);

    /// <summary>LEGACY - pass 4, the TAA resolve. Stage 1d makes it native.</summary>
    private bool PostStepTaaResolve() => RenderOptimumTaaResolve();

    /// <summary>LEGACY - pass 5, the TAA sharpen. Stage 1d makes it native.</summary>
    private int PostStepTaaSharpen(int resolvedScene) => RenderOptimumTaaSharpen(resolvedScene);

    /// <summary>Pass 6, the bloom chain, drawn natively (VulkanClientPlatform.NativePostFinal.cs).</summary>
    private void PostStepBloom(int scene, int glow) => NativeBloom(scene, glow);

    /// <summary>Pass 7, god rays, drawn natively.</summary>
    private void PostStepGodRays(int scene, int glow) => NativeGodRays(scene, glow);

    /// <summary>Pass 8, the FXAA luma prepass or the pass-through blit into Luma, drawn natively.</summary>
    private void PostStepFxaaOrBlit(int scene) => NativePostLuma(scene);

    /// <summary>
    /// The chain's epilogue: blending back on and Primary bound again. State, not a draw - it is
    /// the GL-shaped handoff every stage after the chain inherits, so it stays as it is.
    /// </summary>
    private void PostStepFinish() => OptimumPostFinish();

    /// <summary>LEGACY - pass 6 on the OpenGL body. <see cref="NativeBloom" /> replaces it; kept as the old route.</summary>
    private void LegacyBloom(int scene, int glow) => OptimumPostBloom(scene, glow);

    /// <summary>LEGACY - pass 7 on the OpenGL body. <see cref="NativeGodRays" /> replaces it; kept as the old route.</summary>
    private void LegacyGodRays(int scene, int glow) => OptimumPostGodRays(scene, glow);

    /// <summary>LEGACY - pass 8 on the OpenGL body. <see cref="NativePostLuma" /> replaces it; kept as the old route.</summary>
    private void LegacyPostLuma(int scene) => OptimumPostLuma(scene);

    /// <summary>LEGACY - pass 9 on the OpenGL body. <see cref="NativeFinalComposition" /> replaces it; kept as the old route.</summary>
    private void LegacyFinalComposition()
    {
        SetPassContext("FinalComposition", PassFlags.None);
        base.RenderFinalComposition();
        SetPassContext("Frame", PassFlags.AllowSplit);
    }

    // ------------------------------------------------------------------ shared plumbing

    /// <summary>
    /// The guards <see cref="ClientPlatformWindows.BeginMotionWrite" /> and
    /// <see cref="ClientPlatformWindows.BeginMotionOnlyWrite" /> open their window under, minus
    /// the draw-buffer mask a native pass does not use: a native pass says which slots it
    /// writes, so the window is the pass's colour slots.
    /// </summary>
    private bool NativeMotionAttachmentWritable(FrameBufferRef primary)
    {
        if (OptimumMotionWriteActive) return false;
        if (MotionAttachmentIndex < 0 || !TaaTargetsReady) return false;
        if (!OptimumConfig.EffectiveTaa) return false;
        if (!OptimumTemporal.Frame.JitterActive) return false;
        // The same "Primary is the target being drawn into" invariant, so a native pass never
        // writes the motion attachment from under another target.
        return ReferenceEquals(CurrentFrameBuffer, primary);
    }

    /// <summary>Primary's default colour set: four attachments with the SSAO G-buffer, two without.</summary>
    private uint NativeWorldColorSlots() => OptimumRenderSsao ? 0b1111u : 0b11u;

    private static int NativeSlotCount(uint slots)
    {
        int count = 0;
        for (int i = 0; i < GlStateTracker.MaxColorAttachments; i++)
        {
            if (((slots >> i) & 1) != 0) count = i + 1;
        }
        return count;
    }

    /// <summary>
    /// A pass that keeps the viewport, the way the OpenGL body's bind-only setter does: both
    /// native passes here draw into the viewport the world stage left.
    /// </summary>
    private bool BeginNativeKeepViewportPass(string name, int framebufferId, uint colorSlots, int[] reads)
    {
        Rect2D viewport = device.NativeCurrentViewport;
        return device.BeginNativePass(new NativePassDescription
        {
            Name = name,
            FramebufferId = framebufferId,
            ColorSlots = colorSlots,
            Reads = reads,
            Flags = PassFlags.None,
            ViewportX = viewport.Offset.X,
            ViewportY = viewport.Offset.Y,
            ViewportWidth = (int)viewport.Extent.Width,
            ViewportHeight = (int)viewport.Extent.Height,
        });
    }

    /// <summary>
    /// The pipeline for one native pass of the chain, with the fixed state stated outright. The
    /// device caches by (program, formats, blend, depth, cull, topology), so a pass whose blend
    /// changes with the motion window simply gets the other pipeline, and the placements are
    /// re-resolved whenever the pipeline object changes (a relink makes a new one).
    /// </summary>
    private NativePipeline? NativePostPipeline(NativeFullscreenPass pass, ShaderProgramBase program,
        int framebufferId, uint colorSlots, AttachmentBlend[] blend,
        bool depthTest, bool depthWrite, CompareOp depthCompare)
    {
        RenderTargetFormats? formats = device.NativeTargetFormats(framebufferId, colorSlots);
        if (formats == null) return null;

        NativePipeline? pipeline = device.RequestNativePipeline(new NativePipelineDescription
        {
            ProgramId = program.ProgramId,
            PassName = pass.PassName,
            Blend = blend,
            DepthTest = depthTest,
            DepthWrite = depthWrite,
            DepthCompare = depthCompare,
            Cull = CullModeFlags.None,
            Topology = PrimitiveTopology.TriangleList,
            Targets = formats,
        }, out string error);

        if (pipeline == null)
        {
            if (!pass.Reported)
            {
                pass.Reported = true;
                Logger.Warning("Optimum: no native pipeline for '{0}': {1}", pass.PassName, error);
            }
            pass.Pipeline = null;
            return null;
        }

        pass.Reported = false;
        if (!ReferenceEquals(pipeline, pass.Pipeline)) pass.Adopt(pipeline, formats);
        return pipeline;
    }

    /// <summary>A mat4 at its placement: sixteen floats, column-major, as the program declares it.</summary>
    private void WriteNativeMatrix(NativePipeline pipeline, NativeUniform uniform, float[] values) =>
        device.WriteNative(pipeline, uniform, MemoryMarshal.AsBytes(new ReadOnlySpan<float>(values)));
}
