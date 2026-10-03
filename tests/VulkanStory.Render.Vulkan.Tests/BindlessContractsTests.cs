using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// CPU expectations carried from the original ShaderBindingTests.
public sealed class BindlessContractsTests
{
    private sealed class Clock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    [Fact]
    public void SlotsStayReservedUntilTheirFrameRetirementCompletes()
    {
        var clock = new Clock { FrameRecorded = 7, FrameCompleted = 5 };
        var book = new BindlessSlotBook(clock, Enumerable.Repeat(3u, BindlessKinds.Count).ToArray());
        BindlessSlotKey Key(ulong id) => new(id, TextureKind.Texture2D, SamplerState.Default,
            ImageLayout.ShaderReadOnlyOptimal);

        uint first = book.Acquire(Key(1), out bool created);
        Assert.True(created);
        Assert.NotEqual(0u, first);
        Assert.Equal(first, book.Acquire(Key(1), out created));
        Assert.False(created);
        Assert.Equal(1, book.Release(1));

        uint second = book.Acquire(Key(2), out _);
        Assert.NotEqual(first, second);
        Assert.NotEqual(0u, second);
        Assert.Equal(0u, book.Acquire(Key(3), out _));
        Assert.Equal(1, book.Exhausted);

        var freed = new List<(TextureKind Kind, uint Slot)>();
        clock.FrameCompleted = 6;
        Assert.Equal(0, book.Collect(freed));
        clock.FrameCompleted = 7;
        Assert.Equal(1, book.Collect(freed));
        Assert.Equal(new[] { (TextureKind.Texture2D, first) }, freed);
        Assert.Equal(first, book.Acquire(Key(3), out _));
    }

    [Fact]
    public void SamplerVariantsEvictTheLeastRecentlyUsedKey()
    {
        var book = new BindlessSlotBook(new Clock { FrameRecorded = 1 },
            Enumerable.Repeat(32u, BindlessKinds.Count).ToArray());
        BindlessSlotKey Key(int bias) => new(9, TextureKind.Texture2D,
            SamplerState.Default with { LodBias = bias }, ImageLayout.ShaderReadOnlyOptimal);

        uint first = book.Acquire(Key(0), out _);
        var slots = new HashSet<uint> { first };
        for (int i = 1; i < BindlessSlotBook.MaxVariantsPerTexture; i++)
            Assert.True(slots.Add(book.Acquire(Key(i), out _)));
        Assert.Equal(first, book.Acquire(Key(0), out _));

        book.Acquire(Key(BindlessSlotBook.MaxVariantsPerTexture), out _);
        Assert.Equal(1, book.PendingRetirements);
        Assert.Equal(first, book.Acquire(Key(0), out bool created));
        Assert.False(created);
        book.Acquire(Key(1), out created);
        Assert.True(created);
    }
}
