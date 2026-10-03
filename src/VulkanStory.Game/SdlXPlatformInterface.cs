using OpenTK.Windowing.Desktop;
using VulkanStory.Platform.Sdl;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VulkanStory.Game;

/// <summary>
/// Retains the game's OS services while routing window-owned operations to SDL.
/// The legacy interface has no window in the no-GLFW client path.
/// </summary>
internal sealed class SdlXPlatformInterface(IXPlatformInterface inner, SdlWindowHost window, System.Func<bool> routingEnabled,
    Action requireOwner, Action<IAviWriter>? captureWriter = null)
    : IXPlatformInterface
{
    internal IXPlatformInterface Inner => inner;

    public GameWindow Window
    {
        get => inner.Window;
        set => inner.Window = value;
    }

    public bool ConfirmMessageBox(string title, string text) => inner.ConfirmMessageBox(title, text);
    public void ShowMessageBox(string title, string text) => inner.ShowMessageBox(title, text);
    public Size2i GetScreenSize()
    {
        if (!routingEnabled()) return inner.GetScreenSize();
        requireOwner();
        (int width, int height) = window.DisplaySize;
        return new Size2i(width, height);
    }
    public IAviWriter GetAviWriter(int recordingBufferSize, double framerate, string codeccode)
    {
        if (!routingEnabled()) return inner.GetAviWriter(recordingBufferSize, framerate, codeccode);
        requireOwner();
        IAviWriter writer = inner.GetAviWriter(recordingBufferSize, framerate, codeccode);
        captureWriter?.Invoke(writer);
        return writer;
    }
    public AvailableCodec[] AvailableCodecs() => inner.AvailableCodecs();
    public void MoveFileToRecyclebin(string filepath) => inner.MoveFileToRecyclebin(filepath);
    public long GetFreeDiskSpace(string filepath) => inner.GetFreeDiskSpace(filepath);
    public long GetRamCapacity() => inner.GetRamCapacity();
    public string GetCpuInfo() => inner.GetCpuInfo();

    public void SetClipboardText(string text)
    {
        if (!routingEnabled()) { inner.SetClipboardText(text); return; }
        requireOwner();
        window.SetClipboardText(text);
    }
    public string GetClipboardText()
    {
        if (!routingEnabled()) return inner.GetClipboardText();
        requireOwner();
        return window.GetClipboardText();
    }
    public void FocusWindow()
    {
        if (!routingEnabled()) { inner.FocusWindow(); return; }
        requireOwner();
        window.Focus();
    }
}
