using System.Diagnostics;

namespace VulkanStory.Game.Performance;

/// <summary>Starts a bounded external collector; never runs trace processing on the render thread.</summary>
internal static class PerformanceCaptureControl
{
    private static Process? collector;
    private static string? output;
    internal static string Command(string command)
    {
        try
        {
            string[] words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string action = words.FirstOrDefault()?.ToLowerInvariant() ?? "status";
            if (action == "status") return collector == null ? "No performance capture started." :
                collector.HasExited ? $"Capture finished (exit {collector.ExitCode}): {output}" : $"Capture running: {output}";
            if (action == "stop")
            {
                if (collector == null || collector.HasExited) return "No performance capture is running.";
                collector.StandardInput.WriteLine("stop"); collector.StandardInput.Flush();
                return $"Stopping capture and generating report: {output}";
            }
            if (action != "start") return "Use .vulkanstory profile start [seconds], stop, or status.";
            if (collector != null && !collector.HasExited) return "A performance capture is already running.";
            int seconds = 30;
            if (words.Length > 1 && !int.TryParse(words[1], out seconds)) return "Capture duration must be an integer from 1 to 300 seconds.";
            if (seconds < 1 || seconds > 300) return "Capture duration must be 1–300 seconds.";
            string tool = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(PerformanceCaptureControl).Assembly.Location)!,
                "../tools/performance/VulkanStory.PerformanceHarness.dll"));
            if (!File.Exists(tool)) return "The performance harness is missing from the runtime package.";
            output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VulkanStory", "Performance",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            foreach (string argument in new[] { tool, "capture", Environment.ProcessId.ToString(), seconds.ToString(), output }) start.ArgumentList.Add(argument);
            collector?.Dispose();
            collector = Process.Start(start) ?? throw new InvalidOperationException("Collector did not start.");
            return $"Capturing for {seconds}s. Report will be saved to {output}. Use profile status for completion.";
        }
        catch (Exception error) { return "Performance capture failed: " + error.Message; }
    }
    internal static void Stop()
    {
        if (collector != null && !collector.HasExited)
            try { collector.StandardInput.WriteLine("stop"); collector.StandardInput.Flush(); } catch { }
    }
}
