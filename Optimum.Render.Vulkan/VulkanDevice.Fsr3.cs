using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Optimum.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal int EvaluateFsr3(Fsr3Native api, nint context, int motionRg,
        in UpscalerFrame frame, bool firstFrame)
    {
        if (!_frameActive) return -3;
        VulkanTexture? color = _textures.Get(frame.Color), depth = _textures.Get(frame.Depth),
            sourceMotion = _textures.Get(frame.Motion), motion = _textures.Get(motionRg),
            output = _textures.Get(frame.Output);
        if (color == null || depth == null || sourceMotion == null || motion == null || output == null) return -4;
        CommandBuffer commands = Commands;
        if (!PrepareUpscalerMotion(sourceMotion, motion, color.Width, color.Height, commands)) return -4;
        _textures.Require(_barriers, commands, color, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, depth, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, motion, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, output, ResourceUsage.StorageWriteExternal);
        _barriers.Flush(commands);
        var args = new Fsr3Frame
        {
            Commands = (nint)commands.Handle,
            Color = Fsr3Image.From(color), Depth = Fsr3Image.From(depth),
            Motion = Fsr3Image.From(motion), Output = Fsr3Image.From(output),
            JitterX = frame.Temporal.JitterPx.X, JitterY = frame.Temporal.JitterPx.Y,
            DeltaMs = frame.Temporal.DeltaTimeMs,
            NearPlane = frame.Temporal.ZNear, FarPlane = frame.Temporal.ZFar,
            FovRadians = frame.Temporal.Fov,
            Reset = firstFrame || frame.Temporal.Reset ? 1u : 0u,
        };
        int result = api.Evaluate(context, &args);
        _dynamicState.Invalidate();
        return result;
    }

    internal int EvaluateFsr3FrameGeneration(Fsr3Native api, nint context,
        int backbufferId, int depthId, int motionId, int motionRgId, int hudlessId,
        int uiId, int uprightSceneId, int uprightUiId, int uprightDepthId,
        int uprightMotionId, in IOptimumTemporalContext temporal, ulong frameId)
    {
        if (!_frameActive || Fsr3ProxyContext == 0) return -3;
        VulkanTexture? backbuffer = _textures.Get(backbufferId);
        VulkanTexture? depth = _textures.Get(depthId);
        VulkanTexture? sourceMotion = _textures.Get(motionId);
        VulkanTexture? motion = _textures.Get(motionRgId);
        VulkanTexture? hudless = hudlessId > 0 ? _textures.Get(hudlessId) : null;
        VulkanTexture? ui = _textures.Get(uiId);
        VulkanTexture? uprightScene = _textures.Get(uprightSceneId);
        VulkanTexture? uprightUi = _textures.Get(uprightUiId);
        VulkanTexture? uprightDepth = _textures.Get(uprightDepthId);
        VulkanTexture? uprightMotion = _textures.Get(uprightMotionId);
        if (backbuffer == null || depth == null || sourceMotion == null || motion == null || ui == null ||
            hudless == null || uprightScene == null || uprightUi == null ||
            uprightDepth == null || uprightMotion == null) return -4;
        if (backbuffer.Format != Format.R8G8B8A8Unorm || ui.Format != Format.R8G8B8A8Unorm ||
            backbuffer.Width != ui.Width || backbuffer.Height != ui.Height) return -4;
        float[] inverseView = Mat4f.Invert(new float[16], temporal.CameraMatrixOrigin);
        if (inverseView == null) return -4;

        CommandBuffer commands = Commands;
        _targets.FlushAllPendingClears(commands);
        _targets.EndRendering(commands);
        if (!PrepareUpscalerMotion(sourceMotion, motion, depth.Width, depth.Height, commands)) return -4;
        // The Vulkan renderer keeps GL-oriented offscreen images and flips them only
        // in BlitPresentPath. FFX reads these images directly, while its proxy's
        // presentColor is already upright. All four inputs must share that latter
        // orientation or every generated frame is vertically inverted.
        if (!FlipFrameGenerationInput(commands, hudless, uprightScene) ||
            !FlipFrameGenerationInput(commands, ui, uprightUi) ||
            !FlipFrameGenerationInput(commands, depth, uprightDepth) ||
            !FlipFrameGenerationInput(commands, motion, uprightMotion)) return -5;
        _textures.Require(_barriers, commands, backbuffer, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, uprightScene, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, uprightUi, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, uprightDepth, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, uprightMotion, ResourceUsage.SampleExternal);
        _barriers.Flush(commands);

        var frame = new Fsr3FgFrame
        {
            Commands = (nint)commands.Handle,
            Color = Fsr3Image.From(backbuffer), Depth = Fsr3Image.From(uprightDepth),
            Motion = Fsr3Image.From(uprightMotion), Ui = Fsr3Image.From(uprightUi),
            Hudless = Fsr3Image.From(uprightScene),
            SwapchainContext = Fsr3ProxyContext,
            JitterX = temporal.JitterPx.X, JitterY = -temporal.JitterPx.Y,
            DeltaMs = temporal.DeltaTimeMs, NearPlane = temporal.ZNear,
            FarPlane = temporal.ZFar, FovRadians = temporal.Fov,
            FrameId = frameId, Reset = frameId == 1 || temporal.Reset ? 1u : 0u,
            CameraPosX = temporal.Playerpos.X, CameraPosY = temporal.Playerpos.Y,
            CameraPosZ = temporal.Playerpos.Z,
            CameraUpX = inverseView[4], CameraUpY = inverseView[5], CameraUpZ = inverseView[6],
            CameraRightX = inverseView[0], CameraRightY = inverseView[1], CameraRightZ = inverseView[2],
            CameraForwardX = -inverseView[8], CameraForwardY = -inverseView[9],
            CameraForwardZ = -inverseView[10],
        };
        int result = api.EvaluateFrameGeneration(context, &frame);
        _dynamicState.Invalidate();
        return result;
    }

    private bool FlipFrameGenerationInput(CommandBuffer commands, VulkanTexture source, VulkanTexture destination)
    {
        if (source.Format != destination.Format || source.Width != destination.Width ||
            source.Height != destination.Height) return false;
        _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, source.Format,
            out FormatProperties properties);
        if ((properties.OptimalTilingFeatures &
             (FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit)) !=
            (FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit)) return false;
        _textures.Require(_barriers, commands, source, ResourceUsage.TransferSrc);
        _textures.Require(_barriers, commands, destination, ResourceUsage.TransferDst);
        _barriers.Flush(commands);
        ImageAspectFlags aspect = source.Aspect;
        var region = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(aspect, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(aspect, 0, 0, 1),
        };
        region.SrcOffsets.Element0 = new Offset3D(0, (int)source.Height, 0);
        region.SrcOffsets.Element1 = new Offset3D((int)source.Width, 0, 1);
        region.DstOffsets.Element0 = new Offset3D(0, 0, 0);
        region.DstOffsets.Element1 = new Offset3D((int)destination.Width,
            (int)destination.Height, 1);
        _context.Api.CmdBlitImage(commands, source.Image, ImageLayout.TransferSrcOptimal,
            destination.Image, ImageLayout.TransferDstOptimal, 1, &region, Filter.Nearest);
        return true;
    }
}
