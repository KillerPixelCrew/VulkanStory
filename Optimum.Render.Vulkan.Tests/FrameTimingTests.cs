using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public class FrameTimingTests
{
    [Fact]
    public void PhaseDurationsBelongToThePresentedFrame()
    {
        var tracker = new LatencyPhaseTracker();
        tracker.Mark(42, LatencyMarker.InputSample, 100);
        tracker.Mark(42, LatencyMarker.SimulationStart, 105);
        tracker.Mark(42, LatencyMarker.SimulationEnd, 125);
        tracker.Mark(42, LatencyMarker.RenderSubmitStart, 130);
        tracker.Mark(42, LatencyMarker.RenderSubmitEnd, 160);
        tracker.Mark(42, LatencyMarker.PresentStart, 165);
        tracker.Mark(42, LatencyMarker.PresentEnd, 172);

        Assert.False(tracker.TryComplete(41, 900, out _));
        Assert.True(tracker.TryComplete(42, 901, out var report));
        Assert.Equal(new LatencyFrameReport(42, 901, 5, 20, 30, 7, 72), report);
        Assert.False(tracker.TryComplete(42, 902, out _));
    }

    [Fact]
    public void InterruptedFramesCannotLeakTimestampsIntoTheNextReport()
    {
        var warnings = new List<string>();
        var tracker = new LatencyPhaseTracker(warnings.Add);
        tracker.Mark(1, LatencyMarker.SimulationStart, 10);
        tracker.Mark(2, LatencyMarker.SimulationStart, 100);
        tracker.Mark(3, LatencyMarker.SimulationStart, 200);
        tracker.Mark(3, LatencyMarker.SimulationEnd, 230);
        tracker.Mark(3, LatencyMarker.PresentEnd, 250);

        Assert.True(tracker.TryComplete(3, 7, out var report));
        Assert.Equal(30UL, report.SimulationUs);
        Assert.Equal(50UL, report.TotalUs);
        Assert.Equal(0UL, report.RenderSubmitUs);
        Assert.Equal(0UL, report.PresentUs);
        Assert.Single(warnings);
    }

    [Fact]
    public void UnconsumedReportsStayBoundedAndDrainInFrameOrder()
    {
        var recorder = new FrameTimingRecorder();
        for (ulong frame = 1; frame <= 300; frame++)
        {
            recorder.Marker(frame, LatencyMarker.InputSample);
            recorder.Marker(frame, LatencyMarker.PresentEnd);
            recorder.OnPresent(frame, frame + 1000);
        }
        var reports = recorder.TakeReports();
        Assert.Equal(256, reports.Length);
        Assert.Equal(Enumerable.Range(45, 256).Select(i => (ulong)i), reports.Select(r => r.FrameId));
        Assert.All(reports, r => Assert.Equal(r.FrameId + 1000, r.PresentId));
        Assert.Empty(recorder.TakeReports());
    }

    [Fact]
    public void MissingOrReversedPhasesDoNotInventElapsedTime()
    {
        var report = LatencyFrameReport.FromCpuTimestamps(9, 12, 0, 100, 90, 0, 120, 130, 130);
        Assert.Equal(0UL, report.InputUs);
        Assert.Equal(0UL, report.SimulationUs);
        Assert.Equal(0UL, report.RenderSubmitUs);
        Assert.Equal(0UL, report.PresentUs);
        Assert.Equal(30UL, report.TotalUs);
    }
}
