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
    // Animated arenas also hold the damage-effect and joint streams (six regions);
    // their record rows subtract each slice's vertex base again (EntityRecordShader).
    private sealed class EntityGeometryArena(VulkanBuffer vertices, VulkanBuffer indices, uint capacity, bool animated = false)
    {
        internal readonly VulkanBuffer Vertices = vertices, Indices = indices;
        internal readonly uint Capacity = capacity;
        internal readonly bool Animated = animated;
        internal uint VertexCursor, IndexCursor;
        internal readonly Dictionary<(VulkanMesh Mesh, ulong Revision, int Count), uint> Slices = new();
        internal readonly Dictionary<(VulkanMesh Mesh, ulong Revision, int Count), uint> VertexBases = new();
    }

    private EntityGeometryArena?[] _entityGeometry = [];
    private bool[] _entityGeometryUnavailable = [];
    private EntityGeometryArena?[] _animatedEntityGeometry = [];
    private bool[] _animatedEntityGeometryUnavailable = [];
    private PhysicalDeviceLimits? _entityGeometryLimits;
    private int _entityPackProgram, _animatedEntityPackProgram;
    private readonly Dictionary<int, WeakReference<VulkanMesh>> _entityGeometryRequests = new();
    private readonly Dictionary<int, WeakReference<VulkanMesh>> _animatedEntityGeometryRequests = new();
    private static readonly int[] RigidEntityStreams =
        [MeshManager.BufferXyz, MeshManager.BufferUv, MeshManager.BufferRgba, MeshManager.BufferFlags];
    private static readonly int[] AnimatedEntityStreams = [MeshManager.BufferXyz, MeshManager.BufferUv,
        MeshManager.BufferRgba, MeshManager.BufferFlags, MeshManager.BufferCustomFloat, MeshManager.BufferCustomInt];
    private static readonly uint[] EntityStreamStrides = [12, 8, 4, 4, 4, 4];
    private static readonly Format[] EntityStreamFormats = [Format.R32G32B32Sfloat, Format.R32G32Sfloat,
        Format.R8G8B8A8Unorm, Format.R32Sint, Format.R32Sfloat, Format.R32Sint];
    internal long EntityGeometryPacksRecorded { get; private set; }
    internal long EntityPackedDrawsRecorded { get; private set; }

    private bool TryPrepareEntityGeometry(ShaderProgramResources program, int meshId, VulkanMesh mesh,
        out EntityGeometryArena? arena, out uint firstIndex)
    {
        arena = null; firstIndex = 0;
        if (EntityVisibility is { RigidProgram: 0 }) return TryPrepareAnimatedEntityGeometry(program, meshId, mesh, out arena, out firstIndex);
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

    /// <summary>
    /// Admits a prepared animated body to the slot's animated arena. Only combining gains
    /// from it, so it needs indirect counts and first instances, shared deformation and a
    /// record variant that can address the slice. Its producers keep reading the original
    /// mesh streams; the packed streams feed only the raster draw.
    /// </summary>
    private bool TryPrepareAnimatedEntityGeometry(ShaderProgramResources program, int meshId, VulkanMesh mesh,
        out EntityGeometryArena? arena, out uint firstIndex)
    {
        arena = null; firstIndex = 0;
        if (!_frameActive || !ComputeDeformationEnabled || !_context.DrawIndirectCountEnabled ||
            !_context.DrawIndirectFirstInstanceEnabled || !CanPrepareTerrainDraws ||
            _nativeTarget?.DepthTextureId != OcclusionDepth.SourceDepthTexture ||
            program.LocationOf("vsComputeDeformation") < 0 || program.Interface.VertexInputs.Count != 6 ||
            (program.EntityRecordsAttempted && program.EntityRecordVariant == null) ||
            !program.Interface.StorageBlocks.Exists(block => block.BlockName == DeformationBlock)) return false;
        foreach (var block in program.Interface.StorageBlocks)
            if (block.BlockName is not (DeformationBlock or PreviousDeformationBlock)) return false;
        foreach (var block in program.Interface.UniformBlocks)
            if (block.BlockName is not ("Animation" or "AnimationPrev")) return false;
        if (!TryEntityStreamInputs(mesh, AnimatedEntityStreams, out _)) return false;
        if (_animatedEntityGeometryRequests.TryGetValue(meshId, out var request)) request.SetTarget(mesh);
        else if (_animatedEntityGeometryRequests.Count < MaximumEntityGeometryPacks)
            _animatedEntityGeometryRequests.Add(meshId, new WeakReference<VulkanMesh>(mesh));
        var key = (mesh, mesh.GeometryRevision, mesh.IndexCount);
        arena = _animatedEntityGeometry.Length > CurrentFrameSlot ? _animatedEntityGeometry[CurrentFrameSlot] : null;
        if (arena == null || !arena.Slices.TryGetValue(key, out firstIndex)) return false;
        _entitySliceVertexBase = arena.VertexBases[key];
        return true;
    }

    private static bool TryEntityGeometryInputs(VulkanMesh mesh, out uint vertexCount) =>
        TryEntityStreamInputs(mesh, RigidEntityStreams, out vertexCount);

    private static bool TryEntityStreamInputs(VulkanMesh mesh, int[] slots, out uint vertexCount)
    {
        vertexCount = 0;
        int streams = slots.Length;
        if (mesh.EntityGeometryMappingExposed || mesh.Ssbo || mesh.SharedIndices ||
            mesh.DrawMode != VulkanStory.Contracts.MeshDrawMode.Triangles ||
            mesh.Layout.Bindings.Length != streams || mesh.Layout.Attributes.Length != streams ||
            mesh.BindingOrder.Count != streams ||
            mesh.Indices is not { } sourceIndices || mesh.IndexCount <= 0 ||
            mesh.EntityIndexPrefixCount < mesh.IndexCount) return false;

        ReadOnlySpan<uint> strides = EntityStreamStrides;
        ReadOnlySpan<Format> formats = EntityStreamFormats;
        int positionBytes = mesh.EntityStreamPrefixBytes[MeshManager.BufferXyz];
        if (positionBytes <= 0 || positionBytes % 12 != 0) return false;
        vertexCount = (uint)(positionBytes / 12);
        if (mesh.EntityMaxIndex >= vertexCount) return false;
        for (int i = 0; i < streams; i++)
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
        foreach (var request in _animatedEntityGeometryRequests)
            if (request.Value.TryGetTarget(out var mesh) && ReferenceEquals(_meshes.Get(request.Key), mesh) &&
                TryEntityStreamInputs(mesh, AnimatedEntityStreams, out uint vertexCount))
                PackEntityGeometry(slot, mesh, vertexCount, animated: true);
        _animatedEntityGeometryRequests.Clear();
    }

    private void PackEntityGeometry(int slot, VulkanMesh mesh, uint vertexCount, bool animated = false)
    {
        ulong indexBytes = (ulong)mesh.IndexCount * 4;
        var sourceIndices = mesh.Indices!;
        int[] slots = animated ? AnimatedEntityStreams : RigidEntityStreams;
        int streams = slots.Length;
        // Streams, source indices, packed vertices and packed indices; push words are the
        // two counts, the two cursors and the word base of every region after positions.
        uint bindings = (uint)streams + 3, pushBytes = ((uint)streams + 3) * 4, vertexBytes = animated ? 36u : 28u;
        var sources = new VulkanBuffer[streams + 1];
        for (int i = 0; i < streams; i++) sources[i] = mesh.Buffers[slots[i]]!;
        sources[streams] = sourceIndices;
        var limits = _entityGeometryLimits ?? _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        _entityGeometryLimits ??= limits;
        if (limits.MaxPerStageDescriptorStorageBuffers < bindings || limits.MaxDescriptorSetStorageBuffers < bindings ||
            limits.MaxPerStageResources < bindings || limits.MaxPushConstantsSize < pushBytes ||
            limits.MaxComputeWorkGroupInvocations < 64 || limits.MaxComputeWorkGroupSize[0] < 64 ||
            indexBytes > limits.MaxStorageBufferRange || (ulong)vertexCount * 12 > limits.MaxStorageBufferRange) return;
        ref EntityGeometryArena?[] arenas = ref (animated ? ref _animatedEntityGeometry : ref _entityGeometry);
        ref bool[] unavailable = ref (animated ? ref _animatedEntityGeometryUnavailable : ref _entityGeometryUnavailable);
        if (arenas.Length == 0)
        {
            arenas = new EntityGeometryArena?[_frames.FramesInFlight];
            unavailable = new bool[_frames.FramesInFlight];
        }
        if (unavailable[slot]) return;
        var arena = arenas[slot];
        if (arena == null)
        {
            // Animated bodies carry more vertices per mesh than dropped items and falling blocks.
            uint capacity = (uint)Math.Min(animated ? 131072UL : 65536UL, limits.MaxStorageBufferRange / vertexBytes);
            ulong indicesCapacity = Math.Min((animated ? 524288UL : 262144UL) * 4, limits.MaxStorageBufferRange);
            if (capacity < vertexCount || indicesCapacity < indexBytes) return;
            VulkanBuffer? vertices = null, indices = null;
            try
            {
                vertices = new VulkanBuffer(_context, (ulong)capacity * vertexBytes,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit,
                    MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
                indices = new VulkanBuffer(_context, indicesCapacity,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndexBufferBit,
                    MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
                // The shared allocator may fall back to system memory under
                // pressure. This optional stage admits device-local arenas only.
                if (!_context.Allocator.IsDeviceLocal(vertices.Allocation) || !_context.Allocator.IsDeviceLocal(indices.Allocation))
                {
                    vertices.Dispose(); indices.Dispose(); unavailable[slot] = true; return;
                }
                arena = arenas[slot] = new EntityGeometryArena(vertices, indices, capacity, animated);
            }
            catch (VulkanMemoryAllocationException error) when (error.Result == Result.ErrorOutOfDeviceMemory)
            {
                vertices?.Dispose(); indices?.Dispose(); unavailable[slot] = true; return;
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
        ref int packProgram = ref (animated ? ref _animatedEntityPackProgram : ref _entityPackProgram);
        if (packProgram == 0)
        {
            var packSlots = new ComputeSlot[bindings];
            for (int i = 0; i < packSlots.Length; i++) packSlots[i] = new((uint)i, ComputeSlotKind.Buffer);
            packProgram = animated
                ? CreateComputeProgram(ReadWorldShader("geometry/entity-pack-animated.comp"), "entity-pack-animated",
                    packSlots, pushBytes, 64, 1)
                : CreateComputeProgram(ReadWorldShader("geometry/entity-pack.comp"), "entity-pack", packSlots, pushBytes, 64, 1);
        }
        var pack = _compute.Get(packProgram) ?? throw new InvalidOperationException("Missing entity packing program.");
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
        _graph.OpenComputePass(new PassSignature { NameId = _graph.NameId(animated ? "compute:entity-pack-animated" : "compute:entity-pack"),
            Width = (int)vertexCount, Height = 1, FormatsId = -1, Reads = [], Attachments = [] });
        // Every set is a fresh immutable frame allocation, including after partial
        // submission. Descriptor offsets are zero; ranges cover only active inputs.
        var infos = new DescriptorBufferInfo[bindings];
        for (int i = 0; i < streams; i++) infos[i] = new(sources[i].Handle, 0, (ulong)vertexCount * EntityStreamStrides[i]);
        infos[streams] = new(sourceIndices.Handle, 0, indexBytes);
        infos[streams + 1] = new(arena.Vertices.Handle, 0, arena.Vertices.Size);
        infos[streams + 2] = new(arena.Indices.Handle, 0, arena.Indices.Size);
        var set = _computeArenas[slot].GetBuffers(pack.SetLayout, infos);
        _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(pack, []));
        _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, pack.Layout, 0, 1, &set, 0, null);
        // Word bases of the uv, rgba and flags regions, then damage effect and joints; see BindEntityGeometry.
        uint* push = stackalloc uint[9] { vertexCount, indexCount, arena.VertexCursor, arena.IndexCursor,
            arena.Capacity * 3, arena.Capacity * 5, arena.Capacity * 6, arena.Capacity * 7, arena.Capacity * 8 };
        _context.Api.CmdPushConstants(command, pack.Layout, ShaderStageFlags.ComputeBit, 0, pushBytes, push);
        _context.Api.CmdDispatch(command, (Math.Max(vertexCount, indexCount) + 63) / 64, 1, 1); _graph.NoteDispatch();
        var ready = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.VertexAttributeInputBit | PipelineStageFlags2.IndexInputBit,
            DstAccessMask = AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit };
        var after = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &ready };
        _context.Api.CmdPipelineBarrier2(command, &after);
        uint firstIndex = arena.IndexCursor;
        if (animated) arena.VertexBases.Add(key, arena.VertexCursor);
        arena.VertexCursor += vertexCount; arena.IndexCursor += indexCount;
        arena.Slices.Add(key, firstIndex);
        EntityGeometryPacksRecorded++;
    }

    private void BindEntityGeometry(CommandBuffer command, EntityGeometryArena arena)
    {
        Buffer* buffers = stackalloc Buffer[6] { arena.Vertices.Handle, arena.Vertices.Handle, arena.Vertices.Handle,
            arena.Vertices.Handle, arena.Vertices.Handle, arena.Vertices.Handle };
        ulong* offsets = stackalloc ulong[6] { 0, (ulong)arena.Capacity * 12, (ulong)arena.Capacity * 20,
            (ulong)arena.Capacity * 24, (ulong)arena.Capacity * 28, (ulong)arena.Capacity * 32 };
        _context.Api.CmdBindVertexBuffers(command, 0, arena.Animated ? 6u : 4u, buffers, offsets);
        _context.Api.CmdBindIndexBuffer(command, arena.Indices.Handle, 0, IndexType.Uint32);
        EntityPackedDrawsRecorded++;
    }

    private void BeginEntityGeometryFrame(int slot)
    {
        // Called only by PrepareGraphicsSlot after FrameRing.BeginFrame waits for
        // the slot timeline. Partial submissions and stereo eyes never reset it.
        if (_animatedEntityGeometry.Length > slot)
        {
            _animatedEntityGeometryUnavailable[slot] = false;
            if (_animatedEntityGeometry[slot] is { } animated)
            {
                animated.VertexCursor = animated.IndexCursor = 0;
                animated.Slices.Clear(); animated.VertexBases.Clear();
            }
        }
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
        foreach (var arena in _animatedEntityGeometry)
        {
            arena?.Vertices.Dispose(); arena?.Indices.Dispose();
        }
        _entityGeometry = []; _entityGeometryUnavailable = []; _entityGeometryRequests.Clear();
        _animatedEntityGeometry = []; _animatedEntityGeometryUnavailable = []; _animatedEntityGeometryRequests.Clear();
    }
}
