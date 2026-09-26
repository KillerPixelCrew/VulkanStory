using System;
using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XessPresentationFrame
{
    public uint FrameId;
    public uint Reset;
    public uint Vsync;
    public uint Reserved;
    public nint Color;
    public nint Depth;
    public nint Motion;
    public nint Hudless;
    public nint Ui;
    public fixed float ViewMatrix[16];
    public fixed float ProjectionMatrix[16];
    public float JitterX, JitterY;
    public float MotionScaleX, MotionScaleY;
    public ulong ReadyFenceValue;
    public ulong DoneFenceValue;
}

/// <summary>
/// Owns the matching DX12 device, XeLL context, and Intel XeSS-FG context.
/// The DXGI proxy is initialized only after Vulkan relinquishes the window.
/// </summary>
internal sealed unsafe class XessFgRuntime : IDisposable
{
    private nint _module;
    private nint _context;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _destroy;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, uint, uint, uint, int> _start;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, int> _setEnabled;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, uint, int> _setLatencyMode;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, int> _sleep;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, int, int> _marker;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, uint, uint, nint*, nint*, int> _createSharedImage;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _releaseImage;
    private readonly delegate* unmanaged[Cdecl]<nint, nint*, int> _createSharedFence;
    private readonly delegate* unmanaged[Cdecl]<nint, ulong, int> _waitSharedFence;
    private readonly delegate* unmanaged[Cdecl]<nint, ulong, int> _signalSharedFence;
    private readonly delegate* unmanaged[Cdecl]<nint, XessPresentationFrame*, uint*, int*, uint*, int> _present;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _waitIdle;

    private XessFgRuntime(nint module, nint context)
    {
        _module = module;
        _context = context;
        _destroy = (delegate* unmanaged[Cdecl]<nint, void>)Export("OptimumXessFgDestroy");
        _start = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint, uint, int>)Export("OptimumXessFgStart");
        _setEnabled = (delegate* unmanaged[Cdecl]<nint, uint, int>)Export("OptimumXessFgSetEnabled");
        _setLatencyMode = (delegate* unmanaged[Cdecl]<nint, uint, uint, int>)Export("OptimumXessFgSetLatencyMode");
        _sleep = (delegate* unmanaged[Cdecl]<nint, uint, int>)Export("OptimumXessFgSleep");
        _marker = (delegate* unmanaged[Cdecl]<nint, uint, int, int>)Export("OptimumXessFgMarker");
        _createSharedImage = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, nint*, nint*, int>)Export("OptimumXessFgCreateSharedImage");
        _releaseImage = (delegate* unmanaged[Cdecl]<nint, void>)Export("OptimumXessFgReleaseImage");
        _createSharedFence = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Export("OptimumXessFgCreateSharedFence");
        _waitSharedFence = (delegate* unmanaged[Cdecl]<nint, ulong, int>)Export("OptimumXessFgWaitSharedFence");
        _signalSharedFence = (delegate* unmanaged[Cdecl]<nint, ulong, int>)Export("OptimumXessFgSignalSharedFence");
        _present = (delegate* unmanaged[Cdecl]<nint, XessPresentationFrame*, uint*, int*, uint*, int>)Export("OptimumXessFgPresent");
        _waitIdle = (delegate* unmanaged[Cdecl]<nint, int>)Export("OptimumXessFgWaitIdle");
    }

    private nint Export(string symbol) => NativeLibrary.GetExport(_module, symbol);

    public static bool TryCreate(VulkanContext vulkan, out XessFgRuntime? runtime,
        out string reason)
    {
        runtime = null;
        if (!OperatingSystem.IsWindows()) { reason = "XeSS-FG requires Windows DX12"; return false; }
        string path = Path.Combine(Path.GetDirectoryName(typeof(XessFgRuntime).Assembly.Location)!,
            "OptimumXessFg.dll");
        if (!File.Exists(path)) { reason = "XeSS-FG DX12 bridge is not installed"; return false; }
        nint module = 0;
        nint context = 0;
        try
        {
            module = NativeLibrary.Load(path);
            var create = (delegate* unmanaged[Cdecl]<PhysicalDevice, nint*, int>)
                NativeLibrary.GetExport(module, "OptimumXessFgCreateFromVulkan");
            int result = create(vulkan.PhysicalDevice, &context);
            if (result != 0 || context == 0)
            {
                reason = "XeSS-FG adapter, DX12, or XeLL initialization failed (" + result + ")";
                NativeLibrary.Free(module);
                return false;
            }
            runtime = new XessFgRuntime(module, context);
            reason = "ready";
            return true;
        }
        catch (Exception error)
        {
            if (context != 0 && module != 0)
            {
                var destroy = (delegate* unmanaged[Cdecl]<nint, void>)
                    NativeLibrary.GetExport(module, "OptimumXessFgDestroy");
                destroy(context);
            }
            if (module != 0) NativeLibrary.Free(module);
            reason = "XeSS-FG bridge unavailable: " + error.Message;
            return false;
        }
    }

    public int Start(nint window, uint width, uint height, bool vsync) =>
        _context != 0 ? _start(_context, window, width, height, vsync ? 1u : 0u) : -1;
    public int SetEnabled(bool enabled) => _context != 0 ? _setEnabled(_context, enabled ? 1u : 0u) : -1;
    public int SetLatencyMode(int maxFps, bool enabled) => _context != 0 ?
        _setLatencyMode(_context, maxFps > 0 ? (uint)Math.Max(1, 1_000_000 / maxFps) : 0,
            enabled ? 1u : 0u) : -1;
    public int Sleep(ulong frameId) => _context != 0 ? _sleep(_context, (uint)frameId) : -1;
    public int Marker(ulong frameId, LatencyMarker marker) => _context != 0 ?
        _marker(_context, (uint)frameId, (int)marker) : -1;
    public int CreateSharedImage(uint width, uint height, Format format,
        out nint sharedHandle, out nint resource)
    {
        nint handle = 0;
        nint created = 0;
        int code = _context != 0 ?
            _createSharedImage(_context, width, height, (uint)format, &handle, &created) : -1;
        sharedHandle = handle;
        resource = created;
        return code;
    }
    public void ReleaseImage(nint resource)
    {
        if (resource != 0) _releaseImage(resource);
    }
    public int CreateSharedFence(out nint sharedHandle)
    {
        nint handle = 0;
        int code = _context != 0 ? _createSharedFence(_context, &handle) : -1;
        sharedHandle = handle;
        return code;
    }
    public int WaitSharedFence(ulong value) => _context != 0 ? _waitSharedFence(_context, value) : -1;
    public int SignalSharedFence(ulong value) => _context != 0 ? _signalSharedFence(_context, value) : -1;
    public int WaitIdle() => _context != 0 ? _waitIdle(_context) : -1;
    public int Present(in XessPresentationFrame frame, out uint framesPresented,
        out int frameGenResult, out bool frameGenEnabled)
    {
        XessPresentationFrame copy = frame;
        uint presented = 0;
        int result = 0;
        uint enabled = 0;
        int code = _context != 0 ? _present(_context, &copy, &presented, &result, &enabled) : -1;
        framesPresented = presented;
        frameGenResult = result;
        frameGenEnabled = enabled != 0;
        return code;
    }

    public void Dispose()
    {
        nint context = _context;
        _context = 0;
        if (context != 0) _destroy(context);
        nint module = _module;
        _module = 0;
        if (module != 0) NativeLibrary.Free(module);
    }
}
