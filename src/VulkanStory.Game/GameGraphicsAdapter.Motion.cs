using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

// Retained primary motion window and far-depth sky/cloud reactive pass.
internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeFullscreenPass nativeSkyMotion = new("taa-skymotion",
        ["taaRenderSize", "taaJitterPx", "taaInvViewProjJittered", "taaPrevViewProj", "taaCloudReactive"], ["transparentRevealTex"]);
    private int skyMotionProgram, skyMotionLocation = -1;
    private bool skyMotionFailed, skyMotionOutputReported;
    // Retain exact producer inputs only for the opt-in attachment capture.
    internal object? SkyMotionCapture { get; private set; }
    // Native manifest GBUFFER is derived from SSAOLEVEL, not TAAMOTIONLOCATION.
    // Keep owned motion programs on the same 2/4 attachment layout as scene writers.
    private static string MotionProgramPrefix(int motion) =>
        "#define TAAMOTION 1\n#define TAAMOTIONLOCATION " + motion +
        "\n#define SSAOLEVEL " + (motion == 4 ? 1 : 0) + "\n";
    private void RejectSceneDraw(string reason = "native scene draw rejected")
    {
        if (aoTemporal?.InScene == true && currentFramebuffer != null &&
            platform!.FrameBuffers is { Count: > 0 } targets && ReferenceEquals(currentFramebuffer, targets[0]))
            aoTemporal.RejectMotionDraw(reason + "; pass=" +
                (ShaderProgramBase.CurrentShaderProgram?.PassName ?? "unknown") + "; program=" + StatedProgram);
    }
    /// <summary>Opens camera-motion writes only while the current scene and primary motion target are eligible.</summary>
    /// <returns>True after opening; false when prerequisites decline.</returns>
    internal bool BeginCameraMotionWrite()
    {
        RequireDevice();
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (aoTemporal?.EntityMotion.Enabled != true || !aoTemporal.State.JitterActive || program == null ||
            program.PassName is not ("chunkopaque" or "chunktopsoil" or "decals" or "particlescube") ||
            !program.HasUniform("prevProjectionMatrix") || !program.HasUniform("prevModelViewMatrix")) return false;
        var frame = aoTemporal.State;
        program.UniformMatrix("prevProjectionMatrix", frame.GetPrevProjection(EnumTemporalView.World));
        program.UniformMatrix("prevModelViewMatrix", frame.PrevCameraMatrixOrigin);
        frame.ApplyMotionUniforms(program);
        return BeginMotionWrite(aoTemporal);
    }
    /// <summary>Opens motion writes for an eligible animated scene draw; callers must close the window through EndMotionWrite even if setup or drawing fails.</summary>
    /// <returns>True when the motion write mask was opened; false when the scene/target/program prerequisites decline.</returns>
    internal bool BeginAnimatedMotionWrite()
    {
        RequireDevice();
        if (aoTemporal?.EntityMotion.Enabled != true || !aoTemporal.State.JitterActive ||
            ShaderProgramBase.CurrentShaderProgram is not { PassName: "entityanimated" } program ||
            !program.HasUniform("taaHistoryValid")) return false;
        // Bone upload supplies per-entity model/joint history. Opening the mask
        // alone does not assert that those histories or the complete frame are valid.
        return BeginMotionWrite(aoTemporal);
    }
    internal bool BeginMotionWrite(GameTemporalOwner temporal, bool onlyMotion = false)
    {
        RequireDevice();
        int motion = FrameState.MotionAttachment;
        if (FrameState.MotionWriteActive || motion is < 0 or >= 32 || !AoSettings.EffectiveTemporalPipeline ||
            !temporal.State.JitterActive || currentFramebuffer == null ||
            !ReferenceEquals(currentFramebuffer, platform!.FrameBuffers[0]) || currentFramebuffer.ColorTextureIds.Length <= motion)
            return false;
        uint mask = onlyMotion ? 1u << motion : motion == 31 ? uint.MaxValue : (1u << (motion + 1)) - 1u;
        Stated.SetDrawBuffers(CurrentTargetId, mask);
        FrameState = FrameState with { MotionWriteActive = true };
        Stated.SetSlotBlend(motion, 32774, 1, 0, 1, 0);
        return true;
    }
    /// <summary>Closes the active motion write window and restores its retained attachment mask.</summary>
    internal void EndMotionWrite()
    {
        RequireDevice();
        if (!FrameState.MotionWriteActive) return;
        int motion = FrameState.MotionAttachment;
        FrameState = FrameState with { MotionWriteActive = false };
        if (motion >= 0 && platform!.FrameBuffers is { Count: > 0 } targets && targets[0] is { } primary)
            Stated.SetDrawBuffers(primary.FboId, motion == 0 ? 0u : (1u << motion) - 1u);
    }
    internal bool RenderSkyMotion(GameTemporalOwner temporal, bool transparentRendered = true)
    {
        var renderer = RequireDevice();
        if (HeadlessParityDump.Enabled)
            SkyMotionCapture = new { frameId = temporal.State.RenderedFrameId, submitted = false };
        int motion = FrameState.MotionAttachment;
        if (!AoSettings.EffectiveTemporalPipeline || motion is < 0 or >= 32 || !temporal.HasCurrentSceneSample) return false;
        var primary = NativePostTarget(platform!.FrameBuffers, PrimaryIndex);
        var transparent = NativePostTarget(platform.FrameBuffers, (int)EnumFrameBuffer.Transparent);
        if (primary == null || primary.ColorTextureIds.Length <= motion || transparent?.ColorTextureIds is not { Length: >= 2 }) return false;
        if (skyMotionLocation != motion) { ReloadSkyMotionProgram(); skyMotionLocation = motion; }
        int program = OwnedProgram("taa-skymotion", ref skyMotionProgram, ref skyMotionFailed,
            MotionProgramPrefix(motion));
        if (program <= 0) return false;
        var frame = temporal.State;
        (float[]? inverse, float[] previous) = JitteredReprojection(frame, primary.Width, primary.Height);
        if (inverse == null) return false;
        uint slots = 1u << motion;
        NativePipeline? pipeline = NativePostPipeline(nativeSkyMotion, program, primary.FboId, slots, NativeOpaqueBlend(slots), true, false, CompareOp.LessOrEqual);
        if (pipeline == null) return false;
        if (!pipeline.Program.Interface.WrittenFragmentOutputs.Contains(motion))
        {
            if (!skyMotionOutputReported)
                platform.Logger.Error("VulkanStory: sky motion shader does not write attachment {0}; motion coverage is disabled.", motion);
            skyMotionOutputReported = true;
            return false;
        }
        skyMotionOutputReported = false;
        int reveal = transparent.ColorTextureIds[1]; bool drawn = false;
        // The original loop skipped OIT entirely. Revealage one means zero
        // transparent coverage in the retained shader; never sample an old frame.
        if (!transparentRendered) renderer.ClearNativeColor(transparent.FboId, 1, 1f, 1f, 1f, 1f);
        try
        {
            if (BeginNativeTargetPass("SkyMotion/0", primary.FboId, slots, primary.Width, primary.Height, [reveal]))
            {
                renderer.WriteNative(pipeline, nativeSkyMotion.Uniforms[0], primary.Width, primary.Height);
                renderer.WriteNative(pipeline, nativeSkyMotion.Uniforms[1], frame.JitterPx.X, frame.JitterPx.Y);
                WriteNativeMatrix(pipeline, nativeSkyMotion.Uniforms[2], inverse); WriteNativeMatrix(pipeline, nativeSkyMotion.Uniforms[3], previous);
                renderer.WriteNative(pipeline, nativeSkyMotion.Uniforms[4], 1f);
                drawn = renderer.DrawNativeFullscreen(pipeline, [new NativeTexture(nativeSkyMotion.Samplers[0], reveal)]);
            }
        }
        finally
        {
            renderer.EndNativePass(); Stated.DepthCompare = CompareOp.Less; Stated.DepthWrite = true;
            ToggleBlend(true, EnumBlendMode.Standard); Stated.CullEnabled = true;
        }
        if (HeadlessParityDump.Enabled)
            SkyMotionCapture = new
            {
                frameId = frame.RenderedFrameId, submitted = drawn,
                renderWidth = primary.Width, renderHeight = primary.Height,
                motionAttachment = motion, transparentRendered,
                writtenFragmentOutputs = pipeline.Program.Interface.WrittenFragmentOutputs.ToArray(),
                jitterX = frame.JitterPx.X, jitterY = frame.JitterPx.Y,
                reset = frame.Reset, previousWorldCaptured = frame.WasViewCaptured(EnumTemporalView.World),
                // Mat4f column-major arrays, exactly as supplied to the native uniforms.
                inverseViewProjectionJittered = inverse, previousViewProjection = previous,
                cloudReactive = 1f,
                uniforms = nativeSkyMotion.Uniforms.Select((uniform, index) => new
                {
                    name = nativeSkyMotion.UniformNames[index],
                    block = uniform.Block.ToString(), offset = uniform.Offset
                }).ToArray()
            };
        if (drawn) ScreenManager.FrameProfiler.Mark("rend3D-ret-skymv");
        return drawn;
    }
    internal void ReloadSkyMotionProgram()
    {
        var renderer = RequireDevice(); if (skyMotionProgram > 0) renderer.DeleteProgram(skyMotionProgram);
        skyMotionProgram = 0; skyMotionFailed = skyMotionOutputReported = false; skyMotionLocation = -1; nativeSkyMotion.Pipeline = null;
    }
}
