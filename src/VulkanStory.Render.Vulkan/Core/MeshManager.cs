using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using EnumDrawMode = VulkanStory.Contracts.MeshDrawMode;
using DataConversion = VulkanStory.Contracts.MeshDataConversion;
using CustomMeshDataPartFloat = VulkanStory.Contracts.MeshCustomPartLayout;
using CustomMeshDataPartShort = VulkanStory.Contracts.MeshCustomPartLayout;
using CustomMeshDataPartInt = VulkanStory.Contracts.MeshCustomPartLayout;
using CustomMeshDataPartByte = VulkanStory.Contracts.MeshCustomPartLayout;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>
/// A mesh: one buffer per attribute, plus indices.
///
/// The per-attribute layout is not a choice - it is how the game allocates. Its
/// mesh allocator makes a separate GL buffer for positions, normals, UVs,
/// colours and flags, and only the four "custom" parts are interleaved. Matching
/// that exactly is what lets the existing upload paths, including the
/// persistently mapped writes the chunk tesselator does, work unchanged.
/// </summary>
internal sealed class VulkanMesh : IDisposable
{
    public VulkanBuffer?[] Buffers { get; } = new VulkanBuffer?[MeshManager.MaxBuffers];
    public VulkanBuffer? Indices { get; set; }

    public int IndexCount { get; set; }
    public EnumDrawMode DrawMode { get; set; } = EnumDrawMode.Triangles;
    public bool Persistent { get; set; }
    public bool Ssbo { get; set; }

    public VertexLayoutDescription Layout { get; set; } = VertexLayoutDescription.Empty;
    public int LayoutId { get; set; } = -1;

    /// <summary>Which buffers actually feed vertex bindings, in binding order.</summary>
    public List<int> BindingOrder { get; } = new();

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (VulkanBuffer? buffer in Buffers) buffer?.Dispose();
        Array.Clear(Buffers);
        Indices?.Dispose();
        Indices = null;
    }
}

/// <summary>
/// Owns meshes and hands out integer ids, mirroring the GL VAO the game's
/// <c>MeshRef</c> wraps.
/// </summary>
internal sealed unsafe class MeshManager : IDisposable
{
    /// <summary>xyz, normals, uv, rgba, flags, then the four custom parts.</summary>
    public const int MaxBuffers = 10;
    private const int BufferParticleHistory = 9;

    public const int BufferXyz = 0;
    public const int BufferNormals = 1;
    public const int BufferUv = 2;
    public const int BufferRgba = 3;
    public const int BufferFlags = 4;
    public const int BufferCustomFloat = 5;
    public const int BufferCustomShort = 6;
    public const int BufferCustomInt = 7;
    public const int BufferCustomByte = 8;

    private const int GlUnsignedByte = 0x1401;
    private const int GlShort = 0x1402;
    private const int GlUnsignedShort = 0x1403;
    private const int GlUnsignedInt = 0x1405;

    /// <summary>
    /// The flags and custom-int attributes are fed to shader inputs declared
    /// <c>in int</c>. GL let an unsigned pointer feed a signed input - it
    /// reinterprets - but Vulkan requires the attribute format's numeric type to
    /// match the shader's exactly, so these are signed here.
    /// </summary>
    private const int GlInt = 0x1404;

    private const int GlFloat = 0x1406;
    private const int GlInt2101010Rev = 0x8D9F;

    private readonly VulkanContext _context;
    private readonly UploadManager? _uploads;
    private readonly FrameRing? _frames;
    /// <summary>Invalidates descriptor references when mapped storage is renamed.</summary>
    internal Action<ulong>? BufferRetired { get; set; }
    private readonly Interner<VertexLayoutDescription> _layouts = new();
    private readonly List<VulkanMesh?> _meshes = new();
    private readonly Stack<int> _freeIds = new();
    private bool _disposed;

    /// <summary>
    /// The layout of a pass with no vertex buffers, reserved as id 0.
    ///
    /// The fullscreen post-processing passes generate their vertices from
    /// gl_VertexIndex and bind nothing, but they still need a layout id for the
    /// pipeline key. Interning the empty layout first guarantees the id exists
    /// even before any mesh has been created.
    /// </summary>
    public const int EmptyLayoutId = 0;

    /// <summary>
    /// Static meshes on device-local memory, filled through the upload manager's
    /// staging instead of a host mapping. On since Phase 1B step 5 moved static
    /// meshes off ReBAR; it only takes effect with an upload manager.
    /// </summary>
    internal bool DeviceLocalStaticBuffers { get; set; } = true;

    /// <summary>
    /// Persistent (mapped, game-written) meshes in device-local host-visible memory.
    /// Only with a BAR heap of at least 1 GiB; <c>VULKANSTORY_VK_PERSISTENT_MESH_VRAM=0</c>
    /// keeps them in system memory. Cleared after the first allocation that does not fit.
    /// </summary>
    internal bool PersistentMeshesInVram { get; set; }
    internal long PersistentMeshHeadroomMisses { get; private set; }

    public MeshManager(VulkanContext context, UploadManager? uploads = null, FrameRing? frames = null)
    {
        _context = context;
        _uploads = uploads;
        _frames = frames;
        _meshes.Add(null);   // 0 is never a real mesh
        PersistentMeshesInVram = uploads != null &&
            Environment.GetEnvironmentVariable("VULKANSTORY_VK_PERSISTENT_MESH_VRAM") != "0" &&
            context.Allocator.HasLargeHostVisibleDeviceMemory(1UL << 30);

        int emptyId = _layouts.Intern(VertexLayoutDescription.Empty);
        if (emptyId != EmptyLayoutId)
        {
            throw new InvalidOperationException("the empty vertex layout must intern first");
        }
    }

    /// <summary>Returns a live mesh for a positive in-range ID, otherwise null.</summary>
    public VulkanMesh? Get(int id) => id > 0 && id < _meshes.Count ? _meshes[id] : null;

    public int Count
    {
        get
        {
            int live = 0;
            foreach (VulkanMesh? mesh in _meshes)
            {
                if (mesh != null) live++;
            }
            return live;
        }
    }

    /// <summary>
    /// Creates a mesh with the given per-part byte sizes, matching the shape of
    /// the game's AllocateEmptyMesh. A part with size 0 is absent, and absent
    /// parts do not consume an attribute location - which is what makes the chunk
    /// shaders' location numbering line up without any per-shader knowledge here.
    /// </summary>
    public int CreateEmpty(
        int xyzSize, int normalsSize, int uvSize, int rgbaSize, int flagsSize, int indicesSize,
        CustomMeshDataPartFloat? customFloats, CustomMeshDataPartShort? customShorts,
        CustomMeshDataPartByte? customBytes, CustomMeshDataPartInt? customInts,
        EnumDrawMode drawMode, bool staticDraw, bool ssbo, bool signedCustomShorts = false)
    {
        var mesh = new VulkanMesh
        {
            DrawMode = drawMode,
            Persistent = !staticDraw,
            Ssbo = ssbo,
        };

        var builder = new VertexLayoutBuilder();
        try
        {

        // The order here is the order the GL allocator assigns attribute slots.
        // With SSBO vertex fetch the xyz slot holds packed face records rather
        // than positions: one 64-byte record per four vertices, so 16 bytes per
        // vertex where a position is 12. GL sizes that buffer as xyzSize / 12 * 16
        // and so must this, or the last quarter of every pool is out of range -
        // which robust buffer access reads back as zeros, collapsing those faces
        // onto the origin and stretching their neighbours across the screen.
        int xyzSlotSize = ssbo ? xyzSize / 12 * 16 : xyzSize;
        AddDedicated(mesh, builder, BufferXyz, xyzSlotSize, 3, GlFloat, normalized: false, integer: false, ssbo);

        // Normals, uv and flags have no vertex binding on the SSBO path: their
        // contents ride in the packed face records instead, and GL's SSBO
        // allocator creates neither a buffer nor an attribute pointer for them.
        // Adding one here would push rgba off location 0, so the shader's
        // rgbaLightIn would read the uv stream - block light taken from atlas
        // coordinates, which tints the terrain by texture position.
        AddDedicated(mesh, builder, BufferNormals, ssbo ? 0 : normalsSize, 4, GlInt2101010Rev, normalized: true, integer: false, ssbo);
        AddDedicated(mesh, builder, BufferUv, ssbo ? 0 : uvSize, 2, GlFloat, normalized: false, integer: false, ssbo);
        AddDedicated(mesh, builder, BufferRgba, rgbaSize, 4, GlUnsignedByte, normalized: true, integer: false, ssbo);
        AddDedicated(mesh, builder, BufferFlags, ssbo ? 0 : flagsSize, 1, GlInt, normalized: false, integer: true, ssbo);

        AddCustom(mesh, builder, BufferCustomFloat, customFloats?.AllocationSize * 4 ?? 0,
            customFloats?.InterleaveSizes, customFloats?.InterleaveOffsets,
            customFloats?.InterleaveStride ?? 0, GlFloat, false, false,
            customFloats?.Instanced ?? false);

        // AllocateEmptyMesh/AddCustoms uses GL_UNSIGNED_SHORT for float
        // inputs, including the packed secondary UVs of topsoil. UploadMesh
        // uses GL_SHORT instead (legacy clouds rely on signed offsets).
        // Integer inputs use GL_SHORT on both paths.
        int shortType = signedCustomShorts || customShorts?.Conversion == DataConversion.Integer
            ? GlShort : GlUnsignedShort;
        AddCustom(mesh, builder, BufferCustomShort, customShorts?.AllocationSize * 2 ?? 0,
            customShorts?.InterleaveSizes, customShorts?.InterleaveOffsets,
            customShorts?.InterleaveStride ?? 0, shortType,
            customShorts?.Conversion == DataConversion.NormalizedFloat,
            customShorts?.Conversion == DataConversion.Integer,
            customShorts?.Instanced ?? false);

        (int intBytes, int[]? intSizes, int[]? intOffsets, int intStride) =
            PruneCustomInts(customInts, ssbo);

        AddCustom(mesh, builder, BufferCustomInt, intBytes, intSizes, intOffsets, intStride, GlInt,
            customInts?.Conversion == DataConversion.NormalizedFloat,
            customInts?.Conversion == DataConversion.Integer,
            customInts?.Instanced ?? false);

        AddCustom(mesh, builder, BufferCustomByte, customBytes?.AllocationSize ?? 0,
            customBytes?.InterleaveSizes, customBytes?.InterleaveOffsets,
            customBytes?.InterleaveStride ?? 0, GlUnsignedByte,
            customBytes?.Conversion == DataConversion.NormalizedFloat,
            customBytes?.Conversion == DataConversion.Integer,
            customBytes?.Instanced ?? false);

        if (indicesSize > 0)
        {
            mesh.Indices = CreateBuffer(indicesSize, BufferUsageFlags.IndexBufferBit, mesh.Persistent);
            mesh.IndexCount = indicesSize / sizeof(int);
            if (ssbo) FillQuadIndices(mesh.Indices);
        }

        mesh.Layout = builder.Build();
        mesh.LayoutId = _layouts.Intern(mesh.Layout);

        return Register(mesh);
        }
        catch
        {
            mesh.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The custom-int part as the SSBO path sees it. GL prunes it there: the
    /// first interleaved member is the colormap data, which the face record now
    /// carries, so it is dropped, the rest tighten onto half the stride and the
    /// buffer halves with them. A part that only ever had that one member is
    /// dropped entirely. Off the SSBO path the part passes through unchanged.
    /// </summary>
    private static (int Bytes, int[]? Sizes, int[]? Offsets, int Stride) PruneCustomInts(
        CustomMeshDataPartInt? customInts, bool ssbo)
    {
        if (customInts == null) return (0, null, null, 0);

        int bytes = customInts.AllocationSize * 4;
        int[]? sizes = customInts.InterleaveSizes;
        int[]? offsets = customInts.InterleaveOffsets;
        int stride = customInts.InterleaveStride;

        if (!ssbo) return (bytes, sizes, offsets, stride);

        if (stride <= 4 || sizes == null || sizes.Length < 2) return (0, null, null, 0);

        // Member k reads what member k - 1 used to, because dropping the first
        // one shifts every remaining offset down a slot.
        return (bytes / 2, sizes[1..], offsets?[..^1], stride / 2);
    }

    private void AddDedicated(
        VulkanMesh mesh, VertexLayoutBuilder builder, int slot, int byteSize,
        int components, int glType, bool normalized, bool integer, bool ssbo)
    {
        if (byteSize <= 0) return;

        // The SSBO path reads positions through a storage buffer rather than the
        // vertex input, so that buffer needs the extra usage bit.
        BufferUsageFlags usage = BufferUsageFlags.VertexBufferBit;
        if (ssbo && slot == BufferXyz) usage |= BufferUsageFlags.StorageBufferBit;

        mesh.Buffers[slot] = CreateBuffer(byteSize, usage, mesh.Persistent);

        // With SSBO vertex fetch the position buffer is not a vertex binding.
        if (ssbo && slot == BufferXyz) return;

        mesh.BindingOrder.Add(slot);
        builder.AddDedicated(
            VertexLayoutBuilder.FormatFor(components, glType, normalized, integer),
            VertexLayoutBuilder.SizeOf(components, glType));
    }

    private void AddCustom(
        VulkanMesh mesh, VertexLayoutBuilder builder, int slot, int byteSize,
        int[]? interleaveSizes, int[]? interleaveOffsets, int stride,
        int glType, bool normalized, bool integer, bool instanced)
    {
        // Presence of the part decides whether it takes an attribute location,
        // not how much data it currently holds. The GL allocator does the same:
        // it adds the attribute pointers whenever the part is non-null, and
        // AllocationSize returns Count, which is zero for a part that will be
        // filled after allocation. Gating on size here would shift every later
        // location and silently misfeed the shader.
        if (interleaveSizes == null || interleaveSizes.Length == 0) return;

        // Vulkan rejects a zero-sized buffer, so an empty part still gets a
        // minimal allocation to keep the binding valid.
        mesh.Buffers[slot] = CreateBuffer(
            Math.Max(byteSize, 4), BufferUsageFlags.VertexBufferBit, mesh.Persistent);
        mesh.BindingOrder.Add(slot);

        var members = new (Format, uint)[interleaveSizes.Length];
        uint packedStride = 0;
        for (int i = 0; i < interleaveSizes.Length; i++)
        {
            uint offset = interleaveOffsets != null && i < interleaveOffsets.Length
                ? (uint)interleaveOffsets[i]
                : packedStride;

            members[i] = (VertexLayoutBuilder.FormatFor(interleaveSizes[i], glType, normalized, integer), offset);
            packedStride += VertexLayoutBuilder.SizeOf(interleaveSizes[i], glType);
        }

        builder.AddInterleaved(members, stride > 0 ? (uint)stride : packedStride, instanced);
    }

    private VulkanBuffer CreateBuffer(int byteSize, BufferUsageFlags usage, bool persistent)
    {
        // Phase 1B step 5: a static mesh lives in device-local memory (a type
        // that is not host visible, when the device has one), filled through the
        // upload manager's staging. It never takes ReBAR, which holds only
        // per-frame dynamic data.
        if (!persistent && DeviceLocalStaticBuffers && _uploads != null)
        {
            return new VulkanBuffer(_context, (ulong)byteSize, usage | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
        }

        // A dynamic mesh is host visible and stays mapped, because the game
        // writes straight through the pointer while the GPU may still be
        // reading - the same lack of synchronisation GL allowed and the chunk
        // tesselator relies on. With resizable BAR it is mapped VRAM: chunk pools
        // are read by every chunk pass, and from system memory each vertex fetch
        // crosses PCIe (docs/performance-profile-2026-09-26.md). The CPU only
        // writes these buffers, which write-combined VRAM handles well. When the
        // BAR heap is full the allocation falls back to system memory.
        bool meshHeadroom = !persistent || !PersistentMeshesInVram ||
            _context.Allocator.HasPersistentMeshHeadroom((ulong)byteSize);
        if (!meshHeadroom) PersistentMeshHeadroomMisses++;
        if (persistent && PersistentMeshesInVram && meshHeadroom)
        {
            try
            {
                return new VulkanBuffer(_context, (ulong)byteSize, usage | BufferUsageFlags.TransferDstBit,
                    MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit |
                    MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.DeviceBuffers);
            }
            catch (InvalidOperationException)
            {
                PersistentMeshesInVram = false;
            }
        }

        // A static mesh with no upload manager to stage through (component tests)
        // is host visible too, still off ReBAR.
        return new VulkanBuffer(_context, (ulong)byteSize, usage | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.DeviceBuffers);
    }

    /// <summary>Adds a separate previous-particle instance stream without changing the original game's buffer layout.</summary>
    internal void UpdateParticleHistory(int meshId, float[] values)
    {
        VulkanMesh mesh = Get(meshId) ?? throw new InvalidOperationException("Particle history lost its mesh.");
        int bytes = checked(values.Length * sizeof(float));
        if (bytes == 0) return;
        VulkanBuffer? existing = mesh.Buffers[BufferParticleHistory];
        if (existing == null || existing.Size < (ulong)bytes)
        {
            if (existing != null)
            {
                BufferRetired?.Invoke(existing.Id);
                if (_frames == null) throw new InvalidOperationException("Particle history requires a frame retirement owner.");
                _frames.DeferDeletion(existing);
            }
            mesh.Buffers[BufferParticleHistory] = CreateBuffer(bytes, BufferUsageFlags.VertexBufferBit, persistent: false);
            if (existing == null)
            {
                uint binding = (uint)mesh.Layout.Bindings.Length;
                var bindings = mesh.Layout.Bindings.Append(new VertexBinding(binding, 44, true)).ToArray();
                var attributes = mesh.Layout.Attributes.Concat(new[]
                {
                    new VertexAttribute(9, binding, Format.R32G32B32Sfloat, 0),
                    new VertexAttribute(10, binding, Format.R32G32B32Sfloat, 12),
                    new VertexAttribute(11, binding, Format.R32G32B32A32Sfloat, 24),
                    new VertexAttribute(12, binding, Format.R32Sfloat, 40),
                }).ToArray();
                mesh.Layout = new VertexLayoutDescription(bindings, attributes);
                mesh.LayoutId = _layouts.Intern(mesh.Layout);
                mesh.BindingOrder.Add(BufferParticleHistory);
            }
        }
        fixed (float* source = values) Write(meshId, BufferParticleHistory, 0, (IntPtr)source, bytes);
    }

    private int Register(VulkanMesh mesh)
    {
        if (_freeIds.Count > 0)
        {
            int reused = _freeIds.Pop();
            _meshes[reused] = mesh;
            return reused;
        }

        _meshes.Add(mesh);
        return _meshes.Count - 1;
    }

    /// <summary>Whether the mesh fetches its vertices through a storage buffer.</summary>
    public bool IsSsbo(int meshId) => Get(meshId)?.Ssbo ?? false;

    internal ulong LiveBufferBytes
    {
        get
        {
            ulong bytes = 0;
            foreach (VulkanMesh? mesh in _meshes)
            {
                if (mesh == null) continue;
                bytes += mesh.Indices?.Size ?? 0;
                foreach (VulkanBuffer? buffer in mesh.Buffers) bytes += buffer?.Size ?? 0;
            }
            return bytes;
        }
    }

    /// <summary>
    /// Fills an SSBO mesh's index buffer with the fixed quad pattern.
    ///
    /// GL keeps one shared static index buffer for every SSBO mesh, written once
    /// with this pattern: each four consecutive vertices are a quad, drawn as the
    /// triangles (0,1,2) and (0,2,3). Its chunk update path never uploads indices
    /// on that route, so this fill is the only source of them here, as it is
    /// there.
    /// </summary>
    private void FillQuadIndices(VulkanBuffer indices)
    {
        int count = (int)(indices.Size / sizeof(int));
        if (indices.Mapped == IntPtr.Zero)
        {
            if (_uploads == null) return;
            var pattern = new int[count];
            fixed (int* source = pattern)
            {
                FillQuadPattern(source, count);
                _uploads.UploadToBuffer(indices, 0, (IntPtr)source, (ulong)count * sizeof(int));
            }
            return;
        }

        FillQuadPattern((int*)indices.Mapped, count);
    }

    private static void FillQuadPattern(int* destination, int count)
    {
        for (int i = 0; i + 5 < count; i += 6)
        {
            int quad = i / 6 * 4;
            destination[i] = quad;
            destination[i + 1] = quad + 1;
            destination[i + 2] = quad + 2;
            destination[i + 3] = quad;
            destination[i + 4] = quad + 2;
            destination[i + 5] = quad + 3;
        }
    }

    /// <summary>Returns the requested live mesh buffer, or null when the mesh/slot has no buffer.</summary>
    public VulkanBuffer? BufferOf(int meshId, int slot)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null) return null;
        return slot < 0 ? mesh.Indices : mesh.Buffers[slot];
    }

    /// <summary>Returns a borrowed mapped mesh-buffer pointer, or zero when no mapped buffer exists.</summary>
    public IntPtr MappedPointer(int meshId, int slot)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null) return IntPtr.Zero;

        if (slot >= MaxBuffers) return IntPtr.Zero;
        VulkanBuffer? buffer = slot < 0 ? mesh.Indices : mesh.Buffers[slot];
        if (buffer == null || buffer.Mapped == IntPtr.Zero) return IntPtr.Zero;
        return WritableBuffer(mesh, slot, buffer).Mapped;
    }

    /// <summary>
    /// Copies data into one of a mesh's buffers at a byte offset.
    ///
    /// A write that cannot land - no such buffer, not mapped, or past the end -
    /// is counted and traced rather than dropped in silence. GL would raise
    /// GL_INVALID_VALUE for the same glBufferSubData; a quiet return here turned
    /// a sizing mistake into "the terrain is simply not there", with nothing in
    /// any log to say why.
    /// </summary>
    public void Write(int meshId, int slot, int byteOffset, IntPtr source, int byteCount)
    {
        if (source == IntPtr.Zero || byteCount <= 0) return;

        VulkanMesh? mesh = Get(meshId);
        VulkanBuffer? buffer = mesh == null ? null : slot < 0 ? mesh.Indices : mesh.Buffers[slot];

        string? problem =
            mesh == null ? "no such mesh" :
            buffer == null ? "mesh has no buffer in that slot" :
            buffer.Mapped == IntPtr.Zero && _uploads == null ? "buffer is not host mapped" :
            byteOffset < 0 ? "negative offset" :
            (ulong)byteOffset + (ulong)byteCount > buffer.Size
                ? "write ends past the buffer (" + buffer.Size + " bytes)"
                : null;

        if (problem != null)
        {
            VulkanStats.NoteDroppedMeshWrite();
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write("mesh write dropped: mesh " + meshId + " slot " + slot +
                    " offset " + byteOffset + " bytes " + byteCount + ": " + problem);
            }
            return;
        }

        if (buffer!.Mapped == IntPtr.Zero)
        {
            // Device-local: staged and copied, never waited on.
            _uploads!.UploadToBuffer(buffer, (ulong)byteOffset, source, (ulong)byteCount);
            return;
        }

        buffer = WritableBuffer(mesh!, slot, buffer);
        System.Buffer.MemoryCopy(
            (void*)source, (void*)(buffer.Mapped + byteOffset), byteCount, byteCount);
    }

    /// <summary>Renames previously recorded mapped storage before CPU writes; old command buffers retain their own bytes.</summary>
    /// <remarks>A returned raw pointer is borrowed for writes before the next draw/update. Clients must request it again after GPU use.</remarks>
    private VulkanBuffer WritableBuffer(VulkanMesh mesh, int slot, VulkanBuffer buffer)
    {
        if (buffer.FrameUse == 0) return buffer;
        if (_frames == null) throw new InvalidOperationException("Mapped GPU storage requires a frame retirement owner before reuse.");
        var replacement = new VulkanBuffer(_context, buffer.Size, buffer.Usage,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        System.Buffer.MemoryCopy((void*)buffer.Mapped, (void*)replacement.Mapped, (long)buffer.Size, (long)buffer.Size);
        if (slot < 0) mesh.Indices = replacement;
        else mesh.Buffers[slot] = replacement;
        BufferRetired?.Invoke(buffer.Id);
        _frames.DeferDeletion(buffer);
        return replacement;
    }

    /// <summary>Removes the mesh ID and releases its resources immediately or through the supplied frame ring.</summary>
    public void Delete(int meshId, FrameRing? ring = null)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null) return;

        _meshes[meshId] = null;
        _freeIds.Push(meshId);

        if (ring != null) ring.DeferDeletion(mesh);
        else mesh.Dispose();
    }

    // --------------------------------------------------------------------- draw

    /// <summary>Binds the mesh's vertex and index buffers.</summary>
    public void Bind(CommandBuffer commandBuffer, VulkanMesh mesh)
    {
        Vk api = _context.Api;

        // A staged write to a buffer this frame command buffer already drew from
        // has to go inline to keep GL's order; see UploadManager.
        if (_uploads != null)
        {
            foreach (VulkanBuffer? buffer in mesh.Buffers)
            {
                if (buffer != null) _uploads.NoteUse(commandBuffer, buffer);
            }
            if (mesh.Indices != null) _uploads.NoteUse(commandBuffer, mesh.Indices);
        }

        if (mesh.BindingOrder.Count > 0)
        {
            var buffers = new Buffer[mesh.BindingOrder.Count];
            var offsets = new ulong[mesh.BindingOrder.Count];
            for (int i = 0; i < mesh.BindingOrder.Count; i++)
            {
                buffers[i] = mesh.Buffers[mesh.BindingOrder[i]]!.Handle;
            }

            fixed (Buffer* buffersPtr = buffers)
            fixed (ulong* offsetsPtr = offsets)
            {
                api.CmdBindVertexBuffers(commandBuffer, 0, (uint)buffers.Length, buffersPtr, offsetsPtr);
            }
        }

        if (mesh.Indices != null)
        {
            // Always 32-bit: the game's index arrays are int[].
            api.CmdBindIndexBuffer(commandBuffer, mesh.Indices.Handle, 0, IndexType.Uint32);
        }
    }

    /// <summary>Records a live mesh draw using its stored topology and index count; missing/empty meshes are ignored.</summary>
    public void Draw(CommandBuffer commandBuffer, int meshId, int instanceCount = 1)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null || mesh.IndexCount == 0) return;

        Bind(commandBuffer, mesh);
        _context.Api.CmdDrawIndexed(commandBuffer, (uint)mesh.IndexCount, (uint)instanceCount, 0, 0, 0);
    }

    /// <summary>
    /// The multidraw the chunk renderer issues once per pool, replacing
    /// glMultiDrawElements. Records go through an indirect buffer.
    /// </summary>
    public void DrawMulti(
        CommandBuffer commandBuffer, int meshId,
        int[] indicesStarts, int[] indicesSizes, int groupCount, VulkanBuffer indirectScratch,
        ulong indirectOffset)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null || groupCount <= 0) return;

        Bind(commandBuffer, mesh);

        if (indirectScratch.Mapped == IntPtr.Zero || indirectOffset >= indirectScratch.Size) return;
        var commands = (DrawIndexedIndirectCommand*)(indirectScratch.Mapped + (nint)indirectOffset);

        int capacity = (int)((indirectScratch.Size - indirectOffset) / (ulong)sizeof(DrawIndexedIndirectCommand));
        int count = Math.Min(groupCount, capacity);

        if (count < groupCount && RenderTrace.Enabled)
        {
            RenderTrace.Write("mesh indirect draw clamped: mesh " + meshId + " groupCount " + groupCount +
                " capacity " + capacity);
        }

        WriteIndirectCommands(new Span<DrawIndexedIndirectCommand>(commands, count), indicesStarts, indicesSizes);

        _context.Api.CmdDrawIndexedIndirect(commandBuffer, indirectScratch.Handle, indirectOffset, (uint)count,
            (uint)sizeof(DrawIndexedIndirectCommand));
    }

    internal static void WriteIndirectCommands(
        Span<DrawIndexedIndirectCommand> commands, ReadOnlySpan<int> indicesStarts, ReadOnlySpan<int> indicesSizes)
    {
        for (int i = 0; i < commands.Length; i++)
        {
            // MeshDataPool passes GL's 64-bit pointer array in an int[]. Each
            // offset occupies two words, unlike the tightly packed counts.
            ulong byteOffset = (uint)indicesStarts[i * 2] | ((ulong)(uint)indicesStarts[i * 2 + 1] << 32);
            commands[i] = new DrawIndexedIndirectCommand
            {
                IndexCount = (uint)indicesSizes[i],
                InstanceCount = 1,
                // GL takes a byte offset; Vulkan takes an index count.
                FirstIndex = checked((uint)(byteOffset / sizeof(int))),
                VertexOffset = 0,
                FirstInstance = 0,
            };
        }
    }

    /// <summary>Returns a live mesh for a positive in-range ID, otherwise null.</summary>
    public int LayoutIdOf(int meshId) => Get(meshId)?.LayoutId ?? -1;
    /// <summary>Returns a live mesh for a positive in-range ID, otherwise null.</summary>
    public VertexLayoutDescription LayoutOf(int layoutId) => _layouts.Get(layoutId);
    /// <summary>Number of vertex layouts retained by the mesh layout interner.</summary>
    public int LayoutCount => _layouts.Count;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (VulkanMesh? mesh in _meshes) mesh?.Dispose();
        _meshes.Clear();
    }
}
