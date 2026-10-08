using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VulkanStory.Bootstrap;

/// <summary>Best-effort JSON-lines diagnostic sink available before game logging is initialized.</summary>
internal sealed class BootstrapTrace
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly string path;
    private readonly object sync = new();

    /// <summary>Creates a sink for an explicitly selected file; directories are created on the first write.</summary>
    /// <param name="path">Destination JSON-lines path.</param>
    internal BootstrapTrace(string path) => this.path = path;

    /// <summary>Consumes the optional bootstrap-log environment override and publishes the selected path to the process.</summary>
    /// <returns>A sink using the override or the per-process file under LocalApplicationData.</returns>
    internal static BootstrapTrace Create()
    {
        string? path = Environment.GetEnvironmentVariable("VULKANSTORY_BOOTSTRAP_LOG");
        Environment.SetEnvironmentVariable("VULKANSTORY_BOOTSTRAP_LOG", null);
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VulkanStory", "Logs", $"bootstrap-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}.jsonl");
        AppContext.SetData("VulkanStory.Bootstrap.Log", path);
        return new BootstrapTrace(path);
    }

    /// <summary>Serializes one event with process/thread identities and timing, suppressing diagnostic I/O failures.</summary>
    /// <param name="name">Stable diagnostic event name.</param>
    /// <param name="detail">Event detail serialized as JSON text.</param>
    /// <remarks>Writes through this sink are serialized; readers and other file handles may coexist.</remarks>
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
