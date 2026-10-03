using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal bool TryCreateFsr4SharedFrames(Fsr4Runtime runtime, in UpscalerPlan plan,
        out Fsr4SharedFrames? shared, out string reason) =>
        Fsr4SharedFrames.TryCreate(_context, runtime, plan, out shared, out reason);

    internal int EvaluateFsr4(Fsr4Runtime runtime, Fsr4SharedFrames shared, int motionRg,
        in UpscalerPlan plan, in UpscalerFrame frame, bool firstFrame)
    {
        using GpuSection gpuSection = BeginGpuSection("upscale_fsr4");
        if (!_frameActive) return -3;
        VulkanTexture? color = _textures.Get(frame.Color), depth = _textures.Get(frame.Depth),
            sourceMotion = _textures.Get(frame.Motion), motion = _textures.Get(motionRg),
            output = _textures.Get(frame.Output);
        if (color == null || depth == null || sourceMotion == null || motion == null ||
            output == null) return -4;
        if (color.Width != (uint)plan.RenderWidth || color.Height != (uint)plan.RenderHeight ||
            output.Width != (uint)plan.DisplayWidth || output.Height != (uint)plan.DisplayHeight ||
            output.Format != Format.R16G16B16A16Sfloat ||
            (output.Usage & ImageUsageFlags.TransferDstBit) == 0) return -5;
        if (!PrepareUpscalerMotion(sourceMotion, motion, color.Width, color.Height, Commands))
            return -6;

        Fsr4SharedFrames.PreparedFrame prepared = shared.Next();
        Fsr4SharedFrames.ImageSet images = prepared.Images;
        if (!images.Color.CanBlitFrom(color, out _) ||
            !images.Depth.CanBlitFrom(depth, out _) ||
            !images.Motion.CanBlitFrom(motion, out _)) return -7;

        CommandBuffer commands = Commands;
        _textures.Require(_barriers, commands, color, ResourceUsage.TransferSrc);
        _textures.Require(_barriers, commands, depth, ResourceUsage.TransferSrc);
        _textures.Require(_barriers, commands, motion, ResourceUsage.TransferSrc);
        _barriers.Flush(commands);
        images.Color.RecordCopyFrom(commands, color, flip: false);
        images.Depth.RecordCopyFrom(commands, depth, flip: false);
        images.Motion.RecordCopyFrom(commands, motion, flip: false);
        images.Output.RecordPrepareForDx12(commands);

        // The first submit hands this image set to DX12. The second waits on
        // the SDK's completion value before Vulkan copies the result back.
        _targets.EndRendering(commands);
        _bindless?.Flush();
        _frames.SubmitExternalPartial(shared.SharedSemaphore, prepared.WaitForDx12,
            prepared.ReadyForDx12);
        Checkpoint(Commands, CheckpointMarker.FrameBegin(_frameCounter));
        var native = new Fsr4Frame
        {
            Color = images.Color.D3D12Resource,
            Depth = images.Depth.D3D12Resource,
            Motion = images.Motion.D3D12Resource,
            Output = images.Output.D3D12Resource,
            RenderWidth = (uint)plan.RenderWidth,
            RenderHeight = (uint)plan.RenderHeight,
            DisplayWidth = (uint)plan.DisplayWidth,
            DisplayHeight = (uint)plan.DisplayHeight,
            JitterX = frame.Temporal.JitterX,
            JitterY = frame.Temporal.JitterY,
            DeltaMs = frame.Temporal.DeltaTimeMs,
            NearPlane = frame.Temporal.NearPlane,
            FarPlane = frame.Temporal.FarPlane,
            Fov = frame.Temporal.FovRadians,
            Reset = firstFrame || frame.Temporal.Reset ? 1u : 0u,
            ReadyValue = prepared.ReadyForDx12,
            DoneValue = prepared.DoneByDx12,
        };
        int result = runtime.Evaluate(native);
        if (result != 0) return result;
        shared.MarkDispatched(prepared);
        commands = Commands;
        _textures.Require(_barriers, commands, output, ResourceUsage.TransferDst);
        _barriers.Flush(commands);
        images.Output.RecordCopyTo(commands, output);
        _bindless?.Flush();
        _frames.SubmitExternalPartial(shared.SharedSemaphore, prepared.DoneByDx12, 0);
        Checkpoint(Commands, CheckpointMarker.FrameBegin(_frameCounter));
        _dynamicState.Invalidate();
        return 0;
    }
}
