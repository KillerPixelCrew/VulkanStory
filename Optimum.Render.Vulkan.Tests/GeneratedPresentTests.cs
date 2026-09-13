using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Present;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Frame generation's presents, as the paced present ships them (ROADMAP, "The paced
/// present: the design").
///
/// Step 0 presented every frame twice from the render thread, both blits in one
/// command buffer; that proved the acquire-semaphore free list and the Frame
/// timeline survive twice the present pressure, and it is gone. What replaced it is
/// judged here on numbers, never on a screenshot: with the switch off, the frame is
/// one synchronous present and no thread exists; with it on, every rendered frame
/// yields exactly one Generated and then one Real present from the present thread,
/// carrying the ids the render thread reserved at handoff, in increasing order, with
/// a present between frames and no readback in the loop - the only shape that can
/// see a lifetime or ordering bug - and a validation log clean under sync,best.
///
/// <para>It is still the integration proof of the free-list bound: the swapchain is
/// created with <c>P = 2</c> unconditionally, and the paced run takes the free list
/// through 240 presents at the shipped ring depth.</para>
/// </summary>
public class GeneratedPresentTests
{
    private const int Width = 256;
    private const int Height = 192;
    private const int SynchronousFrames = 32;
    private const int PacedFrames = 120;

    private readonly ITestOutputHelper _output;

    public GeneratedPresentTests(ITestOutputHelper output) => _output = output;

    private static void RenderFrame(VulkanDevice seam, int programId, int frame, int frames)
    {
        seam.BeginFrame();
        seam.BindDefaultFramebuffer();
        seam.ClearColor(0, 0.1f, frame / (float)frames, 0.3f, 1f);
        seam.UseProgram(programId);
        seam.SetViewport(0, 0, Width, Height);
        seam.SetDepthTest(false);
        seam.SetCullFace(false);
        seam.DrawFullscreenTriangle();
        seam.Present();
    }

    [SkippableFact]
    public unsafe void WithPacingOffEveryFrameIsOneSynchronousPresentAndNoThreadStarts()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window),
            "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            if (!device.Initialize((IntPtr)window, Width, Height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
            }

            using (device)
            {
                VulkanDevice seam = device;
                device.PacedPresentEnabled = false;
                int programId = SwapchainTests.LinkFullscreenProgram(seam);
                Swapchain swapchain = device.SwapchainForTests!;

                int presents = 0;
                ulong previousId = 0;
                for (int frame = 0; frame < SynchronousFrames; frame++)
                {
                    RenderFrame(seam, programId, frame, SynchronousFrames);

                    // Off is off: nothing of the paced path exists after any frame.
                    Assert.Null(device.PresentThreadForTests);
                    Assert.Null(device.OutputPairPoolForTests);

                    if (!device.LastPresentTimingsForTests.Presented) continue;
                    presents++;

                    // The render thread made the present submission itself (Submit B has a
                    // Frame value) and presented once: the frame's id maps to this frame, and
                    // no other present of this swapchain belongs to it.
                    Assert.NotEqual(0UL, device.LastPresentTimingsForTests.PresentValue);
                    ulong id = device.LastPresentIdForTests;
                    Assert.True(id > previousId, $"frame {frame}: present id {id} did not increase past {previousId}");
                    Assert.True(swapchain.PresentIds.TryGetFrameId(id, out ulong frameId));
                    Assert.Equal(device.LatencyFrameId, frameId);
                    Assert.False(swapchain.PresentIds.TryGetFrameId(id - 1, out ulong earlier) && earlier == frameId,
                        $"frame {frame} presented twice");
                    previousId = id;
                }

                _output.WriteLine($"pacing off: {presents} presents in {SynchronousFrames} frames, mode {swapchain.PresentMode}");
                Assert.Equal(SynchronousFrames, presents);
                Assert.False(swapchain.FrameGenerationPresentModes);

                // The bound the swapchain was built with, re-derived rather than
                // recompiled: max(imageCount, F x P) + 1 with P = 2, whether or not the
                // switch is on, because the semaphores are created once per swapchain.
                SwapchainSlot slot = swapchain.CurrentSlotForTests!;
                PresentPressure pressure = swapchain.Pressure;
                Assert.Equal(PresentPressure.GeneratedPlusRealPerFrame, pressure.PresentsPerFrame);
                Assert.Equal(device.FramesInFlightForTests, pressure.FramesInFlight);
                Assert.Equal(AcquireSemaphoreFreeList.CapacityFor(slot.ImageCount, pressure), slot.AcquireSemaphoreCount);
                Assert.Equal(Math.Max((int)slot.ImageCount, device.FramesInFlightForTests * 2) + 1,
                    slot.AcquireSemaphoreCount);

                GpuTest.AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    [SkippableFact]
    public unsafe void PacedFramesPresentGeneratedThenRealWithTheIdsReservedAtHandoff()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window),
            "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            if (!device.Initialize((IntPtr)window, Width, Height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
            }

            var records = new List<PacedPresentRecord>();
            var handoffs = new List<VulkanDevice.PacedHandoff>();
            using (device)
            {
                VulkanDevice seam = device;
                int programId = SwapchainTests.LinkFullscreenProgram(seam);
                device.PacedPresentEnabled = true;
                device.ConfigurePresentThreadForTests = thread => thread.PresentedForTests = record =>
                {
                    lock (records) records.Add(record);
                };

                for (int frame = 0; frame < PacedFrames; frame++)
                {
                    RenderFrame(seam, programId, frame, PacedFrames);
                    Assert.True(device.LastPresentTimingsForTests.Presented, $"frame {frame} was not handed off");
                    handoffs.Add(device.LastPacedHandoffForTests);
                }

                PresentThread thread = device.PresentThreadForTests!;
                OutputPairPool pool = device.OutputPairPoolForTests!;
                Assert.True(pool.WaitUntilPresented(TimeSpan.FromSeconds(30)),
                    "the present thread did not present every handed-off pair within 30 s");

                var shutdown = Stopwatch.StartNew();
                device.ShutDownPacedPresent();
                shutdown.Stop();

                Swapchain swapchain = device.SwapchainForTests!;
                FramePacingSnapshot generatedIntervals = thread.PresentIntervals(PacedPresentKind.Generated);
                FramePacingSnapshot realIntervals = thread.PresentIntervals(PacedPresentKind.Real);
                _output.WriteLine($"{PacedFrames} paced frames: {thread.GeneratedPresents} generated, {thread.RealPresents} real, " +
                    $"{thread.SkippedPresents} skipped, back-pressure waits {pool.BackPressureWaits}, mode {swapchain.PresentMode}, " +
                    $"shutdown {shutdown.Elapsed.TotalMilliseconds:F1} ms");
                _output.WriteLine($"intervals before a generated present p50 {generatedIntervals.P50:F2} ms p99 {generatedIntervals.P99:F2}; " +
                    $"before a real present p50 {realIntervals.P50:F2} ms p99 {realIntervals.P99:F2} (stand-in pacer: real follows at once)");

                Assert.True(shutdown.Elapsed < TimeSpan.FromSeconds(5), "the shutdown did not join within its bound");
                Assert.Null(device.PresentThreadForTests);
                Assert.False(thread.IsRunning);
                Assert.Null(thread.Failure);
                Assert.Equal(PacedFrames, thread.GeneratedPresents);
                Assert.Equal(PacedFrames, thread.RealPresents);
                Assert.Equal(0, thread.SkippedPresents);
                // Vsync is on by default: frame generation presents FIFO, never MAILBOX.
                Assert.Equal(PresentModeKHR.FifoKhr, swapchain.PresentMode);

                List<PacedPresentRecord> presented;
                lock (records) presented = new List<PacedPresentRecord>(records);
                Assert.Equal(PacedFrames * 2, presented.Count);

                ulong previousId = 0;
                for (int frame = 0; frame < PacedFrames; frame++)
                {
                    PacedPresentRecord generated = presented[frame * 2];
                    PacedPresentRecord real = presented[frame * 2 + 1];
                    VulkanDevice.PacedHandoff handoff = handoffs[frame];

                    Assert.Equal(PacedPresentKind.Generated, generated.Kind);
                    Assert.Equal(PacedPresentKind.Real, real.Kind);
                    // The ids the render thread reserved at handoff, and no others.
                    Assert.Equal(handoff.GeneratedPresentId, generated.PresentId);
                    Assert.Equal(handoff.RealPresentId, real.PresentId);
                    // Both halves are this frame's: its latency id, its pair, its Frame value.
                    Assert.Equal(handoff.LatencyFrameId, generated.LatencyFrameId);
                    Assert.Equal(handoff.LatencyFrameId, real.LatencyFrameId);
                    Assert.Equal(handoff.PairIndex, generated.PairIndex);
                    Assert.Equal(handoff.PairIndex, real.PairIndex);
                    Assert.Equal(handoff.FrameValue, generated.FrameValue);
                    // Strictly increasing in present order, across both halves and frames.
                    Assert.True(generated.PresentId > previousId,
                        $"frame {frame}: generated id {generated.PresentId} did not increase past {previousId}");
                    Assert.True(real.PresentId > generated.PresentId,
                        $"frame {frame}: real id {real.PresentId} did not follow generated id {generated.PresentId}");
                    Assert.True(real.PresentValue > generated.PresentValue);
                    previousId = real.PresentId;

                    Assert.True(swapchain.PresentIds.TryGetFrameId(real.PresentId, out ulong realFrame) || frame < PacedFrames - 32);
                    if (frame >= PacedFrames - 16) Assert.Equal(handoff.LatencyFrameId, realFrame);
                }

                // The swapchain is the render thread's again: a synchronous frame presents.
                device.PacedPresentEnabled = false;
                RenderFrame(seam, programId, 0, 1);
                Assert.True(device.LastPresentTimingsForTests.Presented);
                Assert.NotEqual(0UL, device.LastPresentTimingsForTests.PresentValue);

                GpuTest.AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }
}
