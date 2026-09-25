using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

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
}
