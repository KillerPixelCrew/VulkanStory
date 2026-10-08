using VulkanStory.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Carried from the source renderer's PresentationTests: no GPU required.
/// <summary>Checks simulated render/present retirement, acquire-semaphore reuse, and present fallback policy.</summary>
/// <remarks>Controlled timelines and handles do not establish native presentation completion.</remarks>
public sealed class SwapchainContractsTests
{
    /// <summary>Controlled timeline counters for render-versus-present retirement scenarios.</summary>
    private sealed class Clock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    /// <summary>Disposal callback probe representing a retired swapchain resource.</summary>
    private sealed class Resource(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Fact]
    public void RetiredSwapchainWaitsForRenderAndPresentCompletion()
    {
        var clock = new Clock();
        var retired = new SwapchainRetirement(clock);
        int disposed = 0;
        bool presented = false;
        retired.Retire(new Resource(() => disposed++), 7, () => presented);
        retired.NoteSuccessorReacquired(8);
        clock.FrameCompleted = 100;
        Assert.Equal(0, retired.Collect());
        presented = true;
        Assert.Equal(1, retired.Collect());
        Assert.Equal(1, disposed);
    }

    [Fact]
    public void AcquireSemaphoreWaitsForItsSubmissionTimeline()
    {
        var pool = new AcquireSemaphoreFreeList([11, 22]);
        ulong first = pool.Take(0), second = pool.Take(0);
        pool.ReturnAfter(first, 8);
        pool.Return(second);
        Assert.Equal(second, pool.Take(7));
        Assert.Throws<InvalidOperationException>(() => pool.Take(7));
        Assert.Equal(first, pool.Take(8));
    }

    [Fact]
    public void AcquireErrorsAndPresentModesKeepTheOriginalFallbackPolicy()
    {
        Assert.Equal(AcquireAction.PresentThenRebuild, SwapchainPolicy.OnAcquire(Result.SuboptimalKhr, 0));
        Assert.Equal(AcquireAction.RebuildAndRetry, SwapchainPolicy.OnAcquire(Result.ErrorOutOfDateKhr, 0));
        Assert.Equal(AcquireAction.SkipFrame, SwapchainPolicy.OnAcquire(Result.ErrorOutOfDateKhr, 1));
        Assert.Equal(AcquireAction.Fail, SwapchainPolicy.OnAcquire(Result.ErrorDeviceLost, 0));
        Assert.True(SwapchainPolicy.IsParked(new Extent2D(0, 20)));

        PresentModeKHR[] modes = [PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr,
            PresentModeKHR.FifoRelaxedKhr];
        Assert.Equal(PresentModeKHR.FifoKhr, SwapchainPolicy.ChoosePresentMode(true, false, modes));
        Assert.Equal(PresentModeKHR.FifoRelaxedKhr, SwapchainPolicy.ChoosePresentMode(true, true, modes));
        Assert.Equal(PresentModeKHR.MailboxKhr, SwapchainPolicy.ChoosePresentMode(false, false, modes));
        Assert.True((PresentWaitStages.FrameWait & PipelineStageFlags.TransferBit) != 0);
    }
}
