using System;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan;

/// <summary>XeSS SR command recording, motion conversion and GPU-safe provider retirement.</summary>
public sealed unsafe partial class VulkanDevice
{
    private bool? upscalerMotionBlitSupported;
    private bool captureUpscalerConstants;
    internal bool CaptureUpscalerConstants
    {
        get => captureUpscalerConstants;
        set
        {
            captureUpscalerConstants = value;
            if (!value) XessConstantsCapture = null;
        }
    }
    /// <summary>Requested-frame XeSS constants and context-scale queries, withdrawn when capture closes.</summary>
    internal object? XessConstantsCapture { get; private set; }
    /// <summary>Transfers native-provider disposal to the renderer's frame retirement queue.</summary>
    internal void RetireUpscalerResource(IDisposable resource) => _frames.DeferDeletion(resource);

    /// <summary>Converts renderer motion and records a XeSS SR execution with the current temporal constants.</summary>
    internal int EvaluateXess(XessNative api, nint context, int motionRg, in UpscalerFrame frame, bool firstFrame)
    {
        using GpuSection gpuSection = BeginGpuSection("upscale_xess");
        XessConstantsCapture = null;
        if (!_frameActive) return -3;
        VulkanTexture? color = _textures.Get(frame.Color), depth = _textures.Get(frame.Depth),
            sourceMotion = _textures.Get(frame.Motion), motion = _textures.Get(motionRg),
            output = _textures.Get(frame.Output);
        if (color == null || depth == null || sourceMotion == null || motion == null || output == null) return -4;
        if (!PrepareUpscalerMotion(sourceMotion, motion, color.Width, color.Height, Commands)) return -4;

        CommandBuffer commands = Commands;
        _textures.Require(_barriers, commands, color, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, depth, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, motion, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, output, ResourceUsage.StorageWriteExternal);
        _barriers.Flush(commands);
        var args = new XessExecute
        {
            Color = XessImage.From(color), Depth = XessImage.From(depth), Velocity = XessImage.From(motion),
            Output = XessImage.From(output), Width = color.Width, Height = color.Height,
            // Positive-height Vulkan rasterization moves image rows by +Jy.
            // XeSS expresses camera jitter with Y up, as in Intel's Vulkan sample.
            JitterX = frame.Temporal.JitterX,
            JitterY = -frame.Temporal.JitterY,
            ExposureScale = 1f, Reset = firstFrame || frame.Temporal.Reset ? 1u : 0u,
        };
        if (captureUpscalerConstants)
        {
            float jitterScaleX = float.NaN, jitterScaleY = float.NaN;
            float velocityScaleX = float.NaN, velocityScaleY = float.NaN;
            int jitterScaleResult = api.JitterScale(context, &jitterScaleX, &jitterScaleY);
            int velocityScaleResult = api.VelocityScale(context, &velocityScaleX, &velocityScaleY);
            static float? CapturedScale(int result, float value) =>
                result >= 0 && float.IsFinite(value) ? value : null;
            XessConstantsCapture = new
            {
                frameId = LatencyFrameId,
                submittedJitterX = args.JitterX, submittedJitterY = args.JitterY,
                rasterJitterX = frame.Temporal.JitterX, rasterJitterY = frame.Temporal.JitterY,
                jitterScaleQueryResult = jitterScaleResult,
                jitterScaleX = CapturedScale(jitterScaleResult, jitterScaleX),
                jitterScaleY = CapturedScale(jitterScaleResult, jitterScaleY),
                velocityScaleQueryResult = velocityScaleResult,
                velocityScaleX = CapturedScale(velocityScaleResult, velocityScaleX),
                velocityScaleY = CapturedScale(velocityScaleResult, velocityScaleY),
                resetHistory = args.Reset != 0, firstFrame,
                exposureScale = args.ExposureScale, initFlags = XessBackend.InitFlags,
                colorTexture = frame.Color, depthTexture = frame.Depth,
                sourceMotionTexture = frame.Motion, motionTexture = motionRg, outputTexture = frame.Output,
                renderWidth = args.Width, renderHeight = args.Height,
                sourceMotionWidth = sourceMotion.Width, sourceMotionHeight = sourceMotion.Height,
                depthWidth = depth.Width, depthHeight = depth.Height,
                motionWidth = motion.Width, motionHeight = motion.Height,
                outputWidth = output.Width, outputHeight = output.Height,
            };
        }
        _lastUpscalerInputTextures = (LatencyFrameId, motionRg, 0);
        int result = api.Execute(context, commands.Handle, &args);
        _dynamicState.Invalidate();
        return result;
    }

    /// <summary>Uses the retained motion-conversion program to prepare an RG motion texture for the supplied render extent.</summary>
    private bool PrepareUpscalerMotion(VulkanTexture sourceMotion, VulkanTexture motion,
        uint width, uint height, CommandBuffer commands)
    {
        using GpuSection gpuSection = BeginGpuSection("upscale_motion_convert");
        if (sourceMotion.Format != Format.R16G16B16A16Sfloat || motion.Format != Format.R16G16Sfloat ||
            sourceMotion.Width != width || sourceMotion.Height != height ||
            motion.Width != width || motion.Height != height) return false;
        if (!upscalerMotionBlitSupported.HasValue)
        {
            _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, sourceMotion.Format, out FormatProperties src);
            _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, motion.Format, out FormatProperties dst);
            upscalerMotionBlitSupported = (src.OptimalTilingFeatures & FormatFeatureFlags.BlitSrcBit) != 0 &&
                (dst.OptimalTilingFeatures & FormatFeatureFlags.BlitDstBit) != 0;
        }
        if (!upscalerMotionBlitSupported.Value) return false;
        _targets.FlushAllPendingClears(commands);
        _targets.EndRendering(commands);
        // RGBA16F also contains reactive/depth metadata. A same-size nearest blit
        // converts only RG into the SDK's RG16F motion image, preserving pixel units.
        _textures.Require(_barriers, commands, sourceMotion, ResourceUsage.TransferSrc);
        _textures.Require(_barriers, commands, motion, ResourceUsage.TransferDst);
        _barriers.Flush(commands);
        ImageBlit region = default;
        region.SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
        region.DstSubresource = region.SrcSubresource;
        region.SrcOffsets.Element1 = new Offset3D((int)width, (int)height, 1);
        region.DstOffsets.Element1 = region.SrcOffsets.Element1;
        _context.Api.CmdBlitImage(commands, sourceMotion.Image, ImageLayout.TransferSrcOptimal,
            motion.Image, ImageLayout.TransferDstOptimal, 1, &region, Filter.Nearest);
        return true;
    }
}
