using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private const int MaximumEntityGeometryPacks = 256;
    // Common SoA binding regions, not per-mesh binding offsets. Rebased uint32
    // indices select the same vertex in all four regions with vertexOffset zero.
    private sealed class EntityGeometryArena(VulkanBuffer vertices, VulkanBuffer indices, uint capacity)
    {
        internal readonly VulkanBuffer Vertices = vertices, Indices = indices;
        internal readonly uint Capacity = capacity;
        internal uint VertexCursor, IndexCursor;
        internal readonly Dictionary<(VulkanMesh Mesh, ulong Revision, int Count), uint> Slices = new();
    }

    private EntityGeometryArena?[] _entityGeometry = [];
    private bool[] _entityGeometryUnavailable = [];
    private PhysicalDeviceLimits? _entityGeometryLimits;
    private int _entityPackProgram;
    private readonly Dictionary<int, WeakReference<VulkanMesh>> _entityGeometryRequests = new();

    private bool TryPrepareEntityGeometry(ShaderProgramResources program, int meshId, VulkanMesh mesh,
        out EntityGeometryArena? arena, out uint firstIndex)
    {
        arena = null; firstIndex = 0;
        // The adapter certifies the exact unmodified standard program for BOTH
        // origins. Its vertex shader uses attributes/model history, not local
        // gl_VertexIndex. Animated, custom, SSBO and particle routes stay original.
        if (!_frameActive || EntityVisibility is not { RigidProgram: > 0 } visibility ||
            visibility.RigidProgram != program.ProgramId || !CanPrepareTerrainDraws ||
            _nativeTarget?.DepthTextureId != OcclusionDepth.SourceDepthTexture ||
            !TryRigidEntityBounds(program, mesh, visibility, out _) ||
            program.LocationOf("vsComputeDeformation") >= 0 ||
            program.Interface.StorageBlocks.Count != 0 || program.Interface.VertexInputs.Count != 4) return false;

        foreach (var block in program.Interface.UniformBlocks)
            if (block.BlockName is "Animation" or "AnimationPrev") return false;
        if (!TryEntityGeometryInputs(mesh, out _)) return false;
        // Admit only real supported draws, but never record a pack at the draw seam.
        // The next frame packs the latest revision before any consuming pass opens.
        if (_entityGeometryRequests.TryGetValue(meshId, out var request)) request.SetTarget(mesh);
        else if (_entityGeometryRequests.Count < MaximumEntityGeometryPacks)
            _entityGeometryRequests.Add(meshId, new WeakReference<VulkanMesh>(mesh));
        arena = _entityGeometry.Length > CurrentFrameSlot ? _entityGeometry[CurrentFrameSlot] : null;
        return arena != null && arena.Slices.TryGetValue((mesh, mesh.GeometryRevision, mesh.IndexCount), out firstIndex);
    }

    private static bool TryEntityGeometryInputs(VulkanMesh mesh, out uint vertexCount)
    {
        vertexCount = 0;
        if (mesh.EntityGeometryMappingExposed || mesh.Ssbo || mesh.SharedIndices ||
            mesh.DrawMode != VulkanStory.Contracts.MeshDrawMode.Triangles ||
            mesh.Layout.Bindings.Length != 4 || mesh.Layout.Attributes.Length != 4 ||
            mesh.BindingOrder.Count != 4 ||
            mesh.Indices is not { } sourceIndices || mesh.IndexCount <= 0 ||
            mesh.EntityIndexPrefixCount < mesh.IndexCount) return false;

        ReadOnlySpan<int> slots = [MeshManager.BufferXyz, MeshManager.BufferUv, MeshManager.BufferRgba, MeshManager.BufferFlags];
        ReadOnlySpan<uint> strides = [12, 8, 4, 4];
        ReadOnlySpan<Format> formats = [Format.R32G32B32Sfloat, Format.R32G32Sfloat, Format.R8G8B8A8Unorm, Format.R32Sint];
        int positionBytes = mesh.EntityStreamPrefixBytes[MeshManager.BufferXyz];
        if (positionBytes <= 0 || positionBytes % 12 != 0) return false;
        vertexCount = (uint)(positionBytes / 12);
        if (mesh.EntityMaxIndex >= vertexCount) return false;
        for (int i = 0; i < 4; i++)
        {
            // Require the ordinary dedicated binding order, no unused custom
            // streams, interleaving, instance rates or nonzero attribute offsets.
            if (mesh.BindingOrder[i] != slots[i] || mesh.Buffers[slots[i]] is not { } source) return false;
            bool bindingFound = false, attributeFound = false;
            foreach (var binding in mesh.Layout.Bindings)
                if (binding.Binding == (uint)i && binding.Stride == strides[i] && !binding.PerInstance) bindingFound = true;
            foreach (var attribute in mesh.Layout.Attributes)
                if (attribute.Location == (uint)i && attribute.Binding == (uint)i &&
                    attribute.Offset == 0 && attribute.Format == formats[i]) attributeFound = true;
            ulong bytes = (ulong)vertexCount * strides[i];
            if (!bindingFound || !attributeFound || source.Size < bytes ||
                (ulong)mesh.EntityStreamPrefixBytes[slots[i]] < bytes ||
                (source.Usage & BufferUsageFlags.StorageBufferBit) == 0) return false;
        }
        ulong indexBytes = (ulong)mesh.IndexCount * 4;
        if (sourceIndices.Size < indexBytes || (sourceIndices.Usage & BufferUsageFlags.StorageBufferBit) == 0) return false;
        return true;
    }

    private void FlushEntityGeometryPacks(int slot)
    {
        // PrepareGraphicsSlot has waited for this slot and reset its descriptor
        // arena. No render scope is open, including for standalone GL recording.
        foreach (var request in _entityGeometryRequests)
            if (request.Value.TryGetTarget(out var mesh) && ReferenceEquals(_meshes.Get(request.Key), mesh) &&
                TryEntityGeometryInputs(mesh, out uint vertexCount))
                PackEntityGeometry(slot, mesh, vertexCount);
        _entityGeometryRequests.Clear();
    }

    private void PackEntityGeometry(int slot, VulkanMesh mesh, uint vertexCount)
    {
        ulong indexBytes = (ulong)mesh.IndexCount * 4;
        var sourceIndices = mesh.Indices!;
        ReadOnlySpan<VulkanBuffer> sources = [mesh.Buffers[MeshManager.BufferXyz]!, mesh.Buffers[MeshManager.BufferUv]!,
            mesh.Buffers[MeshManager.BufferRgba]!, mesh.Buffers[MeshManager.BufferFlags]!, sourceIndices];
        var limits = _entityGeometryLimits ?? _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        _entityGeometryLimits ??= limits;
        if (limits.MaxPerStageDescriptorStorageBuffers < 7 || limits.MaxDescriptorSetStorageBuffers < 7 ||
            limits.MaxPerStageResources < 7 || limits.MaxPushConstantsSize < 28 ||
            limits.MaxComputeWorkGroupInvocations < 64 || limits.MaxComputeWorkGroupSize[0] < 64 ||
            indexBytes > limits.MaxStorageBufferRange || (ulong)vertexCount * 12 > limits.MaxStorageBufferRange) return;
        if (_entityGeometry.Length == 0)
        {
            _entityGeometry = new EntityGeometryArena?[_frames.FramesInFlight];
            _entityGeometryUnavailable = new bool[_frames.FramesInFlight];
        }
        if (_entityGeometryUnavailable[slot]) return;
        var arena = _entityGeometry[slot];
        if (arena == null)
        {
            uint capacity = (uint)Math.Min(65536UL, limits.MaxStorageBufferRange / 28UL);
            ulong indicesCapacity = Math.Min(262144UL * 4, limits.MaxStorageBufferRange);
            if (capacity < vertexCount || indicesCapacity < indexBytes) return;
            VulkanBuffer? vertices = null, indices = null;
            try
            {
                vertices = new VulkanBuffer(_context, (ulong)capacity * 28,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit,
                    MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
                indices = new VulkanBuffer(_context, indicesCapacity,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndexBufferBit,
                    MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
                // The shared allocator may fall back to system memory under
                // pressure. This optional stage admits device-local arenas only.
                if (!_context.Allocator.IsDeviceLocal(vertices.Allocation) || !_context.Allocator.IsDeviceLocal(indices.Allocation))
                {
                    vertices.Dispose(); indices.Dispose(); _entityGeometryUnavailable[slot] = true; return;
                }
                arena = _entityGeometry[slot] = new EntityGeometryArena(vertices, indices, capacity);
            }
            catch (VulkanMemoryAllocationException error) when (error.Result == Result.ErrorOutOfDeviceMemory)
            {
                vertices?.Dispose(); indices?.Dispose(); _entityGeometryUnavailable[slot] = true; return;
            }
            catch { vertices?.Dispose(); indices?.Dispose(); throw; }
        }
        var key = (mesh, mesh.GeometryRevision, mesh.IndexCount);
        if (arena.Slices.ContainsKey(key)) return;
        // Bound command overhead as well as bytes for tiny or repeatedly edited meshes.
        if (arena.Slices.Count >= MaximumEntityGeometryPacks) return;
        uint indexCount = (uint)mesh.IndexCount;
        if (vertexCount > arena.Capacity - arena.VertexCursor ||
            indexCount > arena.Indices.Size / 4 - arena.IndexCursor ||
            (Math.Max(vertexCount, indexCount) + 63UL) / 64 > limits.MaxComputeWorkGroupCount[0]) return;
        if (_entityPackProgram == 0)
            _entityPackProgram = CreateComputeProgram(ReadWorldShader("geometry/entity-pack.comp"), "entity-pack",
                [new(0, ComputeSlotKind.Buffer), new(1, ComputeSlotKind.Buffer), new(2, ComputeSlotKind.Buffer),
                    new(3, ComputeSlotKind.Buffer), new(4, ComputeSlotKind.Buffer), new(5, ComputeSlotKind.Buffer),
                    new(6, ComputeSlotKind.Buffer)], 28, 64, 1);
        var pack = _compute.Get(_entityPackProgram) ?? throw new InvalidOperationException("Missing entity packing program.");
        var command = Commands;
        _barriers.Flush(command);
        foreach (var source in sources) _uploads.NoteUse(command, source);
        // Publish host, staged transfer and GPU source writes to the compute
        // reader. Later API writes see NoteUse and stay ordered after this pack.
        var sourceBarrier = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit | PipelineStageFlags2.HostBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit | AccessFlags2.HostWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit, DstAccessMask = AccessFlags2.ShaderStorageReadBit };
        var before = new DependencyInfo { SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1, PMemoryBarriers = &sourceBarrier };
        _context.Api.CmdPipelineBarrier2(command, &before);
        _graph.OpenComputePass(new PassSignature { NameId = _graph.NameId("compute:entity-pack"),
            Width = (int)vertexCount, Height = 1, FormatsId = -1, Reads = [], Attachments = [] });
        // Every set is a fresh immutable frame allocation, including after partial
        // submission. Descriptor offsets are zero; ranges cover only active inputs.
        var set = _computeArenas[slot].GetBuffers(pack.SetLayout,
            [new(sources[0].Handle, 0, (ulong)vertexCount * 12), new(sources[1].Handle, 0, (ulong)vertexCount * 8),
                new(sources[2].Handle, 0, (ulong)vertexCount * 4), new(sources[3].Handle, 0, (ulong)vertexCount * 4),
                new(sourceIndices.Handle, 0, indexBytes), new(arena.Vertices.Handle, 0, arena.Vertices.Size),
                new(arena.Indices.Handle, 0, arena.Indices.Size)]);
        _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(pack, []));
        _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, pack.Layout, 0, 1, &set, 0, null);
        uint* push = stackalloc uint[7] { vertexCount, indexCount, arena.VertexCursor, arena.IndexCursor,
            arena.Capacity * 3, arena.Capacity * 5, arena.Capacity * 6 };
        _context.Api.CmdPushConstants(command, pack.Layout, ShaderStageFlags.ComputeBit, 0, 28, push);
        _context.Api.CmdDispatch(command, (Math.Max(vertexCount, indexCount) + 63) / 64, 1, 1); _graph.NoteDispatch();
        var ready = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.VertexAttributeInputBit | PipelineStageFlags2.IndexInputBit,
            DstAccessMask = AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit };
        var after = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &ready };
        _context.Api.CmdPipelineBarrier2(command, &after);
        uint firstIndex = arena.IndexCursor;
        arena.VertexCursor += vertexCount; arena.IndexCursor += indexCount;
        arena.Slices.Add(key, firstIndex);
    }

    private void BindEntityGeometry(CommandBuffer command, EntityGeometryArena arena)
    {
        Buffer* buffers = stackalloc Buffer[4] { arena.Vertices.Handle, arena.Vertices.Handle, arena.Vertices.Handle, arena.Vertices.Handle };
        ulong* offsets = stackalloc ulong[4] { 0, (ulong)arena.Capacity * 12, (ulong)arena.Capacity * 20, (ulong)arena.Capacity * 24 };
        _context.Api.CmdBindVertexBuffers(command, 0, 4, buffers, offsets);
        _context.Api.CmdBindIndexBuffer(command, arena.Indices.Handle, 0, IndexType.Uint32);
    }

    private void BeginEntityGeometryFrame(int slot)
    {
        // Called only by PrepareGraphicsSlot after FrameRing.BeginFrame waits for
        // the slot timeline. Partial submissions and stereo eyes never reset it.
        if (_entityGeometry.Length <= slot) return;
        _entityGeometryUnavailable[slot] = false;
        if (_entityGeometry[slot] is not { } arena) return;
        arena.VertexCursor = arena.IndexCursor = 0; arena.Slices.Clear();
    }

    private void DisposeEntityGeometryIdle()
    {
        foreach (var arena in _entityGeometry)
        {
            arena?.Vertices.Dispose(); arena?.Indices.Dispose();
        }
        _entityGeometry = []; _entityGeometryUnavailable = []; _entityGeometryRequests.Clear();
    }
}
