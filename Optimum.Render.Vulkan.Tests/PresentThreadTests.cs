using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Present;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The present thread and its output-pair pool (ROADMAP, "The paced present: the design").
///
/// Synchronization validation sees GPU hazards and nothing else: a CPU race on a pair's
/// state, a torn swapchain request or a quiesce that never wakes is invisible to it. So
/// every cross-thread path here is raced on purpose, every wait is bounded - a deadlock
/// fails the test instead of hanging the suite - and the GPU tests judge on numbers:
/// pair state histories, presented pixels read back through a hook, join times, and the
/// sync,best validation log.
/// </summary>
public class PresentThreadTests
{
    private const int Width = 256;
    private const int Height = 192;

    /// <summary>VulkanPoison's RGBA8 value (1, 0, 1, 1), as the little-endian word the capture reads.</summary>
    private const uint PoisonRgba8 = 0xFFFF00FF;

    private const byte Green = 32;
    private const byte Blue = 200;

    private readonly ITestOutputHelper _output;

    public PresentThreadTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------ CPU: the pool

    [Fact]
    public void ARenderThreadAndAPresentThreadRacingThePoolNeverShareAPair()
    {
        var pool = new OutputPairPool(3);
        pool.Build(8, 8, Format.R8G8B8A8Unorm, NoImages);

        Exception? presenterError = null;
        long presented = 0;
        var presenter = new Thread(() =>
        {
            try
            {
                var random = new Random(7);
                ulong value = 0;
                while (true)
                {
                    PresentWork work = pool.WaitForWork(out OutputPair? pair);
                    if (work == PresentWork.Stop) return;
                    if (work == PresentWork.Quiesce)
                    {
                        pool.DropQueued();
                        pool.AcknowledgeQuiesceAndWait();
                        continue;
                    }
                    if (random.Next(4) == 0) Thread.SpinWait(random.Next(400));
                    pool.ReleasePresented(pair!, ++value);
                    Interlocked.Increment(ref presented);
                }
            }
            catch (Exception error)
            {
                presenterError = error;
                pool.NotePresenterFailed();
            }
        })
        {
            IsBackground = true,
        };
        presenter.Start();

        int rebuilds = 0;
        Task render = Task.Run(() =>
        {
            for (int i = 0; i < 20000; i++)
            {
                Assert.True(pool.TryAcquireForRendering(TimeSpan.FromSeconds(5), out OutputPair? pair),
                    "no pair became free at iteration " + i);
                if (i % 97 == 0)
                {
                    pool.CancelRendering(pair!);
                    continue;
                }
                pool.MarkQueued(pair!, (ulong)i + 1, 0, 0, 0);

                if (i % 1000 == 999)
                {
                    Assert.True(pool.Quiesce(TimeSpan.FromSeconds(5)), "the presenter did not acknowledge a quiesce");
                    pool.Build(8, 8, Format.R8G8B8A8Unorm, NoImages,
                        retired => Assert.Equal(OutputPairState.Free, retired.State));
                    pool.Resume();
                    rebuilds++;
                }
            }
        });

        bool finished = render.Wait(TimeSpan.FromSeconds(60));
        pool.RequestStop();
        bool joined = presenter.Join(TimeSpan.FromSeconds(10));

        _output.WriteLine($"20000 handoffs: {Interlocked.Read(ref presented)} presented, {pool.Dropped} dropped by " +
            $"{rebuilds} quiesces, back-pressure waits {pool.BackPressureWaits}");
        Assert.True(finished, "the render side did not finish within 60 s: a lost wake-up or a deadlock");
        render.GetAwaiter().GetResult();
        Assert.True(joined, "the presenter did not stop within 10 s");
        Assert.Null(presenterError);
        Assert.Equal(19, rebuilds);
        Assert.True(Interlocked.Read(ref presented) > 0);
        Assert.True(pool.BackPressureWaits > 0, "the race never exercised the back-pressure wait");
    }

    [Fact]
    public void AFailedPresenterReleasesARenderThreadWaitingForAPair()
    {
        var pool = new OutputPairPool(1);
        pool.Build(8, 8, Format.R8G8B8A8Unorm, NoImages);
        Assert.True(pool.TryAcquireForRendering(TimeSpan.FromSeconds(1), out OutputPair? only));
        pool.MarkQueued(only!, 1, 0, 0, 0);

        var waited = Stopwatch.StartNew();
        Task<bool> waiter = Task.Run(() => pool.TryAcquireForRendering(TimeSpan.FromSeconds(30), out _));
        Thread.Sleep(50);
        pool.NotePresenterFailed();

        Assert.True(waiter.Wait(TimeSpan.FromSeconds(5)), "a dead presenter left the render thread waiting");
        Assert.False(waiter.Result);
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AQuiesceWithNoPresentThreadGivesUpWithinItsBound()
    {
        var pool = new OutputPairPool(2);
        pool.Build(8, 8, Format.R8G8B8A8Unorm, NoImages);

        var waited = Stopwatch.StartNew();
        Assert.False(pool.Quiesce(TimeSpan.FromMilliseconds(100)));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(2));

        // The request was withdrawn: a presenter that starts later is not stuck quiescing.
        Assert.False(pool.NextPairQueuedOrLeaving);
    }

    [Fact]
    public void AnOutOfOrderTransitionThrowsInsteadOfOverwritingAPair()
    {
        var pool = new OutputPairPool(1);
        pool.Build(8, 8, Format.R8G8B8A8Unorm, NoImages);
        Assert.True(pool.TryAcquireForRendering(TimeSpan.FromSeconds(1), out OutputPair? pair));
        pool.MarkQueued(pair!, 1, 0, 0, 0);

        // Queued belongs to the present thread: neither a second handoff nor a release may touch it.
        Assert.Throws<InvalidOperationException>(() => pool.MarkQueued(pair!, 2, 0, 0, 0));
        Assert.Throws<InvalidOperationException>(() => pool.ReleasePresented(pair!, 1));
        Assert.Throws<InvalidOperationException>(() => pool.Build(8, 8, Format.R8G8B8A8Unorm, NoImages));
    }

    // ----------------------------------------------- CPU: the swapchain request

    [Fact]
    public void ASwapchainRequestPostedFromManyThreadsIsAlwaysTakenWhole()
    {
        var record = new SwapchainRequestRecord();
        const int Writers = 4;
        const int PostsPerWriter = 50000;
        int torn = 0;
        long taken = 0;
        int writersDone = 0;

        var writers = new Task[Writers];
        for (int w = 0; w < Writers; w++)
        {
            int writer = w;
            writers[w] = Task.Run(() =>
            {
                for (int k = 0; k < PostsPerWriter; k++)
                {
                    uint width = (uint)(writer * PostsPerWriter + k);
                    record.Post(width, width * 3 + 1, width % 2 == 0);
                }
                Interlocked.Increment(ref writersDone);
            });
        }

        Task reader = Task.Run(() =>
        {
            while (true)
            {
                bool finished = Volatile.Read(ref writersDone) == Writers;
                while (record.TryTake(out SwapchainRequestRecord.Request request))
                {
                    taken++;
                    if (request.Height != request.Width * 3 + 1 || request.Vsync != (request.Width % 2 == 0)) torn++;
                }
                if (finished) return;
            }
        });

        Assert.True(Task.WaitAll(writers, TimeSpan.FromSeconds(30)), "the writers did not finish");
        Assert.True(reader.Wait(TimeSpan.FromSeconds(30)), "the owner did not finish taking");
        _output.WriteLine($"{Writers * PostsPerWriter} posts, {taken} taken (newest wins), {torn} torn");
        Assert.Equal(0, torn);
        Assert.True(taken > 0);
        Assert.False(record.Pending);
    }

    [Fact]
    public void TheStandInPacerPresentsTheRealFrameNow()
    {
        var pacer = new ImmediateFramePacer();
        long now = LatencyClock.NowUs();
        pacer.NotePresented(PacedPresentKind.Generated, now);
        Assert.True(pacer.RealPresentTargetUs(now, nextPairQueued: false) <= LatencyClock.NowUs());
        Assert.True(pacer.RealPresentTargetUs(now, nextPairQueued: true) <= LatencyClock.NowUs());
    }

    // ------------------------------------------------------------------- GPU

    /// <summary>
    /// Resize and vsync requests arrive from another thread while the present thread
    /// owns the swapchain - exactly the plain-field write the old <c>RequestRebuild</c>
    /// made - and the render thread resizes the display image every 14 frames, which
    /// rebuilds the pool through the quiesce handshake. 120 frames, one request from
    /// another thread every 7 of them.
    /// </summary>
    [SkippableFact]
    public unsafe void AResizeRequestFromAnotherThreadEverySevenFramesNeitherTearsNorHangs()
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

            var posted = new HashSet<(uint, uint, bool)>();
            var taken = new List<SwapchainRequestRecord.Request>();
            using (device)
            {
                VulkanDevice seam = device;
                Swapchain swapchain = device.SwapchainForTests!;
                swapchain.RequestTakenForTests = request =>
                {
                    lock (taken) taken.Add(request);
                };
                long presents = 0;
                device.PacedPresentEnabled = true;
                device.ConfigurePresentThreadForTests = thread =>
                    thread.PresentedForTests = _ => Interlocked.Increment(ref presents);

                int creationsBefore = swapchain.Creations;
                using var signal = new SemaphoreSlim(0);
                int stop = 0;
                var poster = new Thread(() =>
                {
                    uint k = 0;
                    while (signal.Wait(TimeSpan.FromSeconds(30)) && Volatile.Read(ref stop) == 0)
                    {
                        k++;
                        uint width = 200 + k;
                        uint height = 150 + k;
                        bool vsync = k % 3 != 0;
                        lock (posted) posted.Add((width, height, vsync));
                        swapchain.RequestRebuild(width, height, vsync);
                    }
                })
                {
                    IsBackground = true,
                };
                poster.Start();

                int generation = 0;
                Task render = Task.Run(() =>
                {
                    for (int frame = 0; frame < 120; frame++)
                    {
                        bool small = frame / 14 % 2 == 1;
                        int width = small ? Width - 16 : Width;
                        int height = small ? Height - 12 : Height;
                        if (frame > 0 && frame % 14 == 0)
                        {
                            // Resize posts the device's own request (vsync stays on).
                            lock (posted) posted.Add(((uint)width, (uint)height, true));
                            seam.Resize(width, height);
                        }
                        RenderColouredFrame(seam, width, height, (byte)frame);
                        if (frame % 7 == 6) signal.Release();
                    }
                    generation = device.OutputPairPoolForTests!.Pairs()[0].Generation;
                    Assert.True(device.OutputPairPoolForTests.WaitUntilPresented(TimeSpan.FromSeconds(30)));
                });

                bool finished = render.Wait(TimeSpan.FromSeconds(120));
                Volatile.Write(ref stop, 1);
                signal.Release();
                bool posterJoined = poster.Join(TimeSpan.FromSeconds(10));
                Assert.True(finished, "120 frames with resizes did not finish within 120 s");
                render.GetAwaiter().GetResult();
                Assert.True(posterJoined);

                PresentThread thread = device.PresentThreadForTests!;
                var shutdown = Stopwatch.StartNew();
                device.ShutDownPacedPresent();
                shutdown.Stop();

                List<SwapchainRequestRecord.Request> takenCopy;
                lock (taken) takenCopy = new List<SwapchainRequestRecord.Request>(taken);
                _output.WriteLine($"{takenCopy.Count} requests taken, {swapchain.Creations - creationsBefore} swapchains " +
                    $"created, pool generation {generation}, {Interlocked.Read(ref presents)} presents, " +
                    $"{thread.SkippedPresents} skipped, shutdown {shutdown.Elapsed.TotalMilliseconds:F1} ms");

                Assert.Null(thread.Failure);
                Assert.True(shutdown.Elapsed < TimeSpan.FromSeconds(5));
                Assert.NotEmpty(takenCopy);
                lock (posted)
                {
                    foreach (SwapchainRequestRecord.Request request in takenCopy)
                    {
                        Assert.True(posted.Contains((request.Width, request.Height, request.Vsync)),
                            $"the owner took a request nobody posted: {request.Width}x{request.Height} vsync {request.Vsync}");
                    }
                }
                Assert.True(swapchain.Creations > creationsBefore, "no request ever reached a rebuild");
                // Eight display resizes, each a quiesce, a rebuild and a resume.
                Assert.True(generation >= 9, $"the pool was rebuilt only {generation - 1} times");
                Assert.True(Interlocked.Read(ref presents) > 0);

                GpuTest.AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    /// <summary>
    /// The present thread presents slowly; the render thread must wait for a free pair
    /// rather than write into one the present thread still holds. Poison mode makes a
    /// read of an unwritten pair magenta, and each frame clears to its own red value, so
    /// a present that shows poison or another frame's red is exactly the overwrite or the
    /// lifetime bug this pool exists to rule out. The pixels are read back through the
    /// present thread's capture hook, inside each present submission.
    /// </summary>
    [SkippableFact]
    public unsafe void BackPressureNeverOverwritesAPairThePresentThreadHoldsAndNoPresentedPixelIsPoison()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window),
            "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            device.ConfigureContextOptions += options => options.Poison = true;
            if (!device.Initialize((IntPtr)window, Width, Height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
            }

            const int Frames = 30;
            var records = new List<PacedPresentRecord>();
            var expectedRed = new Dictionary<ulong, byte>();
            using (device)
            {
                VulkanDevice seam = device;
                device.PacedPresentEnabled = true;
                device.TrackOutputPairHistoryForTests = true;
                device.CapturePresentedPixelsForTests = true;
                device.ConfigurePresentThreadForTests = thread =>
                {
                    thread.DelayPerPairForTests = TimeSpan.FromMilliseconds(25);
                    thread.PresentedForTests = record =>
                    {
                        lock (records) records.Add(record);
                    };
                };

                for (int frame = 0; frame < Frames; frame++)
                {
                    byte red = (byte)(40 + frame * 5);
                    RenderColouredFrame(seam, Width, Height, red);
                    Assert.True(device.LastPresentTimingsForTests.Presented, $"frame {frame} was not handed off");
                    expectedRed[device.LastPacedHandoffForTests.GeneratedPresentId] = red;
                    expectedRed[device.LastPacedHandoffForTests.RealPresentId] = red;
                }

                OutputPairPool pool = device.OutputPairPoolForTests!;
                Assert.True(pool.WaitUntilPresented(TimeSpan.FromSeconds(30)), "the slow present thread never caught up");
                long waits = pool.BackPressureWaits;
                List<OutputPairState>[] histories = pool.Histories();
                device.ShutDownPacedPresent();

                List<PacedPresentRecord> presented;
                lock (records) presented = new List<PacedPresentRecord>(records);
                _output.WriteLine($"{Frames} frames at 25 ms per pair: {presented.Count} presents, back-pressure waits {waits}, " +
                    $"pair cycles {string.Join(", ", Array.ConvertAll(histories, h => (h.Count - 1) / 4))}");

                Assert.True(waits > 0, "the render thread never had to wait: the test did not exercise back-pressure");
                Assert.Equal(Frames * 2, presented.Count);
                foreach (PacedPresentRecord record in presented)
                {
                    Assert.True(record.Captured);
                    Assert.NotEqual(PoisonRgba8, record.CapturedPixel);
                    byte red = (byte)(record.CapturedPixel & 0xFF);
                    byte green = (byte)((record.CapturedPixel >> 8) & 0xFF);
                    byte blue = (byte)((record.CapturedPixel >> 16) & 0xFF);
                    Assert.True(expectedRed[record.PresentId] == red && green == Green && blue == Blue,
                        $"{record.Kind} present {record.PresentId} of pair {record.PairIndex} showed " +
                        $"({red}, {green}, {blue}), expected ({expectedRed[record.PresentId]}, {Green}, {Blue})");
                }
                for (int i = 0; i < histories.Length; i++) AssertLegalHistory(i, histories[i]);

                GpuTest.AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    /// <summary>
    /// Shutdown while pairs wait: the one method the wiring stage calls before NGX goes
    /// down must return within a bound, drop what is queued rather than present it, and
    /// leave the device presenting synchronously again.
    /// </summary>
    [SkippableFact]
    public unsafe void ShutdownWithPairsQueuedJoinsWithinABoundAndDropsThem()
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
                device.PacedPresentEnabled = true;
                device.ConfigurePresentThreadForTests = thread => thread.DelayPerPairForTests = TimeSpan.FromMilliseconds(150);

                for (int frame = 0; frame <= device.FramesInFlightForTests; frame++)
                {
                    RenderColouredFrame(seam, Width, Height, (byte)frame);
                }

                OutputPairPool pool = device.OutputPairPoolForTests!;
                PresentThread thread = device.PresentThreadForTests!;
                int queued = pool.QueuedCount;

                var shutdown = Stopwatch.StartNew();
                device.ShutDownPacedPresent();
                shutdown.Stop();

                _output.WriteLine($"{queued} pair(s) queued at shutdown, {pool.Dropped} dropped, joined in " +
                    $"{shutdown.Elapsed.TotalMilliseconds:F1} ms, {thread.GeneratedPresents} generated presents made");

                Assert.True(queued > 0, "nothing was queued: the test did not shut down under load");
                Assert.True(shutdown.Elapsed < TimeSpan.FromSeconds(3), "the shutdown did not join within its bound");
                Assert.True(pool.Dropped > 0);
                Assert.False(thread.IsRunning);
                Assert.Null(thread.Failure);
                Assert.Null(device.PresentThreadForTests);

                device.PacedPresentEnabled = false;
                RenderColouredFrame(seam, Width, Height, 1);
                Assert.True(device.LastPresentTimingsForTests.Presented, "the device did not present synchronously again");

                GpuTest.AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    // ---------------------------------------------------------------- helpers

    private static OutputPairImages NoImages(int index) => new(0, null, 0, null);

    private static void RenderColouredFrame(VulkanDevice seam, int width, int height, byte red)
    {
        seam.BeginFrame();
        seam.BindDefaultFramebuffer();
        seam.SetViewport(0, 0, width, height);
        seam.ClearColor(0, red / 255f, Green / 255f, Blue / 255f, 1f);
        seam.Present();
    }

    /// <summary>
    /// Every step of a pair's history is one the design allows. Rendering to Queued is the
    /// handoff; Queued to Free is a drop; nothing ever goes from Queued or Presenting back
    /// to Rendering, which is what an overwrite of a held pair would be.
    /// </summary>
    private static void AssertLegalHistory(int index, List<OutputPairState> history)
    {
        Assert.NotEmpty(history);
        Assert.Equal(OutputPairState.Free, history[0]);
        for (int i = 1; i < history.Count; i++)
        {
            OutputPairState from = history[i - 1];
            OutputPairState to = history[i];
            bool legal = (from, to) switch
            {
                (OutputPairState.Free, OutputPairState.Rendering) => true,
                (OutputPairState.Rendering, OutputPairState.Queued) => true,
                (OutputPairState.Rendering, OutputPairState.Free) => true,
                (OutputPairState.Queued, OutputPairState.Presenting) => true,
                (OutputPairState.Queued, OutputPairState.Free) => true,
                (OutputPairState.Presenting, OutputPairState.Free) => true,
                _ => false,
            };
            Assert.True(legal, $"pair {index} went from {from} to {to} at step {i}");
        }
    }
}
