using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Steps 0 and 1 of the frame-generation design (docs/ROADMAP.md, "DLSS frame
/// generation: the design"): the second present of a frame, and the per-call
/// source that lets a present carry something other than the default colour.
///
/// These pin the seams a GPU test cannot see: that the switch is off by default,
/// that the generated present is stamped through the out-of-band markers rather
/// than the frame's own PresentStart/End, that both blits ride one submission,
/// and that no present path captures its source any more.
/// </summary>
public class DlssgPresentCoverageTests
{
    private const string DevicePath = "Optimum.Render.Vulkan/VulkanDevice.cs";
    private const string PresentPathPath = "Optimum.Render.Vulkan/Present/IPresentPath.cs";
    private const string RingPath = "Optimum.Render.Vulkan/Core/FrameRing.cs";
    private const string SwapchainPath = "Optimum.Render.Vulkan/Present/Swapchain.cs";
    private const string RetirementPath = "Optimum.Render.Vulkan/Present/SwapchainRetirement.cs";
    private const string NvBackendPath = "Optimum.Render.Vulkan/Latency/NvLowLatency2Backend.cs";

    [Fact]
    public void TheSecondPresentIsOffByDefaultAndCostsNothingWhenItIs()
    {
        string device = Read(DevicePath);

        // An env override, and nothing else, turns it on.
        Assert.Contains("internal const string GeneratedPresentVariable = \"OPTIMUM_DLSSG_DOUBLE_PRESENT\";", device);
        Assert.Contains("Environment.GetEnvironmentVariable(GeneratedPresentVariable)?.Trim() == \"1\"", device);

        // Every step of the second present is behind the switch: the acquire
        // short-circuits on it, and the present itself returns at once.
        Assert.Contains("bool generated = GeneratedPresentEnabled", device);
        Assert.Contains("&& _swapchain.TryAcquire(out generatedTarget, allowRebuild: false);", device);
        Assert.Contains("if (!generated || _swapchain == null) return;", device);
    }

    /// <summary>
    /// The review's first hazard, at the one place step 0 opened it: the window
    /// between the frame's two acquires. A rebuild there retires the slot whose
    /// image and acquire semaphore the frame already holds, against the previous
    /// frame's present value - so the semaphores this frame's present submission
    /// waits on can be destroyed under it. The second acquire is therefore
    /// forbidden from rebuilding, and only made at all while the chain can hand
    /// out two images at once.
    /// </summary>
    [Fact]
    public void TheSecondAcquireNeitherRebuildsTheChainNorOutrunsTheImageCount()
    {
        string device = Read(DevicePath);
        string swapchain = Read(SwapchainPath);
        string policy = Read(RetirementPath);

        // The device asks for the second image only when both are true.
        Assert.Contains("_swapchain.SimultaneousAcquireLimit >= PresentPressure.GeneratedPlusRealPerFrame", device);
        Assert.Contains("&& _swapchain.TryAcquire(out generatedTarget, allowRebuild: false);", device);

        // The swapchain honours it on both paths out of TryAcquire: the stale
        // chain at the top, and a rebuild-and-retry from the acquire result.
        Assert.Contains("public bool TryAcquire(out PresentTarget target, bool allowRebuild = true)", swapchain);
        Assert.Equal(2, Regex.Matches(swapchain, Regex.Escape("if (!allowRebuild) return false;")).Count);
        Assert.Equal(2, Regex.Matches(swapchain, Regex.Escape("if (!Build(out _)) return false;")).Count);

        // The limit is derived from what the WSI guarantees rather than assumed,
        // and the image count is untouched - a run with the switch off allocates
        // the images main allocates.
        Assert.Contains("uint imageCount = SwapchainPolicy.ChooseImageCount(\n            capabilities.MinImageCount, capabilities.MaxImageCount, presentMode);", swapchain.Replace("\r\n", "\n"));
        Assert.Contains("public static int SimultaneousAcquireLimit(uint imageCount, uint capabilitiesMin)", policy);
        Assert.Contains("long limit = (long)imageCount - capabilitiesMin + 1;", policy);
    }

    /// <summary>
    /// vkQueueNotifyOutOfBandNV marks the QUEUE and there is no call that marks
    /// it back, so marking the shared graphics queue would take every later
    /// in-band render submission out of Reflex's accounting. The backend refuses
    /// its own device's graphics queue; the call site stays for the present
    /// thread of design step 6, which has a queue of its own.
    /// </summary>
    [Fact]
    public void TheOutOfBandNotificationRefusesTheSharedGraphicsQueue()
    {
        string backend = Read(NvBackendPath);

        Assert.Contains("if (queue.Handle == _context.GraphicsQueue.Handle)", backend);
        Assert.Contains("OutOfBandNotifiesRefused++;", backend);
        Assert.Contains("OutOfBandNotifiesSent++;", backend);

        // The refusal is before the driver call, not after it.
        int refusal = backend.IndexOf("OutOfBandNotifiesRefused++;", StringComparison.Ordinal);
        int call = backend.IndexOf("_functions.QueueNotifyOutOfBand(queue, ref info);", StringComparison.Ordinal);
        Assert.True(refusal > 0 && call > refusal,
            "the graphics-queue refusal must come before vkQueueNotifyOutOfBandNV");
    }

    [Fact]
    public void TheGeneratedPresentUsesTheOutOfBandMarkersAndTheFramesOwnId()
    {
        string device = Read(DevicePath);

        // The frame's own markers stay on the one real present (the latency
        // coverage test pins that they appear exactly once); the generated
        // present gets the pair reserved for work outside the frame loop.
        Assert.Contains("Latency.Marker(_latencyFrameId, LatencyMarker.OutOfBandPresentStart);", device);
        Assert.Contains("Latency.Marker(_latencyFrameId, LatencyMarker.OutOfBandPresentEnd);", device);
        Assert.Single(Regex.Matches(device, Regex.Escape("LatencyMarker.OutOfBandPresentStart")));
        Assert.Single(Regex.Matches(device, Regex.Escape("LatencyMarker.OutOfBandPresentEnd")));

        // Same frame id, so the present id map answers "which frame" for both
        // presents; and the driver is told the queue is out of band where it can be.
        Assert.Contains("_swapchain.Present(generatedTarget, _latencyFrameId);", device);
        Assert.Contains("NotifyOutOfBandPresent(_context.GraphicsQueue);", device);

        // Not Latency.OnPresent: a generated present does not close the frame's
        // latency report, and the NV backend predicts one present id per frame.
        Assert.Single(Regex.Matches(device, Regex.Escape("Latency.OnPresent(")));
    }

    [Fact]
    public void BothBlitsRideOnePresentSubmission()
    {
        string device = Read(DevicePath);
        string ring = Read(RingPath);

        // One BeginPresentCommands, one SubmitPresent: a second command buffer
        // would read the frame image after the first transitioned it, with
        // nothing but submission order in between.
        Assert.Single(Regex.Matches(device, Regex.Escape("_frames.BeginPresentCommands()")));
        Assert.Single(Regex.Matches(device, Regex.Escape("_frames.SubmitPresent(")));

        // The submission waits on both acquire semaphores and signals both
        // present semaphores, at the one acquire stage the rule allows.
        Assert.Contains("PresentWaitStages.RequireAcquireStage(acquireStage);", ring);
        Assert.Contains("if (secondWaitSemaphore.Handle != 0)", ring);
        Assert.Contains("if (secondSignalSemaphore.Handle != 0)", ring);
        Assert.Contains("waitStages[waitCount] = waitStage;", ring);
        Assert.DoesNotContain("PipelineStageFlags.AllCommandsBit", ring);
    }

    [Fact]
    public void ThePresentSourceIsPerCallAndNoPathCapturesIt()
    {
        string present = Read(PresentPathPath);
        string device = Read(DevicePath);

        Assert.Contains("void Record(CommandBuffer commandBuffer, in PresentTarget target, VulkanTexture? source);", present);
        Assert.Contains("public void Record(CommandBuffer commandBuffer, in PresentTarget target, VulkanTexture? source)", present);

        // The constructor-captured delegate is gone.
        Assert.DoesNotContain("Func<VulkanTexture?>", present);
        Assert.Contains("public BlitPresentPath(VulkanContext context, TextureManager textures)", present);
        Assert.Contains("_presentPath = new BlitPresentPath(_context, _textures);", device);

        // Behaviour unchanged: every caller still passes the default colour.
        Assert.Contains("VulkanTexture? presentSource = DefaultColorTexture();", device);
        Assert.Contains("_presentPath.Record(presentCommands, target, presentSource);", device);
        Assert.Contains("_presentPath.Record(presentCommands, generatedTarget, presentSource);", device);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
