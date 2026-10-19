using System;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

internal readonly record struct EntityVisibilityInput(TerrainDrawBounds Bounds, TerrainFrustumPlane[] Planes,
    double CameraX, double CameraY, double CameraZ, int RigidProgram = 0);

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

    private bool TryRigidEntityBounds(ShaderProgramResources program, VulkanMesh mesh,
        EntityVisibilityInput visibility, out TerrainDrawBounds result)
    {
        result = default;
        if (mesh.EntityLocalBounds is not { } local || mesh.Ssbo) return false;
        bool position = false, flags = false;
        foreach (var attribute in mesh.Layout.Attributes)
        {
            if (attribute.Binding >= mesh.BindingOrder.Count || attribute.Offset != 0) continue;
            foreach (var binding in mesh.Layout.Bindings)
            {
                if (binding.Binding != attribute.Binding || binding.PerInstance) continue;
                position |= attribute.Location == 0 && attribute.Format == Format.R32G32B32Sfloat &&
                    binding.Stride == 12 && mesh.BindingOrder[(int)attribute.Binding] == MeshManager.BufferXyz;
                flags |= attribute.Location == 3 && attribute.Format == Format.R32Sint &&
                    binding.Stride == 4 && mesh.BindingOrder[(int)attribute.Binding] == MeshManager.BufferFlags;
            }
        }
        if (!position || !flags) return false;
        Span<byte> matrixBytes = stackalloc byte[64];
        Span<byte> scalar = stackalloc byte[4];
        if (!CopyWorldUniform(program, "modelMatrix", "mat4", matrixBytes) ||
            !CopyWorldUniform(program, "extraZOffset", "float", scalar) ||
            MemoryMarshal.Read<float>(scalar) != 0 ||
            !CopyWorldUniform(program, "dontWarpVertices", "int", scalar)) return false;
        int warp = MemoryMarshal.Read<int>(scalar);
        if (warp != 1)
        {
            // Stock dropped items use mode zero. Wind/water meshes and active
            // perception/global deformation retain the original unculled draw.
            if (warp is not (0 or 2) || !mesh.EntityFlagsWithoutWind ||
                !CopyWorldUniform(program, "addRenderFlags", "int", scalar) ||
                (MemoryMarshal.Read<int>(scalar) & (0xf << 25)) != 0 ||
                !TryReadClientFloatUniform(program.ProgramId, "globalWarpIntensity", out float global) ||
                !float.IsFinite(global) || global != 0 ||
                !TryReadClientFloatUniform(program.ProgramId, "glitchWaviness", out float glitch) ||
                !float.IsFinite(glitch) || glitch > 0.1f ||
                !CopyWorldUniform(program, "perceptionEffectId", "int", scalar)) return false;
            if (MemoryMarshal.Read<int>(scalar) == 2 &&
                (!TryReadClientFloatUniform(program.ProgramId, "perceptionEffectIntensity", out float perception) ||
                    !float.IsFinite(perception) || perception > 0)) return false;
        }
        var model = MemoryMarshal.Cast<byte, float>(matrixBytes);
        foreach (float value in model) if (!float.IsFinite(value)) return false;
        if (model[3] != 0 || model[7] != 0 || model[11] != 0 || model[15] != 1) return false;
        double minX = double.PositiveInfinity, minY = minX, minZ = minX;
        double maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX;
        double margin = 0.001;
        // Transform all eight corners using the exact model shadow consumed by
        // this draw, including item origin, scale, rotation, bobbing and offsets.
        for (int corner = 0; corner < 8; corner++)
        {
            double x = (corner & 1) == 0 ? local.MinX : local.MaxX;
            double y = (corner & 2) == 0 ? local.MinY : local.MaxY;
            double z = (corner & 4) == 0 ? local.MinZ : local.MaxZ;
            double tx = model[0] * x + model[4] * y + model[8] * z + model[12];
            double ty = model[1] * x + model[5] * y + model[9] * z + model[13];
            double tz = model[2] * x + model[6] * y + model[10] * z + model[14];
            double magnitude = 0;
            for (int row = 0; row < 3; row++)
                magnitude = Math.Max(magnitude, Math.Abs(model[row] * x) + Math.Abs(model[row + 4] * y) +
                    Math.Abs(model[row + 8] * z) + Math.Abs(model[row + 12]));
            if (!double.IsFinite(magnitude) || magnitude > 1e6) return false;
            margin = Math.Max(margin, magnitude * 1e-5);
            minX = Math.Min(minX, tx); minY = Math.Min(minY, ty); minZ = Math.Min(minZ, tz);
            maxX = Math.Max(maxX, tx); maxY = Math.Max(maxY, ty); maxZ = Math.Max(maxZ, tz);
        }
        result = new(minX + visibility.CameraX - margin, minY + visibility.CameraY - margin,
            minZ + visibility.CameraZ - margin, maxX + visibility.CameraX + margin,
            maxY + visibility.CameraY + margin, maxZ + visibility.CameraZ + margin, true);
        return true;
    }

    private bool PrepareEntityDraw(ShaderProgramResources program, VulkanMesh mesh,
        out VulkanBuffer? commands, out ulong offset)
    {
        commands = null; offset = 0;
        if (EntityVisibility is not { } visibility || !CanPrepareTerrainDraws ||
            _nativeTarget?.DepthTextureId != OcclusionDepth.SourceDepthTexture) return false;
        var box = visibility.Bounds;
        if (visibility.RigidProgram != 0)
        {
            if (visibility.RigidProgram != program.ProgramId ||
                !TryRigidEntityBounds(program, mesh, visibility, out box)) return false;
        }
        else if (program.LocationOf("vsComputeDeformation") < 0) return false;
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
