using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Optimum.Bootstrap.Core.Patch;

namespace Optimum.Launcher;

internal static class DeltaLaunchRecovery
{
    internal static string? FindOriginalLauncher(string runtime) =>
        DeltaRuntimeGuard.FindOriginalLauncher(runtime);

    internal static bool LaunchOriginal(string runtime)
    {
        string? launcher = FindOriginalLauncher(runtime);
        if (launcher is null)
        {
            Logger.LogError("[Optimum] No launchable original game was found in the delta receipt.");
            return false;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(launcher)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(launcher)!,
            });
            if (process is null) return false;
            Logger.Log("[Optimum] Started the original game at " + launcher);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Logger.LogError("[Optimum] Could not start the original game: " + ex.Message);
            return false;
        }
    }

    internal static bool OfferOriginal(string runtime, string reason)
    {
        string? launcher = FindOriginalLauncher(runtime);
        string message = "Optimum cannot start because its installed runtime or original game changed. " +
            reason + "\n\nRepair or reinstall Optimum with a matching release.";
        Logger.LogError("[Optimum] " + message);
        if (Environment.GetEnvironmentVariable("OPTIMUM_HEADLESS") == "1") return false;
        if (launcher is null)
        {
            ShowError(message + "\n\nThe recorded original game could not be found.");
            return false;
        }
        if (!Confirm(message + "\n\nLaunch the original Vintage Story now? Optimum will stay installed."))
            return false;
        return LaunchOriginal(runtime);
    }

    private static bool Confirm(string message)
    {
        if (OperatingSystem.IsWindows())
        {
            try { return MessageBoxW(IntPtr.Zero, message, "Optimum — startup stopped", 0x34) == 6; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
        if (!HasLinuxDisplay()) return false;
        bool? answer = TryDialog("zenity", "--question", "--title=Optimum — startup stopped", "--text=" + message);
        return answer ?? TryDialog("kdialog", "--yesno", message, "--title", "Optimum — startup stopped") ?? false;
    }

    private static void ShowError(string message)
    {
        if (OperatingSystem.IsWindows())
        {
            try { MessageBoxW(IntPtr.Zero, message, "Optimum — startup stopped", 0x10); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            return;
        }
        if (!HasLinuxDisplay()) return;
        if (TryDialog("zenity", "--error", "--title=Optimum — startup stopped", "--text=" + message) is not null)
            return;
        TryDialog("kdialog", "--error", message, "--title", "Optimum — startup stopped");
    }

    private static bool HasLinuxDisplay() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static bool? TryDialog(string executable, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException) { return null; }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
