using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Optimum.Render.Vulkan.Core;

// AMD FidelityFX SDK 1.1.4 signed Vulkan DLL, wrapped by OptimumFsr3.dll.
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

    private Fsr3Native(nint sdk, nint bridge)
    {
        this.sdk = sdk;
        this.bridge = bridge;
        Open = (delegate* unmanaged[Cdecl]<nint, int>)Export("OptimumFsr3Open");
        Plan = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint*, uint*, int>)Export("OptimumFsr3Plan");
        Create = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint, nint*, int>)Export("OptimumFsr3Create");
        Evaluate = (delegate* unmanaged[Cdecl]<nint, Fsr3Frame*, int>)Export("OptimumFsr3Evaluate");
        Destroy = (delegate* unmanaged[Cdecl]<nint, int>)Export("OptimumFsr3Destroy");
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
            string directory = Path.GetDirectoryName(typeof(Fsr3Native).Assembly.Location)!;
            string sdkPath = Environment.GetEnvironmentVariable("OPTIMUM_FSR3_LIBRARY") ??
                Path.Combine(directory, "amd_fidelityfx_vk.dll");
            string bridgePath = Environment.GetEnvironmentVariable("OPTIMUM_FSR3_BRIDGE") ??
                Path.Combine(directory, "OptimumFsr3.dll");
            nint sdk = 0, bridge = 0;
            try
            {
                sdk = NativeLibrary.Load(Path.GetFullPath(sdkPath));
                bridge = NativeLibrary.Load(bridgePath);
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
internal struct Fsr3Image
{
    public ulong Image;
    public uint Width, Height, Format;
    public static Fsr3Image From(VulkanTexture texture) => new()
    {
        Image = texture.Image.Handle,
        Width = texture.Width,
        Height = texture.Height,
        Format = (uint)texture.Format,
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct Fsr3Frame
{
    public nint Commands;
    public Fsr3Image Color, Depth, Motion, Output;
    public float JitterX, JitterY, DeltaMs, NearPlane, FarPlane, FovRadians;
    public uint Reset;
}
