using System;
using System.Collections.Generic;

namespace VulkanStory.Render.Vulkan.Core;

// Extracted unchanged uniform-buffer state from the retained device facade.
/// <summary>CPU shadow of a named client uniform block, with versioned snapshots in the frame uniform arena.</summary>
internal sealed unsafe class ClientUniformBuffer
{
    /// <summary>Borrows a CPU shadow array and associates it with a shader block name.</summary>
    public ClientUniformBuffer(byte[] shadow, string blockName)
    {
        Shadow = shadow;
        BlockName = blockName;
    }

    /// <summary>Mutable CPU block bytes copied into a frame arena when the version changes.</summary>
    public byte[] Shadow { get; }
    /// <summary>Shader block name used to resolve the client buffer association.</summary>
    public string BlockName { get; }

    /// <summary>Bumped by every write, so an unchanged block reuses its snapshot.</summary>
    public uint Version { get; private set; } = 1;

    /// <summary>Which frame's ring the snapshot below lives in, and what it holds.</summary>
    public uint SnapshotFrame { get; private set; }
    /// <summary>CPU version captured by the latest frame-arena snapshot.</summary>
    public uint SnapshotVersion { get; private set; }
    /// <summary>Byte offset of the latest snapshot inside its frame uniform ring.</summary>
    public uint SnapshotOffset { get; private set; }

    /// <summary>Copies changed input bytes into the CPU shadow and increments the version only when bytes differ.</summary>
    /// <param name="data">Borrowed readable memory containing at least size bytes.</param>
    /// <param name="offset">Destination byte offset within the shadow.</param>
    /// <param name="size">Byte count to compare and copy.</param>
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

    /// <summary>Associates the current CPU version with the supplied frame and uniform-ring offset.</summary>
    public void NoteSnapshot(uint frame, uint offset)
    {
        SnapshotFrame = frame;
        SnapshotVersion = Version;
        SnapshotOffset = offset;
    }

    /// <summary>CPU version captured by the latest frame-arena snapshot.</summary>
    public bool HasSnapshotFor(uint frame) => SnapshotFrame == frame && SnapshotVersion == Version;

    // Retained per-draw copy into the frame's uniform arena. A partial submit
    // keeps the same frame identity and arena, so the snapshot remains valid.
    /// <summary>Reuses a current snapshot or copies the shadow into the current frame's uniform arena.</summary>
    /// <param name="frame">Renderer frame identity, retained across partial submissions.</param>
    /// <param name="slot">Current frame-slot uniform arena.</param>
    /// <param name="offset">Snapshot byte offset, or zero on exhaustion.</param>
    /// <returns>Whether a valid snapshot is available; false when the arena has no room.</returns>
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

/// <summary>Owns client uniform-buffer IDs and the named block-to-buffer associations resolved for each draw.</summary>
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

    /// <summary>Allocates a client buffer ID and at least four shadow bytes, immediately binding a nonempty block name.</summary>
    /// <returns>New client uniform-buffer ID.</returns>
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

    /// <summary>Writes a valid byte range into a known buffer; missing buffers, null data and invalid ranges are ignored.</summary>
    public void UpdateUniformBuffer(int handle, IntPtr data, int offset, int size)
    {
        if (!Buffers.TryGetValue(handle, out ClientUniformBuffer? ubo)) return;
        if (data == IntPtr.Zero || offset < 0 || size < 0) return;
        if ((long)offset + size > ubo.Shadow.Length) return;

        ubo.Write(data, offset, size);
    }

    /// <summary>Associates a known buffer with its nonempty block name for subsequent draws.</summary>
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

    /// <summary>Removes the shadow buffer and clears its block association only when that buffer remains bound.</summary>
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
