using System;
using System.Collections.Generic;
using System.Diagnostics;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Step 4 of the frame-generation design (ROADMAP, "DLSS frame generation: the
/// design"): the frame ring goes from two slots to three, on its own, before any
/// generation code exists, so the cost of three-deep buffering is measured
/// separately from the cost of generation.
///
/// The one bound that was not a constant is the acquire-semaphore free list:
/// <c>imageCount + 1</c> was derived from "at most FramesInFlight - 1 present
/// submissions are uncompleted when an acquire starts", which holds only while a
/// rendered frame presents once. The derivation tests below drive the free list
/// through the worst case the render loop can produce at a given depth and
/// presents-per-frame, and pin that the derived bound is tight: one fewer
/// semaphore fails.
/// </summary>
public class FramesInFlightTests
{
    private readonly ITestOutputHelper _output;

    public FramesInFlightTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------ the override

    [Fact]
    public void TheEnvironmentOverrideTakesTwoToFourAndFallsBackToTheDefault()
    {
        Assert.Equal(3, FrameRing.DefaultFramesInFlight);

        Assert.Equal(FrameRing.DefaultFramesInFlight, FrameRing.ParseFramesInFlight(null));
        Assert.Equal(FrameRing.DefaultFramesInFlight, FrameRing.ParseFramesInFlight(""));
        Assert.Equal(FrameRing.DefaultFramesInFlight, FrameRing.ParseFramesInFlight("   "));
        Assert.Equal(2, FrameRing.ParseFramesInFlight("2"));
        Assert.Equal(3, FrameRing.ParseFramesInFlight(" 3 "));
        Assert.Equal(4, FrameRing.ParseFramesInFlight("4"));

        // Out of range or unparseable degrades to the shipped configuration.
        Assert.Equal(FrameRing.DefaultFramesInFlight, FrameRing.ParseFramesInFlight("1"));
        Assert.Equal(FrameRing.DefaultFramesInFlight, FrameRing.ParseFramesInFlight("0"));
        Assert.Equal(FrameRing.DefaultFramesInFlight, FrameRing.ParseFramesInFlight("-2"));
        Assert.Equal(FrameRing.DefaultFramesInFlight, FrameRing.ParseFramesInFlight("5"));
        Assert.Equal(FrameRing.DefaultFramesInFlight, FrameRing.ParseFramesInFlight("three"));
        Assert.Equal(FrameRing.DefaultFramesInFlight, FrameRing.ParseFramesInFlight("2.5"));
    }

    // ------------------------------------------- the acquire-semaphore bound

    [Fact]
    public void TheAcquireBoundIsFramesTimesPresentsWithImageCountAsAFloor()
    {
        // The historical value where it still dominates: three or four images,
        // two frames, one present. Nothing gets fewer semaphores than before.
        Assert.Equal(4, AcquireSemaphoreFreeList.CapacityFor(3, PresentPressure.ForFrames(2)));
        Assert.Equal(5, AcquireSemaphoreFreeList.CapacityFor(4, PresentPressure.ForFrames(2)));
        Assert.Equal(4, AcquireSemaphoreFreeList.CapacityFor(3, PresentPressure.ForFrames(3)));

        // Frame generation: two presents per rendered frame at three deep needs
        // six live semaphores, which imageCount + 1 does not cover.
        Assert.Equal(7, AcquireSemaphoreFreeList.CapacityFor(3,
            PresentPressure.ForFrames(3, PresentPressure.GeneratedPlusRealPerFrame)));
        Assert.Equal(7, AcquireSemaphoreFreeList.CapacityFor(4,
            PresentPressure.ForFrames(3, PresentPressure.GeneratedPlusRealPerFrame)));
        Assert.Equal(9, AcquireSemaphoreFreeList.CapacityFor(3,
            PresentPressure.ForFrames(4, PresentPressure.GeneratedPlusRealPerFrame)));

        for (int frames = 2; frames <= FrameRing.MaxFramesInFlight; frames++)
        {
            for (int presents = 1; presents <= 2; presents++)
            {
                var pressure = PresentPressure.ForFrames(frames, presents);
                Assert.Equal(frames * presents, pressure.PeakHeldAcquireSemaphores);
                Assert.True(AcquireSemaphoreFreeList.CapacityFor(3, pressure) > frames * presents,
                    "the capacity must exceed the peak, not merely reach it");
            }
        }
    }

    /// <summary>
    /// The derivation driven, not asserted: the render loop's worst case is a GPU
    /// that has completed exactly frame n - FramesInFlight and nothing newer, which
    /// is what the ring's own pacing wait guarantees and no more. A list holding
    /// exactly <c>FramesInFlight x PresentsPerFrame</c> semaphores survives it for
    /// ever; one semaphore fewer runs out. That is what makes the bound tight
    /// rather than lucky, at every depth and both present counts.
    /// </summary>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    public void TheDerivedBoundIsExactlyWhatTheWorstCaseLoopNeeds(int framesInFlight, int presentsPerFrame)
    {
        int peak = PresentPressure.ForFrames(framesInFlight, presentsPerFrame).PeakHeldAcquireSemaphores;
        Assert.Equal(framesInFlight * presentsPerFrame, peak);

        Assert.Null(RunWorstCaseLoop(peak, framesInFlight, presentsPerFrame));
        string? failure = RunWorstCaseLoop(peak - 1, framesInFlight, presentsPerFrame);
        Assert.NotNull(failure);
        _output.WriteLine($"F={framesInFlight} P={presentsPerFrame}: {peak} semaphores hold, " +
            $"{peak - 1} fail with \"{failure}\"");
    }

    /// <summary>
    /// Drives a free list of <paramref name="capacity" /> semaphores through 200
    /// frames of the worst case and returns the failure message, or null when it
    /// held. Every present submission is left uncompleted until the ring's pacing
    /// wait proves it finished: frame n runs with the Frame counter at the last
    /// value frame n - FramesInFlight signalled.
    /// </summary>
    private static string? RunWorstCaseLoop(int capacity, int framesInFlight, int presentsPerFrame)
    {
        var handles = new ulong[capacity];
        for (int i = 0; i < capacity; i++) handles[i] = 100UL + (ulong)i;
        var list = new AcquireSemaphoreFreeList(handles);

        try
        {
            for (int frame = 0; frame < 200; frame++)
            {
                // What FrameRing.BeginFrame waited for: every submission of the
                // frame that used this slot before, and not one value more.
                int completedFrame = frame - framesInFlight;
                ulong frameCompleted = completedFrame < 0
                    ? 0
                    : (ulong)((completedFrame + 1) * presentsPerFrame);

                for (int present = 0; present < presentsPerFrame; present++)
                {
                    ulong semaphore = list.Take(frameCompleted);
                    ulong presentValue = (ulong)(frame * presentsPerFrame + present + 1);
                    list.ReturnAfter(semaphore, presentValue);
                }
            }
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }

        return null;
    }

    // ------------------------------------------------------------- on the GPU

    /// <summary>
    /// The correctness half of the flip: a long loop with deferred deletions in it
    /// stays clean at three slots as it did at two. Nothing reads back inside the
    /// loop - a readback would drain the pipeline and hide exactly the lifetime bug
    /// a third slot could introduce (a resource retired against frame n destroyed
    /// while frame n + 1 or n + 2 still names it), which is what no single-frame
    /// readback can see.
    /// </summary>
    [SkippableTheory]
    [InlineData(2)]
    [InlineData(3)]
    public void ALongLoopOfDeferredDeletionsWithoutReadbacksStaysCleanAtEveryDepth(int depth)
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? probe), "No usable Vulkan device.");
        probe!.Dispose();

        Assert.Equal(0, RunDeferredDeletionLoop(depth, poison: false, assertClean: true));
    }

    /// <summary>
    /// The same loop under poison mode (fresh images NaN/magenta, fresh buffers
    /// 0xDEADBEEF), where an undefined read caused by a resource recycled too early
    /// becomes loud. Run at both depths and compared, because the question is not
    /// "is poison mode clean" but "does the third slot make it worse".
    ///
    /// It is not clean at either depth: the poison fill of a freshly created image
    /// is reported as WRITE_AFTER_WRITE against a vkCmdClearColorImage on the same
    /// VkImage handle, which the driver recycled from the texture a previous frame
    /// destroyed. The same count at two slots and at three, so it is not this
    /// change's doing; counted here rather than hidden.
    /// </summary>
    [SkippableFact]
    public void PoisonModeReportsNoMoreAtThreeSlotsThanAtTwo()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? probe), "No usable Vulkan device.");
        probe!.Dispose();

        int two = RunDeferredDeletionLoop(2, poison: true, assertClean: false);
        int three = RunDeferredDeletionLoop(3, poison: true, assertClean: false);
        _output.WriteLine($"poison sync-hazard reports: two slots {two}, three slots {three}");
        Assert.True(three <= two,
            $"three slots reported {three} sync hazards under poison where two reported {two}");
    }

    /// <summary>
    /// The loop both GPU correctness tests run: every frame creates a texture and a
    /// framebuffer, clears them and a long-lived target, and deletes the pair again
    /// while the older frames are still in flight. Returns how many synchronization
    /// hazards the layers reported.
    /// </summary>
    private unsafe int RunDeferredDeletionLoop(int depth, bool poison, bool assertClean)
    {
        VulkanDevice device = GpuTest.NewDevice();
        device.FramesInFlightOverride = depth;
        device.ConfigureContextOptions += options => options.Poison = poison;
        Assert.True(device.Initialize(IntPtr.Zero, 0, 0, out string failure), failure);
        using (device)
        {
            VulkanDevice seam = device;
            Assert.Equal(depth, device.FramesInFlightForTests);

            const int size = 8;
            const int frames = 150;

            int target = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int targetFramebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(targetFramebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(targetFramebuffer, 1);

            int peakPending = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                seam.BeginFrame();
                peakPending = Math.Max(peakPending, device.PendingRetirementsForTests);

                // Created and destroyed inside one frame, while two older frames
                // are still in flight: the retire queue is the only thing keeping
                // this alive long enough.
                int scratch = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                int scratchFramebuffer = seam.CreateFramebuffer(size, size);
                seam.AttachTexture(scratchFramebuffer, EnumFramebufferAttachment.ColorAttachment0, scratch, 0);
                seam.SetDrawBuffers(scratchFramebuffer, 1);
                seam.BindFramebuffer(scratchFramebuffer);
                seam.ClearColor(0, 0.5f, 0.25f, 0.125f, 1f);

                seam.BindFramebuffer(targetFramebuffer);
                seam.ClearColor(0, frame / 255f, 0.2f, 0.25f, 1f);

                seam.DeleteFramebuffer(scratchFramebuffer);
                seam.DeleteTexture(scratch);
                seam.Present();
            }

            // Only after the loop, so nothing in it drained the pipeline.
            device.TimelineForTests.WaitForFrame(device.TimelineForTests.FrameSignalled, WaitSite.DeviceWaitIdle);
            seam.BeginFrame();
            seam.BindFramebuffer(targetFramebuffer);
            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            Assert.Equal(new byte[] { frames - 1, 51, 64, 255 }, pixels[0..4]);
            seam.Present();

            _output.WriteLine($"{depth} slots, poison {(poison ? "on" : "off")}: {frames} frames, " +
                $"peak retire queue {peakPending}");
            // A bounded queue, not one that grows with the loop.
            Assert.True(peakPending <= depth * 4, $"retire queue grew to {peakPending}");

            int hazards = 0;
            foreach (string message in GpuTest.MessagesOf(seam))
            {
                if (message.Contains("SYNC-HAZARD", StringComparison.Ordinal)) hazards++;
            }
            if (assertClean) GpuTest.AssertClean(seam);
            return hazards;
        }
    }

    /// <summary>
    /// The measurement the design asks for: what three-deep buffering costs in
    /// memory and in pacing, taken before any frame-generation code exists, so the
    /// two can be told apart later. Both depths run the same GPU-bound loop in the
    /// same process; the numbers are printed (the suite has no baseline to gate
    /// against, and one machine's absolute milliseconds are not a threshold).
    ///
    /// Multi-frame with no readback inside the loop: a readback per frame would
    /// serialise CPU and GPU and erase the difference between two and three slots,
    /// which is the whole quantity under test.
    /// </summary>
    [SkippableFact]
    public void TwoAgainstThreeSlotsInMemoryAndPacing()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? probe), "No usable Vulkan device.");
        probe!.Dispose();

        Measurement two = MeasureDepth(2);
        Measurement three = MeasureDepth(3);

        foreach (Measurement measurement in new[] { two, three })
        {
            _output.WriteLine($"frames_in_flight={measurement.FramesInFlight} " +
                $"p50_ms={measurement.Pacing.P50:F3} p95_ms={measurement.Pacing.P95:F3} " +
                $"p99_ms={measurement.Pacing.P99:F3} stddev_ms={measurement.Pacing.StdDev:F3} " +
                $"samples={measurement.Pacing.Samples}");
            _output.WriteLine("  " + VulkanStats.FormatPacingLine(measurement.Pacing, measurement.FramesInFlight));
            _output.WriteLine("  " + VulkanAllocator.FormatMemoryLine(measurement.Memory));
            _output.WriteLine($"  heap_used_bytes={measurement.HeapUsed} rebar_used_bytes={measurement.Memory.ReBarUsed}");
        }

        long heapDelta = (long)three.HeapUsed - (long)two.HeapUsed;
        _output.WriteLine($"three minus two: heap {heapDelta / (1024.0 * 1024.0):F1} MiB, " +
            $"p50 {three.Pacing.P50 - two.Pacing.P50:F3} ms, p99 {three.Pacing.P99 - two.Pacing.P99:F3} ms, " +
            $"stddev {three.Pacing.StdDev - two.Pacing.StdDev:F3} ms");

        Assert.Equal(2, two.FramesInFlight);
        Assert.Equal(3, three.FramesInFlight);
        // The third slot is a third uniform region (16 MiB) plus a third staging
        // region; the deeper ring can only cost memory, never save it.
        Assert.True(heapDelta >= 8 * 1024 * 1024,
            $"three slots used {heapDelta} more bytes than two, expected at least the extra uniform region");
    }

    private const string FullscreenVertex = """
        #version 330 core
        out vec2 uv;
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
            uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
        }
        """;

    private readonly record struct Measurement(
        int FramesInFlight, FramePacingSnapshot Pacing, MemorySnapshot Memory, ulong HeapUsed);

    /// <summary>
    /// A fragment shader heavy enough that the loop below is GPU-bound rather than
    /// a measurement of the CPU. Clears alone are not: a frame of 128 full-target
    /// clears cost 67 us, because nothing consumes them and the frame graph folds
    /// them away - and a CPU-bound loop cannot show what a deeper ring does, since
    /// the whole point of a third slot is that the CPU may run further ahead.
    /// </summary>
    private const string HeavyFragment = """
        #version 330 core
        in vec2 uv;
        out vec4 outColor;
        void main(void)
        {
            float accumulated = 0.0;
            vec2 point = uv;
            for (int i = 0; i < 192; i++)
            {
                point = vec2(point.x * point.x - point.y * point.y, 2.0 * point.x * point.y) + uv;
                accumulated += sin(point.x * 7.0 + float(i)) * cos(point.y * 11.0 - float(i));
            }
            outColor = vec4(fract(accumulated), uv, 1.0);
        }
        """;

    /// <summary>
    /// One depth's numbers: a GPU-bound loop of full-target draws, timed frame
    /// start to frame start exactly as <c>VulkanDevice.BeginFrame</c> times it,
    /// with the first frames discarded as warm-up and nothing read back inside it.
    /// </summary>
    private Measurement MeasureDepth(int framesInFlight)
    {
        const int size = 1024;
        const int drawsPerFrame = 12;
        const int warmup = 30;
        const int measured = 120;

        VulkanDevice device = GpuTest.NewDevice();
        device.FramesInFlightOverride = framesInFlight;
        Assert.True(device.Initialize(IntPtr.Zero, 0, 0, out string failure), failure);
        using (device)
        {
            VulkanDevice seam = device;
            Assert.Equal(framesInFlight, device.FramesInFlightForTests);

            int program = VulkanDeviceIntegrationTests.LinkProgram(device, FullscreenVertex, HeavyFragment, "fif-load");
            int texture = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetDrawBuffers(framebuffer, 1);

            var ring = new FrameIntervalRing(FrameIntervalRing.DefaultCapacity);
            long previous = 0;
            for (int frame = 0; frame < warmup + measured; frame++)
            {
                long start = Stopwatch.GetTimestamp();
                if (previous != 0 && frame > warmup)
                {
                    ring.Add((start - previous) * 1000.0 / Stopwatch.Frequency);
                }
                previous = start;

                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                seam.UseProgram(program);
                seam.SetViewport(0, 0, size, size);
                seam.SetDepthTest(false);
                seam.SetCullFace(false);
                seam.SetBlend(false, EnumBlendMode.Standard);
                seam.ClearColor(0, 0f, 0f, 0f, 1f);
                for (int draw = 0; draw < drawsPerFrame; draw++) seam.DrawFullscreenTriangle();
                seam.Present();
            }

            MemorySnapshot memory = VulkanStats.MemorySource?.Snapshot() ?? default;
            ulong heapUsed = 0;
            if (memory.HeapUsed != null)
            {
                foreach (ulong used in memory.HeapUsed) heapUsed += used;
            }

            GpuTest.AssertClean(seam);
            return new Measurement(framesInFlight, ring.Snapshot(), memory, heapUsed);
        }
    }
}
