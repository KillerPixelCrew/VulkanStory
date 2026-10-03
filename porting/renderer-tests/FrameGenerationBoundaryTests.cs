using System.Diagnostics;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Present;
using Optimum.Render.Vulkan.Platform;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed class FrameGenerationBoundaryTests
{
    [Fact]
    public void MotionWindowWithoutTemporalResolveKeepsNativeProjectionUnjittered()
    {
        var frame = new OptimumTemporalFrame { JitterMagnitudeEnabled = false };
        frame.Advance(16.667f, 1920, 1080, 1f, 0.1f, 1000f, 1f, null);
        frame.JitterActive = true;
        Assert.Equal(0f, frame.JitterPx.X);
        Assert.Equal(0f, frame.JitterPx.Y);

        frame.JitterMagnitudeEnabled = true;
        frame.JitterActive = true;
        Assert.NotEqual(0f, frame.JitterPx.X);
    }

    [Fact]
    public void VulkanVsyncChangesDoNotCallTheOpenGlWindowSetter()
    {
        ClientPlatformAbstract platform = new VulkanClientPlatform(null!);
        platform.SetVSync(false);
        platform.SetVSync(true);
    }

    [Fact]
    public void RealFrameIsScheduledHalfwayAcrossRenderedFrameInterval()
    {
        long clock = Stopwatch.Frequency;
        var pacer = new GeneratedFramePacer(() => clock, _ => { });
        pacer.NoteGeneratedPresent();
        long firstTarget = pacer.PendingRealTargetTicks;
        Assert.InRange(firstTarget - clock, Stopwatch.Frequency / 130,
            Stopwatch.Frequency / 110);

        clock += Stopwatch.Frequency / 60;
        pacer.NoteGeneratedPresent();
        Assert.InRange(pacer.PendingRealTargetTicks - clock,
            Stopwatch.Frequency / 130, Stopwatch.Frequency / 110);

        pacer.Reset();
        Assert.Equal(0, pacer.PendingRealTargetTicks);
    }

    [Fact]
    public void FsrFrameGenerationFrameMatchesNativeBridgeAbi()
    {
        Assert.Equal(248, Marshal.SizeOf<Fsr3FgFrame>());
    }

    [Fact]
    public void PresentWaitCoversTheTransferReadOfTheRenderedImage()
    {
        Assert.True((PresentWaitStages.FrameWait & PipelineStageFlags.TransferBit) != 0);
    }

    [Fact]
    public void FrameGenerationWithVsyncOffAvoidsDroppingGeneratedImages()
    {
        PresentModeKHR[] modes = { PresentModeKHR.MailboxKhr, PresentModeKHR.ImmediateKhr, PresentModeKHR.FifoKhr };
        Assert.Equal(PresentModeKHR.MailboxKhr, SwapchainPolicy.ChoosePresentMode(false, false, modes));
        Assert.Equal(PresentModeKHR.ImmediateKhr, SwapchainPolicy.ChoosePresentMode(false, false, modes, true));
        Assert.Equal(PresentModeKHR.FifoKhr, SwapchainPolicy.ChoosePresentMode(false, false,
            new[] { PresentModeKHR.MailboxKhr, PresentModeKHR.FifoKhr }, true));
    }
}
