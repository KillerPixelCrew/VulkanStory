using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Platform;
using Optimum.Render.Vulkan.Present;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The paced present's spacing pacer (<see cref="FramePacer" />), the paced frame cap
/// (<see cref="PacedFrameCap" />) and the display refresh source. Deterministic: a fake
/// clock and a discrete-event model of the present thread, no device, no sleeping.
/// </summary>
public class PacerTests
{
    private const long Frame60Us = 16_667;
    private const long Origin = 1_000_000;

    private readonly ITestOutputHelper _output;

    public PacerTests(ITestOutputHelper output) => _output = output;

    private sealed class FakeClock
    {
        public long Now = Origin;
        public long Read() => Now;
    }

    private readonly record struct PairTiming(long GeneratedUs, long RealUs, bool Queued, bool Immediate)
    {
        public long SpacingUs => RealUs - GeneratedUs;
    }

    /// <summary>
    /// The present thread as the design has it: a pair is taken when it has arrived and the
    /// thread is free, its generated frame presented at once, its real frame at the pacer's
    /// target (or at once when the target has passed). Presents cost nothing, so every
    /// spacing is the pacer's decision alone.
    /// </summary>
    private static List<PairTiming> Simulate(FramePacer pacer, FakeClock clock, long[] arrivalsUs,
        int presentStallAtPair = -1, long presentStallUs = 0)
    {
        var pairs = new List<PairTiming>(arrivalsUs.Length);
        long t = 0;
        for (int i = 0; i < arrivalsUs.Length; i++)
        {
            t = Math.Max(t, arrivalsUs[i]);
            if (i == presentStallAtPair) t += presentStallUs;

            clock.Now = t;
            pacer.NotePresented(PacedPresentKind.Generated, t);
            long generated = t;

            bool queued = i + 1 < arrivalsUs.Length && arrivalsUs[i + 1] <= t;
            long target = pacer.RealPresentTargetUs(generated, queued);
            bool immediate = target <= clock.Now;
            if (!immediate) t = target;

            clock.Now = t;
            pacer.NotePresented(PacedPresentKind.Real, t);
            pairs.Add(new PairTiming(generated, t, queued, immediate));
        }
        return pairs;
    }

    private static long[] Arrivals(int count, Func<int, long> gapBefore)
    {
        var arrivals = new long[count];
        long t = Origin;
        for (int i = 0; i < count; i++)
        {
            if (i > 0) t += gapBefore(i);
            arrivals[i] = t;
        }
        return arrivals;
    }

    private static long[] Steady(int count, long intervalUs) => Arrivals(count, _ => intervalUs);

    private static double StdDev(IReadOnlyList<double> values)
    {
        double mean = 0;
        foreach (double v in values) mean += v;
        mean /= values.Count;
        double squares = 0;
        foreach (double v in values) squares += (v - mean) * (v - mean);
        return Math.Sqrt(squares / values.Count);
    }

    // ------------------------------------------------------------------ spacing

    [Fact]
    public void SteadyRendersPutTheRealFrameHalfAnIntervalAfterTheGenerated()
    {
        var clock = new FakeClock();
        var ring = new PresentIntervalRing();
        var pacer = new FramePacer(clock.Read, FramePacer.DefaultSeedIntervalUs, ring);

        List<PairTiming> pairs = Simulate(pacer, clock, Steady(240, Frame60Us));

        foreach (PairTiming pair in pairs)
        {
            Assert.False(pair.Immediate);
            Assert.InRange(pair.SpacingUs, 8_333 - 100, 8_333 + 100);
        }

        PresentIntervalSnapshot spacing = ring.Snapshot();
        _output.WriteLine("gen->real {0:F3} ms, real->gen {1:F3} ms, ratio {2:F3}",
            spacing.GeneratedToReal.Mean, spacing.RealToGenerated.Mean, spacing.Ratio);
        Assert.InRange(spacing.GeneratedToReal.Mean, 8.233, 8.433);
        Assert.InRange(spacing.RealToGenerated.Mean, 8.233, 8.433);
        Assert.InRange(spacing.Ratio, 0.98, 1.02);
        Assert.Equal(0, spacing.Unpaired);
    }

    [Fact]
    public void TheFirstPairIsHeldHalfTheSeedAndTheRateIsLearnedWithinThirtyPairs()
    {
        // No history: the first real frame is held half the seed (one 60 Hz refresh), never
        // presented back to back with its generated frame. Renders at 100 fps then pull the
        // average down at most 1/8 of the gap per pair: within 30 pairs, 5 ms +- 0.1.
        const int recoveryPairs = 30;
        var clock = new FakeClock();
        var pacer = new FramePacer(clock.Read);

        List<PairTiming> pairs = Simulate(pacer, clock, Steady(120, 10_000));

        Assert.Equal(8_334, pairs[0].SpacingUs);
        foreach (PairTiming pair in pairs)
        {
            Assert.False(pair.Immediate);
            Assert.True(pair.SpacingUs >= 4_000, "spacing " + pair.SpacingUs);
        }
        for (int i = recoveryPairs; i < pairs.Count; i++) Assert.InRange(pairs[i].SpacingUs, 4_900, 5_100);
    }

    [Fact]
    public void RenderJitterLeavesTheSpacingSteadierThanTheRenders()
    {
        var random = new Random(20260913);
        long[] arrivals = Arrivals(600, _ => Frame60Us + random.Next(-2_000, 2_001));
        var clock = new FakeClock();
        var pacer = new FramePacer(clock.Read);

        List<PairTiming> pairs = Simulate(pacer, clock, arrivals);

        var input = new List<double>();
        var spacing = new List<double>();
        for (int i = 20; i < pairs.Count; i++)
        {
            input.Add(arrivals[i] - arrivals[i - 1]);
            spacing.Add(pairs[i].SpacingUs);
            Assert.False(pairs[i].Immediate);
        }

        double inputStdDev = StdDev(input);
        double spacingStdDev = StdDev(spacing);
        _output.WriteLine("render interval stddev {0:F0} us, generated->real stddev {1:F0} us", inputStdDev, spacingStdDev);
        Assert.True(spacingStdDev < inputStdDev, spacingStdDev + " >= " + inputStdDev);
        // The EMA predicts sqrt(w / (2 - w)) / 2 = 0.13x; allow twice that.
        Assert.True(spacingStdDev < 0.26 * inputStdDev, spacingStdDev + " vs " + inputStdDev);
    }

    [Fact]
    public void ARenderStallOfOneHundredMillisecondsNeitherBurstsNorDisturbsTheSpacing()
    {
        // N = 0: the pair right after the stall is already spaced 8.33 ms, because the
        // stall-sized interval never enters the average.
        const int stallBefore = 60;
        long[] arrivals = Arrivals(180, i => i == stallBefore ? 100_000 : Frame60Us);
        var clock = new FakeClock();
        var pacer = new FramePacer(clock.Read);

        List<PairTiming> pairs = Simulate(pacer, clock, arrivals);

        int immediate = 0;
        foreach (PairTiming pair in pairs) if (pair.Immediate) immediate++;
        Assert.True(immediate <= 1, immediate + " immediate real presents");
        for (int i = stallBefore; i < pairs.Count; i++) Assert.InRange(pairs[i].SpacingUs, 8_233, 8_433);
        Assert.Equal(1, pacer.StallsRejected);
        Assert.InRange(pacer.SmoothedIntervalUs, Frame60Us - 200, Frame60Us + 200);
    }

    [Fact]
    public void APresentThreadStallOfOneHundredMillisecondsDrainsWithOneImmediatePresentAndRecovers()
    {
        // The present thread blocks 100 ms, six pairs pile up. The first real frame goes out
        // at once, every further backlogged one a quarter interval after its generated frame
        // (so no two presents are back to back after the first), the backlog gains 12.5 ms per
        // pair and is gone within 10 pairs, and the spacing is back to 8.33 ms +- 0.1 on the
        // pair that follows: recovery N = 10 pairs.
        const int stallAt = 60;
        const int recoveryPairs = 10;
        var clock = new FakeClock();
        var pacer = new FramePacer(clock.Read);

        List<PairTiming> pairs = Simulate(pacer, clock, Steady(180, Frame60Us), stallAt, 100_000);

        int immediate = 0;
        int lastQueued = -1;
        for (int i = 0; i < pairs.Count; i++)
        {
            if (pairs[i].Immediate) immediate++;
            if (pairs[i].Queued) lastQueued = i;
            _output.WriteLine("pair {0}: spacing {1} us queued {2} immediate {3}", i, pairs[i].SpacingUs,
                pairs[i].Queued, pairs[i].Immediate);
        }

        Assert.Equal(1, immediate);
        Assert.True(pairs[stallAt].Immediate);
        for (int i = stallAt + 1; i <= lastQueued; i++) Assert.InRange(pairs[i].SpacingUs, 4_000, 4_300);
        Assert.InRange(lastQueued, stallAt, stallAt + recoveryPairs - 1);
        for (int i = stallAt + recoveryPairs; i < pairs.Count; i++) Assert.InRange(pairs[i].SpacingUs, 8_233, 8_433);
    }

    [Fact]
    public void AQueuedPairReleasesTheRealFrameImmediately()
    {
        var clock = new FakeClock();
        var pacer = new FramePacer(clock.Read);
        Simulate(pacer, clock, Steady(30, Frame60Us));
        long smoothed = pacer.SmoothedIntervalUs;

        long generated = clock.Now + Frame60Us;
        clock.Now = generated + 50;
        Assert.True(pacer.RealPresentTargetUs(generated, nextPairQueued: true) <= clock.Now);
        Assert.Equal(1, pacer.ImmediateRealPresents);

        // The queue keeps growing: still well under the half interval, so it drains.
        for (int i = 0; i < 5; i++)
        {
            generated += 1_000;
            clock.Now = generated;
            long target = pacer.RealPresentTargetUs(generated, nextPairQueued: true);
            Assert.InRange(target - generated, 1, smoothed / 4 + 1);
        }

        // Caught up: back to half an interval.
        generated += 1_000;
        Assert.Equal(generated + (long)Math.Round(smoothed / 2.0), pacer.RealPresentTargetUs(generated, false));
    }

    [Fact]
    public void ThreeStallSizedIntervalsInARowAreANewFrameRate()
    {
        // 60 -> 15 fps: the third 66.7 ms interval reseeds the average.
        const int dropAt = 40;
        long[] arrivals = Arrivals(80, i => i < dropAt ? Frame60Us : 66_667);
        var clock = new FakeClock();
        var pacer = new FramePacer(clock.Read);

        List<PairTiming> pairs = Simulate(pacer, clock, arrivals);

        Assert.Equal(2, pacer.StallsRejected);
        for (int i = dropAt + FramePacer.StallsBeforeReseed; i < pairs.Count; i++)
            Assert.InRange(pairs[i].SpacingUs, 33_233, 33_433);
    }

    [Fact]
    public void SeedAppliesOnlyBeforeAnythingWasMeasuredAndTheClockGoingBackIsIgnored()
    {
        var clock = new FakeClock();
        var pacer = new FramePacer(clock.Read);
        pacer.SeedIntervalUs(6_944); // 144 Hz
        Assert.Equal(6_944, pacer.SmoothedIntervalUs);
        Assert.Equal(Origin + 3_472, pacer.RealPresentTargetUs(Origin, false));

        pacer.NotePresented(PacedPresentKind.Generated, Origin);
        pacer.NotePresented(PacedPresentKind.Generated, Origin + 7_000);
        pacer.SeedIntervalUs(50_000);
        Assert.InRange(pacer.SmoothedIntervalUs, 6_944, 7_000);

        pacer.NotePresented(PacedPresentKind.Generated, Origin + 1_000);
        Assert.InRange(pacer.SmoothedIntervalUs, 6_944, 7_000);

        Assert.Equal(FramePacer.MaxIntervalUs, new FramePacer(clock.Read, long.MaxValue).SmoothedIntervalUs);
        Assert.Equal(FramePacer.MinIntervalUs, new FramePacer(clock.Read, 0).SmoothedIntervalUs);
    }

    [Fact]
    public async Task PresentThreadAndAReaderRacingThePacerNeverSeeAnIntervalOutOfBounds()
    {
        // The present thread drives both interface methods while another thread (the frame-cap
        // wiring) reads the smoothed interval. Bounded: a deadlock fails instead of hanging.
        var clock = new FakeClock();
        var pacer = new FramePacer(clock.Read, FramePacer.DefaultSeedIntervalUs, new PresentIntervalRing());
        using var stop = new CancellationTokenSource();
        long outOfBounds = 0;
        long reads = 0;

        Task presenter = Task.Run(() =>
        {
            var random = new Random(7);
            long t = Origin;
            for (int i = 0; i < 200_000; i++)
            {
                t += random.Next(1, 40_000);
                pacer.NotePresented(PacedPresentKind.Generated, t);
                long target = pacer.RealPresentTargetUs(t, random.Next(8) == 0);
                t = Math.Max(t, target);
                pacer.NotePresented(PacedPresentKind.Real, t);
            }
        });
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                long smoothed = pacer.SmoothedIntervalUs;
                if (smoothed < FramePacer.MinIntervalUs || smoothed > FramePacer.MaxIntervalUs)
                    Interlocked.Increment(ref outOfBounds);
                Interlocked.Increment(ref reads);
            }
        });

        bool finished = await Task.WhenAny(presenter, Task.Delay(TimeSpan.FromSeconds(30))) == presenter;
        stop.Cancel();
        Assert.True(finished, "the presenter did not finish within 30 s (deadlock?)");
        Assert.True(await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(10))) == reader,
            "the reader did not stop within 10 s");
        await presenter;
        Assert.Equal(0, Interlocked.Read(ref outOfBounds));
        Assert.True(Interlocked.Read(ref reads) > 0);
    }

    // ---------------------------------------------------------------- frame cap

    [Theory]
    // Frame generation off: exactly the user's cap, whatever vsync and refresh say.
    [InlineData(false, true, 144.0, 0ul, 0ul)]
    [InlineData(false, true, 60.0, 8_333ul, 8_333ul)]
    [InlineData(false, false, 60.0, 16_667ul, 16_667ul)]
    [InlineData(false, false, 0.0, 0ul, 0ul)]
    // Vsync off (IMMEDIATE): the user's cap unchanged; the pacer owns the spacing.
    [InlineData(true, false, 144.0, 6_944ul, 6_944ul)]
    [InlineData(true, false, 60.0, 0ul, 0ul)]
    // Frame generation and vsync (FIFO): two refresh intervals per rendered frame.
    [InlineData(true, true, 60.0, 0ul, 33_333ul)]
    [InlineData(true, true, 144.0, 0ul, 13_889ul)]
    [InlineData(true, true, 59.94, 0ul, 33_367ul)]
    [InlineData(true, true, 60.0, 20_000ul, 33_333ul)]
    // ... unless the user asked for fewer frames than that.
    [InlineData(true, true, 60.0, 50_000ul, 50_000ul)]
    // Unknown refresh is 60 Hz.
    [InlineData(true, true, 0.0, 0ul, 33_333ul)]
    [InlineData(true, true, -1.0, 0ul, 33_333ul)]
    [InlineData(true, true, double.NaN, 0ul, 33_333ul)]
    [InlineData(true, true, double.PositiveInfinity, 0ul, 33_333ul)]
    public void FrameCapPolicyRow(bool frameGeneration, bool vsync, double refreshHz, ulong userCapUs, ulong expected)
    {
        Assert.Equal(expected, PacedFrameCap.MinimumIntervalUs(frameGeneration, vsync, refreshHz, userCapUs));
    }

    [Fact]
    public void WithFrameGenerationOffTheCapIsTodaysValue()
    {
        foreach (int maxFps in new[] { 0, -5, 10, 30, 60, 75, 144, 241 })
        {
            ulong today = VulkanClientPlatform.FrameCapIntervalUs(maxFps);
            Assert.Equal(today, PacedFrameCap.MinimumIntervalUs(false, true, 144, today));
            Assert.Equal(today, PacedFrameCap.MinimumIntervalUs(false, false, 60, today));
        }
    }

    // ------------------------------------------------------------ refresh rate

    private sealed class FixedRefresh : IDisplayRefreshSource
    {
        public int Hz;
        public int Reads;
        public int CurrentRefreshHz()
        {
            Reads++;
            return Hz;
        }
    }

    [Fact]
    public void UnknownRefreshResolvesToSixtyHertzWithOneLogLine()
    {
        var lines = new List<string>();
        bool logged = false;
        Assert.Equal(144, DisplayRefresh.Resolve(144, ref logged, lines.Add));
        Assert.Empty(lines);

        foreach (int reported in new[] { 0, -1, DisplayRefresh.MaxPlausibleHz + 1, 0 })
            Assert.Equal(DisplayRefresh.FallbackHz, DisplayRefresh.Resolve(reported, ref logged, lines.Add));
        Assert.Single(lines);
        Assert.Contains("60 Hz", lines[0]);
        Assert.StartsWith("[Optimum]", lines[0]);

        Assert.Equal(DisplayRefresh.MaxPlausibleHz, DisplayRefresh.Resolve(DisplayRefresh.MaxPlausibleHz, ref logged, lines.Add));
    }

    [Fact]
    public void ThePlatformReadsTheInjectedSourceAndFallsBackWithoutAWindow()
    {
        var source = new FixedRefresh { Hz = 165 };
        var platform = new VulkanClientPlatform(null!) { DisplayRefreshSourceOverride = source };
        Assert.Equal(DisplayRefresh.FallbackHz, platform.DisplayRefreshHz);

        platform.ReadDisplayRefreshRate();
        Assert.Equal(165, platform.DisplayRefreshHz);

        source.Hz = 0;
        platform.ReadDisplayRefreshRate();
        platform.ReadDisplayRefreshRate();
        Assert.Equal(DisplayRefresh.FallbackHz, platform.DisplayRefreshHz);
        Assert.Equal(3, source.Reads);

        // No override and no window: unknown, not a throw.
        var bare = new VulkanClientPlatform(null!);
        bare.ReadDisplayRefreshRate();
        Assert.Equal(DisplayRefresh.FallbackHz, bare.DisplayRefreshHz);
        Assert.Equal(0, WindowMonitorRefreshSource.Read(null));
    }

    [Fact]
    public void TheRefreshRateIsReadAtBringUpAndOnResize()
    {
        string platformDir = Path.Combine(ShaderCorpus.RepositoryRoot, "Optimum.Render.Vulkan", "Platform");
        string bringUp = File.ReadAllText(Path.Combine(platformDir, "VulkanClientPlatform.cs"));
        string frame = File.ReadAllText(Path.Combine(platformDir, "VulkanClientPlatform.Frame.cs"));

        int install = bringUp.IndexOf("this.device = device;", StringComparison.Ordinal);
        Assert.True(install > 0);
        Assert.True(bringUp.IndexOf("ReadDisplayRefreshRate();", install, StringComparison.Ordinal) > install,
            "InitializeGraphics must read the refresh rate once the device is installed");

        int resize = frame.IndexOf("public override void OnWindowSizeChanged", StringComparison.Ordinal);
        Assert.True(resize > 0);
        int body = frame.IndexOf("device.Resize(width, height);", resize, StringComparison.Ordinal);
        int read = frame.IndexOf("ReadDisplayRefreshRate();", resize, StringComparison.Ordinal);
        Assert.InRange(read, resize, body);

        // The source reads the window's monitor through OpenTK, not a hard-coded rate.
        string source = File.ReadAllText(Path.Combine(platformDir, "DisplayRefresh.cs"));
        Assert.Contains("Monitors.GetMonitorFromWindow(window)", source);
        Assert.Contains("CurrentVideoMode.RefreshRate", source);
    }
}
