using System;
using System.Collections.Generic;

namespace VulkanStory.Render.Vulkan.Core;

// Extracted unchanged uniform-buffer state from the retained device facade.
internal sealed unsafe class ClientUniformBuffer
{
    public ClientUniformBuffer(byte[] shadow, string blockName)
    {
        Shadow = shadow;
        BlockName = blockName;
    }

    public byte[] Shadow { get; }
    public string BlockName { get; }

    /// <summary>Bumped by every write, so an unchanged block reuses its snapshot.</summary>
    public uint Version { get; private set; } = 1;

    /// <summary>Which frame's ring the snapshot below lives in, and what it holds.</summary>
    public uint SnapshotFrame { get; private set; }
    public uint SnapshotVersion { get; private set; }
    public uint SnapshotOffset { get; private set; }

    public void Write(IntPtr data, int offset, int size)
    {
        // A client that re-uploads identical bytes before every draw would
        // otherwise cost a fresh ring slice per draw; comparing is cheaper.
        var incoming = new ReadOnlySpan<byte>((void*)data, size);
        Span<byte> target = Shadow.AsSpan(offset, size);
        if (incoming.SequenceEqual(target)) return;
        incoming.CopyTo(target);
        Version++;
    }

    public void NoteSnapshot(uint frame, uint offset)
    {
        SnapshotFrame = frame;
        SnapshotVersion = Version;
        SnapshotOffset = offset;
    }

    public bool HasSnapshotFor(uint frame) => SnapshotFrame == frame && SnapshotVersion == Version;

    // Retained per-draw copy into the frame's uniform arena. A partial submit
    // keeps the same frame identity and arena, so the snapshot remains valid.
    public bool TrySnapshot(uint frame, FrameSlot slot, out uint offset)
    {
        if (HasSnapshotFor(frame))
        {
            offset = SnapshotOffset;
            return true;
        }
        if (!slot.TryAllocateUniforms(Shadow.Length, out RingAllocation allocation))
        {
            offset = 0;
            return false;
        }
        fixed (byte* source = Shadow)
            System.Buffer.MemoryCopy(source, (void*)allocation.Pointer, Shadow.Length, Shadow.Length);
        NoteSnapshot(frame, allocation.Offset);
        offset = allocation.Offset;
        return true;
    }
}

internal sealed class ClientUniformBufferManager
{
    internal readonly Dictionary<int, ClientUniformBuffer> Buffers = new();

    /// <summary>
    /// The buffer currently supplying each named block.
    ///
    /// The client's UBO binds with glBindBufferBase to binding point 0 and names
    /// the block when it creates the buffer, so the block name is what actually
    /// identifies which declaration a buffer feeds. Vulkan has no such global
    /// binding point, so the association is kept here and resolved per draw
    /// against the program's own declared blocks.
    /// </summary>
    internal readonly Dictionary<string, int> BoundBlocks = new(StringComparer.Ordinal);

    private int _nextUniformBufferId = 1;

    public int CreateUniformBuffer(int programId, int bindingPoint, string blockName, int size)
    {
        int bytes = Math.Max(size, 4);
        int id = _nextUniformBufferId++;
        Buffers[id] = new ClientUniformBuffer(new byte[bytes], blockName ?? "");

        // GL's glBindBufferBase in the client's constructor takes effect at once,
        // and a buffer is only ever created to be used.
        if (!string.IsNullOrEmpty(blockName)) BoundBlocks[blockName] = id;
        return id;
    }

    public void UpdateUniformBuffer(int handle, IntPtr data, int offset, int size)
    {
        if (!Buffers.TryGetValue(handle, out ClientUniformBuffer? ubo)) return;
        if (data == IntPtr.Zero || offset < 0 || size < 0) return;
        if ((long)offset + size > ubo.Shadow.Length) return;

        ubo.Write(data, offset, size);
    }

    public void BindUniformBuffer(int handle)
    {
        if (Buffers.TryGetValue(handle, out ClientUniformBuffer? ubo) && ubo.BlockName.Length > 0)
        {
            BoundBlocks[ubo.BlockName] = handle;
        }
    }

    /// <summary>
    /// Deliberately does not break the block association.
    ///
    /// The client's Unbind is glBindBuffer(UNIFORM_BUFFER, 0), which clears the
    /// generic target and leaves the glBindBufferBase index binding standing -
    /// and the index binding is what feeds the shader. Dropping the association
    /// here would unbind the block the client still expects to be supplied.
    /// </summary>
    public void UnbindUniformBuffer(int handle) { }

    public void DeleteUniformBuffer(int handle)
    {
        if (!Buffers.Remove(handle, out ClientUniformBuffer? ubo)) return;

        if (ubo.BlockName.Length > 0 &&
            BoundBlocks.TryGetValue(ubo.BlockName, out int bound) && bound == handle)
        {
            BoundBlocks.Remove(ubo.BlockName);
        }
    }
}
