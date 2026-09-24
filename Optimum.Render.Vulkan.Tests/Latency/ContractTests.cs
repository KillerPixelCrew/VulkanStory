// Source: Optimum.Render.Vulkan.Tests/LatencyContractTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

/// <summary>
/// The latency contracts of seam L0, all without a device: the marker enum
/// against Silk.NET's VkLatencyMarkerNV, the settings, the report builder, the
/// OPTIMUM_VULKAN_LATENCY parse and selection table, the self-healing phase
/// tracker, and the None backend's behaviour.
/// </summary>
public class LatencyContractTests
{
    // vkSetLatencyMarkerNV takes the NV enum directly, so a mismatch here would
    // be a silently mis-attributed phase in every NV report. The markers travel
    // as ints: the enum is internal to the backend, and xunit needs public
    // signatures.
    [Fact]
    public void EveryMarkerHasTheValueOfItsNvCounterpart()
    {
        Assert.Equal((int)LatencyMarkerNV.SimulationStartNV, (int)LatencyMarker.SimulationStart);
        Assert.Equal((int)LatencyMarkerNV.SimulationEndNV, (int)LatencyMarker.SimulationEnd);
        Assert.Equal((int)LatencyMarkerNV.RendersubmitStartNV, (int)LatencyMarker.RenderSubmitStart);
        Assert.Equal((int)LatencyMarkerNV.RendersubmitEndNV, (int)LatencyMarker.RenderSubmitEnd);
        Assert.Equal((int)LatencyMarkerNV.PresentStartNV, (int)LatencyMarker.PresentStart);
        Assert.Equal((int)LatencyMarkerNV.PresentEndNV, (int)LatencyMarker.PresentEnd);
        Assert.Equal((int)LatencyMarkerNV.InputSampleNV, (int)LatencyMarker.InputSample);
        Assert.Equal((int)LatencyMarkerNV.TriggerFlashNV, (int)LatencyMarker.TriggerFlash);
        Assert.Equal((int)LatencyMarkerNV.OutOfBandRendersubmitStartNV, (int)LatencyMarker.OutOfBandRenderSubmitStart);
        Assert.Equal((int)LatencyMarkerNV.OutOfBandRendersubmitEndNV, (int)LatencyMarker.OutOfBandRenderSubmitEnd);
        Assert.Equal((int)LatencyMarkerNV.OutOfBandPresentStartNV, (int)LatencyMarker.OutOfBandPresentStart);
        Assert.Equal((int)LatencyMarkerNV.OutOfBandPresentEndNV, (int)LatencyMarker.OutOfBandPresentEnd);
    }

    [Fact]
    public void TheMarkerSetIsExactlyTheNvSetWithNoGaps()
    {
        Array values = Enum.GetValues(typeof(LatencyMarker));
        Assert.Equal(12, values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(i, (int)(LatencyMarker)values.GetValue(i)!);
        }
    }

    [Fact]
    public void TheDefaultSettingsAreOffAndUncapped()
    {
        LatencySettings settings = LatencySettings.Disabled;
        Assert.Equal(LatencyMode.Off, settings.Mode);
        Assert.Equal(0UL, settings.MinimumIntervalUs);
        Assert.False(settings.Enabled);
        Assert.False(settings.Boost);
        Assert.Equal(0u, settings.MaxFps);
    }

    [Theory]
    [InlineData((int)LatencyMode.Off, false, false)]
    [InlineData((int)LatencyMode.On, true, false)]
    [InlineData((int)LatencyMode.Boost, true, true)]
    public void EnabledAndBoostFollowTheMode(int mode, bool enabled, bool boost)
    {
        var settings = new LatencySettings((LatencyMode)mode, 0);
        Assert.Equal(enabled, settings.Enabled);
        Assert.Equal(boost, settings.Boost);
    }

    [Fact]
    public void TheFrameCapConvertsBothWays()
    {
        ulong interval = LatencySettings.IntervalUsForFps(60);
        Assert.Equal(16666UL, interval);
        Assert.Equal(60u, new LatencySettings(LatencyMode.On, interval).MaxFps);
        Assert.Equal(0UL, LatencySettings.IntervalUsForFps(0));
    }

    [Fact]
    public void ACpuReportIsTheDifferenceOfItsMarkersAndLeavesTheDriverFieldsAtZero()
    {
        LatencyFrameReport report = LatencyFrameReport.FromCpuTimestamps(
            frameId: 7, presentId: 5,
            inputSampleUs: 1000,
            simulationStartUs: 1100,
            simulationEndUs: 3100,
            renderSubmitStartUs: 3100,
            renderSubmitEndUs: 4600,
            presentStartUs: 4600,
            presentEndUs: 4900);

        Assert.Equal(7UL, report.FrameId);
        Assert.Equal(5UL, report.PresentId);
        Assert.Equal(100UL, report.InputUs);
        Assert.Equal(2000UL, report.SimulationUs);
        Assert.Equal(1500UL, report.RenderSubmitUs);
        Assert.Equal(300UL, report.PresentUs);
        Assert.Equal(0UL, report.DriverUs);
        Assert.Equal(0UL, report.OsRenderQueueUs);
        Assert.Equal(0UL, report.GpuUs);
        Assert.Equal(3900UL, report.TotalUs);
    }

    [Fact]
    public void AMissingOrOutOfOrderMarkerMakesItsIntervalZeroInsteadOfWrapping()
    {
        LatencyFrameReport report = LatencyFrameReport.FromCpuTimestamps(
            frameId: 1, presentId: 0,
            inputSampleUs: 0,          // never stamped
            simulationStartUs: 2000,
            simulationEndUs: 1000,     // out of order
            renderSubmitStartUs: 2500,
            renderSubmitEndUs: 0,      // never stamped
            presentStartUs: 3000,
            presentEndUs: 3200);

        Assert.Equal(0UL, report.InputUs);
        Assert.Equal(0UL, report.SimulationUs);
        Assert.Equal(0UL, report.RenderSubmitUs);
        Assert.Equal(200UL, report.PresentUs);
        // With no input sample the total runs from simulation start.
        Assert.Equal(1200UL, report.TotalUs);
    }

    [Fact]
    public void AWholeFrameOfMarkersBecomesOneReport()
    {
        var tracker = new LatencyPhaseTracker();
        tracker.Mark(4, LatencyMarker.InputSample, 1000);
        tracker.Mark(4, LatencyMarker.SimulationStart, 1050);
        tracker.Mark(4, LatencyMarker.SimulationEnd, 2050);
        tracker.Mark(4, LatencyMarker.RenderSubmitStart, 2050);
        tracker.Mark(4, LatencyMarker.RenderSubmitEnd, 3050);
        tracker.Mark(4, LatencyMarker.PresentStart, 3060);
        tracker.Mark(4, LatencyMarker.PresentEnd, 3160);

        Assert.False(tracker.HasOpenPhase);
        Assert.True(tracker.TryComplete(4, presentId: 9, out LatencyFrameReport report));
        Assert.Equal(50UL, report.InputUs);
        Assert.Equal(1000UL, report.SimulationUs);
        Assert.Equal(1000UL, report.RenderSubmitUs);
        Assert.Equal(100UL, report.PresentUs);
        Assert.Equal(2160UL, report.TotalUs);
        Assert.Equal(0, tracker.SelfHealCount);
    }

    [Fact]
    public void APresentOfAnotherFrameIsNotCompleted()
    {
        var tracker = new LatencyPhaseTracker();
        tracker.Mark(4, LatencyMarker.SimulationStart, 1000);
        Assert.False(tracker.TryComplete(5, 0, out _));
        Assert.True(tracker.TryComplete(4, 0, out _));
        // The frame is closed: completing it twice reports once.
        Assert.False(tracker.TryComplete(4, 0, out _));
    }

    [Fact]
    public void APhaseStillOpenWhenTheNextFrameStartsIsClosedAndLoggedExactlyOnce()
    {
        int notes = 0;
        var tracker = new LatencyPhaseTracker(_ => notes++);

        for (ulong frame = 1; frame <= 5; frame++)
        {
            long baseUs = (long)frame * 10_000;
            tracker.Mark(frame, LatencyMarker.InputSample, baseUs);
            tracker.Mark(frame, LatencyMarker.SimulationStart, baseUs + 10);
            // SimulationEnd never arrives: the phase is open when the next frame starts.
            tracker.Mark(frame, LatencyMarker.RenderSubmitStart, baseUs + 500);
            tracker.Mark(frame, LatencyMarker.RenderSubmitEnd, baseUs + 900);
            tracker.Mark(frame, LatencyMarker.PresentStart, baseUs + 910);
            tracker.Mark(frame, LatencyMarker.PresentEnd, baseUs + 950);
        }

        // Four frame starts saw the previous frame's simulation phase open...
        Assert.Equal(4, tracker.SelfHealCount);
        // ...and the log heard about it once, not every frame.
        Assert.Equal(1, notes);
        Assert.True(tracker.SelfHealLogged);
    }

    [Fact]
    public void ClosingAnOpenPhaseDoesNotLeakIntoTheNextFramesReport()
    {
        var tracker = new LatencyPhaseTracker();
        tracker.Mark(1, LatencyMarker.InputSample, 1000);
        tracker.Mark(1, LatencyMarker.SimulationStart, 1010);
        // frame 1's simulation stays open.
        tracker.Mark(2, LatencyMarker.InputSample, 2000);
        tracker.Mark(2, LatencyMarker.SimulationStart, 2010);
        tracker.Mark(2, LatencyMarker.SimulationEnd, 2210);
        tracker.Mark(2, LatencyMarker.PresentStart, 2300);
        tracker.Mark(2, LatencyMarker.PresentEnd, 2400);

        Assert.Equal(1, tracker.SelfHealCount);
        Assert.True(tracker.TryComplete(2, 0, out LatencyFrameReport report));
        Assert.Equal(2UL, report.FrameId);
        Assert.Equal(200UL, report.SimulationUs);
        Assert.Equal(400UL, report.TotalUs);
    }

    [Fact]
    public void OutOfBandMarkersDoNotDisturbTheFrameBeingCollected()
    {
        var tracker = new LatencyPhaseTracker();
        tracker.Mark(3, LatencyMarker.InputSample, 1000);
        tracker.Mark(3, LatencyMarker.SimulationStart, 1010);
        tracker.Mark(99, LatencyMarker.OutOfBandRenderSubmitStart, 1020);
        tracker.Mark(99, LatencyMarker.OutOfBandRenderSubmitEnd, 1030);
        tracker.Mark(3, LatencyMarker.SimulationEnd, 1210);
        tracker.Mark(3, LatencyMarker.PresentEnd, 1300);

        Assert.Equal(3UL, tracker.CurrentFrameId);
        Assert.Equal(0, tracker.SelfHealCount);
        Assert.True(tracker.TryComplete(3, 0, out LatencyFrameReport report));
        Assert.Equal(200UL, report.SimulationUs);
    }

    [Fact]
    public void TheNoneBackendNeverSleepsAndNeverOwnsTheFrameCap()
    {
        using var backend = new NoneLatencyBackend();
        Assert.Equal(LatencyBackendKind.None, backend.Kind);
        Assert.False(backend.OwnsFrameCap);
        Assert.Equal(0UL, backend.Sleep(1));

        backend.Apply(new LatencySettings(LatencyMode.Boost, 16666));
        Assert.Equal(LatencyMode.Boost, backend.Settings.Mode);
        // Applying changes nothing about the pacing: this backend paces nothing.
        Assert.False(backend.OwnsFrameCap);
    }

    [Fact]
    public void TheNoneBackendReportsOneFramePerPresentAndDrains()
    {
        using var backend = new NoneLatencyBackend();
        backend.Marker(1, LatencyMarker.InputSample);
        backend.Marker(1, LatencyMarker.SimulationStart);
        backend.Marker(1, LatencyMarker.SimulationEnd);
        backend.Marker(1, LatencyMarker.PresentStart);
        backend.Marker(1, LatencyMarker.PresentEnd);
        backend.OnPresent(1, presentId: 11);

        LatencyFrameReport[] reports = backend.TakeReports();
        LatencyFrameReport report = Assert.Single(reports);
        Assert.Equal(1UL, report.FrameId);
        Assert.Equal(11UL, report.PresentId);
        Assert.Empty(backend.TakeReports());
    }

    [Fact]
    public unsafe void TheNoneBackendAddsNothingToTheSubmitChain()
    {
        using var backend = new NoneLatencyBackend();
        int chain = 42;
        void* pNext = &chain;
        Assert.True(pNext == backend.TagSubmit(1, pNext));
        Assert.True(null == backend.TagSubmit(1, null));
        backend.OnSwapchainCreated(default);
    }

    [Fact]
    public void TheRecordingFakeKeepsMarkerOrderAndCounts()
    {
        var backend = new RecordingLatencyBackend { SleepDurationUs = 1234, OwnsFrameCapValue = true };
        Assert.Equal(1234UL, backend.Sleep(1));
        backend.Marker(1, LatencyMarker.InputSample);
        backend.Marker(1, LatencyMarker.SimulationStart);
        backend.OnSwapchainCreated(default);
        backend.SleepDurationUs = 0;
        Assert.Equal(0UL, backend.Sleep(2));
        backend.Marker(2, LatencyMarker.InputSample);
        backend.OnPresent(2, 7);

        Assert.Equal(new[] { LatencyMarker.InputSample, LatencyMarker.SimulationStart }, backend.MarkersOf(1));
        Assert.Equal(new[] { LatencyMarker.InputSample }, backend.MarkersOf(2));
        Assert.Equal(2, backend.SleepCount);
        Assert.Equal(1, backend.SwapchainCount);
        Assert.True(backend.OwnsFrameCap);
        Assert.Equal((2UL, 7UL), Assert.Single(backend.Presents));
        Assert.Single(backend.TakeReports());
        Assert.Empty(backend.TakeReports());
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/LatencyHookTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.Client.NoObf;
using Xunit;

/// <summary>
/// Vulkan-native plan, "Latency seams" S3: the lib calls <c>LatencySleep()</c> immediately
/// before it samples input, and the Vulkan platform turns that into the backend's sleep
/// followed by the two markers this site owns - InputSample then SimulationStart - against
/// one strictly increasing frame id. The OpenGL platform declares neither member, so the
/// vanilla frame is the vanilla frame plus one neutral virtual call.
///
/// Headless: no device and no window, so nothing here touches Vulkan or GL. The backend is
/// the recording fake the L0 stage shipped, which is the point of it.
/// </summary>
public class LatencyHookTests
{
    private static VulkanClientPlatform PlatformWith(RecordingLatencyBackend backend)
    {
        var platform = new VulkanClientPlatform(null!);
        platform.LatencyBackendOverride = backend;
        return platform;
    }

    [Fact]
    public void TheSleepRunsBeforeTheInputAndSimulationMarkers()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);

        platform.LatencySleep();

        Assert.Equal(new ulong[] { 1 }, backend.Sleeps.ToArray());
        Assert.Equal(
            new List<(ulong, LatencyMarker)>
            {
                (1UL, LatencyMarker.InputSample),
                (1UL, LatencyMarker.SimulationStart),
            },
            backend.Markers);
    }

    [Fact]
    public void EveryFrameGetsItsOwnStrictlyIncreasingId()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);

        for (int frame = 0; frame < 8; frame++) platform.LatencySleep();

        Assert.Equal(8, backend.SleepCount);
        for (int i = 0; i < backend.Sleeps.Count; i++)
        {
            Assert.Equal((ulong)(i + 1), backend.Sleeps[i]);
            // The two markers of frame i carry that frame's id and no other.
            Assert.Equal(backend.Sleeps[i], backend.Markers[i * 2].FrameId);
            Assert.Equal(backend.Sleeps[i], backend.Markers[i * 2 + 1].FrameId);
        }
        Assert.Equal(16, backend.Markers.Count);
    }

    [Fact]
    public void TheFrameCapFollowsTheBackend()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);

        Assert.False(backend.OwnsFrameCap);
        Assert.False(platform.LatencyOwnsFrameCap);

        backend.OwnsFrameCapValue = true;
        Assert.True(platform.LatencyOwnsFrameCap);

        backend.OwnsFrameCapValue = false;
        Assert.False(platform.LatencyOwnsFrameCap);
    }

    /// <summary>
    /// Latency review 2026-09-12: every backend answers <c>OwnsFrameCap</c> true
    /// as soon as its mode is not Off, which stands the lib's own FPS limiter
    /// down (seam S3). The cap therefore has to reach the backend, or turning
    /// LatencyMode on would silently uncap the client. The lib hands it over with
    /// <c>SetLatencyFrameCap</c> immediately before the sleep, and the platform
    /// applies it only when it changed - an Apply per frame would re-arm the
    /// driver's heuristic every frame.
    /// </summary>
    [Fact]
    public void TheClientsFrameCapReachesAPacingBackendOnceAndOnlyWhenItChanges()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);
        // A cap no client setting produces, so the first hand-over always changes it.
        backend.Apply(new LatencySettings(LatencyMode.On, 999_999));
        backend.Applied.Clear();

        for (int frame = 0; frame < 3; frame++)
        {
            platform.SetLatencyFrameCap(60);
            platform.LatencySleep();
        }

        Assert.Equal(
            new List<LatencySettings> { new(LatencyMode.On, LatencySettings.IntervalUsForFps(60)) },
            backend.Applied);
        // The mode is the device's business; this site only ever sets the cap.
        Assert.Equal(LatencyMode.On, backend.Settings.Mode);
    }

    /// <summary>
    /// The behaviour the review left open and this change closes: the lib's
    /// background-window cap (30 fps after sustained focus loss) is folded into
    /// the number <c>window_RenderFrame</c> hands over, so an unfocused window
    /// paces to it even though the lib's own limiter has stood down. Coming back
    /// into focus restores the foreground cap, and a repeated cap applies nothing.
    /// </summary>
    [Fact]
    public void TheBackgroundWindowCapReachesTheBackendAsItsMinimumInterval()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);
        backend.Apply(new LatencySettings(LatencyMode.On, LatencySettings.IntervalUsForFps(144)));
        backend.Applied.Clear();

        // Focus lost: window_RenderFrame's effective cap is OptimumBgMaxFps (30).
        platform.SetLatencyFrameCap(30);
        Assert.Equal(LatencySettings.IntervalUsForFps(30), backend.Settings.MinimumIntervalUs);
        Assert.Equal(33333UL, backend.Settings.MinimumIntervalUs);
        Assert.Equal(30u, backend.Settings.MaxFps);

        // Still unfocused: nothing changed, so nothing is applied.
        for (int frame = 0; frame < 5; frame++) platform.SetLatencyFrameCap(30);
        Assert.Single(backend.Applied);

        // Focused again: back to the foreground cap, one more Apply.
        platform.SetLatencyFrameCap(144);
        Assert.Equal(2, backend.Applied.Count);
        Assert.Equal(LatencySettings.IntervalUsForFps(144), backend.Settings.MinimumIntervalUs);
        Assert.Equal(LatencyMode.On, backend.Settings.Mode);
    }

    /// <summary>
    /// Off is off: a backend whose mode is Off is never applied to, so the frame
    /// with LatencyMode off is the frame Milestone 1 delivered.
    /// </summary>
    [Fact]
    public void ADisabledBackendIsNeverTouchedByTheFrameCap()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);

        for (int frame = 0; frame < 4; frame++)
        {
            platform.SetLatencyFrameCap(60);
            platform.LatencySleep();
        }

        Assert.Empty(backend.Applied);
        Assert.Equal(LatencySettings.Disabled, backend.Settings);
    }

    /// <summary>
    /// The conversion itself. The lib decides when a cap applies at all (vsync
    /// off, MaxFps in the 10..241 window) and hands 0 over when it does not; 0 is
    /// uncapped for every backend, and so is any nonsense below it.
    /// </summary>
    [Theory]
    [InlineData(60, 16666UL)]
    [InlineData(120, 8333UL)]
    [InlineData(240, 4166UL)]
    [InlineData(30, 33333UL)]
    [InlineData(0, 0UL)]
    [InlineData(-1, 0UL)]
    public void TheFrameCapConvertsFpsToAMinimumInterval(int maxFps, ulong expectedUs)
    {
        Assert.Equal(expectedUs, VulkanClientPlatform.FrameCapIntervalUs(maxFps));
    }

    [Fact]
    public void AnUncappedClientLeavesThePacingBackendUncapped()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);
        backend.Apply(new LatencySettings(LatencyMode.On, LatencySettings.IntervalUsForFps(60)));
        backend.Applied.Clear();

        // What the lib hands over with vsync on, or MaxFps at the "unlimited" end.
        platform.SetLatencyFrameCap(0);

        Assert.Equal(new List<LatencySettings> { new(LatencyMode.On, 0) }, backend.Applied);
    }

    [Fact]
    public void WithNoBackendTheFrameCapIsANoOp()
    {
        var platform = new VulkanClientPlatform(null!);
        Assert.Null(platform.LatencyBackend);

        platform.SetLatencyFrameCap(30);
    }

    [Fact]
    public void WithNoBackendAndNoDeviceTheSleepIsANoOp()
    {
        var platform = new VulkanClientPlatform(null!);
        Assert.Null(platform.LatencyBackend);

        platform.LatencySleep();

        Assert.False(platform.LatencyOwnsFrameCap);
    }

    [Fact]
    public void TheOpenGlPlatformKeepsTheNeutralMembers()
    {
        var platform = new ClientPlatformWindows(null!);

        // Neutral: calling them on the base platform does nothing and never throws.
        platform.LatencySleep();
        platform.SetLatencyFrameCap(30);
        Assert.False(platform.LatencyOwnsFrameCap);

        Assert.Equal(typeof(ClientPlatformAbstract),
            typeof(ClientPlatformWindows).GetMethod(nameof(ClientPlatformAbstract.LatencySleep))!.DeclaringType);
        Assert.Equal(typeof(ClientPlatformAbstract),
            typeof(ClientPlatformWindows).GetProperty(nameof(ClientPlatformAbstract.LatencyOwnsFrameCap))!.DeclaringType);
        Assert.Equal(typeof(ClientPlatformAbstract),
            typeof(ClientPlatformWindows).GetMethod(nameof(ClientPlatformAbstract.SetLatencyFrameCap))!.DeclaringType);
    }

    [Fact]
    public void TheVulkanPlatformOverridesBothMembers()
    {
        Assert.Equal(typeof(VulkanClientPlatform),
            typeof(VulkanClientPlatform).GetMethod(nameof(ClientPlatformAbstract.LatencySleep))!.DeclaringType);
        Assert.Equal(typeof(VulkanClientPlatform),
            typeof(VulkanClientPlatform).GetProperty(nameof(ClientPlatformAbstract.LatencyOwnsFrameCap))!.DeclaringType);
        Assert.Equal(typeof(VulkanClientPlatform),
            typeof(VulkanClientPlatform).GetMethod(nameof(ClientPlatformAbstract.SetLatencyFrameCap))!.DeclaringType);
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/LatencyReportBufferTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using Optimum.Render.Vulkan.Core;
using Xunit;

/// <summary>
/// The report buffer every CPU-timestamp backend keeps (None, Native, AMD),
/// added by the latency review of 2026-09-12: the backends used a
/// <c>List</c> with <c>RemoveAt(0)</c>, so once the buffer was full - which it is
/// for the whole session whenever nothing drains it, and nothing does unless
/// OPTIMUM_VULKAN_STATS is set - every present shifted the entire array down by
/// one inside the present path. The ring must drop the oldest entry without
/// moving anything and must keep the order the interface promises (oldest
/// first).
/// </summary>
public class LatencyReportBufferTests
{
    private static LatencyFrameReport ReportOf(ulong frameId) =>
        new(frameId, frameId + 100, 1, 2, 3, 4, 0, 0, 0, 10);

    [Fact]
    public void ItKeepsTheNewestReportsInOrderAndDropsTheOldestWhenFull()
    {
        var buffer = new LatencyReportBuffer(4);
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Take());

        for (ulong frame = 1; frame <= 6; frame++) buffer.Add(ReportOf(frame));

        Assert.Equal(4, buffer.Count);
        LatencyFrameReport[] taken = buffer.Take();
        Assert.Equal(new ulong[] { 3, 4, 5, 6 }, System.Array.ConvertAll(taken, r => r.FrameId));

        // Taking clears: the same reports are never handed out twice.
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Take());
    }

    [Fact]
    public void TakingAndAddingAcrossTheWrapKeepsTheOrder()
    {
        var buffer = new LatencyReportBuffer(4);
        for (ulong frame = 1; frame <= 3; frame++) buffer.Add(ReportOf(frame));
        Assert.Equal(new ulong[] { 1, 2, 3 }, System.Array.ConvertAll(buffer.Take(), r => r.FrameId));

        for (ulong frame = 4; frame <= 9; frame++) buffer.Add(ReportOf(frame));
        Assert.Equal(new ulong[] { 6, 7, 8, 9 }, System.Array.ConvertAll(buffer.Take(), r => r.FrameId));
    }

    /// <summary>
    /// The Native backend fills a frame's GPU interval in after the fact, when
    /// the next sleep observes that frame's present submission completed. It must
    /// find the newest report of that frame, and must not guess once the stats
    /// sample has taken it.
    /// </summary>
    [Fact]
    public void TheGpuIntervalIsAmendedOnTheWaitingReportOnly()
    {
        var buffer = new LatencyReportBuffer(4);
        buffer.Add(ReportOf(1));
        buffer.Add(ReportOf(2));

        Assert.True(buffer.AmendGpuUs(1, 4242));
        Assert.False(buffer.AmendGpuUs(99, 1));

        LatencyFrameReport[] taken = buffer.Take();
        Assert.Equal(4242UL, taken[0].GpuUs);
        Assert.Equal(0UL, taken[1].GpuUs);

        // Already drained: the interval stays 0 rather than landing on a later frame.
        Assert.False(buffer.AmendGpuUs(1, 5));
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/RecordingLatencyBackend.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;

/// <summary>
/// A latency backend that only records. Later stages hand it to a device and
/// assert the marker order of a real frame against it, which is the whole point:
/// the seams can be tested without an NVIDIA or AMD GPU present.
/// </summary>
internal sealed class RecordingLatencyBackend : ILatencyBackend
{
    private readonly List<LatencyFrameReport> _reports = new();

    /// <summary>Every (frameId, marker) in the order it was stamped.</summary>
    public List<(ulong FrameId, LatencyMarker Marker)> Markers { get; } = new();

    /// <summary>The frame ids <see cref="Sleep" /> was called with, in order.</summary>
    public List<ulong> Sleeps { get; } = new();

    /// <summary>The frame/present id pairs <see cref="OnPresent" /> was called with.</summary>
    public List<(ulong FrameId, ulong PresentId)> Presents { get; } = new();

    /// <summary>The swapchains handed to <see cref="OnSwapchainCreated" />.</summary>
    public List<SwapchainKHR> Swapchains { get; } = new();

    /// <summary>Every settings object handed to <see cref="Apply" />, in order.</summary>
    public List<LatencySettings> Applied { get; } = new();

    public int SleepCount => Sleeps.Count;

    public int SwapchainCount => Swapchains.Count;

    public int TagSubmitCount { get; private set; }

    /// <summary>What <see cref="Sleep" /> claims to have waited, in microseconds.</summary>
    public ulong SleepDurationUs { get; set; }

    /// <summary>What <see cref="OwnsFrameCap" /> answers.</summary>
    public bool OwnsFrameCapValue { get; set; }

    public LatencyBackendKind Kind { get; set; } = LatencyBackendKind.Native;

    public LatencySettings Settings { get; private set; } = LatencySettings.Disabled;

    public void Apply(in LatencySettings settings)
    {
        Settings = settings;
        Applied.Add(settings);
    }

    public bool OwnsFrameCap => OwnsFrameCapValue;

    public ulong Sleep(ulong frameId)
    {
        Sleeps.Add(frameId);
        return SleepDurationUs;
    }

    public void Marker(ulong frameId, LatencyMarker marker) => Markers.Add((frameId, marker));

    public void OnSwapchainCreated(SwapchainKHR swapchain)
    {
        Swapchains.Add(swapchain);
        SwapchainEvents.Add(swapchain);
    }

    /// <summary>The handles announced, plus a null entry for every retirement, in order.</summary>
    public List<SwapchainKHR> SwapchainEvents { get; } = new();

    /// <summary>How often the swapchain the backend holds was retired or destroyed.</summary>
    public int SwapchainRetirements { get; private set; }

    public void OnSwapchainRetired()
    {
        SwapchainRetirements++;
        SwapchainEvents.Add(default);
    }

    public unsafe void* TagSubmit(ulong frameId, void* pNext)
    {
        TagSubmitCount++;
        return pNext;
    }

    public void OnPresent(ulong frameId, ulong presentId)
    {
        Presents.Add((frameId, presentId));
        _reports.Add(new LatencyFrameReport(frameId, presentId, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    public LatencyFrameReport[] TakeReports()
    {
        LatencyFrameReport[] taken = _reports.ToArray();
        _reports.Clear();
        return taken;
    }

    /// <summary>The markers of one frame, in order.</summary>
    public LatencyMarker[] MarkersOf(ulong frameId)
    {
        var markers = new List<LatencyMarker>();
        foreach ((ulong id, LatencyMarker marker) in Markers)
        {
            if (id == frameId) markers.Add(marker);
        }
        return markers.ToArray();
    }

    public void Dispose()
    {
    }
}
}
