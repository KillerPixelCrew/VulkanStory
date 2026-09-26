using System;
using System.Runtime.InteropServices;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// XeLL 1.3's C ABI. A context is created on the D3D12 device owned by the
/// XeSS-FG presentation bridge; it cannot be created on a Vulkan device.
/// </summary>
internal sealed class XellNative : IDisposable
{
    private IntPtr _context;

    private XellNative(IntPtr context) => _context = context;

    [StructLayout(LayoutKind.Sequential)]
    private struct SleepParameters
    {
        public uint MinimumIntervalUs;
        // xell_sleep_params_t packs bLowLatencyMode and bLowLatencyBoost in a
        // second 32-bit word. XeLL 1.3 currently ignores boost.
        public uint Flags;
    }

    [DllImport("libxell.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int xellD3D12CreateContext(IntPtr device, out IntPtr context);

    [DllImport("libxell.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int xellDestroyContext(IntPtr context);

    [DllImport("libxell.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int xellSetSleepMode(IntPtr context, in SleepParameters parameters);

    [DllImport("libxell.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int xellSleep(IntPtr context, uint frameId);

    [DllImport("libxell.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int xellAddMarkerData(IntPtr context, uint frameId, int marker);

    public static bool TryCreate(IntPtr d3d12Device, out XellNative? xell)
    {
        xell = null;
        if (d3d12Device == IntPtr.Zero) return false;
        try
        {
            if (xellD3D12CreateContext(d3d12Device, out IntPtr context) != 0 || context == IntPtr.Zero)
                return false;
            xell = new XellNative(context);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public IntPtr Context => _context;

    public int SetFrameCap(int maxFps)
    {
        if (_context == IntPtr.Zero) return -8;
        var parameters = new SleepParameters
        {
            MinimumIntervalUs = maxFps > 0 ? (uint)Math.Max(1, 1_000_000 / maxFps) : 0,
            Flags = 1, // bLowLatencyMode
        };
        return xellSetSleepMode(_context, in parameters);
    }

    public int Sleep(ulong frameId) => _context == IntPtr.Zero ? -8 : xellSleep(_context, (uint)frameId);

    public int Marker(ulong frameId, LatencyMarker marker) =>
        _context == IntPtr.Zero ? -8 : xellAddMarkerData(_context, (uint)frameId, (int)marker);

    public void Dispose()
    {
        IntPtr context = _context;
        _context = IntPtr.Zero;
        if (context != IntPtr.Zero) xellDestroyContext(context);
    }
}
