using System;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Culling;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

/// <summary>World-space bounds supplied by the terrain producer, in the same order as its draw ranges.</summary>
internal readonly record struct TerrainDrawBounds(double MinX, double MinY, double MinZ,
    double MaxX, double MaxY, double MaxZ, bool TestFarPlane = false);

/// <summary>A stock world-space plane, recentered in double precision before GPU upload.</summary>
internal readonly record struct TerrainFrustumPlane(double X, double Y, double Z, double D);

public sealed unsafe partial class VulkanDevice
{
    private int _terrainDrawProgram;
    private VulkanBuffer?[] _terrainInputs = Array.Empty<VulkanBuffer?>();
    private ulong[] _terrainInputOffsets = Array.Empty<ulong>();
    private const int TerrainBatchCapacity = 4096;
    private const ulong TerrainBatchInputBytes = (64 + TerrainBatchCapacity * 12) * 4;
    private const ulong TerrainBatchOutputBytes = TerrainBatchCapacity * 8 * 4;
    private sealed class TerrainDrawBatch(VulkanBuffer input, ulong inputOffset, VulkanBuffer output, ulong outputOffset,
        ulong serial, int eye, OcclusionCamera camera, TerrainFrustumPlane[] planes, double x, double y, double z)
    {
        internal readonly VulkanBuffer Input = input, Output = output;
        internal readonly ulong InputOffset = inputOffset, OutputOffset = outputOffset;
        internal readonly ulong Serial = serial;
        internal readonly int Eye = eye;
        internal readonly OcclusionCamera Camera = camera;
        internal readonly TerrainFrustumPlane[] Planes = planes;
        internal readonly double X = x, Y = y, Z = z;
        internal int Used;
    }
    private TerrainDrawBatch? _terrainDrawBatch;
    private PhysicalDeviceLimits? _terrainLimits;
    private PhysicalDeviceLimits DrawPreparationLimits
    {
        get
        {
            _terrainLimits ??= _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
            return _terrainLimits.Value;
        }
    }
    private readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(VulkanBuffer Buffer, ulong Offset, int Count)>> _terrainResults = new();
    public long TerrainGpuPartsTested { get; private set; }
    public long TerrainGpuPartsVisible { get; private set; }

    // Called only after this slot's fence completed, before its indirect buffers are reused.
    private void CollectTerrainDraws(int slot)
    {
        CollectTerrainSceneParity(slot);
        BeginTerrainSceneFrame();
        if (!_terrainResults.TryGetValue(slot, out var results)) return;
        foreach (var result in results)
        {
            uint* totals = (uint*)((byte*)result.Buffer.Mapped + result.Offset) + result.Count * 7;
            TerrainGpuPartsTested += result.Count;
            for (int group = 0; group < (result.Count + 63) / 64; group++) TerrainGpuPartsVisible += totals[group];
        }
        results.Clear();
    }
    private readonly System.Collections.Generic.HashSet<int> _terrainOccluderMeshes = new();

    private void InvalidateTerrainGeometry(int meshId, bool deleted = false)
    {
        if (_terrainOccluderMeshes.Contains(meshId)) ResetTerrainOcclusion();
        if (deleted) _terrainOccluderMeshes.Remove(meshId);
    }

    internal bool CanPrepareTerrainDraws => _frameActive &&
        OcclusionDepth is { GpuTexture: > 0, GpuCamera: not null };

    private ComputeProgram? DrawPreparationProgram()
    {
        if (_terrainDrawProgram == 0)
            _terrainDrawProgram = CreateComputeProgram(Culling.OcclusionDepth.Source("draws.comp"), "visibility-draws",
                [new ComputeSlot(0, ComputeSlotKind.Sampled), new ComputeSlot(1, ComputeSlotKind.Buffer),
                    new ComputeSlot(2, ComputeSlotKind.Buffer)], 12, 64, 1);
        return _compute.Get(_terrainDrawProgram);
    }

    private static void FillVisibilityHeader(float* values, Culling.OcclusionDepth occlusion,
        ReadOnlySpan<TerrainFrustumPlane> planes)
    {
        new Span<float>(values, 64).Clear();
        var camera = occlusion.GpuCamera!;
        var projection = OcclusionProjection.From(camera.Projection)!.Value;
        camera.View.AsSpan(0, 16).CopyTo(new Span<float>(values, 16));
        camera.Projection.AsSpan(0, 16).CopyTo(new Span<float>(values + 16, 16));
        values[32] = occlusion.GpuWidth; values[33] = occlusion.GpuHeight;
        values[34] = projection.DepthUnpackMul; values[35] = projection.DepthUnpackAdd;
        ((uint*)values)[36] = planes.Length == 6 ? 1u : 0u;
        for (int i = 0; i < Math.Min(planes.Length, 6); i++)
        {
            var plane = planes[i];
            values[40 + i * 4] = (float)plane.X; values[41 + i * 4] = (float)plane.Y;
            values[42 + i * 4] = (float)plane.Z;
            values[43 + i * 4] = (float)((plane.X * camera.CameraX + plane.Y * camera.CameraY) +
                plane.Z * camera.CameraZ + plane.D);
        }
    }

    /// <summary>Produces a compact, ordered indirect list without reading visibility back to the CPU.</summary>
    private bool PrepareTerrainDraws(ReadOnlySpan<TerrainDrawBounds> bounds, int[] starts, int[] sizes,
        int count, ReadOnlySpan<TerrainFrustumPlane> planes, double cameraX, double cameraY, double cameraZ,
        out VulkanBuffer? indirect, out ulong offset, out ulong countOffset)
    {
        indirect = null; offset = countOffset = 0;
        // A fixed per-slot upload budget prevents streaming work from growing GPU memory without bound.
        if (!CanPrepareTerrainDraws || count <= 0 || count > 4096 || bounds.Length != count ||
            starts.Length / 2 < count || sizes.Length < count) return false;
        var occlusion = OcclusionDepth;
        OcclusionCamera captured = occlusion.GpuCamera!;
        if (OcclusionProjection.From(captured.Projection) == null) return false;
        if (!double.IsFinite(cameraX) || !double.IsFinite(cameraY) || !double.IsFinite(cameraZ)) return false;
        var limits = DrawPreparationLimits;
        if ((uint)count > limits.MaxDrawIndirectCount) return false;
        int padded = (count + 63) / 64 * 64;
        var batch = _terrainDrawBatch;
        // As with entity batches, host writes append only until this recording is
        // submitted. A new serial, eye or depth capture gets a separate region.
        if (batch == null || batch.Used + padded > TerrainBatchCapacity ||
            batch.Serial != _frames.Current.RecordingSerial || batch.Eye != TerrainEyeIndex ||
            !ReferenceEquals(batch.Camera, captured) || batch.X != cameraX || batch.Y != cameraY || batch.Z != cameraZ ||
            !planes.SequenceEqual(batch.Planes))
        {
            batch = BeginTerrainDrawBatch(planes, cameraX, cameraY, cameraZ, limits);
            if (batch == null) return false;
            _terrainDrawBatch = batch;
        }
        int first = batch.Used;
        int groups = (count + 63) / 64;
        uint outputWord = (uint)first * 8;
        float* values = (float*)((byte*)batch.Input.Mapped + batch.InputOffset);
        for (int i = 0; i < count; i++)
        {
            TerrainDrawBounds box = bounds[i];
            float* record = values + 64 + (first + i) * 12;
            record[0] = MathF.BitDecrement((float)(box.MinX - captured.CameraX));
            record[1] = MathF.BitDecrement((float)(box.MinY - captured.CameraY));
            record[2] = MathF.BitDecrement((float)(box.MinZ - captured.CameraZ));
            record[4] = MathF.BitIncrement((float)(box.MaxX - captured.CameraX));
            record[5] = MathF.BitIncrement((float)(box.MaxY - captured.CameraY));
            record[6] = MathF.BitIncrement((float)(box.MaxZ - captured.CameraZ));
            ulong byteOffset = (uint)starts[i * 2] | ((ulong)(uint)starts[i * 2 + 1] << 32);
            ((uint*)record)[8] = (uint)sizes[i];
            ((uint*)record)[9] = checked((uint)(byteOffset / 4));
            ((uint*)record)[10] = box.TestFarPlane ? 6u : 5u;
        }
        for (int i = 0; i < padded; i += 64)
        {
            uint* head = (uint*)(values + 64 + (first + i) * 12);
            head[3] = (uint)first; head[7] = (uint)count; head[11] = outputWord;
        }
        batch.Used += padded;
        ((uint*)values)[37] = (uint)batch.Used;
        indirect = batch.Output;
        offset = batch.OutputOffset + outputWord * 4UL;
        countOffset = offset + (ulong)(count * 7 + groups) * 4;
        if (!_terrainResults.TryGetValue(CurrentFrameSlot, out var results))
            _terrainResults.Add(CurrentFrameSlot, results = new());
        results.Add((indirect, offset, count));
        return true;
    }

    private TerrainDrawBatch? BeginTerrainDrawBatch(ReadOnlySpan<TerrainFrustumPlane> planes,
        double cameraX, double cameraY, double cameraZ, PhysicalDeviceLimits limits)
    {
        ComputeProgram? program = DrawPreparationProgram();
        if (program == null || TerrainBatchOutputBytes > limits.MaxStorageBufferRange) return null;
        if (_terrainInputs.Length == 0)
        {
            _terrainInputs = new VulkanBuffer?[_frames.FramesInFlight];
            _terrainInputOffsets = new ulong[_frames.FramesInFlight];
        }
        int slot = CurrentFrameSlot;
        ulong capacity = Math.Min(2UL << 20, limits.MaxStorageBufferRange);
        ulong inputOffset = _terrainInputOffsets[slot];
        if (inputOffset + TerrainBatchInputBytes > capacity) return null;
        VulkanBuffer input = _terrainInputs[slot] ??= new VulkanBuffer(_context, capacity,
            BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            MemoryPoolClass.Staging);
        VulkanBuffer indirect = AllocateIndirect((int)((TerrainBatchOutputBytes + 19) / 20), out ulong offset,
            Math.Max(4UL, limits.MinStorageBufferOffsetAlignment));
        _terrainInputOffsets[slot] += TerrainBatchInputBytes;
        float* values = (float*)((byte*)input.Mapped + inputOffset);
        FillVisibilityHeader(values, OcclusionDepth, planes);
        var captured = OcclusionDepth.GpuCamera!;
        double dx = cameraX - captured.CameraX, dy = cameraY - captured.CameraY, dz = cameraZ - captured.CameraZ;
        values[38] = MathF.BitIncrement((float)(Math.Sqrt(dx * dx + dy * dy + dz * dz) + 0.5));
        CommandBuffer command = Commands;
        using GpuSection section = BeginGpuSection("terrain-draws");
        _targets.EndRendering(command);
        VulkanTexture depth = _textures.Get(OcclusionDepth.GpuTexture)!;
        _graph.OpenComputePass(new PassSignature
        {
            NameId = _graph.NameId("compute:terrain-draws"), Width = TerrainBatchCapacity, Height = 1,
            FormatsId = -1, Reads = [OcclusionDepth.GpuTexture], Attachments = [],
        });
        _textures.Require(_barriers, command, depth, 0, depth.MipLevels, ResourceUsage.SampleCompute);
        _barriers.Flush(command);
        DescriptorSet set = _computeArenas[slot].GetMixed(program.SetLayout,
            [new ComputeImageWrite(0, DescriptorType.CombinedImageSampler, depth.ViewOfMips(0, depth.MipLevels),
                _textures.Samplers.Get(ComputeNearest), ImageLayout.ShaderReadOnlyOptimal)], 1,
            [new DescriptorBufferInfo(input.Handle, 0, input.Size), new DescriptorBufferInfo(indirect.Handle, offset, TerrainBatchOutputBytes)]);
        uint counted = _context.DrawIndirectCountEnabled ? 1u : 0u;
        _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, [1u, 0u, 1u, counted]));
        _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
        uint* push = stackalloc uint[3] { (uint)(inputOffset / 4), 0, TerrainBatchCapacity };
        _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 12, push);
        _context.Api.CmdDispatch(command, TerrainBatchCapacity / 64, 1, 1);
        _graph.NoteDispatch();
        {
            var scanBarrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2, SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit, DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = indirect.Handle, Offset = offset, Size = TerrainBatchOutputBytes,
            };
            var scanDependency = new DependencyInfo { SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &scanBarrier };
            _context.Api.CmdPipelineBarrier2(command, &scanDependency);
            _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, [1u, 1u, 1u, counted]));
            _context.Api.CmdDispatch(command, TerrainBatchCapacity / 64, 1, 1);
            _graph.NoteDispatch();
        }
        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2, SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.DrawIndirectBit | PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.IndirectCommandReadBit | AccessFlags2.HostReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = indirect.Handle, Offset = offset, Size = TerrainBatchOutputBytes,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(command, &dependency);
        return new TerrainDrawBatch(input, inputOffset, indirect, offset, _frames.Current.RecordingSerial,
            TerrainEyeIndex, captured, planes.ToArray(), cameraX, cameraY, cameraZ);
    }

    private void DisposeTerrainInputsIdle()
    {
        DisposeTerrainSceneIdle();
        foreach (VulkanBuffer? buffer in _terrainInputs) buffer?.Dispose();
        _terrainInputs = Array.Empty<VulkanBuffer?>();
        _terrainInputOffsets = Array.Empty<ulong>();
        _terrainResults.Clear();
        _terrainDrawBatch = null;
    }
}
