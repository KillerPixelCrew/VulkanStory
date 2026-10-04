using System;
using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;

namespace VulkanStory.Render.Vulkan.Core;

[StructLayout(LayoutKind.Sequential)]
internal struct Fsr4Frame
{
    public nint Color, Depth, Motion, Output;
    public uint RenderWidth, RenderHeight, DisplayWidth, DisplayHeight;
    public float JitterX, JitterY, DeltaMs, NearPlane, FarPlane, Fov;
    public uint Reset;
    public ulong ReadyValue, DoneValue;
}

/// <summary>The signed FidelityFX DX12 provider and the matching Vulkan adapter.</summary>
internal sealed unsafe class Fsr4Runtime : IDisposable, IDx12SharedRuntime
{
    private nint module;
    private nint context;
    private readonly delegate* unmanaged[Cdecl]<nint, int> destroy;
    private readonly delegate* unmanaged[Cdecl]<nint, int> prepareDestroy;
    private Exception? releaseFailure;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, nint*, nint*, int> createImage;
    private readonly delegate* unmanaged[Cdecl]<nint, void> releaseImage;
    private readonly delegate* unmanaged[Cdecl]<nint, nint*, int> createFence;
    private readonly delegate* unmanaged[Cdecl]<nint, Fsr4Frame*, int> evaluate;
    private readonly delegate* unmanaged[Cdecl]<nint, int> waitIdle;

    private Fsr4Runtime(nint module, nint context)
    {
        this.module = module;
        this.context = context;
        destroy = (delegate* unmanaged[Cdecl]<nint, int>)Export("VulkanStoryFsr4DestroyChecked");
        prepareDestroy = (delegate* unmanaged[Cdecl]<nint, int>)Export("VulkanStoryFsr4PrepareDestroy");
        createImage = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, nint*, nint*, int>)Export("VulkanStoryFsr4CreateSharedImage");
        releaseImage = (delegate* unmanaged[Cdecl]<nint, void>)Export("VulkanStoryFsr4ReleaseImage");
        createFence = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Export("VulkanStoryFsr4CreateSharedFence");
        evaluate = (delegate* unmanaged[Cdecl]<nint, Fsr4Frame*, int>)Export("VulkanStoryFsr4Evaluate");
        waitIdle = (delegate* unmanaged[Cdecl]<nint, int>)Export("VulkanStoryFsr4WaitIdle");
    }

    private nint Export(string name) => NativeLibrary.GetExport(module, name);

    public static bool TryLoad(out nint bridge, out string reason)
    {
        bridge = 0;
        if (!OperatingSystem.IsWindows())
        {
            reason = "FSR 4 requires Windows DX12";
            return false;
        }
        string directory = NativeRuntimePaths.DirectoryContaining(
            "amd_fidelityfx_upscaler_dx12.dll", "VulkanStoryFsr4.dll");
        if (!File.Exists(Path.Combine(directory, "amd_fidelityfx_upscaler_dx12.dll")) ||
            !File.Exists(Path.Combine(directory, "VulkanStoryFsr4.dll")))
        {
            reason = "FSR 4 signed DX12 runtime or bridge is missing";
            return false;
        }
        try
        {
            bridge = NativeRuntimePaths.LoadLibrary(Path.Combine(directory, "VulkanStoryFsr4.dll"));
            _ = NativeLibrary.GetExport(bridge, "VulkanStoryFsr4DestroyChecked");
            _ = NativeLibrary.GetExport(bridge, "VulkanStoryFsr4PrepareDestroy");
            reason = "ready";
            return true;
        }
        catch (Exception error)
        {
            if (bridge != 0) { NativeLibrary.Free(bridge); bridge = 0; }
            reason = "FSR 4 bridge failed to load: " + error.Message;
            return false;
        }
    }

    public static int Probe(nint bridge, nint physical)
    {
        var probe = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(
            bridge, "VulkanStoryFsr4Probe");
        return probe(physical);
    }

    public static bool TryCreate(nint bridge, nint physical, in UpscalerPlan plan,
        out Fsr4Runtime? runtime, out string reason)
    {
        runtime = null;
        nint context = 0;
        Fsr4Runtime? instance = null;
        try
        {
            instance = new Fsr4Runtime(bridge, 0);
            var create = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, nint*, int>)
                NativeLibrary.GetExport(bridge, "VulkanStoryFsr4Create");
            int code = create(physical, (uint)plan.RenderWidth, (uint)plan.RenderHeight,
                (uint)plan.DisplayWidth, (uint)plan.DisplayHeight, &context);
            instance.context = context;
            if (code != 0 || context == 0)
            {
                reason = "FSR 4 DX12 context creation failed (" + code.ToString("X8") + ")";
                instance.Dispose();
                return false;
            }
            runtime = instance;
            reason = "ready";
            return true;
        }
        catch (Exception error)
        {
            if (instance != null)
            {
                try { instance.Dispose(); }
                catch (Exception cleanup)
                {
                    runtime = instance;
                    reason = "FSR 4 initialization cleanup failed; owner retained: " + cleanup.Message;
                    return false;
                }
            }
            reason = "FSR 4 DX12 context failed: " + error.Message;
            return false;
        }
    }

    public int CreateSharedImage(uint width, uint height, Format format, bool writable,
        out nint sharedHandle, out nint resource)
    {
        nint handle = 0, image = 0;
        int code = context != 0 ? createImage(context, width, height, (uint)format,
            writable ? 1u : 0u, &handle, &image) : -1;
        sharedHandle = handle;
        resource = image;
        return code;
    }
    public void ReleaseImage(nint resource)
    {
        if (resource != 0) releaseImage(resource);
    }
    public int CreateSharedFence(out nint sharedHandle)
    {
        nint handle = 0;
        int code = context != 0 ? createFence(context, &handle) : -1;
        sharedHandle = handle;
        return code;
    }
    public int Evaluate(in Fsr4Frame frame)
    {
        Fsr4Frame copy = frame;
        return context != 0 ? evaluate(context, &copy) : -1;
    }
    public int WaitIdle() => context != 0 ? waitIdle(context) : -1;
    internal void PrepareRelease()
    {
        if (releaseFailure != null)
            throw new InvalidOperationException("FSR 4 native release failed; remaining owners retained.", releaseFailure);
        if (context == 0) return;
        try
        {
            int result = prepareDestroy(context);
            if (result != 0) throw new InvalidOperationException("FSR 4 SDK release preparation failed (" + result + ").");
        }
        catch (Exception failure) { releaseFailure = failure; throw; }
    }
    public void Dispose()
    {
        PrepareRelease();
        if (context != 0)
        {
            int result = destroy(context);
            if (result != 0)
            {
                releaseFailure = new InvalidOperationException("FSR 4 native destruction failed (" + result + ").");
                throw releaseFailure;
            }
            context = 0;
        }
        // The bridge module stays loaded until backend shutdown, after every
        // imported Vulkan resource and DX12 context has been destroyed.
    }
}
