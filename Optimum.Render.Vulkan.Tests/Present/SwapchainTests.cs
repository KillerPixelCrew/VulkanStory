// Source: Optimum.Render.Vulkan.Tests/SwapchainRecreationTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Phase 1B step 4 against a real (hidden) window: resizing and vsync toggles
/// rebuild the swapchain with oldSwapchain while frames are in flight, never
/// wait for the device to go idle, retire every replaced slot once the GPU is
/// past it, and stay clean under sync and best-practices validation.
/// </summary>
public class SwapchainRecreationTests
{
    private const int Width = 256;
    private const int Height = 192;

    private readonly ITestOutputHelper _output;

    public SwapchainRecreationTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public unsafe void AHiddenWindowResizeLoopRecreatesWithoutWaitingAndStaysClean()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window), "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            if (!device.Initialize((IntPtr)window, Width, Height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            using (device)
            {
                VulkanDevice seam = device;
                Swapchain swapchain = device.SwapchainForTests!;
                int programId = SwapchainTests.LinkFullscreenProgram(seam);

                void RenderFrames(int count, int w, int h)
                {
                    for (int frame = 0; frame < count; frame++)
                    {
                        seam.BeginFrame();
                        seam.BindDefaultFramebuffer();
                        seam.ClearColor(0, 0.2f, 0.4f, 0.6f, 1f);
                        seam.UseProgram(programId);
                        seam.SetViewport(0, 0, w, h);
                        seam.SetDepthTest(false);
                        seam.SetCullFace(false);
                        seam.DrawFullscreenTriangle();
                        seam.Present();
                    }
                }

                RenderFrames(3, Width, Height);

                (int W, int H)[] sizes =
                {
                    (320, 240), (200, 150), (512, 384), (256, 192), (300, 200), (640, 360), (257, 193),
                };

                long idleBefore = VulkanStats.WaitCount(WaitSite.DeviceWaitIdle);
                int creationsBefore = swapchain.Creations;
                int iterations = 0;
                for (int round = 0; round < 2; round++)
                {
                    foreach ((int w, int h) in sizes)
                    {
                        GLFW.SetWindowSize(window, w, h);
                        GLFW.PollEvents();
                        seam.Resize(w, h);
                        if (iterations % 3 == 2) seam.SetVSync(iterations % 2 == 0);
                        // A frame is recorded before the rebuild happens at its
                        // acquire, so the old chain still has work in flight.
                        RenderFrames(3, w, h);
                        iterations++;
                    }
                }

                long idleWaits = VulkanStats.WaitCount(WaitSite.DeviceWaitIdle) - idleBefore;
                int creations = swapchain.Creations - creationsBefore;
                _output.WriteLine($"{iterations} resizes: {creations} swapchains created, {swapchain.RetiredPending} slots pending, " +
                    $"final extent {swapchain.Extent.Width}x{swapchain.Extent.Height}, mode {swapchain.PresentMode}");

                Assert.Equal(0, idleWaits);
                Assert.True(creations >= iterations, $"{iterations} resizes rebuilt only {creations} swapchains");
                Assert.False(swapchain.Parked);
                Assert.Null(swapchain.RebuildFailure);

                // Every replaced slot goes once the frames after it completed.
                for (int frame = 0; frame < 8 && swapchain.RetiredPending > 0; frame++) RenderFrames(1, 257, 193);
                Assert.Equal(0, swapchain.RetiredPending);

                SwapchainSlot slot = swapchain.CurrentSlotForTests!;
                Assert.Equal(AcquireSemaphoreFreeList.CapacityFor(slot.ImageCount), slot.AcquireSemaphoreCount);
                // Nothing leaked: every semaphore is free or parked behind a submitted present.
                Assert.Equal(slot.AcquireSemaphoreCount, slot.FreeAcquireSemaphores + slot.PendingAcquireSemaphores);

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
}

// Source: Optimum.Render.Vulkan.Tests/SwapchainRetirementTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

/// <summary>
/// The swapchain's lifetime and recreation rules without a window or a device:
/// a replaced slot dies exactly when its last present submission completed,
/// acquire results map to rebuild decisions, the image count and present mode
/// follow the plan, the acquire-semaphore free list never hands out a semaphore
/// twice, and FIFO_RELAXED promotion needs sustained misses.
/// </summary>
public class SwapchainRetirementTests
{
    private sealed class FakeClock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    private sealed class Slot : IDisposable
    {
        private readonly List<string>? _order;
        public string Name { get; }
        public int DisposeCount { get; private set; }

        public Slot(string name, List<string>? order = null)
        {
            Name = name;
            _order = order;
        }

        public void Dispose()
        {
            DisposeCount++;
            _order?.Add(Name);
        }
    }

    // ------------------------------------------------------------- retirement

    [Fact]
    public void ASlotIsNotDestroyedBeforeItsLastPresentSubmissionCompleted()
    {
        var clock = new FakeClock();
        var retirement = new SwapchainRetirement(clock);
        var slot = new Slot("old");
        retirement.Retire(slot, lastPresentValue: 7);

        for (ulong completed = 0; completed < 7; completed++)
        {
            clock.FrameCompleted = completed;
            Assert.Equal(0, retirement.Collect());
            Assert.Equal(0, slot.DisposeCount);
            Assert.Equal(1, retirement.PendingCount);
        }

        clock.FrameCompleted = 7;
        Assert.Equal(1, retirement.Collect());
        Assert.Equal(1, slot.DisposeCount);
        Assert.Equal(0, retirement.PendingCount);

        clock.FrameCompleted = 50;
        Assert.Equal(0, retirement.Collect());
        Assert.Equal(1, slot.DisposeCount);
    }

    /// <summary>
    /// Phase 1 review regression: a replaced slot is keyed on the frame after its last present
    /// submission. That submission completing only signals the present semaphore; the
    /// vkQueuePresentKHR queued after it may still be pending, and only the next frame's
    /// submission (queued after the present) completing proves it was processed.
    /// </summary>
    [Fact]
    public void AReplacedSlotOutlivesItsLastPresentSubmissionByOneFrame()
    {
        Assert.Equal(0UL, SwapchainPolicy.RetireAfter(0));
        Assert.Equal(8UL, SwapchainPolicy.RetireAfter(7));

        var clock = new FakeClock();
        var retirement = new SwapchainRetirement(clock);
        var slot = new Slot("old");
        retirement.Retire(slot, SwapchainPolicy.RetireAfter(7));

        clock.FrameCompleted = 7;
        Assert.Equal(0, retirement.Collect());
        Assert.Equal(0, slot.DisposeCount);

        clock.FrameCompleted = 8;
        Assert.Equal(1, retirement.Collect());
        Assert.Equal(1, slot.DisposeCount);
    }

    [Fact]
    public void ASlotThatNeverPresentedGoesAtTheNextCollect()
    {
        var retirement = new SwapchainRetirement(new FakeClock());
        var slot = new Slot("unused");
        retirement.Retire(slot, lastPresentValue: 0);
        Assert.Equal(1, retirement.Collect());
        Assert.Equal(1, slot.DisposeCount);
    }

    [Fact]
    public void SlotsRetireIndependentlyAndReadyOnesGoInRetirementOrder()
    {
        var order = new List<string>();
        var clock = new FakeClock();
        var retirement = new SwapchainRetirement(clock);
        var a = new Slot("a", order);
        var b = new Slot("b", order);
        var c = new Slot("c", order);
        retirement.Retire(a, 9);
        retirement.Retire(b, 4);
        retirement.Retire(c, 6);

        clock.FrameCompleted = 6;
        Assert.Equal(2, retirement.Collect());
        Assert.Equal(new[] { "b", "c" }, order);
        Assert.Equal(0, a.DisposeCount);

        clock.FrameCompleted = 9;
        Assert.Equal(1, retirement.Collect());
        Assert.Equal(new[] { "b", "c", "a" }, order);
    }

    [Fact]
    public void TeardownDestroysEverySlotRegardlessOfTheTimeline()
    {
        var retirement = new SwapchainRetirement(new FakeClock());
        var a = new Slot("a");
        var b = new Slot("b");
        retirement.Retire(a, 100);
        retirement.Retire(b, 200);
        retirement.DisposeAll();
        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(1, b.DisposeCount);
        Assert.Equal(0, retirement.PendingCount);
    }

    // -------------------------------------------------------------- acquiring

    [Fact]
    public void AcquireResultsMapToThePlansRebuildRules()
    {
        Assert.Equal(AcquireAction.Present, SwapchainPolicy.OnAcquire(Result.Success, 0));
        Assert.Equal(AcquireAction.PresentThenRebuild, SwapchainPolicy.OnAcquire(Result.SuboptimalKhr, 0));
        Assert.Equal(AcquireAction.PresentThenRebuild, SwapchainPolicy.OnAcquire(Result.SuboptimalKhr, 1));
        // OUT_OF_DATE rebuilds and re-acquires exactly once.
        Assert.Equal(AcquireAction.RebuildAndRetry, SwapchainPolicy.OnAcquire(Result.ErrorOutOfDateKhr, 0));
        Assert.Equal(AcquireAction.SkipFrame, SwapchainPolicy.OnAcquire(Result.ErrorOutOfDateKhr, 1));
        Assert.Equal(AcquireAction.Fail, SwapchainPolicy.OnAcquire(Result.ErrorDeviceLost, 0));
        Assert.Equal(AcquireAction.Fail, SwapchainPolicy.OnAcquire(Result.ErrorSurfaceLostKhr, 0));
    }

    [Fact]
    public void AZeroExtentParksPresentation()
    {
        Assert.True(SwapchainPolicy.IsParked(new Extent2D(0, 0)));
        Assert.True(SwapchainPolicy.IsParked(new Extent2D(800, 0)));
        Assert.True(SwapchainPolicy.IsParked(new Extent2D(0, 600)));
        Assert.False(SwapchainPolicy.IsParked(new Extent2D(1, 1)));
    }

    [Theory]
    [InlineData(1u, 0u, PresentModeKHR.FifoKhr, 2u)]
    [InlineData(2u, 0u, PresentModeKHR.FifoKhr, 3u)]
    [InlineData(1u, 0u, PresentModeKHR.MailboxKhr, 3u)]
    [InlineData(1u, 0u, PresentModeKHR.ImmediateKhr, 2u)]
    [InlineData(3u, 0u, PresentModeKHR.MailboxKhr, 4u)]
    [InlineData(2u, 3u, PresentModeKHR.MailboxKhr, 3u)]
    [InlineData(3u, 3u, PresentModeKHR.FifoKhr, 3u)]
    [InlineData(1u, 2u, PresentModeKHR.MailboxKhr, 2u)]
    public void TheImageCountIsMinPlusOneWithAFloorOfTwoOrThreeForMailboxClamped(
        uint min, uint max, PresentModeKHR mode, uint expected) =>
        Assert.Equal(expected, SwapchainPolicy.ChooseImageCount(min, max, mode));

    [Fact]
    public void PresentModesFollowVsyncAndTheRelaxedPromotion()
    {
        var all = new[] { PresentModeKHR.ImmediateKhr, PresentModeKHR.MailboxKhr, PresentModeKHR.FifoKhr, PresentModeKHR.FifoRelaxedKhr };
        var fifoOnly = new[] { PresentModeKHR.FifoKhr };
        var noMailbox = new[] { PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr };

        Assert.Equal(PresentModeKHR.FifoKhr, SwapchainPolicy.ChoosePresentMode(true, false, all));
        Assert.Equal(PresentModeKHR.FifoRelaxedKhr, SwapchainPolicy.ChoosePresentMode(true, true, all));
        Assert.Equal(PresentModeKHR.FifoKhr, SwapchainPolicy.ChoosePresentMode(true, true, fifoOnly));
        Assert.Equal(PresentModeKHR.MailboxKhr, SwapchainPolicy.ChoosePresentMode(false, false, all));
        Assert.Equal(PresentModeKHR.MailboxKhr, SwapchainPolicy.ChoosePresentMode(false, true, all));
        Assert.Equal(PresentModeKHR.ImmediateKhr, SwapchainPolicy.ChoosePresentMode(false, false, noMailbox));
        Assert.Equal(PresentModeKHR.FifoKhr, SwapchainPolicy.ChoosePresentMode(false, false, fifoOnly));
    }

    // -------------------------------------------------------- acquire semaphores

    [Fact]
    public void TheAcquireFreeListHoldsImageCountPlusOneAndNeverHandsOutASemaphoreTwice()
    {
        const uint imageCount = 3;
        int capacity = AcquireSemaphoreFreeList.CapacityFor(imageCount);
        Assert.Equal(4, capacity);

        var handles = new ulong[capacity];
        for (int i = 0; i < capacity; i++) handles[i] = 100UL + (ulong)i;
        var list = new AcquireSemaphoreFreeList(handles);
        Assert.Equal(capacity, list.FreeCount);

        var taken = new HashSet<ulong>();
        for (int i = 0; i < capacity; i++) Assert.True(taken.Add(list.Take(0)), "a semaphore was handed out twice");
        Assert.Equal(0, list.FreeCount);
        Assert.Throws<InvalidOperationException>(() => list.Take(0));

        // A failed acquire returns its semaphore at once.
        ulong returned = 101;
        list.Return(returned);
        Assert.Equal(returned, list.Take(0));

        foreach (ulong handle in taken) list.Return(handle);
        Assert.Throws<InvalidOperationException>(() => list.Return(999));
        Assert.Throws<InvalidOperationException>(() => list.ReturnAfter(999, 1));
    }

    /// <summary>VUID-vkAcquireNextImageKHR-semaphore-01779: a semaphore an uncompleted submission waits on is not reused.</summary>
    [Fact]
    public void ASemaphoreWaitedOnByAPresentSubmissionIsReusedOnlyAfterThatSubmissionCompleted()
    {
        var list = new AcquireSemaphoreFreeList(new ulong[] { 1, 2, 3 });
        ulong first = list.Take(0);
        ulong second = list.Take(0);
        ulong third = list.Take(0);
        list.ReturnAfter(first, frameValue: 10);
        list.ReturnAfter(second, frameValue: 12);
        Assert.Equal(0, list.FreeCount);
        Assert.Equal(2, list.PendingCount);

        // Frame 9 completed: neither submission is done, nothing to hand out.
        Assert.Throws<InvalidOperationException>(() => list.Take(9));
        Assert.Equal(2, list.PendingCount);

        // Frame 10 completed: only the first comes back.
        Assert.Equal(first, list.Take(10));
        Assert.Equal(1, list.PendingCount);
        Assert.Throws<InvalidOperationException>(() => list.Take(11));

        list.ReturnAfter(third, frameValue: 13);
        list.ReturnAfter(first, frameValue: 14);
        var reclaimed = new HashSet<ulong> { list.Take(13), list.Take(13) };
        Assert.Equal(new HashSet<ulong> { second, third }, reclaimed);
        Assert.Equal(1, list.PendingCount);
    }

    // ---------------------------------------------------- FIFO_RELAXED promotion

    [Fact]
    public void SteadyVsyncedFramesNeverPromote()
    {
        var detector = new MissedVsyncDetector();
        for (int i = 0; i < MissedVsyncDetector.Window * 5; i++)
        {
            Assert.False(detector.NoteInterval(16.7));
        }
    }

    [Fact]
    public void AFewMissesDoNotPromoteButSustainedMissesDo()
    {
        var detector = new MissedVsyncDetector();
        int few = MissedVsyncDetector.MissesToPromote - 1;
        for (int i = 0; i < MissedVsyncDetector.Window * 3; i++)
        {
            // Misses spaced so any full window holds at most `few` of them.
            bool miss = i % (MissedVsyncDetector.Window / few + 1) == 0;
            Assert.False(detector.NoteInterval(miss ? 33.4 : 16.7), "promoted at interval " + i);
        }

        detector.Reset();
        bool promoted = false;
        int at = -1;
        for (int i = 0; i < MissedVsyncDetector.Window && !promoted; i++)
        {
            promoted = detector.NoteInterval(i % 5 == 0 ? 33.4 : 16.7);
            at = i;
        }
        Assert.True(promoted, "24 misses in a window must promote");
        Assert.Equal(MissedVsyncDetector.Window - 1, at);

        // The detector starts over after promoting.
        for (int i = 0; i < MissedVsyncDetector.Window - 1; i++)
        {
            Assert.False(detector.NoteInterval(i % 5 == 0 ? 33.4 : 16.7));
        }
    }

    [Fact]
    public void BurstIntervalsDoNotBecomeTheRefreshEstimate()
    {
        var detector = new MissedVsyncDetector();
        bool promoted = false;
        for (int i = 0; i < MissedVsyncDetector.Window; i++)
        {
            // A 1 ms burst after a stall is not a refresh; against a 1 ms period
            // every 16.7 ms frame would look like a miss.
            promoted |= detector.NoteInterval(i % 10 == 0 ? 1.0 : 16.7);
        }
        Assert.False(promoted);
        Assert.False(detector.NoteInterval(double.NaN));
        Assert.False(detector.NoteInterval(-3));
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/SwapchainTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Exercises the presentation path against a real window.
///
/// This is the one part of the backend that cannot be tested headlessly: a
/// swapchain needs a surface, and a surface needs a window. The window is created
/// with <c>ClientApi.NoApi</c>, which is exactly the change the client needs -
/// GLFW must not create an OpenGL context alongside the Vulkan surface.
///
/// Skips where there is no display or no Vulkan-capable window system, so a
/// headless CI machine reports these as skipped rather than failing.
/// </summary>
public class SwapchainTests
{
    private readonly ITestOutputHelper _output;

    public SwapchainTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Creates a hidden window with no graphics API attached, the way the
    /// patched client will.
    /// </summary>
    internal static unsafe bool TryCreateWindow(
        ITestOutputHelper output, int width, int height, out Window* window)
    {
        window = null;
        try
        {
            if (!GLFW.Init())
            {
                output.WriteLine("GLFW could not initialise; no display?");
                return false;
            }

            if (!GLFW.VulkanSupported())
            {
                output.WriteLine("GLFW reports no Vulkan support on this window system.");
                return false;
            }

            GLFW.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
            GLFW.WindowHint(WindowHintBool.Visible, false);

            window = GLFW.CreateWindow(width, height, "Optimum swapchain test", null, null);
            if (window == null)
            {
                output.WriteLine("GLFW could not create a window.");
                return false;
            }
            return true;
        }
        catch (Exception error)
        {
            output.WriteLine("Windowing unavailable: " + error.Message);
            return false;
        }
    }

    [SkippableFact]
    public unsafe void ADeviceComesUpAgainstARealWindowAndPresentsFrames()
    {
        const int width = 320;
        const int height = 240;

        Skip.IfNot(TryCreateWindow(_output, width, height, out Window* window), "No usable window system.");

        try
        {
            var device = GpuTest.NewDevice();
            if (!device.Initialize((IntPtr)window, width, height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            using (device)
            {
                VulkanDevice seam = device;
                _output.WriteLine($"presenting on {seam.RendererString}");

                int programId = LinkFullscreenProgram(seam);

                // Several frames, so the ring rotates and the swapchain cycles
                // through more than one image.
                for (int frame = 0; frame < 8; frame++)
                {
                    seam.BeginFrame();
                    seam.BindDefaultFramebuffer();
                    seam.ClearColor(0, 0.1f, 0.2f, 0.3f, 1f);

                    seam.UseProgram(programId);
                    seam.SetViewport(0, 0, width, height);
                    seam.SetDepthTest(false);
                    seam.SetCullFace(false);
                    seam.DrawFullscreenTriangle();

                    seam.Present();
                }

                AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    /// <summary>
    /// A resize has to rebuild both the swapchain and the offscreen target the
    /// client renders into, and keep presenting afterwards.
    /// </summary>
    [SkippableFact]
    public unsafe void ResizingRebuildsTheChainAndKeepsPresenting()
    {
        const int width = 256;
        const int height = 192;

        Skip.IfNot(TryCreateWindow(_output, width, height, out Window* window), "No usable window system.");

        try
        {
            var device = GpuTest.NewDevice();
            if (!device.Initialize((IntPtr)window, width, height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            using (device)
            {
                VulkanDevice seam = device;
                int programId = LinkFullscreenProgram(seam);

                void RenderFrames(int count, int w, int h)
                {
                    for (int frame = 0; frame < count; frame++)
                    {
                        seam.BeginFrame();
                        seam.BindDefaultFramebuffer();
                        seam.ClearColor(0, 0.2f, 0.4f, 0.6f, 1f);
                        seam.UseProgram(programId);
                        seam.SetViewport(0, 0, w, h);
                        seam.DrawFullscreenTriangle();
                        seam.Present();
                    }
                }

                RenderFrames(4, width, height);

                seam.Resize(width * 2, height * 2);
                RenderFrames(4, width * 2, height * 2);

                seam.Resize(width, height);
                RenderFrames(4, width, height);

                AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    /// <summary>
    /// Toggling vsync swaps the present mode, which means rebuilding the chain
    /// while frames are in flight.
    /// </summary>
    [SkippableFact]
    public unsafe void TogglingVsyncRebuildsTheChainCleanly()
    {
        const int width = 256;
        const int height = 192;

        Skip.IfNot(TryCreateWindow(_output, width, height, out Window* window), "No usable window system.");

        try
        {
            var device = GpuTest.NewDevice();
            if (!device.Initialize((IntPtr)window, width, height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            using (device)
            {
                VulkanDevice seam = device;
                int programId = LinkFullscreenProgram(seam);

                foreach (bool vsync in new[] { false, true, false })
                {
                    seam.SetVSync(vsync);
                    for (int frame = 0; frame < 3; frame++)
                    {
                        seam.BeginFrame();
                        seam.BindDefaultFramebuffer();
                        seam.ClearColor(0, 0f, 0f, 0f, 1f);
                        seam.UseProgram(programId);
                        seam.SetViewport(0, 0, width, height);
                        seam.DrawFullscreenTriangle();
                        seam.Present();
                    }
                }

                AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    private sealed class TestShader : IShader
    {
        public EnumShaderType Type { get; set; }
        public string Code { get; set; } = "";
        public string PrefixCode { get; set; } = "";
        public bool Compile() => true;
    }

    internal static int LinkFullscreenProgram(VulkanDevice device)
    {
        var vertex = new TestShader
        {
            Type = EnumShaderType.VertexShader,
            Code = """
                #version 330 core
                out vec2 uv;
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                    uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
                }
                """,
        };
        var fragment = new TestShader
        {
            Type = EnumShaderType.FragmentShader,
            Code = """
                #version 330 core
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = vec4(uv, 0.5, 1.0); }
                """,
        };

        Assert.True(device.CompileShader(vertex));
        Assert.True(device.CompileShader(fragment));

        var program = new SeamProgram { VertexShader = vertex, FragmentShader = fragment };
        int programId = device.LinkProgram(program);
        Assert.True(programId > 0, device.GetError() ?? "link failed");
        return programId;
    }

    /// <summary>Minimal IShaderProgram; the device only reads the stage properties.</summary>
    private sealed class SeamProgram : IShaderProgram
    {
        public int ProgramId { get; set; }
        public string AssetDomain { get; set; } = "game";
        public int PassId => 0;
        public string PassName => "swapchain-test";
        public bool ClampTexturesToEdge { get; set; }
        public IShader VertexShader { get; set; } = null!;
        public IShader FragmentShader { get; set; } = null!;
        public IShader GeometryShader { get; set; } = null!;
        public bool Oit { get; set; } = true;
        public bool Disposed => false;
        public bool LoadError => false;
        public Vintagestory.API.Datastructures.OrderedDictionary<string, UBORef> UBOs { get; } = new();

        public void Use() { }
        public void Stop() { }
        public bool Compile() => true;
        public void Dispose() { }
        public void Uniform(string uniformName, float value) { }
        public void Uniform(string uniformName, int value) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2f value) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2i value) { }
        public void Uniform(string uniformName, float valueX, float valueY) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec3f value) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ, float valueW) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec4f value) { }
        public void Uniforms4(string uniformName, int count, float[] values) { }
        public void UniformMatrix(string uniformName, float[] matrix) { }
        public void BindTexture2D(string samplerName, int textureId, int textureNumber) { }
        public void BindTextureCube(string samplerName, int textureId, int textureNumber) { }
        public void UniformMatrices(string uniformName, int count, float[] matrix) { }
        public void UniformMatrices4x3(string uniformName, int count, float[] matrix) { }
        public bool HasUniform(string uniformName) => false;
    }

    private static void AssertClean(VulkanDevice device) => GpuTest.AssertClean(device);
}
}
