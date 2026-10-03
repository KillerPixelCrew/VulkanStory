using OpenTK.Windowing.Desktop;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// Retains the game's OS services while routing window-owned operations to SDL.
/// The legacy interface has no window in the no-GLFW client path.
/// </summary>
internal sealed class SdlXPlatformInterface(IXPlatformInterface inner, SdlVulkanWindowHost window)
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
        (int width, int height) = window.DisplaySize;
        return new Size2i(width, height);
    }
    public IAviWriter GetAviWriter(int recordingBufferSize, double framerate, string codeccode) =>
        inner.GetAviWriter(recordingBufferSize, framerate, codeccode);
    public AvailableCodec[] AvailableCodecs() => inner.AvailableCodecs();
    public void MoveFileToRecyclebin(string filepath) => inner.MoveFileToRecyclebin(filepath);
    public long GetFreeDiskSpace(string filepath) => inner.GetFreeDiskSpace(filepath);
    public long GetRamCapacity() => inner.GetRamCapacity();
    public string GetCpuInfo() => inner.GetCpuInfo();

    public void SetClipboardText(string text) => window.SetClipboardText(text);
    public string GetClipboardText() => window.GetClipboardText();
    public void FocusWindow() => window.Focus();
}
