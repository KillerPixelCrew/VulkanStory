// Source: Optimum.Render.Vulkan.Tests/FrameTimelinePacingTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Phase 1B step 1 through the seam: the Frame timeline paces the frame ring and
/// keys every deferred destruction.
/// </summary>
public class FrameTimelinePacingTests
{
    private readonly ITestOutputHelper _output;

    public FrameTimelinePacingTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A hundred presented frames, each of which renders into a texture and a
    /// framebuffer it creates, then deletes both (and a mesh) before presenting.
    /// The frame's own command buffer still names the texture, so destroying it
    /// before the timeline passed that frame is a validation error (image in use
    /// by a pending command buffer). Each frame start is exactly one pacing wait,
    /// nothing else in the loop waits, and the retire queue drains instead of growing.
    /// </summary>
    [SkippableFact]
    public unsafe void HundredFramesWithDeferredDeletesPaceOnceEachAndStayClean()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 4;
            const int frames = 100;

            int target = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int targetFramebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(targetFramebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(targetFramebuffer, 1);

            // Warm-up frame outside the counted window.
            seam.BeginFrame();
            seam.Present();

            long pacingBefore = VulkanStats.WaitCount(WaitSite.FramePacing);
            long[] othersBefore = OtherWaits();
            ulong signalledBefore = device!.TimelineForTests.FrameSignalled;
            int peakPending = 0;

            for (int frame = 0; frame < frames; frame++)
            {
                seam.BeginFrame();
                peakPending = Math.Max(peakPending, device.PendingRetirementsForTests);

                int scratch = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                int scratchFramebuffer = seam.CreateFramebuffer(size, size);
                seam.AttachTexture(scratchFramebuffer, EnumFramebufferAttachment.ColorAttachment0, scratch, 0);
                seam.SetDrawBuffers(scratchFramebuffer, 1);
                seam.BindFramebuffer(scratchFramebuffer);
                seam.ClearColor(0, 1f, 0f, 0f, 1f);

                int mesh = seam.CreateMesh(new MeshData(4, 6)
                {
                    xyz = new[] { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f },
                    VerticesCount = 4,
                    Indices = new[] { 0, 1, 2, 0, 2, 3 },
                    IndicesCount = 6,
                    mode = EnumDrawMode.Triangles,
                }, true);

                seam.BindFramebuffer(targetFramebuffer);
                // Exact in 8 bits (x.5 rounds either way): 0.2 -> 51, 0.25 -> 64.
                seam.ClearColor(0, frame / 255f, 0.2f, 0.25f, 1f);

                seam.DeleteMesh(mesh);
                seam.DeleteFramebuffer(scratchFramebuffer);
                seam.DeleteTexture(scratch);

                seam.Present();
            }

            long pacingDelta = VulkanStats.WaitCount(WaitSite.FramePacing) - pacingBefore;
            long[] othersAfter = OtherWaits();
            ulong signalledDelta = device.TimelineForTests.FrameSignalled - signalledBefore;
            _output.WriteLine($"pacing waits {pacingDelta}, frames signalled {signalledDelta}, peak pending {peakPending}");

            Assert.Equal(frames, pacingDelta);
            Assert.Equal((ulong)frames, signalledDelta);
            for (int i = 0; i < OtherSites.Length; i++)
            {
                long delta = othersAfter[i] - othersBefore[i];
                // A frame submit is counted at its own site; it is not a pacing wait.
                long expected = OtherSites[i] == WaitSite.QueueSubmit ? frames : 0;
                Assert.True(delta == expected,
                    $"{VulkanStats.WaitSiteTokens[(int)OtherSites[i]]}: {delta} waits in the loop, expected {expected}");
            }

            // Two frames in flight: at a frame start at most the last two frames'
            // deletions (texture, mesh, freed descriptor sets) can still be pending.
            Assert.True(peakPending <= 3 * 3, $"retire queue grew to {peakPending}");

            // Once the timeline passed the last frame, the next frame start frees everything.
            device.TimelineForTests.WaitForFrame(device.TimelineForTests.FrameSignalled, WaitSite.DeviceWaitIdle);
            seam.BeginFrame();
            Assert.Equal(0, device.PendingRetirementsForTests);

            seam.BindFramebuffer(targetFramebuffer);
            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            Assert.Equal(new byte[] { 99, 51, 64, 255 }, pixels[0..4]);
            seam.Present();

            GpuTest.AssertClean(seam);
        }
    }

    private static readonly WaitSite[] OtherSites =
    {
        WaitSite.UploadSubmit, WaitSite.FlushFrame, WaitSite.DeviceWaitIdle, WaitSite.Readback,
        WaitSite.OcclusionQuery, WaitSite.SwapchainAcquire, WaitSite.Present, WaitSite.QueueSubmit,
    };

    private static long[] OtherWaits()
    {
        var counts = new long[OtherSites.Length];
        for (int i = 0; i < OtherSites.Length; i++) counts[i] = VulkanStats.WaitCount(OtherSites[i]);
        return counts;
    }

    /// <summary>
    /// Frame values are handed out in order, one per submitted frame including
    /// mid-frame flushes, and the timeline counter reaches the last one.
    /// </summary>
    [SkippableFact]
    public unsafe void EverySubmittedFrameSignalsTheNextFrameValue()
    {
        Skip.IfNot(GpuTest.TryCreateContext(_output, null, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var ring = new FrameRing(context!, framesInFlight: 2, uniformRingSize: 1 << 20);
            for (ulong frame = 1; frame <= 7; frame++)
            {
                FrameSlot slot = ring.BeginFrame();
                Assert.Equal(frame, slot.FrameValue);
                Assert.Equal(frame, ring.Timeline.FrameRecorded);
                ring.EndFrame();
                Assert.Equal(frame, ring.Timeline.FrameSignalled);
            }

            ring.Timeline.WaitForFrame(7, WaitSite.DeviceWaitIdle);
            Assert.Equal(7UL, ring.Timeline.FrameCompleted);
            // Nothing ever signals Transfer yet.
            Assert.Equal(0UL, ring.Timeline.TransferCompleted);

            VulkanStats.WaitDeviceIdle(context!.Api, context.Device);
        }
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/PresentDecouplingTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Phase 1B step 4: the CPU blocks on the acquire only after the frame is in
/// flight. An injected acquire delay (a compositor holding images back) must not
/// lengthen frame recording, the frame must reach the queue before the acquire
/// starts, and the GPU must be able to finish the frame while the CPU still
/// waits for the image.
/// </summary>
public class PresentDecouplingTests
{
    private const int Width = 256;
    private const int Height = 192;
    private const int Frames = 24;
    private const int Warmup = 4;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(60);

    private readonly ITestOutputHelper _output;

    public PresentDecouplingTests(ITestOutputHelper output) => _output = output;

    private sealed class Measurements
    {
        public readonly List<double> RecordingMs = new();
        public readonly List<double> SubmitAfterPresentEntryMs = new();
        public readonly List<double> AcquireAfterSubmitMs = new();
        public int RenderCompletedAtAcquire;
        public int Presented;
        public int Samples;
        public long DeviceIdleWaits;
        public PipelineStageFlags AcquireStage;
    }

    [SkippableFact]
    public unsafe void RecordingTimeDoesNotGrowWithTheInjectedAcquireDelay()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window), "No usable window system.");

        try
        {
            Measurements baseline = Run((IntPtr)window, TimeSpan.Zero);
            Measurements delayed = Run((IntPtr)window, Delay);

            double baselineRecording = Median(baseline.RecordingMs);
            double delayedRecording = Median(delayed.RecordingMs);
            _output.WriteLine($"recording median: no delay {baselineRecording:F2} ms, {Delay.TotalMilliseconds} ms delay {delayedRecording:F2} ms");
            _output.WriteLine($"acquire after frame submit median: no delay {Median(baseline.AcquireAfterSubmitMs):F2} ms, delayed {Median(delayed.AcquireAfterSubmitMs):F2} ms");
            _output.WriteLine($"frame finished on the GPU before the acquire returned: {delayed.RenderCompletedAtAcquire}/{delayed.Samples}");

            Assert.True(delayedRecording < baselineRecording + Delay.TotalMilliseconds / 4,
                $"recording grew with the acquire delay: {baselineRecording:F2} -> {delayedRecording:F2} ms");

            foreach (Measurements run in new[] { baseline, delayed })
            {
                Assert.Equal(run.Samples, run.Presented);
                Assert.Equal(0, run.DeviceIdleWaits);
                Assert.Equal(PipelineStageFlags.TransferBit, run.AcquireStage);
                Assert.NotEqual(PipelineStageFlags.AllCommandsBit, run.AcquireStage);
            }

            // The frame is submitted before the acquire starts, so the delay lands
            // between the two, never before the frame submission.
            foreach (double ms in delayed.SubmitAfterPresentEntryMs)
            {
                Assert.True(ms < Delay.TotalMilliseconds / 3, $"the frame reached the queue {ms:F2} ms into Present");
            }
            foreach (double ms in delayed.AcquireAfterSubmitMs)
            {
                Assert.True(ms >= Delay.TotalMilliseconds * 0.9, $"the acquire returned {ms:F2} ms after the frame submit");
            }

            // With the frame already queued, a 256x192 frame finishes well inside
            // the delay. Two stragglers are tolerated for a busy machine.
            Assert.True(delayed.RenderCompletedAtAcquire >= delayed.Samples - 2,
                $"the GPU finished the frame during the acquire in only {delayed.RenderCompletedAtAcquire} of {delayed.Samples} frames");
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    private Measurements Run(IntPtr window, TimeSpan delay)
    {
        VulkanDevice device = GpuTest.NewDevice();
        Action<VulkanContextOptions>? suite = device.ConfigureContextOptions;
        device.ConfigureContextOptions = options =>
        {
            suite?.Invoke(options);
            options.AcquireDelayForTests = delay;
        };

        if (!device.Initialize(window, Width, Height, out string failureReason))
        {
            device.Dispose();
            Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
        }

        var result = new Measurements();
        using (device)
        {
            VulkanDevice seam = device;
            int programId = SwapchainTests.LinkFullscreenProgram(seam);
            result.AcquireStage = device.PresentAcquireWaitStageForTests;
            long idleBefore = VulkanStats.WaitCount(WaitSite.DeviceWaitIdle);

            for (int frame = 0; frame < Frames; frame++)
            {
                // Recording: from the start of BeginFrame (its pacing wait included)
                // to the moment Present is called.
                long frameStart = Stopwatch.GetTimestamp();
                seam.BeginFrame();
                seam.BindDefaultFramebuffer();
                seam.ClearColor(0, 0.1f, 0.2f, 0.3f, 1f);
                seam.UseProgram(programId);
                seam.SetViewport(0, 0, Width, Height);
                seam.SetDepthTest(false);
                seam.SetCullFace(false);
                seam.DrawFullscreenTriangle();
                long recorded = Stopwatch.GetTimestamp();
                seam.Present();

                if (frame < Warmup) continue;
                VulkanDevice.PresentTimings timings = device.LastPresentTimingsForTests;
                result.Samples++;
                result.RecordingMs.Add(Ms(recorded - frameStart));
                result.SubmitAfterPresentEntryMs.Add(Ms(timings.FrameSubmitted - timings.PresentEntry));
                result.AcquireAfterSubmitMs.Add(Ms(timings.AcquireReturned - timings.FrameSubmitted));
                if (timings.RenderCompletedAtAcquire) result.RenderCompletedAtAcquire++;
                if (timings.Presented)
                {
                    result.Presented++;
                    Assert.True(timings.PresentValue > timings.RenderValue,
                        "the present submission must carry a newer Frame value than the frame it waits on");
                }
            }

            result.DeviceIdleWaits = VulkanStats.WaitCount(WaitSite.DeviceWaitIdle) - idleBefore;
            GpuTest.AssertClean(seam);
        }
        return result;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static double Median(List<double> values)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/PresentWaitStageTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.IO;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

/// <summary>
/// The present submission's wait stages, without a device: the frame is waited
/// on at COLOR_ATTACHMENT_OUTPUT, the acquire semaphore at the swapchain image's
/// first use (TRANSFER for the blit, COLOR_ATTACHMENT_OUTPUT for a raster path),
/// and no ALL_COMMANDS wait stage remains anywhere on the submission path.
/// </summary>
public class PresentWaitStageTests
{
    [Fact]
    public void TheFrameIsWaitedOnAtColorAttachmentOutput() =>
        Assert.Equal(PipelineStageFlags.ColorAttachmentOutputBit, PresentWaitStages.FrameWait);

    [Fact]
    public void TheBlitPathWaitsForTheAcquiredImageAtTransfer()
    {
        IPresentPath blit = new BlitPresentPath(null!, null!, () => null);
        Assert.Equal(PipelineStageFlags.TransferBit, blit.AcquireWaitStage);
        Assert.Equal(blit.AcquireWaitStage, PresentWaitStages.RequireAcquireStage(blit.AcquireWaitStage));
    }

    [Theory]
    [InlineData(PipelineStageFlags.AllCommandsBit)]
    [InlineData(PipelineStageFlags.AllGraphicsBit)]
    [InlineData(PipelineStageFlags.TopOfPipeBit)]
    [InlineData(PipelineStageFlags.BottomOfPipeBit)]
    [InlineData(PipelineStageFlags.FragmentShaderBit)]
    [InlineData(PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit)]
    public void AnyOtherAcquireWaitStageIsRefused(PipelineStageFlags stage) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PresentWaitStages.RequireAcquireStage(stage));

    [Fact]
    public void BothAllowedAcquireStagesAreAccepted()
    {
        Assert.Equal(PipelineStageFlags.TransferBit,
            PresentWaitStages.RequireAcquireStage(PresentWaitStages.BlitAcquireWait));
        Assert.Equal(PipelineStageFlags.ColorAttachmentOutputBit,
            PresentWaitStages.RequireAcquireStage(PresentWaitStages.RasterAcquireWait));
    }

    /// <summary>The submission code itself: no ALL_COMMANDS wait stage, and both waits come from PresentWaitStages.</summary>
    [Fact]
    public void TheSubmissionPathHasNoAllCommandsWaitStage()
    {
        string root = Path.Combine(ShaderCorpus.RepositoryRoot, "Optimum.Render.Vulkan");
        string ring = File.ReadAllText(Path.Combine(root, "Core", "FrameRing.cs"));
        Assert.DoesNotContain("PipelineStageFlags.AllCommandsBit", ring);
        Assert.Contains("waitStages[waitCount] = PresentWaitStages.FrameWait;", ring);
        Assert.Contains("PresentWaitStages.RequireAcquireStage(acquireStage);", ring);

        string device = File.ReadAllText(Path.Combine(root, "VulkanDevice.cs"));
        Assert.Contains("_presentPath.AcquireWaitStage, renderValue, target.PresentSemaphore);", device);
    }
}
}
