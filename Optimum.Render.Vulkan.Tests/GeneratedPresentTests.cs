using System;
using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Frame generation, step 0: every frame is presented twice, from the same
/// composited image, with no vendor code anywhere.
///
/// The question this isolates is mechanical - does the acquire-semaphore free
/// list (<c>imageCount + 1</c>, derived from "at most FramesInFlight - 1 present
/// submissions outstanding") and the Frame timeline bookkeeping survive twice the
/// present pressure. So it runs many frames with a present between them and no
/// readback inside the loop, which is the only shape that can see a lifetime or
/// bookkeeping bug at all, and judges it on numbers: presents per frame, present
/// ids, what the free list held, and the validation log under sync,best.
/// </summary>
public class GeneratedPresentTests
{
    private const int Width = 256;
    private const int Height = 192;
    private const int Frames = 32;

    private readonly ITestOutputHelper _output;

    public GeneratedPresentTests(ITestOutputHelper output) => _output = output;

    private sealed class Run
    {
        public readonly List<ulong> RealPresentIds = new();
        public readonly List<ulong> GeneratedPresentIds = new();
        public readonly List<int> FreeAcquireSemaphores = new();
        public readonly List<int> PendingAcquireSemaphores = new();
        public int GeneratedPresents;
        public int AcquireSemaphoreCapacity;
        public uint ImageCount;
        public int Presents;
        public int IdenticalBlits;
        public int DistinctDestinations;
    }

    [SkippableFact]
    public unsafe void TwoPresentsPerFrameCarryTheSameImageAndSurviveTheFreeList()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window),
            "No usable window system.");

        try
        {
            Run single = Present((IntPtr)window, generated: false);
            Run doubled = Present((IntPtr)window, generated: true);

            _output.WriteLine($"swapchain images {doubled.ImageCount}, acquire semaphores {doubled.AcquireSemaphoreCapacity}");
            _output.WriteLine($"presents: single {single.Presents} in {Frames} frames, doubled {doubled.Presents} ({doubled.GeneratedPresents} generated)");
            _output.WriteLine($"free acquire semaphores at present: single min {Min(single.FreeAcquireSemaphores)} max {Max(single.FreeAcquireSemaphores)}, " +
                $"doubled min {Min(doubled.FreeAcquireSemaphores)} max {Max(doubled.FreeAcquireSemaphores)}");
            _output.WriteLine($"parked (waiting for their present submission) : single max {Max(single.PendingAcquireSemaphores)}, " +
                $"doubled max {Max(doubled.PendingAcquireSemaphores)}");

            // The switch is what it says: off, one present per frame.
            Assert.Equal(0, single.GeneratedPresents);
            Assert.Equal(Frames, single.Presents);
            Assert.Empty(single.GeneratedPresentIds);

            // On: two per frame, and the second is a present of its own - its own
            // id, its own acquired image, from the same picture.
            Assert.Equal(Frames, doubled.GeneratedPresents);
            Assert.Equal(Frames * 2, doubled.Presents);
            Assert.Equal(Frames, doubled.IdenticalBlits);
            Assert.Equal(Frames, doubled.DistinctDestinations);

            // Present ids increase over both presents of a frame and across frames.
            ulong previous = 0;
            for (int frame = 0; frame < Frames; frame++)
            {
                ulong real = doubled.RealPresentIds[frame];
                ulong generatedId = doubled.GeneratedPresentIds[frame];
                Assert.True(real > previous, $"frame {frame}: present id {real} did not increase past {previous}");
                Assert.True(generatedId > real,
                    $"frame {frame}: the generated present id {generatedId} did not follow the real one {real}");
                previous = generatedId;
            }

            // Nothing leaked out of the free list: everything is either free or
            // parked against a submission, never lost.
            foreach (Run run in new[] { single, doubled })
            {
                for (int i = 0; i < run.FreeAcquireSemaphores.Count; i++)
                {
                    Assert.True(run.FreeAcquireSemaphores[i] + run.PendingAcquireSemaphores[i] <= run.AcquireSemaphoreCapacity,
                        $"the free list held {run.FreeAcquireSemaphores[i]} free and {run.PendingAcquireSemaphores[i]} parked " +
                        $"semaphores of {run.AcquireSemaphoreCapacity}");
                }
            }
            Assert.Equal((int)doubled.ImageCount + 1, doubled.AcquireSemaphoreCapacity);
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    /// <summary>
    /// <paramref name="generated" /> frames, presented, with no readback in the
    /// loop: a readback would drain the queue every frame and hide exactly the
    /// bookkeeping this is about.
    /// </summary>
    private Run Present(IntPtr window, bool generated)
    {
        VulkanDevice device = GpuTest.NewDevice();
        if (!device.Initialize(window, Width, Height, out string failureReason))
        {
            device.Dispose();
            Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
        }

        var run = new Run();
        using (device)
        {
            VulkanDevice seam = device;
            device.GeneratedPresentEnabled = generated;
            int programId = SwapchainTests.LinkFullscreenProgram(seam);

            for (int frame = 0; frame < Frames; frame++)
            {
                seam.BeginFrame();
                seam.BindDefaultFramebuffer();
                seam.ClearColor(0, frame / (float)Frames, 0.2f, 0.3f, 1f);
                seam.UseProgram(programId);
                seam.SetViewport(0, 0, Width, Height);
                seam.SetDepthTest(false);
                seam.SetCullFace(false);
                seam.DrawFullscreenTriangle();

                int generatedBefore = device.GeneratedPresentsForTests;
                seam.Present();

                Swapchain? swapchain = device.SwapchainForTests;
                SwapchainSlot? slot = swapchain?.CurrentSlotForTests;
                if (slot != null)
                {
                    run.ImageCount = slot.ImageCount;
                    run.AcquireSemaphoreCapacity = slot.AcquireSemaphoreCount;
                    run.FreeAcquireSemaphores.Add(slot.FreeAcquireSemaphores);
                    run.PendingAcquireSemaphores.Add(slot.PendingAcquireSemaphores);
                }

                if (!device.LastPresentTimingsForTests.Presented) continue;
                run.Presents++;
                run.RealPresentIds.Add(device.LastPresentIdForTests);

                if (device.GeneratedPresentsForTests == generatedBefore) continue;
                run.GeneratedPresents++;
                run.Presents++;
                ulong generatedId = device.LastGeneratedPresentIdForTests;
                run.GeneratedPresentIds.Add(generatedId);

                // Both presents of the frame belong to one frame id, and the map
                // that the driver reports are matched through holds both.
                Assert.True(swapchain!.PresentIds.TryGetFrameId(device.LastPresentIdForTests, out ulong realFrameId));
                Assert.True(swapchain.PresentIds.TryGetFrameId(generatedId, out ulong generatedFrameId));
                Assert.Equal(realFrameId, generatedFrameId);

                // "The same image" is what the two recorded blits say: one source
                // image, one source extent, two different acquired destinations.
                BlitPresentPath path = device.BlitPresentPathForTests!;
                PresentBlitRecord real = path.PreviousRecordedBlit;
                PresentBlitRecord copy = path.LastRecordedBlit;
                if (real.SourceImage != 0 && real.SourceImage == copy.SourceImage &&
                    real.SourceWidth == copy.SourceWidth && real.SourceHeight == copy.SourceHeight &&
                    real.DestinationExtent.Width == copy.DestinationExtent.Width &&
                    real.DestinationExtent.Height == copy.DestinationExtent.Height)
                {
                    run.IdenticalBlits++;
                }
                if (real.DestinationImage != copy.DestinationImage) run.DistinctDestinations++;
            }

            GpuTest.AssertClean(seam);
        }
        return run;
    }

    private static int Min(List<int> values)
    {
        int min = int.MaxValue;
        foreach (int value in values) min = Math.Min(min, value);
        return values.Count == 0 ? 0 : min;
    }

    private static int Max(List<int> values)
    {
        int max = int.MinValue;
        foreach (int value in values) max = Math.Max(max, value);
        return values.Count == 0 ? 0 : max;
    }
}
