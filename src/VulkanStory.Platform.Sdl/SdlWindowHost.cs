using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VulkanStory.Platform.Sdl;

/// <summary>
/// SDL-owned window and Vulkan surface source. The game adapter owns the frame
/// loop; this host supplies window operations and the native surface handle.
/// Dispose the Vulkan device/swapchain before disposing this window.
/// </summary>
public sealed class SdlWindowHost : IDisposable
{
    private const uint VideoSubsystem = 0x00000020;
    private const ulong HiddenFlag = 0x0000000000000008;
    private const int HintOverridePriority = 2; // SDL_HINT_OVERRIDE
    private const ulong ResizableFlag = 0x0000000000000020;
    private const ulong HighPixelDensityFlag = 0x0000000000002000;
    private const ulong VulkanFlag = 0x0000000010000000;
    private const ulong FullscreenFlag = 0x0000000000000001;
    private const ulong BorderlessFlag = 0x0000000000000010;
    private const ulong MinimizedFlag = 0x0000000000000040;
    private const ulong MaximizedFlag = 0x0000000000000080;
    private const ulong FocusFlag = 0x0000000000000200;
    private IntPtr window;
    private readonly Dictionary<string, nint> cursors = new(StringComparer.Ordinal);
    private nint activeCursor;
    private uint pclPingMessage;
    private nint pclWindowHandle;
    private Action? pclPing;
    private static SdlWindowHost? pclHookOwner;
    private static readonly WindowsMessageHook PclWindowsHook = HandleWindowsMessage;

    private readonly bool keepHidden;
    private SdlWindowHost(IntPtr window, bool keepHidden = false) { this.window = window; this.keepHidden = keepHidden; }
    public bool KeepsHidden => keepHidden;

    public IntPtr NativeHandle => window;
    public uint WindowId => window == IntPtr.Zero ? 0 : SDL_GetWindowID(window);
    public bool IsFocused => (SDL_GetWindowFlags(RequireWindow()) & FocusFlag) != 0;
    public bool IsVisible => (SDL_GetWindowFlags(RequireWindow()) & HiddenFlag) == 0;
    public bool IsFullscreen => (SDL_GetWindowFlags(RequireWindow()) & FullscreenFlag) != 0;
    public bool IsMinimized => (SDL_GetWindowFlags(RequireWindow()) & MinimizedFlag) != 0;
    public bool IsMaximized => (SDL_GetWindowFlags(RequireWindow()) & MaximizedFlag) != 0;
    public bool IsBorderless => (SDL_GetWindowFlags(RequireWindow()) & BorderlessFlag) != 0;
    public bool IsResizable => (SDL_GetWindowFlags(RequireWindow()) & ResizableFlag) != 0;
    public bool RelativeMouseMode => SDL_GetWindowRelativeMouseMode(RequireWindow());
    public bool TextInputActive => SDL_TextInputActive(RequireWindow());
    public uint DisplayId => SDL_GetDisplayForWindow(RequireWindow());
    public (float X, float Y) MousePosition
    {
        get
        {
            RequireWindow();
            SDL_GetMouseState(out float x, out float y);
            return (x, y);
        }
    }
    public (int Width, int Height) WindowSize
    {
        get
        {
            Check(SDL_GetWindowSize(RequireWindow(), out int width, out int height), "SDL_GetWindowSize");
            return (width, height);
        }
    }
    public (int Width, int Height) PixelSize
    {
        get
        {
            Check(SDL_GetWindowSizeInPixels(RequireWindow(), out int width, out int height),
                "SDL_GetWindowSizeInPixels");
            return (width, height);
        }
    }
    public (int Width, int Height) DisplaySize
    {
        get
        {
            uint display = SDL_GetDisplayForWindow(RequireWindow());
            if (display == 0) throw new InvalidOperationException("SDL_GetDisplayForWindow failed: " + Error());
            Check(SDL_GetDisplayBounds(display, out SdlRect bounds), "SDL_GetDisplayBounds");
            return (bounds.Width, bounds.Height);
        }
    }
    public nint Win32Handle => OperatingSystem.IsWindows() && window != IntPtr.Zero
        ? SDL_GetPointerProperty(SDL_GetWindowProperties(window), "SDL.window.win32.hwnd", IntPtr.Zero)
        : 0;

    public static SdlWindowHost Create(string title, int width, int height, bool hidden = false, bool keepHidden = false)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        SdlNativeLibrary.EnsureRegistered();
        // The game has no inline composition renderer. Let the OS display IME
        // preedit and candidates; SDL_TEXT_INPUT remains the only committed text.
        SDL_SetHintWithPriority("SDL_IME_IMPLEMENTED_UI", "none", HintOverridePriority);
        if (!SDL_InitSubSystem(VideoSubsystem))
            throw new InvalidOperationException("SDL3 video initialization failed: " + Error());
        ulong flags = VulkanFlag | ResizableFlag | HighPixelDensityFlag | (hidden || keepHidden ? HiddenFlag : 0);
        IntPtr window = SDL_CreateWindow(title, width, height, flags);
        if (window != IntPtr.Zero) return new SdlWindowHost(window, keepHidden);
        string reason = Error();
        SDL_QuitSubSystem(VideoSubsystem);
        throw new InvalidOperationException("SDL3 Vulkan window creation failed: " + reason);
    }

    public void SetTitle(string title) => Check(SDL_SetWindowTitle(RequireWindow(), title), "SDL_SetWindowTitle");
    public unsafe void SetIcon(int width, int height, byte[] rgba)
    {
        RequireWindow();
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("Window icon must contain width × height RGBA pixels", nameof(rgba));
        uint format = SDL_GetPixelFormatForMasks(32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000);
        if (format == 0) throw new InvalidOperationException("SDL icon pixel format unavailable: " + Error());
        fixed (byte* pixels = rgba)
        {
            nint surface = SDL_CreateSurfaceFrom(width, height, format, (nint)pixels, width * 4);
            if (surface == 0) throw new InvalidOperationException("SDL_CreateSurfaceFrom failed: " + Error());
            try { Check(SDL_SetWindowIcon(window, surface), "SDL_SetWindowIcon"); }
            finally { SDL_DestroySurface(surface); }
        }
    }
    public void SetSize(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Check(SDL_SetWindowSize(RequireWindow(), width, height), "SDL_SetWindowSize");
    }
    public void SetMinimumSize(int width, int height)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        Check(SDL_SetWindowMinimumSize(RequireWindow(), width, height), "SDL_SetWindowMinimumSize");
    }
    public void SetFullscreen(bool enabled) { if (!keepHidden) Check(SDL_SetWindowFullscreen(RequireWindow(), enabled), "SDL_SetWindowFullscreen"); }
    public void Minimize() { if (!keepHidden) Check(SDL_MinimizeWindow(RequireWindow()), "SDL_MinimizeWindow"); }
    public void Center() => Check(SDL_SetWindowPosition(RequireWindow(), 0x2FFF0000, 0x2FFF0000), "SDL_SetWindowPosition");
    public void Maximize() { if (!keepHidden) Check(SDL_MaximizeWindow(RequireWindow()), "SDL_MaximizeWindow"); }
    public void Restore() { if (!keepHidden) Check(SDL_RestoreWindow(RequireWindow()), "SDL_RestoreWindow"); }
    public void SetBordered(bool bordered) => Check(SDL_SetWindowBordered(RequireWindow(), bordered),
        "SDL_SetWindowBordered");
    public void SetResizable(bool resizable) => Check(SDL_SetWindowResizable(RequireWindow(), resizable),
        "SDL_SetWindowResizable");
    public bool SetMinimizeOnFocusLoss(bool enabled)
    {
        RequireWindow();
        return SDL_SetHint("SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS", enabled ? "1" : "0");
    }
    public void Sync() => Check(SDL_SyncWindow(RequireWindow()), "SDL_SyncWindow");
    public void Show() { if (!keepHidden) Check(SDL_ShowWindow(RequireWindow()), "SDL_ShowWindow"); }
    public void Raise() { if (!keepHidden) Check(SDL_RaiseWindow(RequireWindow()), "SDL_RaiseWindow"); }
    public void Focus()
    {
        if (IsMinimized) Restore();
        Raise();
    }
    public void Hide() => Check(SDL_HideWindow(RequireWindow()), "SDL_HideWindow");
    public bool InstallPclPingHook(uint messageId, Action onPing)
    {
        ArgumentNullException.ThrowIfNull(onPing);
        if (!OperatingSystem.IsWindows() || messageId == 0 ||
            (pclHookOwner != null && pclHookOwner != this)) return false;
        nint hwnd = Win32Handle;
        if (hwnd == 0) return false;
        pclPingMessage = messageId;
        pclWindowHandle = hwnd;
        pclPing = onPing;
        pclHookOwner = this;
        SDL_SetWindowsMessageHook(PclWindowsHook, 0);
        return true;
    }

    public void RemovePclPingHook()
    {
        if (!ReferenceEquals(pclHookOwner, this)) return;
        SDL_SetWindowsMessageHook(null, 0);
        pclHookOwner = null;
        pclPing = null;
        pclPingMessage = 0;
        pclWindowHandle = 0;
    }

    private static bool HandleWindowsMessage(nint userdata, nint message)
    {
        SdlWindowHost? owner = pclHookOwner;
        if (owner != null && IsPclPingMessage(message, owner.pclWindowHandle, owner.pclPingMessage))
        {
            // SDL invokes this inside its message pump; the current frame token was
            // acquired before SDL_PumpEvents, so the ping belongs to that frame.
            try { owner.pclPing?.Invoke(); }
            catch { /* Never unwind a managed exception through SDL's callback. */ }
        }
        return true; // SDL must still process the Windows message.
    }

    public static bool IsPclPingMessage(nint message, nint windowHandle, uint pingMessage) =>
        message != 0 && windowHandle != 0 && pingMessage != 0 &&
        Marshal.ReadIntPtr(message) == windowHandle &&
        unchecked((uint)Marshal.ReadInt32(message, IntPtr.Size)) == pingMessage;
    public void SetRelativeMouseMode(bool enabled) { if (!keepHidden) Check(
        SDL_SetWindowRelativeMouseMode(RequireWindow(), enabled), "SDL_SetWindowRelativeMouseMode"); }
    public void WarpMouse(float x, float y) { if (!keepHidden) SDL_WarpMouseInWindow(RequireWindow(), x, y); }
    public void SetTextInputActive(bool active) { if (keepHidden) return; Check(active
        ? SDL_StartTextInput(RequireWindow()) : SDL_StopTextInput(RequireWindow()),
        active ? "SDL_StartTextInput" : "SDL_StopTextInput"); }
    public bool ClearComposition() => SDL_ClearComposition(RequireWindow());
    public void SetTextInputArea(int x, int y, int width, int height, int cursor)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        var area = new SdlRect(x, y, width, height);
        Check(SDL_SetTextInputArea(RequireWindow(), ref area, cursor), "SDL_SetTextInputArea");
    }

    public string GetClipboardText()
    {
        RequireWindow();
        nint utf8 = SDL_GetClipboardText();
        if (utf8 == 0) throw new InvalidOperationException("SDL_GetClipboardText failed: " + Error());
        try { return Marshal.PtrToStringUTF8(utf8) ?? ""; }
        finally { SDL_free(utf8); }
    }

    public void SetClipboardText(string text)
    {
        RequireWindow();
        Check(SDL_SetClipboardText(text), "SDL_SetClipboardText");
    }

    public unsafe void LoadCursor(string code, int hotX, int hotY, int width, int height, byte[] rgba)
    {
        RequireWindow();
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("Cursor image must contain width × height RGBA pixels", nameof(rgba));
        uint format = SDL_GetPixelFormatForMasks(32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000);
        if (format == 0) throw new InvalidOperationException("SDL cursor pixel format unavailable: " + Error());
        nint cursor;
        fixed (byte* pixels = rgba)
        {
            nint surface = SDL_CreateSurfaceFrom(width, height, format, (nint)pixels, width * 4);
            if (surface == 0) throw new InvalidOperationException("SDL_CreateSurfaceFrom failed: " + Error());
            try { cursor = SDL_CreateColorCursor(surface, hotX, hotY); }
            finally { SDL_DestroySurface(surface); }
        }
        if (cursor == 0) throw new InvalidOperationException("SDL_CreateColorCursor failed: " + Error());
        if (cursors.TryGetValue(code, out nint old))
        {
            if (old == activeCursor)
            {
                // A cursor can be reloaded while selected. Swap before freeing
                // the old handle so the platform's selected code stays valid.
                if (!SDL_SetCursor(cursor))
                {
                    string reason = Error();
                    SDL_DestroyCursor(cursor);
                    throw new InvalidOperationException("SDL_SetCursor failed: " + reason);
                }
                activeCursor = cursor;
            }
            SDL_DestroyCursor(old);
        }
        cursors[code] = cursor;
    }

    public bool UseCursor(string code)
    {
        RequireWindow();
        if (!cursors.TryGetValue(code, out nint cursor)) { RestoreCursor(); return false; }
        Check(SDL_SetCursor(cursor), "SDL_SetCursor");
        activeCursor = cursor;
        return true;
    }

    public void RestoreCursor()
    {
        RequireWindow();
        nint cursor = SDL_GetDefaultCursor();
        if (cursor == 0) throw new InvalidOperationException("SDL_GetDefaultCursor failed: " + Error());
        Check(SDL_SetCursor(cursor), "SDL_SetCursor");
        activeCursor = 0;
    }

    public string[] RequiredInstanceExtensions()
    {
        uint count;
        nint names = SDL_Vulkan_GetInstanceExtensions(out count);
        if (names == 0 || count == 0) throw new InvalidOperationException("SDL3 Vulkan extensions unavailable: " + Error());
        var extensions = new string[count];
        for (int i = 0; i < extensions.Length; i++)
            extensions[i] = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(names, i * IntPtr.Size))
                ?? throw new InvalidOperationException("SDL3 returned a null Vulkan extension name");
        return extensions;
    }

    public bool TryCreateVulkanSurface(nint instanceHandle, out ulong surface, out string? failureReason)
    {
        surface = 0;
        failureReason = null;
        if (window == IntPtr.Zero)
        {
            failureReason = "SDL3 window is closed";
            return false;
        }
        if (instanceHandle == 0)
        {
            failureReason = "Vulkan instance is null";
            return false;
        }
        try
        {
            if (!SDL_Vulkan_CreateSurface(window, instanceHandle, IntPtr.Zero, out surface))
            {
                failureReason = "SDL_Vulkan_CreateSurface failed: " + Error();
                return false;
            }
            return true;
        }
        catch (Exception error)
        {
            failureReason = "SDL3 surface creation threw: " + error.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (window == IntPtr.Zero) return;
        RemovePclPingHook();
        if (cursors.Count > 0)
        {
            nint defaultCursor = SDL_GetDefaultCursor();
            if (defaultCursor != 0) SDL_SetCursor(defaultCursor);
            foreach (nint cursor in cursors.Values) SDL_DestroyCursor(cursor);
            cursors.Clear();
            activeCursor = 0;
        }
        SDL_DestroyWindow(window);
        window = IntPtr.Zero;
        SDL_QuitSubSystem(VideoSubsystem);
    }

    private IntPtr RequireWindow() => window != IntPtr.Zero ? window : throw new ObjectDisposedException(nameof(SdlWindowHost));
    private static void Check(bool success, string operation)
    {
        if (!success) throw new InvalidOperationException(operation + " failed: " + Error());
    }
    private static string Error() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "unknown SDL3 error";

    [StructLayout(LayoutKind.Sequential)]
    private struct SdlRect(int x, int y, int width, int height)
    {
        public int X = x;
        public int Y = y;
        public int Width = width;
        public int Height = height;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool WindowsMessageHook(nint userdata, nint message);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetWindowsMessageHook(WindowsMessageHook? callback, nint userdata);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_InitSubSystem(uint flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetError();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_CreateWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string title, int width, int height, ulong flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_DestroyWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetWindowID(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong SDL_GetWindowFlags(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetWindowSize(IntPtr window, out int width, out int height);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetWindowSizeInPixels(IntPtr window, out int width, out int height);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetDisplayForWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetDisplayBounds(uint display, out SdlRect bounds);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowTitle(
        IntPtr window, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowIcon(
        IntPtr window, nint icon);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowSize(IntPtr window, int width, int height);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowMinimumSize(IntPtr window, int width, int height);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowFullscreen(
        IntPtr window, [MarshalAs(UnmanagedType.I1)] bool fullscreen);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_MinimizeWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowPosition(IntPtr window, int x, int y);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_MaximizeWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_RestoreWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowBordered(
        IntPtr window, [MarshalAs(UnmanagedType.I1)] bool bordered);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowResizable(
        IntPtr window, [MarshalAs(UnmanagedType.I1)] bool resizable);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetHint(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetHintWithPriority(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int priority);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_ClearComposition(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SyncWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_ShowWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_RaiseWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_HideWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowRelativeMouseMode(
        IntPtr window, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetWindowRelativeMouseMode(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_WarpMouseInWindow(IntPtr window, float x, float y);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetMouseState(out float x, out float y);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_StartTextInput(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_StopTextInput(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_TextInputActive(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetTextInputArea(IntPtr window, ref SdlRect area, int cursor);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetClipboardText();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetClipboardText(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_free(nint memory);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetPixelFormatForMasks(int bitsPerPixel,
        uint redMask, uint greenMask, uint blueMask, uint alphaMask);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_CreateSurfaceFrom(int width, int height, uint format, nint pixels, int pitch);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_DestroySurface(nint surface);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_CreateColorCursor(nint surface, int hotX, int hotY);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_DestroyCursor(nint cursor);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetDefaultCursor();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetCursor(nint cursor);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetWindowProperties(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetPointerProperty(uint properties, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint defaultValue);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_Vulkan_GetInstanceExtensions(out uint count);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_Vulkan_CreateSurface(
        IntPtr window, nint instance, IntPtr allocator, out ulong surface);
}
