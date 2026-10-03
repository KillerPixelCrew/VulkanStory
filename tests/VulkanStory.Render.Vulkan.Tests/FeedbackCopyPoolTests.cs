using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Retained ResourceLifetimeTests feedback-copy retirement regression.
public sealed class FeedbackCopyPoolTests
{
    private sealed class Clock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    [Fact]
    public void FeedbackCopiesWaitForCompletionBeforeReuseOrRetirement()
    {
        var clock = new Clock { FrameRecorded = 7 };
        int next = 0;
        var destroyed = new List<int>();
        var pool = new FeedbackCopyPool(clock, _ => ++next, destroyed.Add, idleFrames: 2);
        var shape = new FeedbackCopyDesc(16, 16, Format.R8G8B8A8Unorm, 1, 1, false);
        int first = pool.Acquire(shape);
        pool.Release(first); pool.EndFrame(); pool.Collect();
        int second = pool.Acquire(shape);
        Assert.NotEqual(first, second);
        Assert.Empty(destroyed);
        clock.FrameCompleted = 7; pool.Collect();
        Assert.Equal(first, pool.Acquire(shape));
        pool.Release(first); pool.Release(second); pool.EndFrame();
        for (int i = 0; i < 5; i++) pool.Collect();
        Assert.Equal(0, pool.Live);
        Assert.Contains(first, destroyed);
        Assert.Contains(second, destroyed);
    }
}
