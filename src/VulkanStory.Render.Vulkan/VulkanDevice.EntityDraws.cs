using System;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

internal readonly record struct EntityVisibilityInput(TerrainDrawBounds Bounds, TerrainFrustumPlane[] Planes,
    double CameraX, double CameraY, double CameraZ);

public sealed unsafe partial class VulkanDevice
{
    private const int EntityBatchCapacity = 2048;
    private const ulong EntityBatchBytes = (64 + EntityBatchCapacity * 12) * 4;
    private sealed class EntityDrawBatch(VulkanBuffer input, ulong inputOffset, VulkanBuffer commands,
        ulong commandOffset, ulong serial, int eye, OcclusionCamera camera, TerrainFrustumPlane[] planes)
    {
        internal readonly VulkanBuffer Input = input, Commands = commands;
        internal readonly ulong InputOffset = inputOffset, CommandOffset = commandOffset, Serial = serial;
        internal readonly int Eye = eye;
        internal readonly OcclusionCamera Camera = camera;
        internal readonly TerrainFrustumPlane[] Planes = planes;
        internal int Count;
    }
    private VulkanBuffer?[] _entityInputs = Array.Empty<VulkanBuffer?>();
    private ulong[] _entityInputOffsets = Array.Empty<ulong>();
    private EntityDrawBatch? _entityDrawBatch;
    internal EntityVisibilityInput? EntityVisibility { get; set; }
    internal long EntityVisibilityDispatchesRecorded { get; private set; }
    internal long EntityIndirectDrawsRecorded { get; private set; }

    private bool PrepareEntityDraw(ShaderProgramResources program, VulkanMesh mesh,
        out VulkanBuffer? commands, out ulong offset)
    {
        commands = null; offset = 0;
        if (EntityVisibility is not { } visibility || !CanPrepareTerrainDraws ||
            _nativeTarget?.DepthTextureId != OcclusionDepth.SourceDepthTexture ||
            program.LocationOf("vsComputeDeformation") < 0) return false;
        var camera = OcclusionDepth.GpuCamera!;
        var batch = _entityDrawBatch;
        // Inputs may be appended after recording the dispatch, but never after submitting it.
        // A partial submission starts a fresh serial and a disjoint input/output region.
        if (batch == null || batch.Count == EntityBatchCapacity || batch.Serial != _frames.Current.RecordingSerial ||
            batch.Eye != TerrainEyeIndex || !ReferenceEquals(batch.Camera, camera) ||
            !ReferenceEquals(batch.Planes, visibility.Planes))
        {
            batch = BeginEntityDrawBatch(visibility);
            if (batch == null) return false;
            _entityDrawBatch = batch;
        }
        float* header = (float*)((byte*)batch.Input.Mapped + batch.InputOffset);
        float* record = header + 64 + batch.Count * 12;
        var box = visibility.Bounds;
        double dx = visibility.CameraX - camera.CameraX, dy = visibility.CameraY - camera.CameraY,
            dz = visibility.CameraZ - camera.CameraZ;
        double grow = Math.Sqrt(dx * dx + dy * dy + dz * dz) + 0.5;
        record[0] = (float)(box.MinX - camera.CameraX - grow);
        record[1] = (float)(box.MinY - camera.CameraY - grow);
        record[2] = (float)(box.MinZ - camera.CameraZ - grow); record[3] = 0;
        record[4] = (float)(box.MaxX - camera.CameraX + grow);
        record[5] = (float)(box.MaxY - camera.CameraY + grow);
        record[6] = (float)(box.MaxZ - camera.CameraZ + grow); record[7] = 0;
        ((uint*)record)[8] = (uint)mesh.IndexCount; ((uint*)record)[9] = 0;
        ((uint*)record)[10] = 6; ((uint*)record)[11] = 1;
        commands = batch.Commands;
        offset = batch.CommandOffset + (ulong)batch.Count * 20;
        batch.Count++;
        ((uint*)header)[37] = (uint)batch.Count;
        return true;
    }

    private EntityDrawBatch? BeginEntityDrawBatch(EntityVisibilityInput visibility)
    {
        var program = DrawPreparationProgram();
        if (program == null || Culling.OcclusionProjection.From(OcclusionDepth.GpuCamera!.Projection) == null) return null;
        if (_entityInputs.Length == 0)
        {
            _entityInputs = new VulkanBuffer?[_frames.FramesInFlight];
            _entityInputOffsets = new ulong[_frames.FramesInFlight];
        }
        int slot = CurrentFrameSlot;
        var limits = _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        ulong alignment = Math.Max(4UL, limits.MinStorageBufferOffsetAlignment);
        ulong at = (_entityInputOffsets[slot] + alignment - 1) / alignment * alignment;
        ulong capacity = Math.Min(2UL << 20, limits.MaxStorageBufferRange);
        if (at + EntityBatchBytes > capacity) return null;
        var input = _entityInputs[slot] ??= new VulkanBuffer(_context, capacity, BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);
        var indirect = AllocateIndirect(EntityBatchCapacity, out ulong indirectOffset, alignment);
        _entityInputOffsets[slot] = at + EntityBatchBytes;
        FillVisibilityHeader((float*)((byte*)input.Mapped + at), OcclusionDepth, visibility.Planes);
        var depth = _textures.Get(OcclusionDepth.GpuTexture)!;
        var command = Commands;
        using GpuSection section = BeginGpuSection("entity-draws");
        _targets.EndRendering(command);
        _graph.OpenComputePass(new PassSignature
        {
            NameId = _graph.NameId("compute:entity-draws"), Width = EntityBatchCapacity, Height = 1,
            FormatsId = -1, Reads = [OcclusionDepth.GpuTexture], Attachments = [],
        });
        _textures.Require(_barriers, command, depth, 0, depth.MipLevels, ResourceUsage.SampleCompute);
        _barriers.Flush(command);
        var set = _computeArenas[slot].GetMixed(program.SetLayout,
            [new ComputeImageWrite(0, DescriptorType.CombinedImageSampler, depth.ViewOfMips(0, depth.MipLevels),
                _textures.Samplers.Get(ComputeNearest), ImageLayout.ShaderReadOnlyOptimal)], 1,
            [new DescriptorBufferInfo(input.Handle, at, EntityBatchBytes),
                new DescriptorBufferInfo(indirect.Handle, indirectOffset, EntityBatchCapacity * 20)]);
        _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, [0u]));
        _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
        uint* push = stackalloc uint[3] { 0, 0, EntityBatchCapacity };
        _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 12, push);
        _context.Api.CmdDispatch(command, EntityBatchCapacity / 64, 1, 1); _graph.NoteDispatch();
        EntityVisibilityDispatchesRecorded++;
        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2, SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit, DstStageMask = PipelineStageFlags2.DrawIndirectBit,
            DstAccessMask = AccessFlags2.IndirectCommandReadBit, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = indirect.Handle, Offset = indirectOffset,
            Size = EntityBatchCapacity * 20,
        };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
        _context.Api.CmdPipelineBarrier2(command, &dependency);
        return new EntityDrawBatch(input, at, indirect, indirectOffset, _frames.Current.RecordingSerial,
            TerrainEyeIndex, OcclusionDepth.GpuCamera!, visibility.Planes);
    }

    private void BeginEntityDrawFrame(int slot)
    {
        _entityDrawBatch = null; EntityVisibility = null;
        if (_entityInputOffsets.Length > slot) _entityInputOffsets[slot] = 0;
    }

    private void DisposeEntityDrawsIdle()
    {
        foreach (var input in _entityInputs) input?.Dispose();
        _entityInputs = Array.Empty<VulkanBuffer?>();
        _entityInputOffsets = Array.Empty<ulong>();
        _entityDrawBatch = null;
    }
}
