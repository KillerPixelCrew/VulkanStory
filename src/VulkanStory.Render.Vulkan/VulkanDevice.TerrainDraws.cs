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
    private readonly System.Collections.Generic.HashSet<int> _terrainOccluderMeshes = new();

    private void InvalidateTerrainGeometry(int meshId, bool deleted = false)
    {
        if (_terrainOccluderMeshes.Contains(meshId)) ResetTerrainOcclusion();
        if (deleted) _terrainOccluderMeshes.Remove(meshId);
    }

    internal bool CanPrepareTerrainDraws => _frameActive &&
        OcclusionDepth is { GpuTexture: > 0, GpuCamera: not null };

    /// <summary>Produces a compact, ordered indirect list without reading visibility back to the CPU.</summary>
    private bool PrepareTerrainDraws(ReadOnlySpan<TerrainDrawBounds> bounds, int[] starts, int[] sizes,
        int count, ReadOnlySpan<TerrainFrustumPlane> planes, double cameraX, double cameraY, double cameraZ,
        out VulkanBuffer? indirect, out ulong offset)
    {
        indirect = null; offset = 0;
        // A fixed per-slot upload budget prevents streaming work from growing GPU memory without bound.
        if (!CanPrepareTerrainDraws || count <= 0 || count > 4096 || bounds.Length != count ||
            starts.Length / 2 < count || sizes.Length < count) return false;
        var occlusion = OcclusionDepth;
        OcclusionCamera captured = occlusion.GpuCamera!;
        if (OcclusionProjection.From(captured.Projection) is not { } projection) return false;
        if (!double.IsFinite(cameraX) || !double.IsFinite(cameraY) || !double.IsFinite(cameraZ)) return false;
        if (_terrainDrawProgram == 0)
            _terrainDrawProgram = CreateComputeProgram(Culling.OcclusionDepth.Source("draws.comp"), "terrain-draws",
                [new ComputeSlot(0, ComputeSlotKind.Sampled), new ComputeSlot(1, ComputeSlotKind.Buffer),
                    new ComputeSlot(2, ComputeSlotKind.Buffer)], 12, 64, 1);
        ComputeProgram? program = _compute.Get(_terrainDrawProgram);
        if (program == null) return false;
        if (_terrainInputs.Length == 0)
        {
            _terrainInputs = new VulkanBuffer?[_frames.FramesInFlight];
            _terrainInputOffsets = new ulong[_frames.FramesInFlight];
        }
        int slot = CurrentFrameSlot;
        var limits = _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        ulong maxRange = limits.MaxStorageBufferRange;
        if ((uint)count > limits.MaxDrawIndirectCount) return false;
        VulkanBuffer input = _terrainInputs[slot] ??= new VulkanBuffer(_context, Math.Min(2UL * 1024 * 1024, maxRange),
            BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            MemoryPoolClass.Staging);
        const int headerWords = 64;
        ulong bytes = (ulong)(headerWords + count * 12) * 4, inputOffset = _terrainInputOffsets[slot];
        if (inputOffset + bytes > input.Size) return false;
        indirect = AllocateIndirect(count, out offset, Math.Max(4UL, limits.MinStorageBufferOffsetAlignment));
        _terrainInputOffsets[slot] += bytes;

        float* values = (float*)((byte*)input.Mapped + inputOffset);
        new Span<byte>(values, (int)bytes).Clear();
        captured.View.AsSpan(0, 16).CopyTo(new Span<float>(values, 16));
        captured.Projection.AsSpan(0, 16).CopyTo(new Span<float>(values + 16, 16));
        values[32] = occlusion.GpuWidth; values[33] = occlusion.GpuHeight;
        values[34] = projection.DepthUnpackMul; values[35] = projection.DepthUnpackAdd;
        ((uint*)values)[36] = planes.Length == 6 ? 1u : 0u;
        for (int i = 0; i < Math.Min(planes.Length, 6); i++)
        {
            TerrainFrustumPlane plane = planes[i];
            values[40 + i * 4] = (float)plane.X;
            values[41 + i * 4] = (float)plane.Y;
            values[42 + i * 4] = (float)plane.Z;
            values[43 + i * 4] = (float)((plane.X * captured.CameraX + plane.Y * captured.CameraY) +
                plane.Z * captured.CameraZ + plane.D);
        }
        double dx = cameraX - captured.CameraX, dy = cameraY - captured.CameraY, dz = cameraZ - captured.CameraZ;
        double grow = Math.Sqrt(dx * dx + dy * dy + dz * dz) + 0.5;
        for (int i = 0; i < count; i++)
        {
            TerrainDrawBounds box = bounds[i];
            float* record = values + headerWords + i * 12;
            record[0] = (float)(box.MinX - captured.CameraX - grow);
            record[1] = (float)(box.MinY - captured.CameraY - grow);
            record[2] = (float)(box.MinZ - captured.CameraZ - grow);
            record[4] = (float)(box.MaxX - captured.CameraX + grow);
            record[5] = (float)(box.MaxY - captured.CameraY + grow);
            record[6] = (float)(box.MaxZ - captured.CameraZ + grow);
            ulong byteOffset = (uint)starts[i * 2] | ((ulong)(uint)starts[i * 2 + 1] << 32);
            ((uint*)record)[8] = (uint)sizes[i];
            ((uint*)record)[9] = checked((uint)(byteOffset / 4));
            ((uint*)record)[10] = box.TestFarPlane ? 6u : 5u;
        }

        CommandBuffer command = Commands;
        using GpuSection section = BeginGpuSection("terrain-draws");
        _targets.EndRendering(command);
        VulkanTexture depth = _textures.Get(occlusion.GpuTexture)!;
        _graph.OpenComputePass(new PassSignature
        {
            NameId = _graph.NameId("compute:terrain-draws"), Width = count, Height = 1,
            FormatsId = -1, Reads = [occlusion.GpuTexture], Attachments = [],
        });
        _textures.Require(_barriers, command, depth, 0, depth.MipLevels, ResourceUsage.SampleCompute);
        _barriers.Flush(command);
        DescriptorSet set = _computeArenas[slot].GetMixed(program.SetLayout,
            [new ComputeImageWrite(0, DescriptorType.CombinedImageSampler, depth.ViewOfMips(0, depth.MipLevels),
                _textures.Samplers.Get(ComputeNearest), ImageLayout.ShaderReadOnlyOptimal)], 1,
            [new DescriptorBufferInfo(input.Handle, 0, input.Size), new DescriptorBufferInfo(indirect.Handle, offset, (ulong)count * 20)]);
        _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, Array.Empty<uint>()));
        _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
        uint* push = stackalloc uint[3] { (uint)(inputOffset / 4), 0, (uint)count };
        _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 12, push);
        _context.Api.CmdDispatch(command, 1, 1, 1);
        _graph.NoteDispatch();
        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2, SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit, DstStageMask = PipelineStageFlags2.DrawIndirectBit,
            DstAccessMask = AccessFlags2.IndirectCommandReadBit, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = indirect.Handle, Offset = offset, Size = (ulong)count * 20,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(command, &dependency);
        return true;
    }

    private void DisposeTerrainInputsIdle()
    {
        foreach (VulkanBuffer? buffer in _terrainInputs) buffer?.Dispose();
        _terrainInputs = Array.Empty<VulkanBuffer?>();
        _terrainInputOffsets = Array.Empty<ulong>();
    }
}
