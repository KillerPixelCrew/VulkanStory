using System;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Three independent image sets for Vulkan-to-DX12 FSR 4 dispatches.</summary>
internal sealed class Fsr4SharedFrames : IDisposable
{
    /// <summary>One shared color, depth, motion and output set with its most recent DX12 completion value.</summary>
    internal sealed class ImageSet : IDisposable
    {
        public required VulkanSharedImage Color { get; init; }
        public required VulkanSharedImage Depth { get; init; }
        public required VulkanSharedImage Motion { get; init; }
        public required VulkanSharedImage Output { get; init; }
        public ulong LastDx12Done;

        /// <inheritdoc/>
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

    /// <summary>Imported Vulkan timeline semaphore backed by the shared DX12 fence.</summary>
    public Semaphore SharedSemaphore => fence.Semaphore;

    /// <summary>Creates three independent shared image sets and their shared fence.</summary>
    /// <param name="context">Borrowed Vulkan context; must outlive the shared resources.</param>
    /// <param name="runtime">Borrowed DX12 runtime; must outlive the imported images.</param>
    /// <param name="plan">Input and display extents.</param>
    /// <param name="frames">New owner on success.</param>
    /// <param name="reason">Creation detail.</param>
    /// <returns>Whether all image sets and the imported fence were created.</returns>
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

    /// <summary>Selected image set and ordered fence values for a Vulkan-to-DX12-to-Vulkan exchange.</summary>
    public readonly record struct PreparedFrame(ImageSet Images, ulong WaitForDx12,
        ulong ReadyForDx12, ulong DoneByDx12);

    /// <summary>Rotates to the next image set and reserves ready/done fence values.</summary>
    /// <remarks>The caller must wait for WaitForDx12 before overwriting this set and signal ReadyForDx12 after input copies.</remarks>
    /// <returns>The prepared set; call MarkDispatched when its DX12 dispatch is submitted.</returns>
    public PreparedFrame Next()
    {
        RequireLifetime();
        ObjectDisposedException.ThrowIf(disposed, this);
        ImageSet set = sets[nextSet];
        nextSet = (nextSet + 1) % sets.Length;
        return new PreparedFrame(set, set.LastDx12Done,
            nextFenceValue++, nextFenceValue++);
    }

    /// <summary>Records the submitted DX12 completion value for the prepared image set.</summary>
    public void MarkDispatched(in PreparedFrame prepared) =>
        prepared.Images.LastDx12Done = prepared.DoneByDx12;

    private void RequireLifetime()
    {
        if (releaseFailure != null)
            throw new InvalidOperationException("FSR 4 shared resource release failed; remaining owners are retained.", releaseFailure);
    }
    /// <inheritdoc/>
    public void Dispose()
    {
        RequireLifetime();
        if (disposed) return;
        try
        {
            VulkanResult.Check(context.WaitDeviceIdle(), "draining Vulkan before FSR 4 shared resource release");
            runtime.PrepareRelease();
            for (int i = sets.Length - 1; i >= 0; i--) sets[i].Dispose();
            fence.Dispose();
            disposed = true;
        }
        catch (Exception failure) { releaseFailure = failure; throw; }
    }
}
