using System;
using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

// Intel XeSS SDK 3.0.2, inc/xess/{xess.h,xess_vk.h}, pack(8), Windows x64 ABI.
// Keep the module loaded for the process: deferred contexts can outlive a selected provider.
internal sealed unsafe class XessNative
{
    private static readonly object loadGate = new();
    private static XessNative? loaded;
    private readonly nint module;
    public readonly delegate* unmanaged[Cdecl]<uint*, byte***, uint*, int> InstanceExtensions;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint*, byte***, int> DeviceExtensions;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void**, int> DeviceFeatures;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, nint*, int> Create;
    public readonly delegate* unmanaged[Cdecl]<nint, XessInit*, int> Init;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, XessExecute*, int> Execute;
    public readonly delegate* unmanaged[Cdecl]<nint, int> Destroy;
    public readonly delegate* unmanaged[Cdecl]<nint, XessSize*, int, XessSize*, XessSize*, XessSize*, int> Resolution;

    private XessNative(nint module)
    {
        this.module = module;
        InstanceExtensions = (delegate* unmanaged[Cdecl]<uint*, byte***, uint*, int>)Export("xessVKGetRequiredInstanceExtensions");
        DeviceExtensions = (delegate* unmanaged[Cdecl]<nint, nint, uint*, byte***, int>)Export("xessVKGetRequiredDeviceExtensions");
        DeviceFeatures = (delegate* unmanaged[Cdecl]<nint, nint, void**, int>)Export("xessVKGetRequiredDeviceFeatures");
        Create = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint*, int>)Export("xessVKCreateContext");
        Init = (delegate* unmanaged[Cdecl]<nint, XessInit*, int>)Export("xessVKInit");
        Execute = (delegate* unmanaged[Cdecl]<nint, nint, XessExecute*, int>)Export("xessVKExecute");
        Destroy = (delegate* unmanaged[Cdecl]<nint, int>)Export("xessDestroyContext");
        Resolution = (delegate* unmanaged[Cdecl]<nint, XessSize*, int, XessSize*, XessSize*, XessSize*, int>)Export("xessGetOptimalInputResolution");
    }
    private nint Export(string name) => NativeLibrary.GetExport(module, name);
    public static XessNative? TryLoad(out string? error)
    {
        lock (loadGate)
        {
            error = null;
            if (loaded != null) return loaded;
            if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            { error = "XeSS-SR requires the Windows x64 runtime"; return null; }
            string path = Environment.GetEnvironmentVariable("OPTIMUM_XESS_LIBRARY") ??
                Path.Combine(Path.GetDirectoryName(typeof(XessNative).Assembly.Location)!, "libxess.dll");
            nint handle = 0;
            try { handle = NativeLibrary.Load(Path.GetFullPath(path)); return loaded = new XessNative(handle); }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            { if (handle != 0) NativeLibrary.Free(handle); error = "libxess.dll unavailable: " + e.Message; return null; }
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct XessSize { public uint Width, Height; public XessSize(int w, int h) { Width = (uint)w; Height = (uint)h; } }
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct XessInit
{
    public XessSize Output;
    public int Quality;
    public uint Flags, CreationNodeMask, VisibleNodeMask;
    public ulong BufferHeap, BufferOffset, TextureHeap, TextureOffset, PipelineCache;
}
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct XessImage
{
    public ulong View, Image;
    public ImageSubresourceRange Range;
    public Format Format;
    public uint Width, Height;
    public static XessImage From(VulkanTexture texture) => new()
    {
        View = texture.View.Handle,
        Image = texture.Image.Handle,
        Range = new ImageSubresourceRange(texture.Aspect, 0, 1, 0, 1),
        Format = texture.Format,
        Width = texture.Width,
        Height = texture.Height,
    };
}
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct XessExecute
{
    public XessImage Color, Velocity, Depth, Exposure, ResponsiveMask, Output;
    public float JitterX, JitterY, ExposureScale;
    public uint Reset, Width, Height;
    public XessSize ColorBase, MotionBase, DepthBase, MaskBase, Reserved, OutputBase;
}
