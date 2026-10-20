using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private const int MaximumEntityRecordPrograms = 32;
    private const int MaximumEntityRecordRows = 2048;
    private const int MaximumEntityRecordSegments = 32;
    private const ulong EntityRecordArenaBytes = 8UL << 20;
    private int _entityRecordProgramCount;
    private PhysicalDeviceLimits? _entityRecordLimits;

    private sealed class EntityRecordSegment(ShaderProgramResources source, VulkanBuffer buffer,
        ulong offset, int stride, int capacity)
    {
        internal readonly ShaderProgramResources Source = source;
        internal readonly VulkanBuffer Buffer = buffer;
        internal readonly ulong Offset = offset;
        internal readonly int Stride = stride, Capacity = capacity;
        internal ulong Range => (ulong)(Stride * Capacity);
        internal int Count;
    }

    private sealed class EntityRecordArena(VulkanBuffer buffer)
    {
        internal readonly VulkanBuffer Buffer = buffer;
        internal readonly List<EntityRecordSegment> Segments = new();
        internal ulong Cursor;
        internal int Rows;
    }

    // Element bases of an animated row (EntityRecordShader.Base*), written after its producers ran.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EntityRecordBases(uint Vertex, uint Deformation, uint PreviousDeformation,
        uint Animation, uint AnimationPrev);
    // Combinable: the row's set 2 is the one every row of its run shares. Rigid rows always;
    // animated rows only once their inputs were placed in the slot's shared heaps.
    private readonly record struct EntityRecordDraw(EntityRecordSegment Segment, uint Row,
        ShaderProgramResources Program, Pipeline Pipeline, EntityRecordBases Bases = default, bool Combinable = true);
    private EntityRecordArena?[] _entityRecords = [];
    private bool[] _entityRecordsUnavailable = [];
    // Present only while rebinding the internal derived program. Never a mesh fallback.
    private EntityRecordSegment? _drawEntityRecords;
    internal long EntityRecordDrawsRecorded { get; private set; }
    internal long EntityRecordRefusals { get; private set; }

    // Per slot: animated rows' deformation (device-local, filled by pre-graphics copies)
    // and joint matrices (host-visible: CPU poses are written, GPU poses copied). Regions
    // are bump-allocated and never rewritten before the slot retires, like the rows.
    private const ulong EntityDeformationHeapBytes = 32UL << 20;
    private const ulong EntityJointHeapBytes = 2UL << 20;
    private const ulong EntityHeapAlignment = 256;
    private sealed class EntityInputHeaps(VulkanBuffer deformation, VulkanBuffer joints)
    {
        internal readonly VulkanBuffer Deformation = deformation, Joints = joints;
        internal ulong DeformationCursor, JointCursor;
        // Immutable producer outputs already copied this slot cycle, by buffer identity.
        internal readonly Dictionary<ulong, ulong> Placed = new();
    }
    private readonly record struct EntityInputCopy(int Base, VulkanBuffer? Buffer, byte[]? Host, ulong Bytes, bool Joint);
    private EntityInputHeaps?[] _entityInputHeaps = [];
    private bool[] _entityInputHeapsUnavailable = [];
    private readonly List<EntityInputCopy> _entityInputCopies = new();
    // Present only while binding a combinable animated row's derived program.
    private EntityInputHeaps? _drawEntityInputHeaps;
    // The vertex base of the animated slice the last TryPrepareEntityGeometry admitted.
    private uint _entitySliceVertexBase;

    private bool TryPrepareEntityRecord(NativePipeline original, out EntityRecordDraw draw)
    {
        // Only packed rigid draws attempt records. Count each refused draw, even
        // when its shader variant or this frame slot was refused previously.
        if (TryPrepareEntityRecordCore(original, out draw)) return true;
        EntityRecordRefusals++;
        return false;
    }

    private bool TryPrepareEntityRecordCore(NativePipeline original, out EntityRecordDraw draw)
    {
        draw = default;
        if (!_context.DrawIndirectFirstInstanceEnabled) return false;
        if (_entityRecords.Length == 0)
        {
            _entityRecords = new EntityRecordArena?[_frames.FramesInFlight];
            _entityRecordsUnavailable = new bool[_frames.FramesInFlight];
        }
        int slot = CurrentFrameSlot;
        if (_entityRecordsUnavailable[slot]) return false;
        ShaderProgramResources source = original.Program;
        var limits = _entityRecordLimits ?? _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        _entityRecordLimits ??= limits;
        if (!source.EntityRecordsAttempted)
        {
            if (_entityRecordProgramCount >= MaximumEntityRecordPrograms) return false;
            source.EntityRecordsAttempted = true;
            if (!EntityRecordShader.TryCreate(source, out var translated, out int components) ||
                components > limits.MaxVertexOutputComponents || components > limits.MaxFragmentInputComponents)
                return false;
            source.EntityRecordVariant = new ShaderProgramResources(_context, _nextProgramId++, translated!, _sharedLayout!.Layout);
            _entityRecordProgramCount++;
        }
        if (source.EntityRecordVariant is not { } program) return false;
        // The PSO uses the very same geometry, attachments and fixed state. It has
        // a private program identity, so no game-owned program or uniforms change.
        var key = original.Key with { ProgramId = program.ProgramId };
        var request = new GraphicsPipelineCache.PipelineRequest
        {
            Program = program, VertexLayout = original.Request.VertexLayout,
            Targets = original.Request.Targets, Blend = original.Request.Blend,
            PolygonMode = original.Request.PolygonMode, Topology = original.Request.Topology,
        };
        if (!_pipelines.TryGet(key, request, out Pipeline pipeline)) return false;
        var arena = _entityRecords[slot];
        if (arena == null)
        {
            ulong capacity = Math.Min(EntityRecordArenaBytes, limits.MaxStorageBufferRange);
            if (capacity < (ulong)EntityRecordShader.Stride(source)) return false;
            try
            {
                arena = new EntityRecordArena(new VulkanBuffer(_context, capacity, BufferUsageFlags.StorageBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging));
            }
            catch (VulkanMemoryAllocationException error) when (error.Result == Result.ErrorOutOfDeviceMemory)
            {
                // This optional slot arena has no submitted users yet. As with
                // geometry packing, retry only after this slot retires. Other
                // allocation failures and device loss retain their original handling.
                _entityRecordsUnavailable[slot] = true;
                return false;
            }
            _entityRecords[slot] = arena;
        }
        if (arena.Rows >= MaximumEntityRecordRows) return false;
        EntityRecordSegment? segment = null;
        foreach (var candidate in arena.Segments)
            if (ReferenceEquals(candidate.Source, source) && candidate.Count < candidate.Capacity)
            { segment = candidate; break; }
        if (segment == null)
        {
            if (arena.Segments.Count >= MaximumEntityRecordSegments) return false;
            ulong alignment = Math.Max(16UL, limits.MinStorageBufferOffsetAlignment);
            ulong offset = (arena.Cursor + alignment - 1) / alignment * alignment;
            int stride = EntityRecordShader.Stride(source);
            if (offset >= arena.Buffer.Size) return false;
            int capacity = (int)Math.Min(256UL, (arena.Buffer.Size - offset) / (ulong)stride);
            if (capacity <= 0) return false;
            segment = new EntityRecordSegment(source, arena.Buffer, offset, stride, capacity);
            arena.Segments.Add(segment); arena.Cursor = offset + segment.Range;
        }
        uint row = (uint)segment.Count++;
        arena.Rows++;
        // Animated rows start with their own bindings: base zero in each input block.
        draw = EntityRecordShader.HasBases(source)
            ? new EntityRecordDraw(segment, row, program, pipeline, new EntityRecordBases(_entitySliceVertexBase, 0, 0, 0, 0), false)
            : new EntityRecordDraw(segment, row, program, pipeline);
        return true;
    }

    private void WriteEntityRecord(in EntityRecordDraw draw)
    {
        EntityRecordSegment segment = draw.Segment;
        // BeginNativeDraw has now resolved actual bindless slots and push scalars.
        // This is a new row; nothing submitted by this slot is overwritten.
        var bytes = new Span<byte>((byte*)segment.Buffer.Mapped + segment.Offset + (ulong)draw.Row * (ulong)segment.Stride,
            segment.Stride);
        bytes.Clear();
        segment.Source.UniformShadow.AsSpan().CopyTo(bytes);
        _pushShadow.AsSpan(0, segment.Source.Interface.PushConstantSize)
            .CopyTo(bytes[EntityRecordShader.PushOffset(segment.Source)..]);
        if (EntityRecordShader.HasBases(segment.Source))
        {
            EntityRecordBases bases = draw.Bases;
            MemoryMarshal.Write(bytes[EntityRecordShader.BaseOffset(segment.Source)..], in bases);
        }
    }

    /// <summary>
    /// Places an animated row's inputs, exactly the buffers BeginNativeDraw bound for the
    /// original program, at element bases in this slot's shared heaps: GPU outputs by copies
    /// recorded ahead of the frame commands, CPU poses by host writes. Following rows of a
    /// run then read theirs through the same set. A row whose input is missing, was produced
    /// in the frame commands since their last submission, or does not fit keeps its own
    /// bindings with base zero, and never opens or joins a run. Rigid rows are unaffected.
    /// </summary>
    private void PlaceEntityRecordInputs(ref EntityRecordDraw draw)
    {
        ShaderProgramResources source = draw.Segment.Source;
        if (!EntityRecordShader.HasBases(source)) return;
        var copies = _entityInputCopies;
        copies.Clear();
        foreach (var block in source.Interface.StorageBlocks)
        {
            int index = block.BlockName == DeformationBlock ? EntityRecordShader.BaseDeformation :
                block.BlockName == PreviousDeformationBlock ? EntityRecordShader.BasePreviousDeformation : -1;
            if (index < 0) return;
            VulkanBuffer? buffer = index == EntityRecordShader.BaseDeformation ? _drawDeformation : _drawPreviousDeformation;
            // Unprepared deformation reads the placeholder in the original draw.
            if (buffer == null || ProducedInScope(buffer)) return;
            copies.Add(new EntityInputCopy(index, buffer, null, buffer.Size, false));
        }
        foreach (var block in source.Interface.UniformBlocks)
        {
            int index = block.BlockName == "Animation" ? EntityRecordShader.BaseAnimation :
                block.BlockName == "AnimationPrev" ? EntityRecordShader.BaseAnimationPrev : -1;
            if (index < 0 || !_clientUniformBuffers.BoundBlocks.TryGetValue(block.BlockName, out int handle) ||
                !_clientUniformBuffers.Buffers.TryGetValue(handle, out var ubo)) return;
            // The same choice BindStorageSet made: a GPU pose, else the CPU shadow it snapshotted.
            VulkanBuffer? pose = index == EntityRecordShader.BaseAnimation ? drawCurrentJointPose : drawPreviousJointPose;
            if (pose != null)
            {
                if (ProducedInScope(pose)) return;
                copies.Add(new EntityInputCopy(index, pose, null, pose.Size, true));
                continue;
            }
            ubo.MaterializeJointPose();
            if (ubo.Shadow.Length == 0) return;
            copies.Add(new EntityInputCopy(index, null, ubo.Shadow, (ulong)ubo.Shadow.Length, true));
        }
        if (copies.Count == 0 || !TryEntityInputHeaps(out var heaps)) return;

        // Reserve every region first, so a refused row records nothing.
        Span<ulong> at = stackalloc ulong[copies.Count];
        Span<uint> bases = stackalloc uint[5];
        bases[EntityRecordShader.BaseVertex] = draw.Bases.Vertex;
        ulong deformationCursor = heaps.DeformationCursor, jointCursor = heaps.JointCursor;
        bool copiesOnGpu = false;
        for (int i = 0; i < copies.Count; i++)
        {
            var copy = copies[i];
            ulong stride = copy.Joint ? EntityRecordShader.AnimationStride : EntityRecordShader.DeformationStride;
            at[i] = ulong.MaxValue;
            if (copy.Buffer != null && heaps.Placed.TryGetValue(copy.Buffer.Id, out ulong placed))
            {
                bases[copy.Base] = (uint)(placed / stride);
                continue;
            }
            // An unchanged pose shares one output between the current and previous blocks.
            int same = -1;
            for (int j = 0; j < i && copy.Buffer != null; j++)
                if (ReferenceEquals(copies[j].Buffer, copy.Buffer) && at[j] != ulong.MaxValue) same = j;
            if (same >= 0)
            {
                bases[copy.Base] = (uint)(at[same] / stride);
                continue;
            }
            ref ulong cursor = ref (copy.Joint ? ref jointCursor : ref deformationCursor);
            ulong capacity = copy.Joint ? heaps.Joints.Size : heaps.Deformation.Size;
            ulong offset = (cursor + EntityHeapAlignment - 1) / EntityHeapAlignment * EntityHeapAlignment;
            if (copy.Bytes == 0 || offset > capacity || copy.Bytes > capacity - offset) return;
            cursor = offset + copy.Bytes;
            at[i] = offset; bases[copy.Base] = (uint)(offset / stride);
            copiesOnGpu |= copy.Buffer != null;
        }
        CommandBuffer commands = default;
        if (copiesOnGpu && !_frames.Current.TryGetPreGraphicsCommands(out commands)) return;
        heaps.DeformationCursor = deformationCursor; heaps.JointCursor = jointCursor;
        for (int i = 0; i < copies.Count; i++)
        {
            var copy = copies[i];
            if (at[i] == ulong.MaxValue) continue;
            VulkanBuffer heap = copy.Joint ? heaps.Joints : heaps.Deformation;
            if (copy.Host != null)
            {
                // Host-coherent; the submission that carries this row publishes the write.
                copy.Host.AsSpan().CopyTo(new Span<byte>((byte*)heap.Mapped + at[i], copy.Host.Length));
                continue;
            }
            var region = new BufferCopy { SrcOffset = 0, DstOffset = at[i], Size = copy.Bytes };
            // Producer barriers already make their outputs visible to copies after them.
            _context.Api.CmdCopyBuffer(commands, copy.Buffer!.Handle, heap.Handle, 1, &region);
            heaps.Placed[copy.Buffer.Id] = at[i];
        }
        if (copiesOnGpu)
        {
            var ready = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.CopyBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit };
            var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &ready };
            _context.Api.CmdPipelineBarrier2(commands, &dependency);
        }
        draw = draw with
        {
            Bases = new EntityRecordBases(bases[0], bases[1], bases[2], bases[3], bases[4]), Combinable = true,
        };
    }

    private bool TryEntityInputHeaps(out EntityInputHeaps heaps)
    {
        heaps = null!;
        if (_entityInputHeaps.Length == 0)
        {
            _entityInputHeaps = new EntityInputHeaps?[_frames.FramesInFlight];
            _entityInputHeapsUnavailable = new bool[_frames.FramesInFlight];
        }
        int slot = CurrentFrameSlot;
        if (_entityInputHeaps[slot] is { } existing) { heaps = existing; return true; }
        if (_entityInputHeapsUnavailable[slot]) return false;
        var limits = _entityRecordLimits ?? _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        _entityRecordLimits ??= limits;
        ulong deformationBytes = Math.Min(EntityDeformationHeapBytes, limits.MaxStorageBufferRange) / EntityHeapAlignment * EntityHeapAlignment;
        ulong jointBytes = Math.Min(EntityJointHeapBytes, limits.MaxStorageBufferRange) / EntityHeapAlignment * EntityHeapAlignment;
        VulkanBuffer? deformation = null, joints = null;
        try
        {
            deformation = new VulkanBuffer(_context, deformationBytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
            joints = new VulkanBuffer(_context, jointBytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);
            // As with geometry packing, this optional stage admits device-local deformation only.
            if (!_context.Allocator.IsDeviceLocal(deformation.Allocation) || joints.Mapped == IntPtr.Zero)
            {
                deformation.Dispose(); joints.Dispose(); _entityInputHeapsUnavailable[slot] = true; return false;
            }
        }
        catch (VulkanMemoryAllocationException error) when (error.Result == Result.ErrorOutOfDeviceMemory)
        {
            // No submitted users yet; retry only after this slot retires.
            deformation?.Dispose(); joints?.Dispose(); _entityInputHeapsUnavailable[slot] = true; return false;
        }
        catch { deformation?.Dispose(); joints?.Dispose(); throw; }
        heaps = _entityInputHeaps[slot] = new EntityInputHeaps(deformation, joints);
        return true;
    }

    private bool BindEntityRecord(CommandBuffer command, int meshId, in EntityRecordDraw draw)
    {
        EntityRecordSegment segment = draw.Segment;
        WriteEntityRecord(draw);
        // Descriptors still retain the original frame globals, read-self texture
        // lifetime and sampler readiness. The derived UBO is unused by its shader.
        _drawEntityRecords = segment;
        // A combinable animated row binds the slot's heaps; any other keeps its own inputs.
        _drawEntityInputHeaps = draw.Combinable && EntityRecordShader.HasBases(segment.Source)
            ? _entityInputHeaps[CurrentFrameSlot] : null;
        try
        {
            if (!BindProgramSets(command, draw.Program, meshId))
            {
                EntityRecordRefusals++;
                return false;
            }
            _context.Api.CmdBindPipeline(command, PipelineBindPoint.Graphics, draw.Pipeline);
            _nativeBoundPipelineSerial = _frames.Current.CommandBuffer.Handle == command.Handle
                ? _frames.Current.RecordingSerial : 0;
            _nativeBoundPipelineHandle = draw.Pipeline.Handle;
            EntityRecordDrawsRecorded++;
            return true;
        }
        finally { _drawEntityRecords = null; _drawEntityInputHeaps = null; }
    }

    private void RetireEntityRecordProgram(ShaderProgramResources original)
    {
        if (original.EntityRecordVariant is not { } variant) return;
        _pipelines.CancelProgram(variant);
        if (_pipelines.RemoveProgramPipelines(variant.ProgramId) is { } pipelines) _frames.DeferDeletion(pipelines);
        // Original owns the modules; its already established timeline retirement
        // disposes both after all in-flight uses. No public program ID is registered.
        _entityRecordProgramCount--;
    }

    private void BeginEntityRecordFrame(int slot)
    {
        // Only slot retirement resets rows, never a partial submit or eye change.
        if (_entityRecordsUnavailable.Length > slot) _entityRecordsUnavailable[slot] = false;
        if (_entityInputHeapsUnavailable.Length > slot) _entityInputHeapsUnavailable[slot] = false;
        if (_entityInputHeaps.Length > slot && _entityInputHeaps[slot] is { } heaps)
        {
            heaps.DeformationCursor = heaps.JointCursor = 0; heaps.Placed.Clear();
        }
        if (_entityRecords.Length <= slot || _entityRecords[slot] is not { } arena) return;
        arena.Segments.Clear(); arena.Cursor = 0; arena.Rows = 0;
    }

    private void DisposeEntityRecordsIdle()
    {
        foreach (var arena in _entityRecords) arena?.Buffer.Dispose();
        _entityRecords = []; _entityRecordsUnavailable = []; _drawEntityRecords = null;
        foreach (var heaps in _entityInputHeaps)
        {
            if (heaps == null) continue;
            // Cached storage sets may name them; the device is idle.
            _descriptors.Release(heaps.Deformation.Id); _descriptors.Release(heaps.Joints.Id);
            heaps.Deformation.Dispose(); heaps.Joints.Dispose();
        }
        _entityInputHeaps = []; _entityInputHeapsUnavailable = []; _drawEntityInputHeaps = null;
    }
}
