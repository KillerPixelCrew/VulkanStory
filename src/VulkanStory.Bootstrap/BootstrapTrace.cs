using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VulkanStory.Bootstrap;

internal sealed class BootstrapTrace
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly string path;
    private readonly object sync = new();

    internal BootstrapTrace(string path) => this.path = path;

    internal static BootstrapTrace Create()
    {
        string? path = Environment.GetEnvironmentVariable("VULKANSTORY_BOOTSTRAP_LOG");
        Environment.SetEnvironmentVariable("VULKANSTORY_BOOTSTRAP_LOG", null);
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VulkanStory", "Logs", $"bootstrap-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}.jsonl");
        AppContext.SetData("VulkanStory.Bootstrap.Log", path);
        return new BootstrapTrace(path);
    }

    internal void Write(string name, string detail)
    {
        // Diagnostics must never turn an observer patch into a game startup failure.
        try
        {
            string record = JsonSerializer.Serialize(new
            {
                @event = name, detail, pid = Environment.ProcessId,
                managedThread = Environment.CurrentManagedThreadId,
                nativeThread = OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0,
                qpc = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency,
                source = "managed", utc = DateTime.UtcNow
            });
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using FileStream file = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                byte[] bytes = Encoding.UTF8.GetBytes(record + "\n");
                file.Write(bytes);
            }
        }
        catch (Exception error) { Debug.WriteLine($"VulkanStory bootstrap trace unavailable: {error.Message}"); }
    }
}
