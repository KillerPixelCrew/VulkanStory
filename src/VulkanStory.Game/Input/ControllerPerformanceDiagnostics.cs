using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VulkanStory.Game.Input;

// CPU timings on the SDL/render thread. These are not GPU or generated-frame timings.
internal sealed class ControllerPerformanceDiagnostics(Action<string> write)
{
    private struct Sample
    {
        internal long Count, IntervalCount, Interval, MaxInterval, Total, MaxTotal;
        internal long Pacing, Sleep, Events, Update, Controller, Render, Allocated;
        internal long MaxPacing, MaxSleep, MaxEvents, MaxUpdate, MaxController, MaxRender;
    }

    private readonly Sample[] samples = new Sample[8];
    private readonly bool allowed = Environment.GetEnvironmentVariable("VULKANSTORY_CONTROLLER_DIAGNOSTICS") != "0";
    private long start, previousStart, pacingEnd, eventsEnd, controllerEnd, allocatedStart, reportDue, updateTicks, sleepTicks;
    private int gc0, gc1, gc2, bucket, previousBucket;
    private bool recording, physical;
    internal bool Recording => recording;

    internal void BeginFrame(bool enabled)
    {
        physical = false;
        updateTicks = sleepTicks = 0;
        if (!allowed || !enabled)
        {
            if (recording)
            {
                previousStart = reportDue = 0;
                Array.Clear(samples);
            }
            recording = false;
            return;
        }
        recording = true;
        start = Stopwatch.GetTimestamp();
        allocatedStart = GC.GetAllocatedBytesForCurrentThread();
        if (reportDue == 0)
        {
            reportDue = start + Stopwatch.Frequency * 5;
            gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
        }
    }

    internal void PhysicalInput() => physical = true;
    internal void EndPacing() { if (recording) pacingEnd = Stopwatch.GetTimestamp(); }
    internal void EndEvents() { if (recording) eventsEnd = Stopwatch.GetTimestamp(); }
    internal void RecordGamepadUpdate(long ticks) { if (recording) updateTicks += ticks; }
    internal void RecordVendorSleep(long ticks) { if (recording) sleepTicks += ticks; }
    internal void EndControllers(bool active, bool world)
    {
        if (!recording) return;
        controllerEnd = Stopwatch.GetTimestamp();
        bucket = (world ? 0 : 4) + (active ? 1 : 0) + (physical ? 2 : 0);
    }

    internal void EndFrame()
    {
        if (!recording) return;
        long end = Stopwatch.GetTimestamp();
        ref Sample sample = ref samples[bucket];
        sample.Count++;
        if (previousStart != 0)
        {
            long interval = start - previousStart;
            // The elapsed interval belongs to the previous frame's input state.
            ref Sample previous = ref samples[previousBucket];
            previous.IntervalCount++; previous.Interval += interval;
            previous.MaxInterval = Math.Max(previous.MaxInterval, interval);
        }
        previousStart = start;
        previousBucket = bucket;
        long total = end - start;
        sample.Total += total; sample.MaxTotal = Math.Max(sample.MaxTotal, total);
        sample.Pacing += pacingEnd - start;
        sample.Sleep += sleepTicks; // Subset of Pacing.
        sample.Events += eventsEnd - pacingEnd;
        sample.Update += updateTicks; // Subset of Events, not an additional phase.
        sample.Controller += controllerEnd - eventsEnd;
        sample.Render += end - controllerEnd;
        sample.MaxPacing = Math.Max(sample.MaxPacing, pacingEnd - start);
        sample.MaxSleep = Math.Max(sample.MaxSleep, sleepTicks);
        sample.MaxEvents = Math.Max(sample.MaxEvents, eventsEnd - pacingEnd);
        sample.MaxUpdate = Math.Max(sample.MaxUpdate, updateTicks);
        sample.MaxController = Math.Max(sample.MaxController, controllerEnd - eventsEnd);
        sample.MaxRender = Math.Max(sample.MaxRender, end - controllerEnd);
        sample.Allocated += GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
        if (end < reportDue) return;

        var text = new StringBuilder("[VulkanStory] Controller performance: CPU ms avg/max; update is inside events; vendorSleep is inside pacing; pacing includes pre-input work/frame-cap waits; render includes UI/simulation/GPU waits; interval is real-frame cadence. GC process-wide=");
        text.Append(GC.CollectionCount(0) - gc0).Append('/')
            .Append(GC.CollectionCount(1) - gc1).Append('/')
            .Append(GC.CollectionCount(2) - gc2);
        for (int i = 0; i < samples.Length; i++)
        {
            Sample s = samples[i];
            if (s.Count == 0) continue;
            text.Append(" | ").Append(i < 4 ? "world/" : "menu/")
                .Append((i % 4) switch { 0 => "idle", 1 => "controller-state", 2 => "physical-event", _ => "mixed" })
                .Append(" n=").Append(s.Count)
                .Append(" interval(avg/max)=").Append(Ms(s.Interval, s.IntervalCount)).Append('/').Append(Ms(s.MaxInterval, 1))
                .Append(" pump(avg/max)=").Append(Ms(s.Total, s.Count)).Append('/').Append(Ms(s.MaxTotal, 1))
                .Append(" pacing=").Append(Ms(s.Pacing, s.Count)).Append('/').Append(Ms(s.MaxPacing, 1))
                .Append(" vendorSleep=").Append(Ms(s.Sleep, s.Count)).Append('/').Append(Ms(s.MaxSleep, 1))
                .Append(" events=").Append(Ms(s.Events, s.Count)).Append('/').Append(Ms(s.MaxEvents, 1))
                .Append(" update=").Append(Ms(s.Update, s.Count)).Append('/').Append(Ms(s.MaxUpdate, 1))
                .Append(" controller=").Append(Ms(s.Controller, s.Count)).Append('/').Append(Ms(s.MaxController, 1))
                .Append(" render=").Append(Ms(s.Render, s.Count)).Append('/').Append(Ms(s.MaxRender, 1))
                .Append(" allocated-B/frame=").Append(s.Allocated / s.Count);
        }
        write(text.ToString());
        Array.Clear(samples);
        previousStart = 0;
        reportDue = end + Stopwatch.Frequency * 5;
        gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
    }

    private static string Ms(long ticks, long count) => count == 0 ? "n/a" :
        (ticks * 1000d / Stopwatch.Frequency / count).ToString("0.000", CultureInfo.InvariantCulture);
}
