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

    [Fact]
    public void TheSecondPresentIsOffByDefaultAndCostsNothingWhenItIs()
    {
        string device = Read(DevicePath);

        // An env override, and nothing else, turns it on.
        Assert.Contains("internal const string GeneratedPresentVariable = \"OPTIMUM_DLSSG_DOUBLE_PRESENT\";", device);
        Assert.Contains("Environment.GetEnvironmentVariable(GeneratedPresentVariable)?.Trim() == \"1\"", device);

        // Every step of the second present is behind the switch: the acquire
        // short-circuits on it, and the present itself returns at once.
        Assert.Contains("bool generated = GeneratedPresentEnabled && _swapchain.TryAcquire(out generatedTarget);", device);
        Assert.Contains("if (!generated || _swapchain == null) return;", device);
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
