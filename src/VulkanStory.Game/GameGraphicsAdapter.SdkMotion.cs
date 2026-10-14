using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeFullscreenPass nativeSdkMotion = new("sdk-motion",
        ["renderSize", "jitterPx", "invViewProjJittered", "prevViewProj", "cameraDelta"],
        ["depthTex", "motionTex"]);
    private int sdkMotionProgram, sdkMotionSource, sdkMotionDepth, sdkMotionTexture;
    private bool sdkMotionFailed;
    private ulong sdkMotionFrame = ulong.MaxValue;
    private readonly float[] sdkMotionProjection = new float[16], sdkMotionView = new float[16],
        sdkMotionViewProjection = new float[16], sdkMotionInverse = new float[16],
        sdkMotionPreviousProjection = new float[16], sdkMotionPreviousView = new float[16],
        sdkMotionPrevious = new float[16];

    internal string SdkMotionReadiness { get; private set; } = "SDK motion not prepared";
    internal int PreparedSdkMotionTexture => device != null && sdkMotionFrame == device.LatencyFrameId
        ? sdkMotionTexture : 0;
    internal object? SdkMotionCapture { get; private set; }

    private FrameBufferRef CreateSdkMotionTarget(int width, int height)
    {
        var renderer = RequireDevice();
        var target = new FrameBufferRef
        {
            Width = width, Height = height, FboId = renderer.CreateFramebuffer(width, height), ColorTextureIds = new int[1],
        };
        try
        {
            target.ColorTextureIds[0] = renderer.CreateTexture2D(width, height,
                VulkanStory.Contracts.TextureInternalFormat.Rgba16f, VulkanStory.Contracts.TexturePixelFormat.Rgba,
                IntPtr.Zero, false);
            SetupTextureSampler(target.ColorTextureIds[0], 9728, 33071);
            renderer.AttachTexture(target.FboId, VulkanStory.Contracts.FramebufferAttachment.ColorAttachment0,
                target.ColorTextureIds[0], 0);
            StateDrawBuffers(target.FboId, 1);
            if (!renderer.CheckFramebufferComplete(target.FboId, out string status))
                throw new InvalidOperationException("SDK motion framebuffer is incomplete: " + status);
            return RegisterFramebuffer(target);
        }
        catch { ReleaseUnpublishedFramebuffer(target); throw; }
    }

    private void ResetSdkMotionPublication()
    {
        sdkMotionFrame = ulong.MaxValue;
        sdkMotionSource = sdkMotionDepth = sdkMotionTexture = 0;
        SdkMotionCapture = null;
        SdkMotionReadiness = "SDK motion not prepared";
    }

    /// <summary>
    /// Produces render-pixel current-to-previous vectors once for the current real
    /// frame. A matching object writer wins; uncovered depth uses camera
    /// reprojection. Camera fallback keeps alpha zero: it cannot certify object
    /// transform/animation history for geometry owned by an arbitrary mod.
    /// </summary>
    internal bool PrepareSdkMotion(in GameTemporalFrame frame, IReadOnlyList<FrameBufferRef> targets)
    {
        var renderer = RequireDevice();
        FrameBufferRef? primary = NativePostTarget(targets, PrimaryIndex);
        FrameBufferRef? output = NativePostTarget(targets, SdkMotionIndex);
        int motion = FrameState.MotionAttachment;
        string? refusal = frame.FrameId != renderer.LatencyFrameId ? "SDK motion frame identity mismatch" :
            !frame.HasCamera || !frame.MotionValid ? "SDK motion camera or scene producers unavailable" :
            primary == null || output == null || motion < 0 || primary.ColorTextureIds.Length <= motion ||
                primary.DepthTextureId <= 0 || primary.Width != output.Width || primary.Height != output.Height ||
                output.Width <= 0 || output.Height <= 0 ? "SDK motion matching images unavailable" : null;
        if (refusal != null)
        {
            ResetSdkMotionPublication(); SdkMotionReadiness = refusal; return false;
        }
        int raw = primary!.ColorTextureIds[motion], depth = primary.DepthTextureId;
        int prepared = output!.ColorTextureIds[0];
        if (sdkMotionFrame == frame.FrameId && sdkMotionSource == raw && sdkMotionDepth == depth &&
            sdkMotionTexture == prepared) return true;
        ResetSdkMotionPublication();
        if (prepared == raw || prepared == depth)
            throw new InvalidOperationException("SDK motion cannot sample its own output image.");
        if (!PrepareSdkMotionMatrices(frame, primary.Width, primary.Height))
        { SdkMotionReadiness = "SDK motion camera reprojection is singular or nonfinite"; return false; }
        int program = OwnedProgram("sdk-motion", ref sdkMotionProgram, ref sdkMotionFailed);
        if (program <= 0)
        { SdkMotionReadiness = "SDK motion resolve shader unavailable"; return false; }
        if (!DrawSdkMotion(program, output, [depth, raw], frame)) return false;
        sdkMotionFrame = frame.FrameId; sdkMotionSource = raw; sdkMotionDepth = depth; sdkMotionTexture = prepared;
        SdkMotionReadiness = "ready (object writers plus camera/depth reprojection)";
        if (CaptureSrMotionInputs || HeadlessParityDump.Enabled)
            SdkMotionCapture = new
            {
                frameId = frame.FrameId, renderWidth = primary.Width, renderHeight = primary.Height,
                sourceMotionTexture = raw, depthTexture = depth, preparedMotionTexture = prepared,
                jitterX = frame.Provider.JitterX, jitterY = frame.Provider.JitterY,
                inverseViewProjectionJittered = (float[])sdkMotionInverse.Clone(),
                previousViewProjection = (float[])sdkMotionPrevious.Clone(),
                cameraDelta = new[] { frame.CameraDeltaX, frame.CameraDeltaY, frame.CameraDeltaZ },
                motionConvention = "current-to-previous render pixels, GL row order, jitter excluded",
                alphaConvention = "matching original writer depth; zero for camera fallback",
            };
        return true;
    }

    private bool PrepareSdkMotionMatrices(in GameTemporalFrame frame, int width, int height)
    {
        if (!float.IsFinite(frame.Provider.JitterX) || !float.IsFinite(frame.Provider.JitterY) ||
            !float.IsFinite(frame.CameraDeltaX) || !float.IsFinite(frame.CameraDeltaY) || !float.IsFinite(frame.CameraDeltaZ))
            return false;
        frame.Projection.Span.CopyTo(sdkMotionProjection); frame.View.Span.CopyTo(sdkMotionView);
        frame.PreviousProjection.Span.CopyTo(sdkMotionPreviousProjection);
        frame.PreviousView.Span.CopyTo(sdkMotionPreviousView);
        // Exact raster convention used by TemporalFrameState.CopyRasterProjection.
        // Snapshot preserves the applied jitter after the scene closes its window.
        sdkMotionProjection[8] -= (float)(2.0 * frame.Provider.JitterX / width);
        sdkMotionProjection[9] -= (float)(2.0 * frame.Provider.JitterY / height);
        Mat4f.Mul(sdkMotionViewProjection, sdkMotionProjection, sdkMotionView);
        if (Mat4f.Invert(sdkMotionInverse, sdkMotionViewProjection) == null) return false;
        Mat4f.Mul(sdkMotionPrevious, sdkMotionPreviousProjection, sdkMotionPreviousView);
        for (int index = 0; index < 16; index++)
            if (!float.IsFinite(sdkMotionInverse[index]) || !float.IsFinite(sdkMotionPrevious[index])) return false;
        return true;
    }

    private bool DrawSdkMotion(int program, FrameBufferRef output, int[] inputs, in GameTemporalFrame frame)
    {
        var renderer = RequireDevice();
        using VulkanDevice.GpuSection section = renderer.BeginGpuSection("sdk_motion_resolve");
        NativePipeline? pipeline = NativePostPipeline(nativeSdkMotion, program, output.FboId, 1u,
            NativeOpaqueBlend(1u), false, false, CompareOp.Less);
        if (pipeline == null)
        { SdkMotionReadiness = "SDK motion resolve pipeline unavailable"; return false; }
        if (!pipeline.Program.Interface.WrittenFragmentOutputs.Contains(0))
        { SdkMotionReadiness = "SDK motion resolve shader does not write its output"; return false; }
        try
        {
            if (!BeginNativeTargetPass("SdkMotion/" + output.FboId, output.FboId, 1u,
                output.Width, output.Height, inputs))
            { SdkMotionReadiness = "SDK motion resolve pass declined: " + renderer.NativeDrawRefusal; return false; }
            var uniforms = nativeSdkMotion.Uniforms;
            renderer.WriteNative(pipeline, uniforms[0], output.Width, output.Height);
            renderer.WriteNative(pipeline, uniforms[1], frame.Provider.JitterX, frame.Provider.JitterY);
            WriteNativeMatrix(pipeline, uniforms[2], sdkMotionInverse);
            WriteNativeMatrix(pipeline, uniforms[3], sdkMotionPrevious);
            renderer.WriteNative(pipeline, uniforms[4], frame.CameraDeltaX, frame.CameraDeltaY, frame.CameraDeltaZ);
            bool drawn = renderer.DrawNativeFullscreen(pipeline,
                [new NativeTexture(nativeSdkMotion.Samplers[0], inputs[0]),
                 new NativeTexture(nativeSdkMotion.Samplers[1], inputs[1])], requirePipeline: true);
            if (!drawn) SdkMotionReadiness = "SDK motion resolve draw declined: " + renderer.NativeDrawRefusal;
            return drawn;
        }
        finally { renderer.EndNativePass(); }
    }

    private void PrepareSdkMotionPipeline(IReadOnlyList<FrameBufferRef> targets)
    {
        if (NativePostTarget(targets, SdkMotionIndex) is not { } output) return;
        int program = OwnedProgram("sdk-motion", ref sdkMotionProgram, ref sdkMotionFailed);
        if (program > 0)
            NativePostPipeline(nativeSdkMotion, program, output.FboId, 1u,
                NativeOpaqueBlend(1u), false, false, CompareOp.Less);
    }

    private void ReloadSdkMotionProgram()
    {
        if (sdkMotionProgram > 0) RequireDevice().DeleteProgram(sdkMotionProgram);
        sdkMotionProgram = 0; sdkMotionFailed = false; nativeSdkMotion.Pipeline = null;
        ResetSdkMotionPublication();
    }
}
