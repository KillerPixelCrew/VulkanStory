using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Optimum.Render.Vulkan.Core;

[StructLayout(LayoutKind.Sequential)]
internal struct StreamlineTaggedImage
{
    public ulong Image, View;
    public uint Layout, Format, Width, Height, Usage;

    public static StreamlineTaggedImage From(VulkanTexture texture) => new()
    {
        Image = texture.Image.Handle,
        View = texture.View.Handle,
        Layout = (uint)texture.Layout,
        Format = (uint)texture.Format,
        Width = texture.Width,
        Height = texture.Height,
        Usage = (uint)texture.Usage,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct StreamlineFrameCamera
{
    public float* ViewToClip, ClipToView, ClipToPrevClip, PrevClipToClip;
    public float Near, Far, Fov, Aspect;
    public float JitterX, JitterY, MotionScaleX, MotionScaleY;
    public fixed float Position[3], Up[3], Right[3], Forward[3];
    public uint Reset;
}

/// <summary>
/// Owns one Streamline session. Its proxy Vulkan calls and frame token calls
/// have a C ABI; the C++ bridge keeps Streamline's C++ ABI private.
/// </summary>
internal sealed unsafe class StreamlineRuntime : IDisposable
{
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<void> _shutdown;
    private readonly delegate* unmanaged[Cdecl]<InstanceCreateInfo*, Instance*, Result> _createInstance;
    private readonly delegate* unmanaged[Cdecl]<Instance, uint*, PhysicalDevice*, Result> _enumeratePhysicalDevices;
    private readonly delegate* unmanaged[Cdecl]<Instance, PhysicalDevice, DeviceCreateInfo*, Device*, Result> _createDevice;
    private readonly delegate* unmanaged[Cdecl]<Device, SwapchainCreateInfoKHR*, SwapchainKHR*, Result> _createSwapchain;
    private readonly delegate* unmanaged[Cdecl]<Device, SwapchainKHR, void> _destroySwapchain;
    private readonly delegate* unmanaged[Cdecl]<Device, SwapchainKHR, uint*, Image*, Result> _getImages;
    private readonly delegate* unmanaged[Cdecl]<Device, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result> _acquire;
    private readonly delegate* unmanaged[Cdecl]<Device, Queue, PresentInfoKHR*, Result> _present;
    private readonly delegate* unmanaged[Cdecl]<Instance, Win32SurfaceCreateInfoKHR*, SurfaceKHR*, Result> _createSurface;
    private readonly delegate* unmanaged[Cdecl]<Instance, SurfaceKHR, void> _destroySurface;
    private readonly delegate* unmanaged[Cdecl]<Device, Result> _deviceWaitIdle;
    private readonly delegate* unmanaged[Cdecl]<int> _bindFeatures;
    private readonly delegate* unmanaged[Cdecl]<PhysicalDevice, int> _isFrameGenerationSupported;
    private readonly delegate* unmanaged[Cdecl]<int, uint, int> _setReflex;
    private readonly delegate* unmanaged[Cdecl]<uint, int> _beginFrame;
    private readonly delegate* unmanaged[Cdecl]<int> _reflexSleep;
    private readonly delegate* unmanaged[Cdecl]<uint, int> _marker;
    private readonly delegate* unmanaged[Cdecl]<int, uint, uint, uint, uint, int> _setFg;
    private readonly delegate* unmanaged[Cdecl]<uint*, uint*, int> _getFgState;
    private readonly delegate* unmanaged[Cdecl]<int> _invalidateFrameTags;
    private readonly delegate* unmanaged[Cdecl]<int> _takePresentError;
    private readonly delegate* unmanaged[Cdecl]<CommandBuffer, StreamlineTaggedImage*, StreamlineTaggedImage*,
        StreamlineTaggedImage*, StreamlineTaggedImage*, StreamlineFrameCamera*, uint, uint, int> _tagFrame;
    private bool _disposed;

    private StreamlineRuntime(nint library)
    {
        _library = library;
        nint Export(string name) => NativeLibrary.GetExport(library, name);
        _shutdown = (delegate* unmanaged[Cdecl]<void>)Export("OptimumSlShutdown");
        _createInstance = (delegate* unmanaged[Cdecl]<InstanceCreateInfo*, Instance*, Result>)Export("OptimumSlCreateInstance");
        _enumeratePhysicalDevices = (delegate* unmanaged[Cdecl]<Instance, uint*, PhysicalDevice*, Result>)Export("OptimumSlEnumeratePhysicalDevices");
        _createDevice = (delegate* unmanaged[Cdecl]<Instance, PhysicalDevice, DeviceCreateInfo*, Device*, Result>)Export("OptimumSlCreateDevice");
        _createSwapchain = (delegate* unmanaged[Cdecl]<Device, SwapchainCreateInfoKHR*, SwapchainKHR*, Result>)Export("OptimumSlCreateSwapchain");
        _destroySwapchain = (delegate* unmanaged[Cdecl]<Device, SwapchainKHR, void>)Export("OptimumSlDestroySwapchain");
        _getImages = (delegate* unmanaged[Cdecl]<Device, SwapchainKHR, uint*, Image*, Result>)Export("OptimumSlGetSwapchainImages");
        _acquire = (delegate* unmanaged[Cdecl]<Device, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result>)Export("OptimumSlAcquire");
        _present = (delegate* unmanaged[Cdecl]<Device, Queue, PresentInfoKHR*, Result>)Export("OptimumSlPresent");
        _createSurface = (delegate* unmanaged[Cdecl]<Instance, Win32SurfaceCreateInfoKHR*, SurfaceKHR*, Result>)Export("OptimumSlCreateWin32Surface");
        _destroySurface = (delegate* unmanaged[Cdecl]<Instance, SurfaceKHR, void>)Export("OptimumSlDestroySurface");
        _deviceWaitIdle = (delegate* unmanaged[Cdecl]<Device, Result>)Export("OptimumSlDeviceWaitIdle");
        _bindFeatures = (delegate* unmanaged[Cdecl]<int>)Export("OptimumSlBindFeatures");
        _isFrameGenerationSupported = (delegate* unmanaged[Cdecl]<PhysicalDevice, int>)Export("OptimumSlIsFrameGenerationSupported");
        _setReflex = (delegate* unmanaged[Cdecl]<int, uint, int>)Export("OptimumSlSetReflex");
        _beginFrame = (delegate* unmanaged[Cdecl]<uint, int>)Export("OptimumSlBeginFrame");
        _reflexSleep = (delegate* unmanaged[Cdecl]<int>)Export("OptimumSlReflexSleep");
        _marker = (delegate* unmanaged[Cdecl]<uint, int>)Export("OptimumSlMarker");
        _setFg = (delegate* unmanaged[Cdecl]<int, uint, uint, uint, uint, int>)Export("OptimumSlSetFrameGeneration");
        _getFgState = (delegate* unmanaged[Cdecl]<uint*, uint*, int>)Export("OptimumSlGetFrameGenerationState");
        _invalidateFrameTags = (delegate* unmanaged[Cdecl]<int>)Export("OptimumSlInvalidateFrameTags");
        _takePresentError = (delegate* unmanaged[Cdecl]<int>)Export("OptimumSlTakePresentError");
        _tagFrame = (delegate* unmanaged[Cdecl]<CommandBuffer, StreamlineTaggedImage*, StreamlineTaggedImage*,
            StreamlineTaggedImage*, StreamlineTaggedImage*, StreamlineFrameCamera*, uint, uint, int>)Export("OptimumSlTagFrame");
    }

    internal static bool TryCreate(out StreamlineRuntime? runtime, out string reason)
    {
        runtime = null;
        string directory = Path.GetDirectoryName(typeof(StreamlineRuntime).Assembly.Location) ?? AppContext.BaseDirectory;
        string bridge = Path.Combine(directory, "OptimumStreamline.dll");
        if (!File.Exists(bridge) || !File.Exists(Path.Combine(directory, "sl.interposer.dll")))
        {
            reason = "Streamline bridge or runtime missing";
            return false;
        }
        if (!NativeLibrary.TryLoad(bridge, out nint library))
        {
            reason = "Streamline bridge could not load";
            return false;
        }
        try
        {
            var instance = new StreamlineRuntime(library);
            var initialize = (delegate* unmanaged[Cdecl]<char*, byte*, int>)NativeLibrary.GetExport(library, "OptimumSlInitialize");
            byte[] project = Encoding.UTF8.GetBytes(NgxSession.ProjectId + "\0");
            fixed (char* directoryPtr = directory)
            fixed (byte* projectPtr = project)
            {
                int result = initialize(directoryPtr, projectPtr);
                if (result != 0)
                {
                    reason = "slInit failed (" + result + ")";
                    NativeLibrary.Free(library);
                    return false;
                }
            }
            runtime = instance;
            reason = "ready";
            return true;
        }
        catch (Exception error)
        {
            NativeLibrary.Free(library);
            reason = error.Message;
            return false;
        }
    }

    internal Result CreateInstance(InstanceCreateInfo* info, out Instance instance)
    {
        instance = default;
        fixed (Instance* pointer = &instance) return _createInstance(info, pointer);
    }
    internal Result CreateDevice(Instance instance, PhysicalDevice physical, DeviceCreateInfo* info, out Device device)
    {
        device = default;
        fixed (Device* pointer = &device) return _createDevice(instance, physical, info, pointer);
    }
    internal Result EnumeratePhysicalDevices(Instance instance, ref uint count, PhysicalDevice* devices)
    {
        fixed (uint* pointer = &count) return _enumeratePhysicalDevices(instance, pointer, devices);
    }
    internal Result CreateSwapchain(Device device, SwapchainCreateInfoKHR* info, out SwapchainKHR chain)
    {
        chain = default;
        fixed (SwapchainKHR* pointer = &chain) return _createSwapchain(device, info, pointer);
    }
    internal void DestroySwapchain(Device device, SwapchainKHR chain) => _destroySwapchain(device, chain);
    internal Result GetImages(Device device, SwapchainKHR chain, ref uint count, Image* images)
    {
        fixed (uint* pointer = &count) return _getImages(device, chain, pointer, images);
    }
    internal Result Acquire(Device device, SwapchainKHR chain, Semaphore semaphore, ref uint index)
    {
        fixed (uint* pointer = &index) return _acquire(device, chain, ulong.MaxValue, semaphore, default, pointer);
    }
    internal Result Present(Device device, Queue queue, PresentInfoKHR* info) => _present(device, queue, info);
    internal Result CreateSurface(Instance instance, Win32SurfaceCreateInfoKHR* info, out SurfaceKHR surface)
    {
        surface = default;
        fixed (SurfaceKHR* pointer = &surface) return _createSurface(instance, info, pointer);
    }
    internal void DestroySurface(Instance instance, SurfaceKHR surface) => _destroySurface(instance, surface);
    internal Result DeviceWaitIdle(Device device) => _deviceWaitIdle(device);
    internal int BindFeatures() => _bindFeatures();
    internal int IsFrameGenerationSupported(PhysicalDevice physical) => _isFrameGenerationSupported(physical);
    internal int SetReflex(int mode, int maxFps) => _setReflex(mode, (uint)Math.Max(maxFps, 0));
    internal int BeginFrame(ulong id) => _beginFrame((uint)id);
    internal int ReflexSleep() => _reflexSleep();
    internal int Marker(uint marker) => _marker(marker);
    internal int SetFrameGeneration(bool enabled, uint width, uint height, Format colorFormat, uint buffers) =>
        _setFg(enabled ? 1 : 0, width, height, (uint)colorFormat, buffers);
    internal int GetFrameGenerationState(out uint status, out uint presented)
    {
        status = 0; presented = 0;
        fixed (uint* statusPtr = &status)
        fixed (uint* presentedPtr = &presented)
            return _getFgState(statusPtr, presentedPtr);
    }
    internal int TakePresentError() => _takePresentError();
    internal int InvalidateFrameTags() => _invalidateFrameTags();
    internal int TagFrame(CommandBuffer commands, StreamlineTaggedImage* depth,
        StreamlineTaggedImage* motion, StreamlineTaggedImage* hudless, StreamlineTaggedImage* ui,
        StreamlineFrameCamera* camera, uint width, uint height) =>
        _tagFrame(commands, depth, motion, hudless, ui, camera, width, height);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown();
        NativeLibrary.Free(_library);
    }
}
