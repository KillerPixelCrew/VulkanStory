using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

/// <summary>Checks CPU uniform shadows, byte-change versioning, frame snapshots, and named-block binding ownership.</summary>
/// <remarks>No Vulkan upload or draw is submitted.</remarks>
public sealed class ClientUniformBufferTests
{
    [Fact]
    public unsafe void IdenticalUploadsReuseSnapshotButChangedBytesAndNewFramesInvalidateIt()
    {
        var buffers = new ClientUniformBufferManager();
        int id = buffers.CreateUniformBuffer(1, 0, "Animation", 8);
        ClientUniformBuffer block = buffers.Buffers[id];
        byte[] upload = [1, 2, 3, 4];
        fixed (byte* source = upload)
        {
            buffers.UpdateUniformBuffer(id, (IntPtr)source, 0, upload.Length);
            block.NoteSnapshot(7, 128);
            uint version = block.Version;
            buffers.UpdateUniformBuffer(id, (IntPtr)source, 0, upload.Length);
            Assert.True(block.HasSnapshotFor(7));
            Assert.Equal(version, block.Version);
            Assert.Equal(128u, block.SnapshotOffset);
            Assert.False(block.HasSnapshotFor(8));
            upload[0] = 9;
            buffers.UpdateUniformBuffer(id, (IntPtr)source, 0, upload.Length);
            Assert.False(block.HasSnapshotFor(7));
            Assert.Equal(version + 1, block.Version);
            Assert.Equal(new byte[] { 9, 2, 3, 4, 0, 0, 0, 0 }, block.Shadow);
            block.NoteSnapshot(7, 256);
            Assert.True(block.HasSnapshotFor(7));
            Assert.Equal(256u, block.SnapshotOffset);
        }
    }

    [Fact]
    public unsafe void InvalidWritesLeaveShadowAndSnapshotUntouched()
    {
        var buffers = new ClientUniformBufferManager();
        int id = buffers.CreateUniformBuffer(1, 0, "Block", 8);
        ClientUniformBuffer block = buffers.Buffers[id];
        block.NoteSnapshot(3, 64);
        byte[] upload = [1, 2, 3, 4];
        fixed (byte* source = upload)
        {
            buffers.UpdateUniformBuffer(id, IntPtr.Zero, 0, 4);
            buffers.UpdateUniformBuffer(id, (IntPtr)source, -1, 4);
            buffers.UpdateUniformBuffer(id, (IntPtr)source, 0, -1);
            buffers.UpdateUniformBuffer(id, (IntPtr)source, 6, 4);
            buffers.UpdateUniformBuffer(id, (IntPtr)source, int.MaxValue, 4);
            buffers.UpdateUniformBuffer(999, (IntPtr)source, 0, 4);
            Assert.Equal(new byte[8], block.Shadow);
            Assert.True(block.HasSnapshotFor(3));
            buffers.UpdateUniformBuffer(id, (IntPtr)source, 4, 4);
            Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 2, 3, 4 }, block.Shadow);
            Assert.False(block.HasSnapshotFor(3));
        }
    }

    [Fact]
    public void NamedBlockBindingSurvivesGenericUnbindAndDeletionOfAnOlderBuffer()
    {
        var buffers = new ClientUniformBufferManager();
        int first = buffers.CreateUniformBuffer(1, 0, "Animation", 64);
        int second = buffers.CreateUniformBuffer(2, 0, "Animation", 64);
        Assert.Equal(second, buffers.BoundBlocks["Animation"]);
        buffers.BindUniformBuffer(first);
        buffers.UnbindUniformBuffer(first);
        Assert.Equal(first, buffers.BoundBlocks["Animation"]);
        buffers.BindUniformBuffer(second);
        buffers.DeleteUniformBuffer(first);
        Assert.Equal(second, buffers.BoundBlocks["Animation"]);
        buffers.DeleteUniformBuffer(second);
        Assert.False(buffers.BoundBlocks.ContainsKey("Animation"));
        Assert.Empty(buffers.Buffers);
    }
}
