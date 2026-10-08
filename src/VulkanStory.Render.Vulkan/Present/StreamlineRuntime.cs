using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Sequential six-word snapshot returned by the Streamline frame-generation bridge.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StreamlineFrameGenerationState
{
    internal uint Status, Presents, MaximumGenerated, MinimumSize, VsyncSupport, DynamicMfgSupport;
}

/// <summary>Borrowed Vulkan image metadata in the Streamline bridge C ABI.</summary>
/// <remarks>Tag lifetime is controlled by the frame-tagging/presentation owner; this record does not own the image.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal partial struct StreamlineTaggedImage
{
    public ulong Image, View;
    public uint Layout, Format, Width, Height, Usage;
}

/// <summary>Camera transforms, jitter, motion scale and reset constants for matching Streamline frame tags.</summary>
/// <remarks>Matrix pointers borrow caller storage for the native bridge call.</remarks>
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
    /// <summary>Win32 display-adapter enumeration record used only to choose optional NVIDIA plugins.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayDevice
    {
        public uint Size;
        public fixed char Name[32];
        public fixed char Description[128];
        public uint StateFlags;
        public fixed char DeviceId[128];
        public fixed char DeviceKey[128];
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(char* deviceName, uint index,
        DisplayDevice* device, uint flags);

    private static bool HasNvidiaDisplayAdapter()
    {
        if (!OperatingSystem.IsWindows()) return false;
        for (uint index = 0; index < 32; index++)
        {
            DisplayDevice device = default;
            device.Size = (uint)sizeof(DisplayDevice);
            if (!EnumDisplayDevices(null, index, &device, 0)) break;
            if (new string(device.Description).Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Applies an explicit plugin override or enables DLSS-G only for an unpinned NVIDIA-adapter selection.</summary>
    internal static bool ShouldLoadDlssG(int preferredDeviceIndex, bool hasNvidiaAdapter,
        string? setting) => setting switch
    {
        "1" => true,
        "0" => false,
        // An explicit Vulkan device pin may select a non-NVIDIA adapter in a
        // hybrid system. Its optional DLSS-G plugin must not inject NVIDIA-only
        // Vulkan extensions before Reflex and PCL can start.
        _ => preferredDeviceIndex < 0 && hasNvidiaAdapter,
    };

    /// <summary>Applies an explicit plugin override or enables DLSS-G only for an unpinned NVIDIA-adapter selection.</summary>
    internal static bool ShouldLoadReflex(int preferredDeviceIndex, bool hasNvidiaAdapter,
        string? setting) => ShouldLoadDlssG(preferredDeviceIndex, hasNvidiaAdapter, setting);

    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<void> _shutdown;
    private readonly delegate* unmanaged[Cdecl]<void> _unload;
    private readonly delegate* unmanaged[Cdecl]<Instance, byte*, nint> _instanceProc;
    private readonly delegate* unmanaged[Cdecl]<Device, byte*, nint> _deviceProc;
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
    private readonly delegate* unmanaged[Cdecl]<int> _bindReflex;
    private readonly delegate* unmanaged[Cdecl]<int> _bindFrameGeneration;
    private readonly delegate* unmanaged[Cdecl]<int> _bindPcl;
    private readonly delegate* unmanaged[Cdecl]<uint*, uint*, int> _getReflexState;
    private readonly delegate* unmanaged[Cdecl]<uint> _pclWindowMessage;
    private readonly delegate* unmanaged[Cdecl]<PhysicalDevice, int> _isFrameGenerationSupported;
    private readonly delegate* unmanaged[Cdecl]<int, uint, int> _setReflex;
    private readonly delegate* unmanaged[Cdecl]<uint, int> _beginFrame;
    private readonly delegate* unmanaged[Cdecl]<nint> _currentFrameToken;
    private readonly delegate* unmanaged[Cdecl]<int> _reflexSleep;
    private readonly delegate* unmanaged[Cdecl]<uint, int> _marker;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, int> _markerForToken;
    private readonly delegate* unmanaged[Cdecl]<int, uint, uint, uint, uint, uint, int> _setFg;
    private readonly delegate* unmanaged[Cdecl]<uint*, uint*, uint*, int> _getFgState;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, int> _invalidateFrameTags;
    private readonly delegate* unmanaged[Cdecl]<int> _freeFrameGenerationResources;
    private readonly delegate* unmanaged[Cdecl]<int> _disableFrameGenerationForRelease;
    private readonly delegate* unmanaged[Cdecl]<uint*, uint, int> _getFgStateDetails;
    private readonly delegate* unmanaged[Cdecl]<int> _takePresentError;
    private readonly delegate* unmanaged[Cdecl]<CommandBuffer, StreamlineTaggedImage*, StreamlineTaggedImage*,
        StreamlineTaggedImage*, StreamlineTaggedImage*, StreamlineFrameCamera*, uint, uint, int> _tagFrame;
    private bool _disposed;
    private bool _shutdownComplete;

    private StreamlineRuntime(nint library)
    {
        _library = library;
        nint Export(string name) => NativeLibrary.GetExport(library, name);
        _shutdown = (delegate* unmanaged[Cdecl]<void>)Export("VulkanStorySlShutdown");
        _unload = (delegate* unmanaged[Cdecl]<void>)Export("VulkanStorySlUnload");
        _instanceProc = (delegate* unmanaged[Cdecl]<Instance, byte*, nint>)Export("VulkanStorySlGetInstanceProcAddr");
        _deviceProc = (delegate* unmanaged[Cdecl]<Device, byte*, nint>)Export("VulkanStorySlGetDeviceProcAddr");
        _createInstance = (delegate* unmanaged[Cdecl]<InstanceCreateInfo*, Instance*, Result>)Export("VulkanStorySlCreateInstance");
        _enumeratePhysicalDevices = (delegate* unmanaged[Cdecl]<Instance, uint*, PhysicalDevice*, Result>)Export("VulkanStorySlEnumeratePhysicalDevices");
        _createDevice = (delegate* unmanaged[Cdecl]<Instance, PhysicalDevice, DeviceCreateInfo*, Device*, Result>)Export("VulkanStorySlCreateDevice");
        _createSwapchain = (delegate* unmanaged[Cdecl]<Device, SwapchainCreateInfoKHR*, SwapchainKHR*, Result>)Export("VulkanStorySlCreateSwapchain");
        _destroySwapchain = (delegate* unmanaged[Cdecl]<Device, SwapchainKHR, void>)Export("VulkanStorySlDestroySwapchain");
        _getImages = (delegate* unmanaged[Cdecl]<Device, SwapchainKHR, uint*, Image*, Result>)Export("VulkanStorySlGetSwapchainImages");
        _acquire = (delegate* unmanaged[Cdecl]<Device, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result>)Export("VulkanStorySlAcquire");
        _present = (delegate* unmanaged[Cdecl]<Device, Queue, PresentInfoKHR*, Result>)Export("VulkanStorySlPresent");
        _createSurface = (delegate* unmanaged[Cdecl]<Instance, Win32SurfaceCreateInfoKHR*, SurfaceKHR*, Result>)Export("VulkanStorySlCreateWin32Surface");
        _destroySurface = (delegate* unmanaged[Cdecl]<Instance, SurfaceKHR, void>)Export("VulkanStorySlDestroySurface");
        _deviceWaitIdle = (delegate* unmanaged[Cdecl]<Device, Result>)Export("VulkanStorySlDeviceWaitIdle");
        _bindReflex = (delegate* unmanaged[Cdecl]<int>)Export("VulkanStorySlBindReflex");
        _bindFrameGeneration = (delegate* unmanaged[Cdecl]<int>)Export("VulkanStorySlBindFrameGeneration");
        _bindPcl = NativeLibrary.TryGetExport(library, "VulkanStorySlBindPcl", out nint bindPcl)
            ? (delegate* unmanaged[Cdecl]<int>)bindPcl : null;
        _getReflexState = (delegate* unmanaged[Cdecl]<uint*, uint*, int>)Export("VulkanStorySlGetReflexState");
        _pclWindowMessage = NativeLibrary.TryGetExport(library, "VulkanStorySlPclWindowMessage", out nint pclMessage)
            ? (delegate* unmanaged[Cdecl]<uint>)pclMessage : null;
        _isFrameGenerationSupported = (delegate* unmanaged[Cdecl]<PhysicalDevice, int>)Export("VulkanStorySlIsFrameGenerationSupported");
        _setReflex = (delegate* unmanaged[Cdecl]<int, uint, int>)Export("VulkanStorySlSetReflex");
        _beginFrame = (delegate* unmanaged[Cdecl]<uint, int>)Export("VulkanStorySlBeginFrame");
        _currentFrameToken = (delegate* unmanaged[Cdecl]<nint>)Export("VulkanStorySlCurrentFrameToken");
        _reflexSleep = (delegate* unmanaged[Cdecl]<int>)Export("VulkanStorySlReflexSleep");
        _marker = (delegate* unmanaged[Cdecl]<uint, int>)Export("VulkanStorySlMarker");
        _markerForToken = (delegate* unmanaged[Cdecl]<nint, uint, int>)Export("VulkanStorySlMarkerForToken");
        _setFg = (delegate* unmanaged[Cdecl]<int, uint, uint, uint, uint, uint, int>)Export("VulkanStorySlSetFrameGeneration");
        _getFgState = (delegate* unmanaged[Cdecl]<uint*, uint*, uint*, int>)Export("VulkanStorySlGetFrameGenerationState");
        _invalidateFrameTags = (delegate* unmanaged[Cdecl]<uint, uint, int>)Export("VulkanStorySlInvalidateFrameTagsWithExtent");
        _freeFrameGenerationResources = (delegate* unmanaged[Cdecl]<int>)Export("VulkanStorySlFreeFrameGenerationResources");
        _disableFrameGenerationForRelease = (delegate* unmanaged[Cdecl]<int>)Export("VulkanStorySlDisableFrameGenerationForRelease");
        _getFgStateDetails = (delegate* unmanaged[Cdecl]<uint*, uint, int>)Export("VulkanStorySlGetFrameGenerationStateDetails");
        _takePresentError = (delegate* unmanaged[Cdecl]<int>)Export("VulkanStorySlTakePresentError");
        _tagFrame = (delegate* unmanaged[Cdecl]<CommandBuffer, StreamlineTaggedImage*, StreamlineTaggedImage*,
            StreamlineTaggedImage*, StreamlineTaggedImage*, StreamlineFrameCamera*, uint, uint, int>)Export("VulkanStorySlTagFrame");
    }

    /// <summary>Loads the private Streamline bridge and initializes selected PCL, Reflex and DLSS-G plugins.</summary>
    /// <param name="preferredDeviceIndex">Explicit Vulkan device index, or a negative value for automatic selection.</param>
    /// <param name="runtime">New session owner on success.</param>
    /// <param name="reason">Load or initialization detail.</param>
    /// <param name="preferredVendorId">Optional Vulkan vendor pin used to avoid NVIDIA feature injection on another adapter.</param>
    /// <returns>Whether initialization succeeded.</returns>
    internal static bool TryCreate(int preferredDeviceIndex,
        out StreamlineRuntime? runtime, out string reason, uint? preferredVendorId = null)
    {
        runtime = null;
        string directory = NativeRuntimePaths.DirectoryContaining(
            "VulkanStoryStreamline.dll", "sl.interposer.dll");
        string bridge = Path.Combine(directory, "VulkanStoryStreamline.dll");
        if (!File.Exists(bridge) || !File.Exists(Path.Combine(directory, "sl.interposer.dll")))
        {
            reason = "Streamline bridge or runtime missing";
            return false;
        }
        if (!NativeRuntimePaths.TryLoadLibrary(bridge, out nint library))
        {
            reason = "Streamline bridge could not load";
            return false;
        }
        try
        {
            var instance = new StreamlineRuntime(library);
            var initialize = (delegate* unmanaged[Cdecl]<char*, byte*, uint, uint, int>)
                NativeLibrary.GetExport(library, "VulkanStorySlInitialize");
            // A different NVIDIA adapter in a hybrid system must not enable
            // NVIDIA plugin extension injection on an explicitly selected Intel/AMD device.
            bool nvidia = HasNvidiaDisplayAdapter() &&
                (preferredVendorId == null || preferredVendorId == 0x10DE);
            bool loadReflex = ShouldLoadReflex(preferredDeviceIndex, nvidia,
                Environment.GetEnvironmentVariable("VULKANSTORY_STREAMLINE_REFLEX"));
            bool loadDlssG = loadReflex && ShouldLoadDlssG(preferredDeviceIndex, nvidia,
                Environment.GetEnvironmentVariable("VULKANSTORY_STREAMLINE_DLSS_G"));
            Console.Error.WriteLine("[VulkanStory] Streamline features: PCL" +
                (loadReflex ? " + Reflex" : "") + (loadDlssG ? " + DLSS-G" : ""));
            byte[] project = Encoding.UTF8.GetBytes(NgxSession.ProjectId + "\0");
            fixed (char* directoryPtr = directory)
            fixed (byte* projectPtr = project)
            {
                int result = initialize(directoryPtr, projectPtr,
                    loadReflex ? 1u : 0u, loadDlssG ? 1u : 0u);
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

    /// <summary>Resolves Vulkan commands through the Streamline proxy with instance fallback.</summary>
    /// <param name="instance">Active instance, or default for global commands.</param>
    /// <param name="device">Active device, or default before device creation.</param>
    /// <param name="name">Vulkan command name encoded as a terminated UTF-8 string.</param>
    /// <returns>Native command address, or zero when unresolved.</returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    internal nint GetVulkanProcAddress(Instance instance, Device device, string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* pointer = bytes)
        {
            // Global commands remain null-instance lookups even after Silk
            // switches its table to the active instance/device.
            if (name is "vkCreateInstance" or "vkEnumerateInstanceVersion" or
                "vkEnumerateInstanceExtensionProperties" or "vkEnumerateInstanceLayerProperties")
                return _instanceProc(default, pointer);
            nint address = device.Handle != 0 ? _deviceProc(device, pointer) : 0;
            return address != 0 ? address : _instanceProc(instance, pointer);
        }
    }
    /// <summary>Creates a Vulkan instance through the Streamline proxy so its required extensions can be applied.</summary>
    internal Result CreateInstance(InstanceCreateInfo* info, out Instance instance)
    {
        instance = default;
        fixed (Instance* pointer = &instance) return _createInstance(info, pointer);
    }
    /// <summary>Creates the selected Vulkan logical device through Streamline.</summary>
    internal Result CreateDevice(Instance instance, PhysicalDevice physical, DeviceCreateInfo* info, out Device device)
    {
        device = default;
        fixed (Device* pointer = &device) return _createDevice(instance, physical, info, pointer);
    }
    /// <summary>Enumerates physical devices through the proxy using Vulkan count/query semantics.</summary>
    internal Result EnumeratePhysicalDevices(Instance instance, ref uint count, PhysicalDevice* devices)
    {
        fixed (uint* pointer = &count) return _enumeratePhysicalDevices(instance, pointer, devices);
    }
    /// <summary>Creates a swapchain through the Streamline presentation proxy.</summary>
    internal Result CreateSwapchain(Device device, SwapchainCreateInfoKHR* info, out SwapchainKHR chain)
    {
        chain = default;
        fixed (SwapchainKHR* pointer = &chain) return _createSwapchain(device, info, pointer);
    }
    /// <summary>Destroys a proxy-owned swapchain after its rendering and presentation users are drained.</summary>
    internal void DestroySwapchain(Device device, SwapchainKHR chain) => _destroySwapchain(device, chain);
    /// <summary>Enumerates proxy swapchain images using Vulkan count/query semantics.</summary>
    internal Result GetImages(Device device, SwapchainKHR chain, ref uint count, Image* images)
    {
        fixed (uint* pointer = &count) return _getImages(device, chain, pointer, images);
    }
    /// <summary>Acquires a proxy swapchain image with an infinite timeout and the supplied binary semaphore.</summary>
    internal Result Acquire(Device device, SwapchainKHR chain, Semaphore semaphore, ref uint index)
    {
        fixed (uint* pointer = &index) return _acquire(device, chain, ulong.MaxValue, semaphore, default, pointer);
    }
    /// <summary>Submits presentation through the Streamline proxy with the caller-owned Vulkan present information.</summary>
    internal Result Present(Device device, Queue queue, PresentInfoKHR* info) => _present(device, queue, info);
    /// <summary>Creates a Win32 surface through the Streamline proxy.</summary>
    internal Result CreateSurface(Instance instance, Win32SurfaceCreateInfoKHR* info, out SurfaceKHR surface)
    {
        surface = default;
        fixed (SurfaceKHR* pointer = &surface) return _createSurface(instance, info, pointer);
    }
    /// <summary>Destroys a proxy surface after its swapchains are released.</summary>
    internal void DestroySurface(Instance instance, SurfaceKHR surface) => _destroySurface(instance, surface);
    /// <summary>Waits for device idle through the Streamline proxy and returns the Vulkan result.</summary>
    internal Result DeviceWaitIdle(Device device) => _deviceWaitIdle(device);
    /// <summary>Binds Reflex functions for the created device and returns the bridge status.</summary>
    internal int BindReflex() => _bindReflex();
    /// <summary>Binds DLSS-G functions for the created device and returns the bridge status.</summary>
    internal int BindFrameGeneration() => _bindFrameGeneration();
    /// <summary>Binds PCL functions when exported; returns -1 when the bridge lacks that optional export.</summary>
    internal int BindPcl() => _bindPcl == null ? -1 : _bindPcl();
    /// <summary>Queries independent Reflex low-latency and latency-report availability flags.</summary>
    internal int GetReflexState(out bool lowLatencyAvailable, out bool latencyReportAvailable)
    {
        uint available = 0, reports = 0;
        int result = _getReflexState(&available, &reports);
        lowLatencyAvailable = available != 0;
        latencyReportAvailable = reports != 0;
        return result;
    }
    /// <summary>Returns the PCL latency-ping window message, or zero when unavailable.</summary>
    internal uint PclWindowMessage() => _pclWindowMessage == null ? 0 : _pclWindowMessage();
    /// <summary>Queries DLSS-G support for the selected Vulkan physical device.</summary>
    internal int IsFrameGenerationSupported(PhysicalDevice physical) => _isFrameGenerationSupported(physical);
    /// <summary>Applies the native Reflex mode and a nonnegative maximum FPS.</summary>
    internal int SetReflex(int mode, int maxFps) => _setReflex(mode, (uint)Math.Max(maxFps, 0));
    /// <summary>Obtains Streamline frame identity using the low 32 bits of the renderer frame ID.</summary>
    internal int BeginFrame(ulong id) => _beginFrame((uint)id);
    /// <summary>Returns the bridge-held token for the frame begun most recently.</summary>
    internal nint CurrentFrameToken() => _currentFrameToken();
    /// <summary>Invokes Reflex sleep for the current frame token before input collection.</summary>
    internal int ReflexSleep() => _reflexSleep();
    /// <summary>Emits a latency marker for the bridge-held current frame token.</summary>
    internal int Marker(uint marker) => _marker(marker);
    /// <summary>Emits a latency marker for an explicitly retained token, including asynchronous present work.</summary>
    internal int MarkerForToken(nint token, uint marker) => _markerForToken(token, marker);
    /// <summary>Applies DLSS-G enablement, generated-frame count and presentation-resource dimensions.</summary>
    internal int SetFrameGeneration(bool enabled, uint generatedFrames, uint width,
        uint height, Format colorFormat, uint buffers) =>
        _setFg(enabled ? 1 : 0, generatedFrames, width, height, (uint)colorFormat, buffers);
    /// <summary>Queries provider status, presented count and maximum generated-frame count.</summary>
    internal int GetFrameGenerationState(out uint status, out uint presented,
        out uint maxGenerated)
    {
        status = 0; presented = 0; maxGenerated = 0;
        fixed (uint* statusPtr = &status)
        fixed (uint* presentedPtr = &presented)
        fixed (uint* maxPtr = &maxGenerated)
            return _getFgState(statusPtr, presentedPtr, maxPtr);
    }
    /// <summary>Consumes the bridge-recorded asynchronous presentation error.</summary>
    internal int TakePresentError() => _takePresentError();
    /// <summary>Reads all six frame-generation capability/state words from the bridge.</summary>
    internal int GetFrameGenerationStateDetails(out StreamlineFrameGenerationState state)
    {
        state = default;
        fixed (StreamlineFrameGenerationState* pointer = &state)
            return _getFgStateDetails((uint*)pointer, 6);
    }
    /// <summary>Clears scene inputs before a lifecycle transition; the SDK resolves full backbuffer dimensions at presentation.</summary>
    /// <param name="width">Presentation width retained by the native ABI; no backbuffer subregion is tagged.</param>
    /// <param name="height">Presentation height retained by the native ABI; no backbuffer subregion is tagged.</param>
    /// <returns>The native resource-tagging result.</returns>
    internal int InvalidateFrameTags(uint width, uint height) => _invalidateFrameTags(width, height);
    /// <summary>Requests DLSS-G resource release after its rendering and presentation work is drained.</summary>
    internal int FreeFrameGenerationResources() => _freeFrameGenerationResources();

    /// <summary>Accepts a completed disable or drains/frees DLSS-G after its documented VRAM warning.</summary>
    internal static void CheckDisableResult(VulkanContext context, int result)
    {
        if (result == 0) return;
        const int outOfVramWarning = 39;
        if (result != outOfVramWarning)
            throw new InvalidOperationException("Disabling DLSS-G failed (" + result + ").");
        VulkanResult.Check(context.WaitDeviceIdle(), "draining DLSS-G after VRAM warning");
        int release = context.Streamline!.FreeFrameGenerationResources();
        if (release != 0)
            throw new InvalidOperationException("Releasing DLSS-G after VRAM warning failed (" + release + ").");
    }
    /// <summary>Disables DLSS-G through the bridge before releasing provider-owned resources.</summary>
    internal int DisableFrameGenerationForRelease() => _disableFrameGenerationForRelease();
    /// <summary>Tags matching borrowed depth, motion, HUD-free scene, UI and camera constants on the current command buffer.</summary>
    internal int TagFrame(CommandBuffer commands, StreamlineTaggedImage* depth,
        StreamlineTaggedImage* motion, StreamlineTaggedImage* hudless, StreamlineTaggedImage* ui,
        StreamlineFrameCamera* camera, uint width, uint height) =>
        _tagFrame(commands, depth, motion, hudless, ui, camera, width, height);
    /// <summary>Shuts down the native Streamline session once; module unloading belongs to disposal.</summary>
    internal void Shutdown()
    {
        if (_shutdownComplete) return;
        _shutdown();
        _shutdownComplete = true;
    }
    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
        _unload();
        NativeLibrary.Free(_library);
    }
}
