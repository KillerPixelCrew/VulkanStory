// Source: Optimum.Render.Vulkan.Tests/LatencyMarkerOrderTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Latency seams S2 and S4, against a real (hidden) window: several presented
/// frames through the recording backend must stamp the renderer's markers in one
/// order, once each, under one frame id per frame that increases by exactly one.
///
/// The two markers the lib hook owns (InputSample and SimulationStart, stamped in
/// <c>VulkanClientPlatform.LatencySleep</c>) are not the renderer's, so they are
/// not asserted here; what is asserted is that the renderer stamps its own five
/// and never stamps one twice, however often the client brackets a render stage.
/// </summary>
public class LatencyMarkerOrderTests
{
    private const int Width = 256;
    private const int Height = 192;
    private const int Frames = 6;

    private readonly ITestOutputHelper _output;

    public LatencyMarkerOrderTests(ITestOutputHelper output) => _output = output;

    /// <summary>What the renderer owns, in the order one frame must produce it.</summary>
    private static readonly LatencyMarker[] ExpectedPerFrame =
    {
        LatencyMarker.SimulationEnd,
        LatencyMarker.RenderSubmitStart,
        LatencyMarker.RenderSubmitEnd,
        LatencyMarker.PresentStart,
        LatencyMarker.PresentEnd,
    };

    [SkippableFact]
    public unsafe void EveryFrameStampsTheRenderersMarkersOnceInOrderUnderItsOwnId()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window), "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            var latency = new RecordingLatencyBackend();
            // Before Initialize: the backend has to be the one the first
            // swapchain, the frame ring and the stats source see.
            device.SetLatencyBackend(latency);

            if (!device.Initialize((IntPtr)window, Width, Height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            using (device)
            {
                VulkanDevice seam = device;
                int programId = SwapchainTests.LinkFullscreenProgram(seam);
                var frameIds = new List<ulong>();

                for (int frame = 0; frame < Frames; frame++)
                {
                    // What the lib hook does before input is sampled (seam S3).
                    ulong frameId = seam.BeginLatencyFrame();
                    frameIds.Add(frameId);

                    seam.BeginFrame();
                    Assert.Equal(frameId, seam.LatencyFrameId);

                    // The client brackets many render stages per frame; only the
                    // first may stamp the pair.
                    seam.NoteRenderStageStarted();
                    seam.NoteRenderStageStarted();
                    seam.NoteRenderStageStarted();

                    seam.BindDefaultFramebuffer();
                    seam.ClearColor(0, 0.1f, 0.3f, 0.5f, 1f);
                    seam.UseProgram(programId);
                    seam.SetViewport(0, 0, Width, Height);
                    seam.SetDepthTest(false);
                    seam.SetCullFace(false);
                    seam.DrawFullscreenTriangle();
                    seam.Present();
                }

                // One id per frame, increasing by exactly one.
                for (int i = 1; i < frameIds.Count; i++)
                {
                    Assert.Equal(frameIds[i - 1] + 1, frameIds[i]);
                }

                foreach (ulong id in frameIds)
                {
                    LatencyMarker[] markers = latency.MarkersOf(id);
                    _output.WriteLine($"frame {id}: {string.Join(", ", markers)}");
                    Assert.Equal(ExpectedPerFrame, markers);
                }

                // Every frame presented, each present paired with its own frame
                // id, and the present ids increase.
                Assert.Equal(frameIds.Count, latency.Presents.Count);
                for (int i = 0; i < frameIds.Count; i++)
                {
                    Assert.Equal(frameIds[i], latency.Presents[i].FrameId);
                    if (i > 0) Assert.True(latency.Presents[i].PresentId > latency.Presents[i - 1].PresentId);
                }

                // Seam S4: every submit of the frame passed the tag hook. Two
                // submits a frame at least (Submit A and Submit B).
                Assert.True(latency.TagSubmitCount >= 2 * Frames,
                    $"{latency.TagSubmitCount} tagged submits over {Frames} frames");

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
    /// A headless device (no swapchain) still owns the frame identity and the
    /// submit markers: nothing presents, so PresentStart/End and OnPresent stay
    /// absent rather than being stamped against a present that never happened.
    /// </summary>
    [SkippableFact]
    public void AHeadlessFrameStampsRenderSubmitEndAndNoPresentMarkers()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? created), "No Vulkan device.");
        using VulkanDevice device = created!;

        var latency = new RecordingLatencyBackend();
        device.SetLatencyBackend(latency);

        VulkanDevice seam = device;
        for (int frame = 0; frame < 3; frame++)
        {
            seam.BeginFrame();
            seam.NoteRenderStageStarted();
            seam.Present();
        }

        Assert.Empty(latency.Presents);
        // VK_KHR_present_id hangs off the swapchain: a headless device detects it and enables nothing.
        Assert.False(device.ContextForTests.Capabilities.PresentIdEnabled);
        Assert.DoesNotContain("VK_KHR_present_id", device.ContextForTests.EnabledDeviceExtensions);
        for (ulong id = 1; id <= 3; id++)
        {
            Assert.Equal(
                new[] { LatencyMarker.SimulationEnd, LatencyMarker.RenderSubmitStart, LatencyMarker.RenderSubmitEnd },
                latency.MarkersOf(id));
        }

        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// A frame that reaches BeginFrame without the lib hook (headless, or any
    /// path with no platform) allocates its own id, so the identity exists
    /// exactly once either way and the ids still increase by one.
    /// </summary>
    [SkippableFact]
    public void AFrameWithoutTheHookAllocatesItsOwnIdExactlyOnce()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? created), "No Vulkan device.");
        using VulkanDevice device = created!;

        VulkanDevice seam = device;
        var ids = new List<ulong>();
        for (int frame = 0; frame < 4; frame++)
        {
            if (frame % 2 == 0) seam.BeginLatencyFrame();
            seam.BeginFrame();
            ids.Add(seam.LatencyFrameId);
            seam.Present();
        }

        Assert.Equal(new ulong[] { 1, 2, 3, 4 }, ids);
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/LatencySwapchainLifetimeTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Latency review 2026-09-12, seam S5: the backend has to be told when the
/// swapchain handle it holds goes away, not only when a new one appears.
///
/// <c>Swapchain.Build</c> hands the current slot to the retirement queue before
/// it creates the replacement, and does so even when that creation fails, after
/// which the client keeps rendering frames on a chain that no longer exists.
/// VK_NV_low_latency2 keys every one of its calls on a live
/// <c>VkSwapchainKHR</c> (vkLatencySleepNV, vkSetLatencyMarkerNV,
/// vkGetLatencyTimingsNV, vkSetLatencySleepModeNV), so without a retirement
/// notice it would keep calling into a handle the retirement queue is about to
/// destroy. Before the fix there was no notice at all.
/// </summary>
public class LatencySwapchainLifetimeTests
{
    private const int Width = 256;
    private const int Height = 192;

    private readonly ITestOutputHelper _output;

    public LatencySwapchainLifetimeTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public unsafe void EveryRetiredSwapchainIsAnnouncedBeforeTheOneThatReplacesIt()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window), "No usable window system.");

        var latency = new RecordingLatencyBackend();

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            device.SetLatencyBackend(latency);

            if (!device.Initialize((IntPtr)window, Width, Height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            int creations;
            using (device)
            {
                VulkanDevice seam = device;
                Swapchain swapchain = device.SwapchainForTests!;
                int programId = SwapchainTests.LinkFullscreenProgram(seam);

                void RenderFrames(int count, int w, int h)
                {
                    for (int frame = 0; frame < count; frame++)
                    {
                        seam.BeginLatencyFrame();
                        seam.BeginFrame();
                        seam.NoteRenderStageStarted();
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

                // The first swapchain exists without anything having been retired.
                Assert.Single(latency.Swapchains);
                Assert.Equal(0, latency.SwapchainRetirements);

                RenderFrames(2, Width, Height);

                (int W, int H)[] sizes = { (320, 240), (200, 150), (256, 192) };
                foreach ((int w, int h) in sizes)
                {
                    GLFW.SetWindowSize(window, w, h);
                    GLFW.PollEvents();
                    seam.Resize(w, h);
                    RenderFrames(2, w, h);
                }

                creations = swapchain.Creations;
                Assert.True(creations > 1, creations + " swapchain creations");
                GpuTest.AssertClean(seam);
            }

            // In order: handle, then (retired, handle) for every rebuild, and a
            // final retirement when the device disposed the swapchain. A new
            // handle is never announced while the old one is still the backend's.
            _output.WriteLine(creations + " creations, " + latency.SwapchainRetirements + " retirements, events: " +
                string.Join(", ", System.Array.ConvertAll(latency.SwapchainEvents.ToArray(), Describe)));

            Assert.Equal(creations, latency.Swapchains.Count);
            Assert.Equal(creations, latency.SwapchainRetirements);

            bool holdsOne = false;
            foreach (SwapchainKHR handle in latency.SwapchainEvents)
            {
                if (handle.Handle != 0)
                {
                    Assert.False(holdsOne, "a new swapchain was announced while the old one was still live");
                    holdsOne = true;
                }
                else
                {
                    holdsOne = false;
                }
            }

            // Disposal is the last word: the backend holds nothing afterwards.
            Assert.False(holdsOne, "the backend still holds a swapchain after the device was disposed");
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    private static string Describe(SwapchainKHR handle) => handle.Handle == 0 ? "retired" : "created";
}
}

// Source: Optimum.Render.Vulkan.Tests/PresentIdentityTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Latency seams S2 and S5, against a real (hidden) window: the present id is
/// allocated once per vkQueuePresentKHR, strictly increases, survives every
/// swapchain recreation a resize or a vsync toggle causes, and each creation
/// tells the latency backend exactly once so a backend can re-apply its
/// per-swapchain state.
/// </summary>
public class PresentIdentityTests
{
    private const int Width = 256;
    private const int Height = 192;

    private readonly ITestOutputHelper _output;

    public PresentIdentityTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public unsafe void PresentIdsIncreaseAcrossResizesAndEachSwapchainIsAnnouncedOnce()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window), "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            var latency = new RecordingLatencyBackend();
            device.SetLatencyBackend(latency);

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
                var presentIds = new List<ulong>();

                void RenderFrames(int count, int w, int h)
                {
                    for (int frame = 0; frame < count; frame++)
                    {
                        seam.BeginLatencyFrame();
                        seam.BeginFrame();
                        seam.NoteRenderStageStarted();
                        seam.BindDefaultFramebuffer();
                        seam.ClearColor(0, 0.2f, 0.4f, 0.6f, 1f);
                        seam.UseProgram(programId);
                        seam.SetViewport(0, 0, w, h);
                        seam.SetDepthTest(false);
                        seam.SetCullFace(false);
                        seam.DrawFullscreenTriangle();
                        seam.Present();
                        if (device.LastPresentTimingsForTests.Presented)
                        {
                            presentIds.Add(device.LastPresentIdForTests);
                            // The map keeps the frame that produced it (1:1 until
                            // frame generation presents one frame twice).
                            Assert.True(swapchain.PresentIds.TryGetFrameId(
                                device.LastPresentIdForTests, out ulong mapped));
                            Assert.Equal(seam.LatencyFrameId, mapped);
                        }
                    }
                }

                // Present ids ride VkPresentIdKHR whenever the device offers the extension
                // and its feature on a presentable device - the frame identity reaches the
                // display without any pacing backend - and never otherwise, which would be a
                // validation error. AssertClean below covers the chained presents.
                VulkanCapabilities caps = device.ContextForTests.Capabilities;
                _output.WriteLine(caps.LatencySummary);
                Assert.Equal(caps.LatencySupport.PresentId, caps.PresentIdEnabled);
                Assert.Equal(caps.PresentIdEnabled, swapchain.PresentIdEnabled);
                Assert.Equal(caps.PresentIdEnabled,
                    Array.IndexOf(device.ContextForTests.EnabledDeviceExtensions, "VK_KHR_present_id") >= 0);
                Assert.Equal(LatencyBackendKind.None, caps.LatencyBackend);

                // The first swapchain is announced like every later one.
                Assert.Equal(swapchain.Creations, latency.SwapchainCount);
                Assert.Equal(1, latency.SwapchainCount);

                RenderFrames(3, Width, Height);

                (int W, int H)[] sizes = { (320, 240), (200, 150), (512, 384), (256, 192) };
                int iterations = 0;
                foreach ((int w, int h) in sizes)
                {
                    GLFW.SetWindowSize(window, w, h);
                    GLFW.PollEvents();
                    seam.Resize(w, h);
                    if (iterations % 2 == 1) seam.SetVSync(iterations % 4 == 1);
                    RenderFrames(3, w, h);
                    iterations++;
                }

                _output.WriteLine($"{presentIds.Count} presents, ids {presentIds[0]}..{presentIds[^1]}, " +
                    $"{swapchain.Creations} swapchains created, {latency.SwapchainCount} announced");

                // Strictly increasing, recreation included: the counter is global
                // and is never reset by a rebuild.
                Assert.True(presentIds.Count >= 12, $"only {presentIds.Count} presents");
                for (int i = 1; i < presentIds.Count; i++)
                {
                    Assert.True(presentIds[i] > presentIds[i - 1],
                        $"present id {presentIds[i]} did not exceed {presentIds[i - 1]} at index {i}");
                }

                // Exactly one announcement per creation, and more than one
                // creation happened (the resizes rebuilt the chain).
                Assert.True(swapchain.Creations > 1, $"{swapchain.Creations} swapchain creations");
                Assert.Equal(swapchain.Creations, latency.SwapchainCount);

                // Distinct handles: a re-announced old handle would let a backend
                // re-apply state to a chain that is already retired.
                Assert.Equal(latency.Swapchains.Count, new HashSet<ulong>(HandlesOf(latency)).Count);

                GpuTest.AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    private static IEnumerable<ulong> HandlesOf(RecordingLatencyBackend latency)
    {
        foreach (Silk.NET.Vulkan.SwapchainKHR handle in latency.Swapchains) yield return handle.Handle;
    }

    /// <summary>
    /// The counter itself, without a GPU: one value per present, never reused,
    /// and the map answers with the frame that produced each id while it holds it.
    /// </summary>
    [Fact]
    public void ThePresentIdCounterAndMapArePlainMonotonicBookkeeping()
    {
        ulong first = PresentIdCounter.Next();
        ulong second = PresentIdCounter.Next();
        Assert.True(second > first);
        Assert.Equal(second, PresentIdCounter.Current);

        var map = new PresentIdMap(4);
        Assert.False(map.TryGetFrameId(1, out _));
        for (ulong i = 1; i <= 4; i++) map.Record(i, 100 + i);
        Assert.True(map.TryGetFrameId(3, out ulong frame));
        Assert.Equal(103UL, frame);
        Assert.Equal(4UL, map.LastPresentId);
        Assert.Equal(104UL, map.LastFrameId);

        // It wraps rather than growing; the oldest entry is the one that goes.
        map.Record(5, 105);
        Assert.False(map.TryGetFrameId(1, out _));
        Assert.True(map.TryGetFrameId(5, out frame));
        Assert.Equal(105UL, frame);
    }
}
}
