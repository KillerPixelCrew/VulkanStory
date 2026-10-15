using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Window = OpenTK.Windowing.GraphicsLibraryFramework.Window;
using Monitor = OpenTK.Windowing.GraphicsLibraryFramework.Monitor;

namespace VulkanStory.Game;

internal static unsafe partial class ManagedWindowBridge
{
    private static void PrepareMonitorBindings(List<(MethodBase Method, MethodInfo Prefix)> bindings)
    {
        Add(typeof(GLFW).GetMethod(nameof(GLFW.GetPrimaryMonitor), Type.EmptyTypes)!, nameof(GetPrimaryMonitorPointer));
        Add(AccessTools.PropertyGetter(typeof(Monitors), "Count")!, nameof(MonitorCount));
        Add(AccessTools.Method(typeof(Monitors), nameof(Monitors.GetMonitors), Type.EmptyTypes)!, nameof(GetMonitors));
        Add(AccessTools.Method(typeof(Monitors), nameof(Monitors.GetPrimaryMonitor), Type.EmptyTypes)!, nameof(GetPrimaryMonitor));
        Add(AccessTools.Method(typeof(Monitors), nameof(Monitors.GetMonitorFromWindow), [typeof(NativeWindow)])!, nameof(GetWindowMonitor));
        Add(AccessTools.Method(typeof(Monitors), nameof(Monitors.GetMonitorFromWindow), [typeof(Window).MakePointerType()])!, nameof(GetPointerMonitor));
        foreach (MethodInfo method in typeof(Monitors).GetMethods(BindingFlags.Static | BindingFlags.Public).Where(value => value.Name == nameof(Monitors.TryGetMonitorInfo)))
            Add(method, nameof(TryMonitor));
        void Add(MethodBase method, string name) => bindings.Add((method, typeof(ManagedWindowBridge).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!));
    }

    private static bool MonitorCount(ref int __result)
    { if (!ManagedWindowRouting) return true; __result = DisplayIds().Length; return false; }
    private static bool GetMonitors(ref List<MonitorInfo> __result)
    { if (!ManagedWindowRouting) return true; __result = DisplayIds().Select(ReadMonitor).ToList(); return false; }
    private static bool GetPrimaryMonitor(ref MonitorInfo __result)
    {
        if (!ManagedWindowRouting) return true;
        uint id = SDL_GetPrimaryDisplay();
        if (id == 0) throw new InvalidOperationException("SDL has no primary display.");
        __result = ReadMonitor(id); return false;
    }
    private static bool GetPrimaryMonitorPointer(ref Monitor* __result)
    { if (!ManagedWindowRouting) return true; __result = (Monitor*)(nint)SDL_GetPrimaryDisplay(); return false; }
    private static bool GetWindowMonitor(NativeWindow __0, ref MonitorInfo __result)
    { if (!TryWindowEntry(__0, out Entry? entry)) return true; entry.RequireOwner(); __result = MonitorFor(entry); return false; }
    private static bool GetPointerMonitor(Window* __0, ref MonitorInfo __result)
    { if (!ManagedWindowRouting) return true; Entry entry = RequireTokenEntry((nint)__0); entry.RequireOwner(); __result = MonitorFor(entry); return false; }
    private static bool TryMonitor(object[] __args, ref bool __result)
    {
        if (!ManagedWindowRouting) return true;
        uint id;
        if (__args[0] is int index)
        {
            uint[] ids = DisplayIds();
            if (index < 0 || index >= ids.Length) { __args[1] = null!; __result = false; return false; }
            id = ids[index];
        }
        else id = checked((uint)((MonitorHandle)__args[0]).Pointer);
        if (!DisplayIds().Contains(id)) { __args[1] = null!; __result = false; return false; }
        __args[1] = ReadMonitor(id); __result = true; return false;
    }
    private static bool MonitorScale(NativeWindow __instance, MethodBase __originalMethod, ref float __0, ref float __1, ref bool __result)
    {
        if (!TryWindowEntry(__instance, out Entry? entry)) return true;
        entry.RequireOwner();
        // SDL3 exposes content scale, not physical display millimeters/raw DPI.
        // The optional raw-DPI query explicitly declines rather than returning fabricated data.
        if (__originalMethod.Name == "TryGetCurrentMonitorDpiRaw") { __0 = __1 = 0; __result = false; return false; }
        float scale = SDL_GetDisplayContentScale(entry.Host.DisplayId);
        __result = float.IsFinite(scale) && scale > 0;
        __0 = __1 = __result ? scale * (__originalMethod.Name.EndsWith("Dpi", StringComparison.Ordinal) ? 96 : 1) : 0;
        return false;
    }
    private static MonitorInfo MonitorFor(Entry entry) => ReadMonitor(entry.Host.DisplayId);
    private static uint[] DisplayIds()
    {
        nint pointer = SDL_GetDisplays(out int count);
        if (pointer == 0) throw new InvalidOperationException("SDL_GetDisplays failed.");
        try
        {
            var result = new uint[count];
            for (int index = 0; index < count; index++) result[index] = unchecked((uint)Marshal.ReadInt32(pointer, index * sizeof(uint)));
            return result;
        }
        finally { SDL_free(pointer); }
    }
    private static MonitorInfo ReadMonitor(uint id)
    {
        CheckSdl(SDL_GetDisplayBounds(id, out DisplayRectangle bounds), "SDL_GetDisplayBounds");
        CheckSdl(SDL_GetDisplayUsableBounds(id, out DisplayRectangle work), "SDL_GetDisplayUsableBounds");
        float scale = SDL_GetDisplayContentScale(id);
        if (!float.IsFinite(scale) || scale <= 0) throw new InvalidOperationException("SDL display content scale is unavailable.");
        nint modePointer = SDL_GetCurrentDisplayMode(id);
        if (modePointer == 0) throw new InvalidOperationException("SDL current display mode is unavailable.");
        DisplayMode currentMode = Marshal.PtrToStructure<DisplayMode>(modePointer);
        nint modePointers = SDL_GetFullscreenDisplayModes(id, out int count);
        var modes = new List<VideoMode>();
        if (modePointers == 0) throw new InvalidOperationException("SDL fullscreen display modes are unavailable.");
        try
        {
            for (int index = 0; index < count; index++)
                modes.Add(ToVideoMode(Marshal.PtrToStructure<DisplayMode>(Marshal.ReadIntPtr(modePointers, index * IntPtr.Size))));
        }
        finally { SDL_free(modePointers); }
        var result = (MonitorInfo)RuntimeHelpers.GetUninitializedObject(typeof(MonitorInfo));
        Put(result, "_handle", new MonitorHandle(checked((nint)id)));
        Put(result, "<Name>k__BackingField", Marshal.PtrToStringUTF8(SDL_GetDisplayName(id)) ?? "");
        Put(result, "<ClientArea>k__BackingField", bounds.Box);
        Put(result, "<WorkArea>k__BackingField", work.Box);
        Put(result, "<HorizontalScale>k__BackingField", scale);
        Put(result, "<VerticalScale>k__BackingField", scale);
        Put(result, "<HorizontalDpi>k__BackingField", 96 * scale);
        Put(result, "<VerticalDpi>k__BackingField", 96 * scale);
        Put(result, "<HorizontalRawDpi>k__BackingField", float.NaN);
        Put(result, "<VerticalRawDpi>k__BackingField", float.NaN);
        Put(result, "_supportedVideoModes", modes.ToArray());
        Put(result, "<CurrentVideoMode>k__BackingField", ToVideoMode(currentMode));
        return result;
    }
    private static VideoMode ToVideoMode(DisplayMode mode)
    {
        // SDL's format token encodes packed component layout. Ask SDL for the masks
        // instead of assuming the desktop uses eight-bit RGB channels.
        if (!SDL_GetMasksForPixelFormat(mode.Format, out _, out uint red, out uint green, out uint blue, out _))
            throw new InvalidOperationException("SDL display pixel format has no RGB masks.");
        return new VideoMode
        {
            Width = mode.Width, Height = mode.Height,
            RedBits = System.Numerics.BitOperations.PopCount(red), GreenBits = System.Numerics.BitOperations.PopCount(green),
            BlueBits = System.Numerics.BitOperations.PopCount(blue), RefreshRate = (int)MathF.Round(mode.RefreshRate)
        };
    }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayRectangle
    {
        internal int X, Y, Width, Height;
        internal readonly Box2i Box => new(X, Y, X + Width, Y + Height);
    }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayMode
    {
        internal uint DisplayId, Format;
        internal int Width, Height;
        internal float PixelDensity, RefreshRate;
        internal int RefreshRateNumerator, RefreshRateDenominator;
        internal nint Internal;
    }
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern nint SDL_GetDisplays(out int count);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern uint SDL_GetPrimaryDisplay();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern nint SDL_GetDisplayName(uint display);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern float SDL_GetDisplayContentScale(uint display);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetDisplayBounds(uint display, out DisplayRectangle bounds);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetDisplayUsableBounds(uint display, out DisplayRectangle bounds);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern nint SDL_GetCurrentDisplayMode(uint display);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern nint SDL_GetFullscreenDisplayModes(uint display, out int count);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetMasksForPixelFormat(uint format, out int bitsPerPixel, out uint red, out uint green, out uint blue, out uint alpha);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_free(nint pointer);
}
