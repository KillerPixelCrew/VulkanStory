using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace VulkanStory.PerformanceHarness;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? output = null;
        try
        {
            if (args.Length == 3 && args[0] == "report")
            { Report.Write(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])); return 0; }
            if (args.Length != 4 || args[0] != "capture" || !int.TryParse(args[1], out int pid) ||
                !int.TryParse(args[2], out int seconds) || seconds is < 1 or > 300)
            {
                Console.Error.WriteLine("capture <pid> <seconds:1-300> <fresh-output-directory> | report <trace.nettrace> <output-directory>");
                return 2;
            }
            output = Path.GetFullPath(args[3]);
            if (Directory.Exists(output)) throw new IOException("Choose a fresh capture output directory.");
            Directory.CreateDirectory(output);
            string trace = Path.Combine(output, "capture.nettrace");
            var providers = new[]
            {
                new EventPipeProvider(SampleProfilerTraceEventParser.ProviderName, EventLevel.Informational),
                new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Verbose,
                    (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.Loader)),
                new EventPipeProvider("VulkanStory-Performance", EventLevel.Informational),
            };
            var client = new DiagnosticsClient(pid);
            using var session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 128);
            using var metricsStop = new CancellationTokenSource();
            Task metrics = SampleProcess(pid, output, metricsStop.Token);
            await using (var stream = File.Create(trace))
            {
                Task drain = session.EventStream.CopyToAsync(stream);
                using var cancel = new CancellationTokenSource();
                ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
                Console.CancelKeyPress += cancelHandler;
                Task stopInput = Task.Run(async () =>
                {
                    while (true)
                    {
                        string? line = Console.ReadLine();
                        if (line?.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase) == true) return;
                        // EOF on a detached/noninteractive invocation is not a request to stop capture.
                        if (line == null) { await Task.Delay(Timeout.Infinite, cancel.Token); return; }
                    }
                });
                Task deadline = Task.Delay(TimeSpan.FromSeconds(seconds), cancel.Token);
                try
                {
                    await Task.WhenAny(deadline, stopInput, drain);
                    session.Stop();
                    await drain;
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                    metricsStop.Cancel(); await metrics;
                }
            }
            Report.Write(trace, output);
            Console.WriteLine(Path.Combine(output, "report.html"));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (output != null && Directory.Exists(output)) File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString());
            return 1;
        }
    }
    private static async Task SampleProcess(int pid, string output, CancellationToken stop)
    {
        var samples = new List<object>();
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            var clock = Stopwatch.StartNew();
            TimeSpan previousCpu = process.TotalProcessorTime;
            double previousTime = 0;
            File.WriteAllText(Path.Combine(output, "target.json"), JsonSerializer.Serialize(new
            {
                Pid = pid, Name = process.ProcessName, StartedUtc = process.StartTime.ToUniversalTime(),
                LogicalProcessors = Environment.ProcessorCount,
                CpuUnits = "CorePercent: 100% is one fully occupied logical core; TotalPercent divides by logical processor count.",
            }));
            while (!stop.IsCancellationRequested && !process.HasExited)
            {
                process.Refresh();
                double now = clock.Elapsed.TotalMilliseconds;
                TimeSpan cpu = process.TotalProcessorTime;
                double corePercent = now > previousTime ? (cpu - previousCpu).TotalMilliseconds * 100 / (now - previousTime) : 0;
                samples.Add(new { TimeUtc = DateTime.UtcNow, CorePercent = corePercent, TotalPercent = corePercent / Environment.ProcessorCount,
                    WorkingSetBytes = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64 });
                previousCpu = cpu; previousTime = now;
                await Task.Delay(1000, stop);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { File.WriteAllText(Path.Combine(output, "metrics-error.txt"), error.Message); }
        finally { process?.Dispose(); File.WriteAllText(Path.Combine(output, "process-metrics.json"), JsonSerializer.Serialize(samples)); }
    }
}
