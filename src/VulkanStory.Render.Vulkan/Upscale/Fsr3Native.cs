using System;
using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace VulkanStory.Render.Vulkan.Core;

// AMD FidelityFX SDK 1.1.4 signed Vulkan DLL, wrapped by VulkanStoryFsr3.dll.
// Both modules stay loaded because context destruction is deferred on the GPU timeline.
internal sealed unsafe class Fsr3Native
{
    private static readonly object loadGate = new();
    private static Fsr3Native? loaded;
    private readonly nint sdk;
    private readonly nint bridge;
    public readonly delegate* unmanaged[Cdecl]<nint, int> Open;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, uint, uint*, uint*, int> Plan;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint, uint, nint*, int> Create;
    public readonly delegate* unmanaged[Cdecl]<nint, Fsr3Frame*, int> Evaluate;
    public readonly delegate* unmanaged[Cdecl]<nint, int> Destroy;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint, uint, uint, nint*, int> CreateFrameGeneration;
    public readonly delegate* unmanaged[Cdecl]<nint, Fsr3FgFrame*, int> EvaluateFrameGeneration;
    public readonly delegate* unmanaged[Cdecl]<nint, Fsr3FgFrame*, int> EvaluateDirectFrameGeneration;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, int> DisableFrameGeneration;
    public readonly delegate* unmanaged[Cdecl]<nint, int> DestroyFrameGeneration;
    public readonly delegate* unmanaged[Cdecl]<PhysicalDevice, Device, Queue, Queue, Queue, Queue, uint,
        SwapchainCreateInfoKHR*, nint*, int> CreateSwapchain;
    public readonly delegate* unmanaged[Cdecl]<nint, SwapchainKHR> SwapchainHandle;
    public readonly delegate* unmanaged[Cdecl]<nint, SwapchainCreateInfoKHR*, SwapchainKHR*, Result> RecreateSwapchain;
    public readonly delegate* unmanaged[Cdecl]<nint, SwapchainKHR, uint*, Image*, Result> GetSwapchainImages;
    public readonly delegate* unmanaged[Cdecl]<nint, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result> AcquireSwapchain;
    public readonly delegate* unmanaged[Cdecl]<nint, Queue, PresentInfoKHR*, Result> PresentSwapchain;
    public readonly delegate* unmanaged[Cdecl]<nint, SwapchainKHR, int> PrepareSwapchainDestroy;
    public readonly delegate* unmanaged[Cdecl]<nint, SwapchainKHR, int> DestroySwapchainChain;
    public readonly delegate* unmanaged[Cdecl]<nint, SwapchainKHR, ulong> SwapchainLastPresentCount;
    public readonly delegate* unmanaged[Cdecl]<nint, int> DestroySwapchain;

    private Fsr3Native(nint sdk, nint bridge)
    {
        this.sdk = sdk;
        this.bridge = bridge;
        Open = (delegate* unmanaged[Cdecl]<nint, int>)Export("VulkanStoryFsr3Open");
        Plan = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint*, uint*, int>)Export("VulkanStoryFsr3Plan");
        Create = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint, nint*, int>)Export("VulkanStoryFsr3Create");
        Evaluate = (delegate* unmanaged[Cdecl]<nint, Fsr3Frame*, int>)Export("VulkanStoryFsr3Evaluate");
        Destroy = (delegate* unmanaged[Cdecl]<nint, int>)Export("VulkanStoryFsr3Destroy");
        CreateFrameGeneration = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint, uint, nint*, int>)Export("VulkanStoryFsr3FgCreate");
        EvaluateFrameGeneration = (delegate* unmanaged[Cdecl]<nint, Fsr3FgFrame*, int>)Export("VulkanStoryFsr3FgEvaluate");
        EvaluateDirectFrameGeneration = (delegate* unmanaged[Cdecl]<nint, Fsr3FgFrame*, int>)Export("VulkanStoryFsr3FgEvaluateDirect");
        DisableFrameGeneration = (delegate* unmanaged[Cdecl]<nint, nint, int>)Export("VulkanStoryFsr3FgDisable");
        DestroyFrameGeneration = (delegate* unmanaged[Cdecl]<nint, int>)Export("VulkanStoryFsr3FgDestroy");
        CreateSwapchain = (delegate* unmanaged[Cdecl]<PhysicalDevice, Device, Queue, Queue, Queue, Queue, uint,
            SwapchainCreateInfoKHR*, nint*, int>)Export("VulkanStoryFsr3SwapchainCreate");
        SwapchainHandle = (delegate* unmanaged[Cdecl]<nint, SwapchainKHR>)Export("VulkanStoryFsr3SwapchainHandle");
        RecreateSwapchain = (delegate* unmanaged[Cdecl]<nint, SwapchainCreateInfoKHR*, SwapchainKHR*, Result>)Export("VulkanStoryFsr3SwapchainRecreate");
        GetSwapchainImages = (delegate* unmanaged[Cdecl]<nint, SwapchainKHR, uint*, Image*, Result>)Export("VulkanStoryFsr3SwapchainGetImages");
        AcquireSwapchain = (delegate* unmanaged[Cdecl]<nint, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result>)Export("VulkanStoryFsr3SwapchainAcquire");
        PresentSwapchain = (delegate* unmanaged[Cdecl]<nint, Queue, PresentInfoKHR*, Result>)Export("VulkanStoryFsr3SwapchainPresent");
        // Required symbols prevent pairing checked teardown with an old bridge.
        PrepareSwapchainDestroy = (delegate* unmanaged[Cdecl]<nint, SwapchainKHR, int>)Export("VulkanStoryFsr3SwapchainPrepareDestroy");
        DestroySwapchainChain = (delegate* unmanaged[Cdecl]<nint, SwapchainKHR, int>)Export("VulkanStoryFsr3SwapchainDestroyChainChecked");
        SwapchainLastPresentCount = (delegate* unmanaged[Cdecl]<nint, SwapchainKHR, ulong>)Export("VulkanStoryFsr3SwapchainLastPresentCount");
        DestroySwapchain = (delegate* unmanaged[Cdecl]<nint, int>)Export("VulkanStoryFsr3SwapchainDestroy");
    }
    private nint Export(string symbol) => NativeLibrary.GetExport(bridge, symbol);

    public static Fsr3Native? TryLoad(out string? error)
    {
        lock (loadGate)
        {
            error = null;
            if (loaded != null) return loaded;
            if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            { error = "the FSR 3.1 Vulkan runtime is available on Windows x64 in this build"; return null; }
            string directory = NativeRuntimePaths.DirectoryContaining(
                "amd_fidelityfx_vk.dll", "VulkanStoryFsr3.dll");
            string sdkPath = Environment.GetEnvironmentVariable("VULKANSTORY_FSR3_LIBRARY") ??
                Path.Combine(directory, "amd_fidelityfx_vk.dll");
            string bridgePath = Environment.GetEnvironmentVariable("VULKANSTORY_FSR3_BRIDGE") ??
                Path.Combine(directory, "VulkanStoryFsr3.dll");
            nint sdk = 0, bridge = 0;
            try
            {
                sdk = NativeRuntimePaths.LoadLibrary(Path.GetFullPath(sdkPath));
                bridge = NativeRuntimePaths.LoadLibrary(Path.GetFullPath(bridgePath));
                var candidate = new Fsr3Native(sdk, bridge);
                if (candidate.Open(sdk) != 0) throw new EntryPointNotFoundException("FSR 3.1 Vulkan exports are missing");
                return loaded = candidate;
            }
            catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                if (bridge != 0) NativeLibrary.Free(bridge);
                if (sdk != 0) NativeLibrary.Free(sdk);
                error = "FSR 3.1 Vulkan runtime unavailable: " + exception.Message;
                return null;
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal partial struct Fsr3Image
{
    public ulong Image;
    public uint Width, Height, Format;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct Fsr3Frame
{
    public nint Commands;
    public Fsr3Image Color, Depth, Motion, Output;
    public float JitterX, JitterY, DeltaMs, NearPlane, FarPlane, FovRadians;
    public uint Reset;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct Fsr3FgFrame
{
    public nint Commands;
    public Fsr3Image Color, Depth, Motion, Output, Hudless;
    public float JitterX, JitterY, DeltaMs, NearPlane, FarPlane, FovRadians;
    public ulong FrameId;
    public uint Reset;
    public float CameraPosX, CameraPosY, CameraPosZ;
    public float CameraUpX, CameraUpY, CameraUpZ;
    public float CameraRightX, CameraRightY, CameraRightZ;
    public float CameraForwardX, CameraForwardY, CameraForwardZ;
    public Fsr3Image Ui;
    public nint SwapchainContext;
}
