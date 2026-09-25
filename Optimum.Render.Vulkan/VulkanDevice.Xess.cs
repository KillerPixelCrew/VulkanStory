using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private bool? upscalerMotionBlitSupported;
    internal void RetireUpscalerResource(IDisposable resource) => _frames.DeferDeletion(resource);

    internal int EvaluateXess(XessNative api, nint context, int motionRg, in UpscalerFrame frame, bool firstFrame)
    {
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
            // Positive-height offscreen viewport: use raster displacement directly.
            // XessTests measures registration against all four sign combinations.
            JitterX = frame.Temporal.JitterPx.X, JitterY = frame.Temporal.JitterPx.Y,
            ExposureScale = 1f, Reset = firstFrame || frame.Temporal.Reset ? 1u : 0u,
        };
        int result = api.Execute(context, commands.Handle, &args);
        _dynamicState.Invalidate();
        return result;
    }

    private bool PrepareUpscalerMotion(VulkanTexture sourceMotion, VulkanTexture motion,
        uint width, uint height, CommandBuffer commands)
    {
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
