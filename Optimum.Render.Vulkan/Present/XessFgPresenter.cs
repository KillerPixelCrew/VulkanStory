using System;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

internal readonly record struct XessSourceImages(
    VulkanTexture Color, VulkanTexture Depth, VulkanTexture Motion,
    VulkanTexture Hudless, VulkanTexture Ui);

/// <summary>
/// Owns the Intel proxy and three sets of shared images. Vulkan copies one set
/// while DX12 may still be reading a different set. One D3D12 fence timeline
/// carries the release/acquire values for all sets.
/// </summary>
internal sealed unsafe class XessFgPresenter : IDisposable
{
    private sealed class ImageSet : IDisposable
    {
        public required VulkanSharedImage Color { get; init; }
        public required VulkanSharedImage Depth { get; init; }
        public required VulkanSharedImage Motion { get; init; }
        public required VulkanSharedImage Hudless { get; init; }
        public required VulkanSharedImage Ui { get; init; }
        public ulong LastDx12Done;

        public void Dispose()
        {
            Ui.Dispose();
            Hudless.Dispose();
            Motion.Dispose();
            Depth.Dispose();
            Color.Dispose();
        }
    }

    private readonly VulkanContext _context;
    private readonly XessFgRuntime _runtime;
    private readonly XessSharedFence _fence;
    private readonly ImageSet[] _images;
    private ulong _nextFenceValue = 1;
    private int _nextImageSet;
    private bool _disposed;

    private XessFgPresenter(VulkanContext context, XessFgRuntime runtime,
        XessSharedFence fence, ImageSet[] images, uint width, uint height)
    {
        _context = context;
        _runtime = runtime;
        _fence = fence;
        _images = images;
        Width = width;
        Height = height;
    }

    public uint Width { get; }
    public uint Height { get; }
    public Silk.NET.Vulkan.Semaphore SharedSemaphore => _fence.Semaphore;
    public XessFgRuntime Runtime => _runtime;

    public static bool TryCreate(VulkanContext context, nint window, uint width,
        uint height, bool vsync, in XessSourceImages sources,
        out XessFgPresenter? presenter, out string reason)
    {
        presenter = null;
        if (!XessFgRuntime.TryCreate(context, out XessFgRuntime? runtime, out reason))
            return false;
        XessSharedFence? fence = null;
        var sets = new ImageSet[3];
        try
        {
            if (!XessSharedFence.TryCreate(context, runtime!, out fence, out reason))
                return false;
            for (int i = 0; i < sets.Length; i++)
            {
                VulkanSharedImage? color = null, depth = null, motion = null, hudless = null, ui = null;
                try
                {
                    color = Create(context, runtime!, sources.Color, Format.B8G8R8A8Unorm);
                    depth = Create(context, runtime!, sources.Depth, Format.D32Sfloat);
                    motion = Create(context, runtime!, sources.Motion, Format.R16G16Sfloat);
                    hudless = Create(context, runtime!, sources.Hudless, Format.B8G8R8A8Unorm);
                    ui = Create(context, runtime!, sources.Ui, Format.B8G8R8A8Unorm);
                    sets[i] = new ImageSet
                    {
                        Color = color, Depth = depth, Motion = motion,
                        Hudless = hudless, Ui = ui,
                    };
                }
                catch
                {
                    ui?.Dispose(); hudless?.Dispose(); motion?.Dispose();
                    depth?.Dispose(); color?.Dispose();
                    throw;
                }
            }
            int code = runtime!.Start(window, width, height, vsync);
            if (code != 0) throw new InvalidOperationException("Intel proxy initialization failed (" + code + ")");
            code = runtime.SetEnabled(true);
            if (code < 0) throw new InvalidOperationException("Intel proxy enable failed (" + code + ")");
            presenter = new XessFgPresenter(context, runtime, fence!, sets, width, height);
            reason = "ready";
            return true;
        }
        catch (Exception error)
        {
            reason = "XeSS-FG presentation failed: " + error.Message;
            return false;
        }
        finally
        {
            if (presenter == null)
            {
                for (int i = sets.Length - 1; i >= 0; i--) sets[i]?.Dispose();
                fence?.Dispose();
                runtime?.Dispose();
            }
        }
    }

    private static VulkanSharedImage Create(VulkanContext context, XessFgRuntime runtime,
        VulkanTexture source, Format sharedFormat)
    {
        if (!VulkanSharedImage.TryCreate(context, runtime, source.Width, source.Height,
            sharedFormat, out VulkanSharedImage? image, out string reason))
            throw new InvalidOperationException(reason);
        if (!image!.CanBlitFrom(source, out reason))
        {
            image.Dispose();
            throw new InvalidOperationException(reason);
        }
        return image;
    }

    public readonly record struct PreparedFrame(int SetIndex, ulong WaitForDx12,
        ulong ReadyForDx12, ulong DoneByDx12);

    public PreparedFrame RecordCopies(CommandBuffer commands, TextureManager textures,
        BarrierBatcher barriers, in XessSourceImages sources)
    {
        ImageSet set = _images[_nextImageSet];
        if (!set.Color.CanBlitFrom(sources.Color, out string reason) ||
            !set.Depth.CanBlitFrom(sources.Depth, out reason) ||
            !set.Motion.CanBlitFrom(sources.Motion, out reason) ||
            !set.Hudless.CanBlitFrom(sources.Hudless, out reason) ||
            !set.Ui.CanBlitFrom(sources.Ui, out reason))
            throw new InvalidOperationException(reason);
        textures.Require(barriers, commands, sources.Color, ResourceUsage.TransferSrc);
        textures.Require(barriers, commands, sources.Depth, ResourceUsage.TransferSrc);
        textures.Require(barriers, commands, sources.Motion, ResourceUsage.TransferSrc);
        textures.Require(barriers, commands, sources.Hudless, ResourceUsage.TransferSrc);
        textures.Require(barriers, commands, sources.Ui, ResourceUsage.TransferSrc);
        barriers.Flush(commands);
        set.Color.RecordFlippedCopy(commands, sources.Color);
        set.Depth.RecordFlippedCopy(commands, sources.Depth);
        set.Motion.RecordFlippedCopy(commands, sources.Motion);
        set.Hudless.RecordFlippedCopy(commands, sources.Hudless);
        set.Ui.RecordFlippedCopy(commands, sources.Ui);
        ulong ready = _nextFenceValue++;
        ulong done = _nextFenceValue++;
        PreparedFrame frame = new(_nextImageSet, set.LastDx12Done, ready, done);
        _nextImageSet = (_nextImageSet + 1) % _images.Length;
        return frame;
    }

    public int Present(in PreparedFrame prepared, in XessPresentationFrame constants,
        out uint framesPresented, out int frameGenResult, out bool frameGenEnabled)
    {
        ImageSet set = _images[prepared.SetIndex];
        XessPresentationFrame frame = constants;
        frame.Color = set.Color.D3D12Resource;
        frame.Depth = set.Depth.D3D12Resource;
        frame.Motion = set.Motion.D3D12Resource;
        frame.Hudless = set.Hudless.D3D12Resource;
        frame.Ui = set.Ui.D3D12Resource;
        frame.ReadyFenceValue = prepared.ReadyForDx12;
        frame.DoneFenceValue = prepared.DoneByDx12;
        int code = _runtime.Present(frame, out framesPresented,
            out frameGenResult, out frameGenEnabled);
        if (code == 0) set.LastDx12Done = prepared.DoneByDx12;
        return code;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.WaitDeviceIdle();
        _runtime.SetEnabled(false);
        if (_runtime.WaitIdle() != 0)
            throw new InvalidOperationException("XeSS-FG DX12 queue did not become idle");
        for (int i = _images.Length - 1; i >= 0; i--) _images[i].Dispose();
        _fence.Dispose();
        _runtime.Dispose();
    }
}
