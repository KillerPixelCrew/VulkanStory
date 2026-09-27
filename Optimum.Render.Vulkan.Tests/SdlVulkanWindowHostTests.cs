using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Platform;
using Silk.NET.Vulkan;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

[Collection("Hidden Vulkan Window")]
public sealed unsafe class SdlVulkanWindowHostTests
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [Fact]
    public void PclPingMatchesOnlyItsWindowsMessageAndWindow()
    {
        nint message = Marshal.AllocHGlobal(IntPtr.Size + sizeof(uint));
        try
        {
            nint hwnd = (nint)0x1234;
            Marshal.WriteIntPtr(message, hwnd);
            Marshal.WriteInt32(message, IntPtr.Size, 0x4567);
            Assert.True(SdlVulkanWindowHost.IsPclPingMessage(message, hwnd, 0x4567));
            Assert.False(SdlVulkanWindowHost.IsPclPingMessage(message, hwnd, 0x4568));
            Assert.False(SdlVulkanWindowHost.IsPclPingMessage(message, (nint)0x1235, 0x4567));
            Assert.False(SdlVulkanWindowHost.IsPclPingMessage(0, hwnd, 0x4567));
        }
        finally { Marshal.FreeHGlobal(message); }
    }

    [SkippableFact]
    public void SdlWindowsHookReceivesPingDuringMessagePump()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "SDL Windows hook probe runs on Windows.");
        using var window = SdlVulkanWindowHost.Create("SDL PCL hook probe", 128, 96, hidden: true);
        SdlEventPump.Drain();
        int pings = 0;
        const uint pingMessage = 0x8001; // WM_APP + 1, local to this hidden window.
        Assert.True(window.InstallPclPingHook(pingMessage, () => pings++));
        Assert.True(PostMessageW(window.Win32Handle, pingMessage, 0, 0));
        SdlEventPump.Drain();
        Assert.Equal(1, pings);
    }

    [SkippableFact]
    public void HiddenSdlWindowDeliversStreamlinePclPingOnCurrentFrame()
    {
        Skip.If(Environment.GetEnvironmentVariable("OPTIMUM_STREAMLINE_PROBE") != "1",
            "Run this Streamline probe alone with OPTIMUM_STREAMLINE_PROBE=1.");
        string directory = Path.GetDirectoryName(typeof(VulkanDevice).Assembly.Location)!;
        Skip.IfNot(File.Exists(Path.Combine(directory, "OptimumStreamline.dll")) &&
            File.Exists(Path.Combine(directory, "sl.interposer.dll")), "Streamline runtime unavailable.");
        using var window = SdlVulkanWindowHost.Create("SDL PCL runtime probe", 128, 96, hidden: true);
        using var device = new VulkanDevice { NativeShadersEnabled = false };
        Assert.True(device.InitializeWindow(window, 128, 96, out string reason), reason);
        Assert.NotNull(device.ContextForTests.Streamline);
        uint message = device.PclWindowMessage;
        Assert.NotEqual(0u, message);
        Assert.True(window.InstallPclPingHook(message, device.MarkPclLatencyPing));
        ulong frame = device.BeginLatencyFrame();
        device.SleepVendorLatency(frame, mayGenerate: false);
        Assert.True(PostMessageW(window.Win32Handle, message, 0, 0));
        SdlEventPump.Drain();
        Assert.Equal(1, device.PclPingSuccessCountForTests);
        window.RemovePclPingHook();
    }

    private sealed class PlatformServicesStub : IXPlatformInterface
    {
        public GameWindow Window { get; set; } = null!;
        public bool ConfirmMessageBox(string title, string text) => true;
        public void SetClipboardText(string text) { }
        public string GetClipboardText() => "";
        public void ShowMessageBox(string title, string text) { }
        public Vintagestory.API.MathTools.Size2i GetScreenSize() => new(128, 96);
        public IAviWriter GetAviWriter(int recordingBufferSize, double framerate, string codeccode) =>
            throw new NotSupportedException();
        public AvailableCodec[] AvailableCodecs() => Array.Empty<AvailableCodec>();
        public void MoveFileToRecyclebin(string filepath) { }
        public long GetFreeDiskSpace(string filepath) => 0;
        public long GetRamCapacity() => 0;
        public string GetCpuInfo() => "test";
        public void FocusWindow() { }
    }

    private sealed class FrameProbe : NewFrameHandler
    {
        public int Count;
        public void OnNewFrame(float dt)
        {
            Assert.True(float.IsFinite(dt) && dt >= 0);
            Count++;
        }
    }

    [SkippableFact]
    public void HiddenSdlWindowProvidesVulkanSurfaceAndNativeHandle()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Win32 handle probe runs on Windows.");
        using var window = SdlVulkanWindowHost.Create("SDL3 Vulkan surface probe", 128, 96, hidden: true);
        Assert.NotEqual(IntPtr.Zero, window.NativeHandle);
        Assert.NotEqual(0, window.Win32Handle);
        string[] extensions = window.RequiredInstanceExtensions();
        Assert.Contains("VK_KHR_surface", extensions);
        Assert.Contains("VK_KHR_win32_surface", extensions);

        var options = new VulkanContextOptions
        {
            RequiredInstanceExtensions = extensions,
            EnableValidation = false,
        };
        Assert.True(VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason), reason);
        Assert.NotNull(context);
        SurfaceKHR surface = default;
        try
        {
            Assert.True(window.TryCreate(context!, out surface, out reason), reason);
            Assert.NotEqual(0UL, surface.Handle);
        }
        finally
        {
            if (surface.Handle != 0) WindowSurface.Destroy(context!, surface);
            context!.Dispose();
        }
    }

    [SkippableFact]
    public void HiddenSdlWindowExposesLogicalAndPixelSizeAndTextInput()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "SDL window operation probe runs on Windows.");
        using var window = SdlVulkanWindowHost.Create("SDL3 window operation probe", 128, 96, hidden: true);
        Assert.Equal((128, 96), window.WindowSize);
        Assert.True(window.PixelSize.Width > 0 && window.PixelSize.Height > 0);
        Assert.False(window.IsFocused);
        Assert.False(window.IsFullscreen);
        window.SetTitle("SDL3 operation probe updated");
        Assert.True(window.IsResizable);
        window.SetResizable(false);
        Assert.False(window.IsResizable);
        window.SetResizable(true);
        window.SetBordered(false);
        Assert.True(window.IsBorderless);
        window.SetBordered(true);
        Assert.False(window.IsBorderless);
        window.SetMinimumSize(100, 80);
        window.SetSize(160, 120);
        Assert.Equal((160, 120), window.WindowSize);
        Assert.True(window.PixelSize.Width > 0 && window.PixelSize.Height > 0);
        Assert.False(window.TextInputActive);
        window.SetTextInputArea(8, 12, 72, 24, 4);
        Assert.Equal("none", Marshal.PtrToStringUTF8(SDL_GetHint("SDL_IME_IMPLEMENTED_UI")));
        window.SetTextInputActive(true);
        Assert.True(window.TextInputActive);
        Assert.True(window.ClearComposition());
        window.SetTextInputActive(false);
        Assert.False(window.TextInputActive);
        window.SetRelativeMouseMode(true);
        Assert.True(window.RelativeMouseMode);
        window.SetRelativeMouseMode(false);
        Assert.False(window.RelativeMouseMode);
    }

    [SkippableFact]
    public void HiddenSdlWindowCanInstallIconAndColorCursor()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "SDL cursor probe runs on Windows.");
        using var window = SdlVulkanWindowHost.Create("SDL3 cursor probe", 128, 96, hidden: true);
        byte[] rgba =
        {
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255,
        };
        window.SetIcon(2, 2, rgba);
        window.LoadCursor("test", 0, 0, 2, 2, rgba);
        window.UseCursor("test");
        window.RestoreCursor();
        window.UseCursor("test");
    }

    [SkippableFact]
    public void RendererCreatesSwapchainFromSdlWindow()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windowed renderer probe runs on Windows.");
        using var window = SdlVulkanWindowHost.Create("SDL3 renderer probe", 128, 96, hidden: true);
        using var renderer = new VulkanDevice { NativeShadersEnabled = false, EnableStreamline = false };
        Assert.True(renderer.InitializeWindow(window, 128, 96, out string reason), reason);
        Assert.NotNull(renderer.SwapchainForTests);
    }

    [SkippableFact]
    public unsafe void ClientPlatformInitializesAgainstSdlWindow()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windowed platform probe runs on Windows.");
        using var window = SdlVulkanWindowHost.Create("SDL3 platform probe", 128, 96, hidden: true);
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-sdl-platform-" + Guid.NewGuid().ToString("N"));
        var platform = new VulkanClientPlatform(null!)
        {
            DeviceFactory = () => new VulkanDevice { NativeShadersEnabled = false, EnableStreamline = false },
            CrashMarkerDataPath = dataPath,
        };
        int originalScreenWidth = Vintagestory.Client.NoObf.ClientSettings.ScreenWidth;
        int originalScreenHeight = Vintagestory.Client.NoObf.ClientSettings.ScreenHeight;
        var originalPlatformInterface = new PlatformServicesStub();
        typeof(Vintagestory.Client.NoObf.ClientPlatformAbstract)
            .GetProperty(nameof(platform.XPlatInterface), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(platform, originalPlatformInterface);
        try
        {
            Assert.True(platform.InitializeSdlGraphics(window, out string reason), reason);
            Assert.Same(originalPlatformInterface,
                Assert.IsType<SdlXPlatformInterface>(platform.XPlatInterface).Inner);
            Assert.True(platform.ScreenSize.Width > 0 && platform.ScreenSize.Height > 0);
            Assert.True(platform.XPlatInterface.GetScreenSize().Width > 0);
            Assert.Equal(window.WindowId, platform.SdlWindowId);
            Assert.NotNull(platform.GraphicsDevice?.SwapchainForTests);
            Assert.Equal((uint)window.PixelSize.Width, platform.GraphicsDevice!.SwapchainForTests!.Extent.Width);
            Assert.Equal((uint)window.PixelSize.Height, platform.GraphicsDevice.SwapchainForTests.Extent.Height);
            Assert.True(platform.HasControllerWindow);
            Assert.Equal(window.PixelSize, platform.ControllerWindowSize);
            Assert.Equal((WindowState)0, platform.GetWindowState());
            platform.WindowBorder = Vintagestory.Client.NoObf.EnumWindowBorder.Hidden;
            Assert.True(window.IsBorderless);
            Assert.True(window.IsResizable);
            platform.WindowBorder = Vintagestory.Client.NoObf.EnumWindowBorder.Resizable;
            Assert.False(window.IsBorderless);
            platform.WindowBorder = Vintagestory.Client.NoObf.EnumWindowBorder.Fixed;
            Assert.False(window.IsResizable);
            platform.WindowBorder = Vintagestory.Client.NoObf.EnumWindowBorder.Resizable;
            Assert.True(window.IsResizable);
            Assert.False(platform.IsFocused);
            Assert.False(platform.MouseGrabbed);
            platform.MouseGrabbed = true;
            Assert.True(platform.MouseGrabbed);
            platform.MouseGrabbed = false;
            platform.ControllerCursorPosition = new Vector2(24, 30);
            Assert.Equal(new Vector2(24, 30), platform.ControllerCursorPosition);
            Assert.Equal(platform.ControllerCursorPosition, platform.OptimumWindowMousePosition());
            FrameProfilerUtil? previousProfiler = ScreenManager.FrameProfiler;
            var frames = new FrameProbe();
            try
            {
                ScreenManager.FrameProfiler = new FrameProfilerUtil(_ => { });
                platform.SetFrameHandler(frames);
                for (int frame = 0; frame < 3; frame++) platform.OptimumRunSdlFrame();
                Assert.Equal(3, frames.Count);
                window.SetSize(160, 120);
                window.Sync();
                byte* resize = stackalloc byte[128];
                new Span<byte>(resize, 128).Clear();
                *(uint*)resize = SdlEventPump.WindowResized;
                *(uint*)(resize + 16) = window.WindowId;
                *(int*)(resize + 20) = 160;
                *(int*)(resize + 24) = 120;
                Assert.True(SDL_PushEvent(resize));
                SdlEventPump.Drain(platform.DispatchSdlInput);
                platform.ApplyPendingSdlResize();
                platform.OptimumRunSdlFrame(); // swapchain rebuilds at the next acquire
                Assert.Equal(window.PixelSize, platform.ControllerWindowSize);
                Assert.Equal(160, Vintagestory.Client.NoObf.ClientSettings.ScreenWidth);
                Assert.Equal(120, Vintagestory.Client.NoObf.ClientSettings.ScreenHeight);
                platform.SetWindowState((WindowState)3);
                window.Sync();
                SdlEventPump.Drain(platform.DispatchSdlInput);
                platform.ApplyPendingSdlResize();
                platform.OptimumRunSdlFrame();
                Assert.True(window.IsFullscreen);
                Assert.Equal((WindowState)3, platform.GetWindowState());
                Assert.Equal((uint)window.PixelSize.Width,
                    platform.GraphicsDevice!.SwapchainForTests!.Extent.Width);
                Assert.Equal((uint)window.PixelSize.Height,
                    platform.GraphicsDevice.SwapchainForTests.Extent.Height);
                platform.SetWindowState((WindowState)0);
                window.Sync();
                SdlEventPump.Drain(platform.DispatchSdlInput);
                platform.ApplyPendingSdlResize();
                platform.OptimumRunSdlFrame();
                Assert.False(window.IsFullscreen);
                Assert.Equal((WindowState)0, platform.GetWindowState());
                Assert.Equal((uint)window.PixelSize.Width,
                    platform.GraphicsDevice.SwapchainForTests!.Extent.Width);
                Assert.Equal((uint)window.PixelSize.Height,
                    platform.GraphicsDevice.SwapchainForTests.Extent.Height);
                Assert.NotEqual(0u, window.DisplayId);
                platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.DisplayFirst, 0, 0,
                    DisplayId: window.DisplayId + 1));
                Assert.False(platform.SdlGuiRecomposePendingForTests);
                platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.DisplayFirst, 0, 0,
                    DisplayId: window.DisplayId));
                Assert.True(platform.SdlGuiRecomposePendingForTests);
                platform.ApplyPendingSdlResize();
                Assert.False(platform.SdlGuiRecomposePendingForTests);
                byte* close = stackalloc byte[128];
                new Span<byte>(close, 128).Clear();
                *(uint*)close = SdlEventPump.WindowCloseRequested;
                *(uint*)(close + 16) = window.WindowId;
                Assert.True(SDL_PushEvent(close));
                using var loopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                platform.RunSdlClientLoop(loopTimeout.Token);
                Assert.Equal(7, frames.Count);
            }
            finally { ScreenManager.FrameProfiler = previousProfiler!; }
        }
        finally
        {
            Vintagestory.Client.NoObf.ClientSettings.ScreenWidth = originalScreenWidth;
            Vintagestory.Client.NoObf.ClientSettings.ScreenHeight = originalScreenHeight;
            platform.ShutdownGraphics();
            string resolved = Path.GetFullPath(dataPath);
            string tempRoot = Path.GetFullPath(Path.GetTempPath());
            Assert.StartsWith(Path.Combine(tempRoot, "optimum-sdl-platform-"), resolved,
                StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
        Assert.Same(originalPlatformInterface, platform.XPlatInterface);
        Assert.Equal(0u, platform.SdlWindowId);
        Assert.False(platform.HasControllerWindow);
    }

    [SkippableFact]
    public void ClientContractCreatesAndOwnsItsSdlWindow()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "SDL client startup probe runs on Windows.");
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-sdl-platform-" + Guid.NewGuid().ToString("N"));
        var platform = new VulkanClientPlatform(null!)
        {
            DeviceFactory = () => new VulkanDevice { NativeShadersEnabled = false, EnableStreamline = false },
            CrashMarkerDataPath = dataPath,
        };
        IOptimumSdlClientPlatform contract = platform;
        try
        {
            Assert.True(contract.TryInitializeSdlWindow("SDL3 owned window probe", 128, 96,
                hidden: true, fullscreen: false, out string reason), reason);
            Assert.NotEqual(0u, platform.SdlWindowId);
            Assert.True(contract.SdlPixelWidth > 0 && contract.SdlPixelHeight > 0);
            Assert.NotNull(platform.GraphicsDevice?.SwapchainForTests);
        }
        finally
        {
            platform.ShutdownGraphics();
            string resolved = Path.GetFullPath(dataPath);
            Assert.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "optimum-sdl-platform-"),
                resolved, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
        Assert.Equal(0, contract.SdlPixelWidth);
        Assert.Equal(0u, platform.SdlWindowId);
    }

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_PushEvent(void* eventData);
}
