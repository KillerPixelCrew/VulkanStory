using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VulkanStory.Bootstrap;

/// <summary>Records managed fatal exceptions independently of the game's own logger.</summary>
internal static class CrashDiagnostics
{
    private static BootstrapTrace? trace;
    private static string? directory;

    internal static void Install(BootstrapTrace sink)
    {
        trace = sink;
        directory = Environment.GetEnvironmentVariable("VULKANSTORY_CRASH_DIRECTORY");
        Environment.SetEnvironmentVariable("VULKANSTORY_CRASH_DIRECTORY", null);
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VulkanStory", "Crashes", Path.GetFileNameWithoutExtension(sink.FilePath));
        AppContext.SetData("VulkanStory.Crash.Write", (Action<Exception>)RecordManaged);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => RecordManaged(
            args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown fatal exception"));
        TaskScheduler.UnobservedTaskException += (_, args) => sink.Write("managed.task.unobserved", args.Exception.ToString());
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "bootstrap-path.txt"), sink.FilePath, Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "game-directory.txt"), AppContext.BaseDirectory, Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "environment.txt"),
                $"VulkanStory {typeof(CrashDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}\n" +
                $"UTC: {DateTime.UtcNow:O}\nPID: {Environment.ProcessId}\n" +
                $"OS: {RuntimeInformation.OSDescription}\n.NET: {RuntimeInformation.FrameworkDescription}\n" +
                $"Architecture: {RuntimeInformation.ProcessArchitecture}\nGame: {FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileVersion}\n",
                Encoding.UTF8);
        }
        catch (Exception error) { sink.Write("managed.crash_context.failed", error.Message); }
        sink.Write("managed.crash_capture.ready", directory);
    }

    internal static void SetDataPath(string value)
    {
        if (directory is null || !Path.IsPathFullyQualified(value)) return;
        try { File.WriteAllText(Path.Combine(directory, "game-data-path.txt"), value, Encoding.UTF8); }
        catch (Exception error) { trace?.Write("managed.crash_context.failed", error.Message); }
    }

    private static void RecordManaged(Exception error)
    {
        try
        {
            trace?.Write("managed.exception.unhandled", error.ToString());
            if (directory is null) return;
            File.WriteAllText(Path.Combine(directory, "managed-crash.txt"),
                $"UTC: {DateTime.UtcNow:O}\nManaged thread: {Environment.CurrentManagedThreadId}\n\n{error}\n", Encoding.UTF8);
        }
        catch { /* Never replace or suppress the original exception. Native capture remains independent. */ }
    }
}
