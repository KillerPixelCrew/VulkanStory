using Silk.NET.Vulkan;
using Vintagestory.API.Client;

namespace VulkanStory.Game;

// Early pipeline requests for the temporal stages; nothing here draws.
internal sealed partial class GameGraphicsAdapter
{
    /// <summary>
    /// Links the owned temporal programs and requests the native pipelines of the TAA resolve and
    /// sharpen, the sky motion pass and the FSR EASU/RCAS blit for <paramref name="targets" />, so
    /// their background compiles start now instead of on the frame each stage first becomes valid.
    /// </summary>
    /// <param name="targets">The target list just built, or the current one; it need not be published yet.</param>
    /// <param name="worldLoaded">Whether a world client exists, which allows linking the liquid motion program from game assets.</param>
    /// <remarks>
    /// Only requests: each pipeline is described exactly as its draw describes it, so the draw finds
    /// it in the device table. Waits for the initial scene shader load, as provider target rebuilds
    /// do. The liquid motion mesh pipeline depends on the liquid pool's vertex layout and is still
    /// requested by its first draw. A failure here is logged and left to the stage's own draw path.
    /// </remarks>
    internal void PrepareTemporalPipelines(IReadOnlyList<FrameBufferRef>? targets, bool worldLoaded)
    {
        // Only an optimisation: callers include world-ready controls, whose failures stop frame
        // work, so even a dormant route or missing settings is logged here rather than thrown.
        try
        {
            RequireDevice();
            if (postSettings is not { } settings || targets == null ||
                !GameFramebufferBindings.OffscreenEnabled(platform!)) return;
            PrepareTemporalImageTargets(targets);
            if (!TerrainShadersReady) return;
            int motion = FrameState.MotionAttachment;
            if (settings.EffectiveTemporalPipeline && motion is >= 0 and < 32)
            {
                PrepareSkyMotionPipeline(targets, motion);
                if (worldLoaded) LiquidMotionProgram(motion);
            }
            bool fsrBlit = !settings.UpscalerReplacesTaa && !fsrDisabled && settings.Settings.RenderScale < 1f &&
                NativePostTarget(targets, FsrFramebufferIndex) != null;
            // Sharpen draws only with a positive strength and when FSR's RCAS does not replace it.
            if (settings.EffectiveTaa && TaaTargetsReady)
                PrepareTaaPipelines(targets, sharpen: settings.Settings.TaaSharpness > 0f && !fsrBlit);
            if (fsrBlit) PrepareFsrBlitPipelines(targets);
        }
        catch (Exception failure)
        {
            platform!.Logger.Warning("VulkanStory: temporal pipeline preparation skipped: {0}", failure.Message);
        }
    }
    private void PrepareTaaPipelines(IReadOnlyList<FrameBufferRef> targets, bool sharpen)
    {
        // Both history targets share formats, so one request serves either parity.
        if (NativePostTarget(targets, TaaHistoryIndexA) is { ColorTextureIds.Length: >= 3 } history)
        {
            int resolve = OwnedProgram("taa-resolve", ref taaResolveProgram, ref taaResolveFailed);
            if (resolve > 0)
            {
                NativePostPipeline(nativeTaaResolve, resolve, history.FboId, 5u, NativeOpaqueBlend(5u), false, false, CompareOp.Less);
                NativePostPipeline(nativeTaaResolve, resolve, history.FboId, 2u, NativeOpaqueBlend(2u), false, false, CompareOp.Less);
            }
            if (taaSampleBanks is { } banks)
            {
                int cache = OwnedProgram("taa-cache", ref taaCacheProgram, ref taaCacheFailed);
                if (cache > 0)
                {
                    NativePostPipeline(nativeTaaCache, cache, banks[0].Low.FboId, 255u, NativeOpaqueBlend(255u), false, false, CompareOp.Less);
                    NativePostPipeline(nativeTaaCache, cache, banks[0].High.FboId, 127u, NativeOpaqueBlend(127u), false, false, CompareOp.Less);
                }
            }
        }
        if (sharpen && NativePostTarget(targets, TaaSharpenIndex) is { } sharpenTarget)
        {
            int program = OwnedProgram("taa-sharpen", ref taaSharpenProgram, ref taaSharpenFailed);
            if (program > 0)
                NativePostPipeline(nativeTaaSharpen, program, sharpenTarget.FboId, 1u, NativeOpaqueBlend(1u), false, false, CompareOp.Less);
        }
    }
    private void PrepareSkyMotionPipeline(IReadOnlyList<FrameBufferRef> targets, int motion)
    {
        if (NativePostTarget(targets, PrimaryIndex) is not { } primary || primary.ColorTextureIds.Length <= motion) return;
        if (skyMotionLocation != motion) { ReloadSkyMotionProgram(); skyMotionLocation = motion; }
        int program = OwnedProgram("taa-skymotion", ref skyMotionProgram, ref skyMotionFailed, MotionProgramPrefix(motion));
        if (program <= 0) return;
        uint slots = 1u << motion;
        NativePostPipeline(nativeSkyMotion, program, primary.FboId, slots, NativeOpaqueBlend(slots), true, false, CompareOp.LessOrEqual);
    }
    private void PrepareFsrBlitPipelines(IReadOnlyList<FrameBufferRef> targets)
    {
        if (NativePostTarget(targets, FsrFramebufferIndex) is not { } target) return;
        int easu = OwnedProgram("fsr-easu", ref fsrEasuProgram, ref fsrEasuFailed);
        int rcas = OwnedProgram("fsr-rcas", ref fsrRcasProgram, ref fsrRcasFailed);
        if (easu > 0) NativePipelineFor(nativeFsrEasu, easu, target.FboId);
        // RCAS writes the window image; the UI scope is closed whenever targets are (re)built.
        if (rcas > 0) NativePipelineFor(nativeFsrRcas, rcas, NativeDefaultTarget);
    }
}
