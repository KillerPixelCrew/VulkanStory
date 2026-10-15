using System;
using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Platform.Sdl;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Presents a secondary HWND through DXGI using the existing Vulkan/DX12 image and fence handoff.</summary>
/// <remarks>This retains a single Vulkan presentation device for the SDK and RTSS.</remarks>
internal sealed unsafe class AuxiliaryPresenter : IDisposable, IDx12SharedRuntime
{
    private readonly VulkanContext _renderer;
    private nint _module, _native;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _destroy, _waitIdle;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, uint, uint, nint*, nint*, int> _createImage;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _releaseImage;
    private readonly delegate* unmanaged[Cdecl]<nint, nint*, int> _createFence;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, uint, uint, uint, ulong, ulong, int> _present;
    private XessSharedFence? _fence;
    private VulkanSharedImage? _image;
    private BarrierBatcher? _barriers;
    private ulong _done;
    private bool _disposed;

    public AuxiliaryPresenter(VulkanContext renderer, SdlWindowHost host, uint width, uint height)
    {
        _renderer = renderer;
        string path = Path.Combine(NativeRuntimePaths.DirectoryContaining("VulkanStoryAuxPresent.dll"), "VulkanStoryAuxPresent.dll");
        _module = NativeRuntimePaths.LoadLibrary(path);
        try
        {
            _destroy = (delegate* unmanaged[Cdecl]<nint, int>)Export("AuxDestroy");
            _waitIdle = (delegate* unmanaged[Cdecl]<nint, int>)Export("AuxWaitIdle");
            _createImage = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, nint*, nint*, int>)Export("AuxCreateSharedImage");
            _releaseImage = (delegate* unmanaged[Cdecl]<nint, void>)Export("AuxReleaseImage");
            _createFence = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Export("AuxCreateSharedFence");
            _present = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint, uint, ulong, ulong, int>)Export("AuxPresent");
            var create = (delegate* unmanaged[Cdecl]<ulong, nint, uint, uint, nint*, int>)Export("AuxCreate");
            var identity = new PhysicalDeviceIDProperties { SType = StructureType.PhysicalDeviceIDProperties };
            var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &identity };
            renderer.Api.GetPhysicalDeviceProperties2(renderer.PhysicalDevice, &properties);
            if (!identity.DeviceLuidvalid) throw new InvalidOperationException("The Vulkan adapter has no Windows LUID.");
            ulong luid = *(ulong*)identity.DeviceLuid;
            nint native = 0;
            Check(create(luid, host.Win32Handle, width, height, &native), "create");
            _native = native;
            if (!XessSharedFence.TryCreate(renderer, this, out _fence, out string reason))
                throw new InvalidOperationException(reason);
        }
        catch { Dispose(); throw; }
    }
    private nint Export(string name) => NativeLibrary.GetExport(_module, name);
    private static void Check(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"Auxiliary DXGI {operation} failed (0x{result:X8}).");
    }
    public bool Present(FrameRing rendererFrames, TextureManager textures, VulkanTexture source,
        ulong renderValue, bool vsync, out ulong presentId, out ulong presentValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_image == null || _image.Width != source.Width || _image.Height != source.Height)
        {
            Drain();
            _image?.Dispose();
            _image = null;
            if (!VulkanSharedImage.TryCreate(_renderer, this, source.Width, source.Height,
                Format.R8G8B8A8Unorm, out _image, out string reason))
                throw new InvalidOperationException(reason);
        }
        CommandBuffer write = rendererFrames.BeginPresentCommands();
        BarrierBatcher barriers = _barriers ??= textures.CreateBatcher();
        textures.Require(barriers, write, source, ResourceUsage.TransferSrc);
        barriers.Flush(write);
        _image!.RecordFlippedCopy(write, source);
        ulong ready = _done + 1, done = _done + 2;
        presentValue = rendererFrames.SubmitExternalPresent(renderValue, _fence!.Semaphore, _done, ready);
        Check(_present(_native, _image.D3D12Resource, source.Width, source.Height, vsync ? 1u : 0u, ready, done), "present");
        _done = done;
        presentId = done / 2;
        return true;
    }
    public int CreateSharedImage(uint width, uint height, Format format, bool writable, out nint handle, out nint resource)
    {
        nint h = 0, r = 0;
        int result = writable ? -1 : _createImage(_native, width, height, (uint)format, &h, &r);
        handle = h; resource = r;
        return result;
    }
    public void ReleaseImage(nint resource) => _releaseImage(resource);
    public int CreateSharedFence(out nint handle)
    {
        nint h = 0;
        int result = _createFence(_native, &h);
        handle = h;
        return result;
    }
    private void Drain()
    {
        VulkanResult.Check(_renderer.WaitDeviceIdle(), "draining auxiliary Vulkan image copies");
        if (_native != 0) Check(_waitIdle(_native), "drain");
    }
    public void Dispose()
    {
        if (_disposed) return;
        Drain();
        _image?.Dispose(); _image = null;
        _fence?.Dispose(); _fence = null;
        if (_native != 0) { Check(_destroy(_native), "destroy"); _native = 0; }
        if (_module != 0) { NativeLibrary.Free(_module); _module = 0; }
        _disposed = true;
    }
}
