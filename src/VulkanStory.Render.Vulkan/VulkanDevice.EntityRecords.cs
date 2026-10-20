using System;
using System.Collections.Generic;
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

    private readonly record struct EntityRecordDraw(EntityRecordSegment Segment, uint Row,
        ShaderProgramResources Program, Pipeline Pipeline);
    private EntityRecordArena?[] _entityRecords = [];
    private bool[] _entityRecordsUnavailable = [];
    // Present only while rebinding the internal derived program. Never a mesh fallback.
    private EntityRecordSegment? _drawEntityRecords;
    internal long EntityRecordDrawsRecorded { get; private set; }
    internal long EntityRecordRefusals { get; private set; }

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
        draw = new EntityRecordDraw(segment, row, program, pipeline);
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
    }

    private bool BindEntityRecord(CommandBuffer command, int meshId, in EntityRecordDraw draw)
    {
        EntityRecordSegment segment = draw.Segment;
        WriteEntityRecord(draw);
        // Descriptors still retain the original frame globals, read-self texture
        // lifetime and sampler readiness. The derived UBO is unused by its shader.
        _drawEntityRecords = segment;
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
        finally { _drawEntityRecords = null; }
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
        if (_entityRecords.Length <= slot || _entityRecords[slot] is not { } arena) return;
        arena.Segments.Clear(); arena.Cursor = 0; arena.Rows = 0;
    }

    private void DisposeEntityRecordsIdle()
    {
        foreach (var arena in _entityRecords) arena?.Buffer.Dispose();
        _entityRecords = []; _entityRecordsUnavailable = []; _drawEntityRecords = null;
    }
}
