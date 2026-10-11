using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace VulkanStory.PerformanceHarness;

internal static class Report
{
    private sealed record Sample(double TimeMs, int Thread, string Kind, string[] Stack);
    private sealed record Span(double TimeMs, int Thread, string Name, double DurationMs, long AllocatedBytes);
    private sealed record Allocation(double TimeMs, int Thread, string Type, long Bytes, string[] Stack);

    internal static void Write(string trace, string output)
    {
        Directory.CreateDirectory(output);
        using var log = new TraceLog(TraceLog.CreateFromEventPipeDataFile(trace));
        var samples = new List<Sample>();
        var spans = new List<Span>();
        var allocations = new List<Allocation>();
        var settings = new List<object>();
        var gc = new List<object>();
        int missingStacks = 0;
        foreach (var data in log.Events)
        {
            if (data is ClrThreadSampleTraceData sample)
            {
                string[] stack = Stack(sample.CallStack());
                if (stack.Length == 0) { missingStacks++; stack = ["[unresolved stack]"]; }
                samples.Add(new(sample.TimeStampRelativeMSec, sample.ThreadID, sample.Type.ToString(), stack));
            }
            else if (data is GCAllocationTickTraceData allocation)
                allocations.Add(new(allocation.TimeStampRelativeMSec, allocation.ThreadID, allocation.TypeName ?? "[unknown type]",
                    allocation.AllocationAmount64, Stack(allocation.CallStack())));
            else if (data is GCStartTraceData collection)
                gc.Add(new { TimeMs = collection.TimeStampRelativeMSec, collection.ThreadID, Kind = "start", collection.Count,
                    collection.Depth, Reason = collection.Reason.ToString(), Type = collection.Type.ToString() });
            else if (data is GCEndTraceData ended)
                gc.Add(new { TimeMs = ended.TimeStampRelativeMSec, ended.ThreadID, Kind = "end", ended.Count, ended.Depth });
            else if (data.ProviderName == "VulkanStory-Performance")
            {
                if ((int)data.ID == 1)
                    spans.Add(new(data.TimeStampRelativeMSec, data.ThreadID, Convert.ToString(data.PayloadByName("scope")) ?? "unknown",
                        Convert.ToDouble(data.PayloadByName("durationMs")), Convert.ToInt64(data.PayloadByName("allocatedBytes"))));
                else if ((int)data.ID == 2)
                    settings.Add(new { TimeMs = data.TimeStampRelativeMSec, Json = Convert.ToString(data.PayloadByName("json")) });
            }
        }
        string capturedDirectory = Path.GetDirectoryName(trace)!;
        JsonElement? ReadJson(string name) => File.Exists(Path.Combine(capturedDirectory, name))
            ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(capturedDirectory, name))) : null;
        var report = new
        {
            Schema = 1, Source = Path.GetFileName(trace), CreatedUtc = DateTime.UtcNow,
            DurationMs = log.SessionDuration.TotalMilliseconds, EventsLost = log.EventsLost, MissingStacks = missingStacks,
            Notes = "Managed samples identify managed execution hotspots. External samples include native execution and waiting; they are not CPU time. Allocation ticks are sampled byte estimates, not exact object counts. Span bytes count only allocations on the measured thread; nested spans overlap. GPU execution time is not measured.",
            Samples = samples, Spans = spans, Allocations = allocations, Settings = settings, GarbageCollections = gc,
            Target = ReadJson("target.json"), ProcessMetrics = ReadJson("process-metrics.json"), TraceStartUtc = log.SessionStartTime.ToUniversalTime(),
        };
        string json = JsonSerializer.Serialize(report);
        File.WriteAllText(Path.Combine(output, "report.json"), json);
        string template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "report.html"));
        File.WriteAllText(Path.Combine(output, "report.html"), template.Replace("/*REPORT_DATA*/null", json, StringComparison.Ordinal));
        WriteSpeedscope(samples, Path.Combine(output, "capture.speedscope.json"));
    }

    private static string[] Stack(TraceCallStack? stack)
    {
        var frames = new List<string>();
        for (; stack != null; stack = stack.Caller)
            frames.Add(stack.CodeAddress.FullMethodName ?? "[unresolved method]");
        frames.Reverse();
        return frames.ToArray();
    }
    private static void WriteSpeedscope(List<Sample> samples, string output)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        int Frame(string name)
        {
            if (!names.TryGetValue(name, out int index)) { index = names.Count; names.Add(name, index); }
            return index;
        }
        var profiles = samples.GroupBy(s => (s.Thread, s.Kind)).Select(group => new
        {
            type = "sampled", name = $"Thread {group.Key.Thread}: {group.Key.Kind}", unit = "none", startValue = 0, endValue = group.Count(),
            samples = group.Select(s => s.Stack.Select(Frame).ToArray()).ToArray(), weights = group.Select(_ => 1).ToArray(),
        }).ToArray();
        // An anonymous member cannot spell '$schema'; use an explicit dictionary for the standard format.
        var document = new Dictionary<string, object>
        {
            ["$schema"] = "https://www.speedscope.app/file-format-schema.json",
            ["name"] = "VulkanStory performance capture", ["exporter"] = "VulkanStory.PerformanceHarness",
            ["shared"] = new { frames = names.OrderBy(p => p.Value).Select(p => new { name = p.Key }).ToArray() },
            ["profiles"] = profiles, ["activeProfileIndex"] = 0,
        };
        File.WriteAllText(output, JsonSerializer.Serialize(document));
    }
}
