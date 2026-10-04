using System;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Three independent image sets for Vulkan-to-DX12 FSR 4 dispatches.</summary>
internal sealed class Fsr4SharedFrames : IDisposable
{
    internal sealed class ImageSet : IDisposable
    {
        public required VulkanSharedImage Color { get; init; }
        public required VulkanSharedImage Depth { get; init; }
        public required VulkanSharedImage Motion { get; init; }
        public required VulkanSharedImage Output { get; init; }
        public ulong LastDx12Done;

        public void Dispose()
        {
            Output.Dispose(); Motion.Dispose(); Depth.Dispose(); Color.Dispose();
        }
    }

    private readonly VulkanContext context;
    private readonly Fsr4Runtime runtime;
    private readonly XessSharedFence fence;
    private readonly ImageSet[] sets;
    private ulong nextFenceValue = 1;
    private int nextSet;
    private bool disposed;
    private Exception? releaseFailure;

    private Fsr4SharedFrames(VulkanContext context, Fsr4Runtime runtime,
        XessSharedFence fence, ImageSet[] sets)
    {
        this.context = context;
        this.runtime = runtime;
        this.fence = fence;
        this.sets = sets;
    }

    public Semaphore SharedSemaphore => fence.Semaphore;

    public static bool TryCreate(VulkanContext context, Fsr4Runtime runtime,
        in UpscalerPlan plan, out Fsr4SharedFrames? frames, out string reason)
    {
        frames = null;
        XessSharedFence? fence = null;
        var sets = new ImageSet[3];
        try
        {
            if (!XessSharedFence.TryCreate(context, runtime, out fence, out reason))
                return false;
            for (int i = 0; i < sets.Length; i++)
            {
                VulkanSharedImage? color = null, depth = null, motion = null, output = null;
                try
                {
                    color = Create(context, runtime, (uint)plan.RenderWidth,
                        (uint)plan.RenderHeight, Format.R16G16B16A16Sfloat);
                    depth = Create(context, runtime, (uint)plan.RenderWidth,
                        (uint)plan.RenderHeight, Format.D32Sfloat);
                    motion = Create(context, runtime, (uint)plan.RenderWidth,
                        (uint)plan.RenderHeight, Format.R16G16Sfloat);
                    output = Create(context, runtime, (uint)plan.DisplayWidth,
                        (uint)plan.DisplayHeight, Format.R16G16B16A16Sfloat, true);
                    sets[i] = new ImageSet { Color = color, Depth = depth,
                        Motion = motion, Output = output };
                }
                catch
                {
                    output?.Dispose(); motion?.Dispose(); depth?.Dispose(); color?.Dispose();
                    throw;
                }
            }
            frames = new Fsr4SharedFrames(context, runtime, fence!, sets);
            reason = "ready";
            return true;
        }
        catch (Exception error)
        {
            reason = "FSR 4 shared resources failed: " + error.Message;
            return false;
        }
        finally
        {
            if (frames == null)
            {
                for (int i = sets.Length - 1; i >= 0; i--) sets[i]?.Dispose();
                fence?.Dispose();
            }
        }
    }

    private static VulkanSharedImage Create(VulkanContext context, Fsr4Runtime runtime,
        uint width, uint height, Format format, bool writable = false)
    {
        if (!VulkanSharedImage.TryCreate(context, runtime, width, height, format,
            out VulkanSharedImage? image, out string reason, writable))
            throw new InvalidOperationException(reason);
        return image!;
    }

    public readonly record struct PreparedFrame(ImageSet Images, ulong WaitForDx12,
        ulong ReadyForDx12, ulong DoneByDx12);

    public PreparedFrame Next()
    {
        RequireLifetime();
        ObjectDisposedException.ThrowIf(disposed, this);
        ImageSet set = sets[nextSet];
        nextSet = (nextSet + 1) % sets.Length;
        return new PreparedFrame(set, set.LastDx12Done,
            nextFenceValue++, nextFenceValue++);
    }

    public void MarkDispatched(in PreparedFrame prepared) =>
        prepared.Images.LastDx12Done = prepared.DoneByDx12;

    private void RequireLifetime()
    {
        if (releaseFailure != null)
            throw new InvalidOperationException("FSR 4 shared resource release failed; remaining owners are retained.", releaseFailure);
    }
    public void Dispose()
    {
        RequireLifetime();
        if (disposed) return;
        try
        {
            VulkanResult.Check(context.WaitDeviceIdle(), "draining Vulkan before FSR 4 shared resource release");
            int idle = runtime.WaitIdle();
            if (idle != 0)
                throw new InvalidOperationException("FSR 4 DX12 queue did not become idle (" + idle + ").");
            runtime.PrepareRelease();
            for (int i = sets.Length - 1; i >= 0; i--) sets[i].Dispose();
            fence.Dispose();
            disposed = true;
        }
        catch (Exception failure) { releaseFailure = failure; throw; }
    }
}
