using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Optimum.Launcher;

internal static class VulkanErrorDialog
{
    internal static void Show(string reason, string dataPath)
    {
        string message = "Optimum cannot start the selected Vulkan renderer. " + reason +
            "\n\nUpdate your graphics driver or repair Optimum. To use OpenGL instead, set Renderer to opengl in " +
            Path.Combine(dataPath, "ModConfig", "optimum.json") + ".";
        Logger.LogError("[Optimum] " + message);
        if (Environment.GetEnvironmentVariable("OPTIMUM_HEADLESS") == "1") return;

        if (OperatingSystem.IsWindows())
        {
            try { MessageBoxW(IntPtr.Zero, message, "Optimum — Vulkan unavailable", 0x10); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            return;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return;
        if (TryDialog("zenity", "--error", "--title=Optimum — Vulkan unavailable", "--text=" + message)) return;
        TryDialog("kdialog", "--error", message, "--title", "Optimum — Vulkan unavailable");
    }

    private static bool TryDialog(string executable, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException) { return false; }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
