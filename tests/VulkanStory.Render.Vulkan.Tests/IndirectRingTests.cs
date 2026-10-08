using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Retained SubmissionTests indirect-ring regression at migration baseline.
/// <summary>Checks indirect-ring slot capacity and deferred growth at frame-slot recycling.</summary>
/// <remarks>CPU offset bookkeeping does not allocate or synchronize a native command buffer.</remarks>
public sealed class IndirectRingTests
{
    [Fact]
    public void IndirectRegionsDoNotWrapOrGrowUntilTheirSlotIsRecycled()
    {
        var ring = new IndirectRing(2, minimumCapacity: 100);
        ring.BeginFrame(0, out _); ring.Attach(100);
        for (ulong i = 0; i < 5; i++)
        {
            Assert.True(ring.TryAllocate(20, out ulong offset));
            Assert.Equal(i * 20, offset);
        }
        for (int i = 0; i < 3; i++) Assert.False(ring.TryAllocate(20, out _));
        Assert.Equal(100UL, ring.CapacityOf(0));
        ring.BeginFrame(1, out _);
        Assert.Equal(100UL, ring.CursorOf(0));
        Assert.True(ring.NeedsBuffer(20, out ulong capacity));
        Assert.True(capacity >= 160);
        ring.Attach(capacity); Assert.True(ring.TryAllocate(20, out _));
        Assert.True(ring.BeginFrame(0, out capacity));
        ring.Attach(capacity);
        for (ulong i = 0; i < 8; i++)
        {
            Assert.True(ring.TryAllocate(20, out ulong offset));
            Assert.Equal(i * 20, offset);
        }
        Assert.Equal(20UL, ring.CursorOf(1));
        Assert.Equal(0, ring.OverflowsOf(0));
    }
}
