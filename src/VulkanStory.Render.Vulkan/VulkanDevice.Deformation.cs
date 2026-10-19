using System;
using System.Collections.Generic;
using System.IO;
using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal const string DeformationBlock = "VulkanDeformation";
    internal const string PreviousDeformationBlock = "VulkanPreviousDeformation";
    private const ulong DeformationBudget = 64UL << 20;
    private readonly record struct DeformationKey(VulkanMesh Mesh, ulong Revision, int PoseHash);
    private sealed class DeformationGeometry(VulkanBuffer buffer, byte[] pose, uint frame)
    {
        internal readonly VulkanBuffer Buffer = buffer;
        internal readonly byte[] Pose = pose;
        internal uint Frame = frame;
    }
    private readonly Dictionary<DeformationKey, List<DeformationGeometry>> _deformedGeometry = new();
    private readonly List<DeformationKey> _deformationRetire = new();
    private ulong _deformationBytes;
    private int _deformationProgram;
    private VulkanBuffer? _drawDeformation, _drawPreviousDeformation;
    internal bool ComputeDeformationEnabled { get; set; }
    internal long DeformationDispatchesRecorded { get; private set; }
    internal long DeformationCacheReuses { get; private set; }

    private void SetDeformationMode(ShaderProgramResources program, bool enabled)
    {
        int location = program.LocationOf("vsComputeDeformation");
        if (location < 0) return;
        int value = enabled ? 1 : 0;
        ReadOnlySpan<byte> bytes = new(&value, sizeof(int));
        if (ShaderProgramResources.IsPushLocation(location)) program.SetPushUniform(location, bytes);
        else program.SetUniform(location, bytes);
    }

    private void PrepareSharedDeformation(ShaderProgramResources program, int meshId)
    {
        _drawDeformation = _drawPreviousDeformation = null;
        bool prepared = TryPrepareSharedDeformation(program, meshId);
        SetDeformationMode(program, prepared);
        SetWorldDeformationMode(program, prepared && TryPrepareWorldGeometry(program, meshId));
    }

    private bool TryPrepareSharedDeformation(ShaderProgramResources program, int meshId)
    {
        if (!ComputeDeformationEnabled || meshId <= 0 || program.LocationOf("vsComputeDeformation") < 0) return false;
        bool current = false, previous = false;
        foreach (var block in program.Interface.StorageBlocks)
        {
            current |= block.BlockName == DeformationBlock;
            previous |= block.BlockName == PreviousDeformationBlock;
        }
        if (!current || _meshes.Get(meshId) is not { } mesh ||
            !_clientUniformBuffers.Buffers.TryGetValue(_clientUniformBuffers.BoundHandle("Animation"), out var bones)) return false;
        if (!TryDeformationInputs(mesh, out var positions, out var ids, out var position, out var joint,
                out uint positionStride, out uint jointStride, out uint count)) return false;
        _drawDeformation = Deform(mesh, bones, positions!, ids!, position, joint, positionStride, jointStride, count);
        if (_drawDeformation == null) return false;
        if (previous)
        {
            if (!_clientUniformBuffers.Buffers.TryGetValue(_clientUniformBuffers.BoundHandle("AnimationPrev"), out var oldBones)) return false;
            _drawPreviousDeformation = Deform(mesh, oldBones, positions!, ids!, position, joint, positionStride, jointStride, count);
            if (_drawPreviousDeformation == null) return false;
        }
        return true;
    }

    internal void ResetWorldComputeGeometry()
    {
        ResetTerrainOcclusion();
        _entityDrawBatch = null; EntityVisibility = null;
        RetireDeformationGeometry(all: true);
        RetireJointPoses(all: true);
        RetireWorldGeometry(all: true);
        RetireParticleGeometry(all: true);
        RetireParticleVisuals(all: true);
    }

    private bool TryDeformationInputs(VulkanMesh mesh, out VulkanBuffer? positions, out VulkanBuffer? ids,
        out VertexAttribute position, out VertexAttribute joint, out uint positionStride, out uint jointStride, out uint count)
    {
        positions = ids = null; position = joint = default; positionStride = jointStride = count = 0;
        foreach (var attribute in mesh.Layout.Attributes)
        {
            if (attribute.Location == 0 && attribute.Format == Format.R32G32B32Sfloat) position = attribute;
            if (attribute.Location == 5 && attribute.Format == Format.R32Sint) joint = attribute;
        }
        if (position.Format != Format.R32G32B32Sfloat || joint.Format != Format.R32Sint) return false;
        foreach (var binding in mesh.Layout.Bindings)
        {
            if (binding.PerInstance && (binding.Binding == position.Binding || binding.Binding == joint.Binding)) return false;
            if (binding.Binding == position.Binding) positionStride = binding.Stride;
            if (binding.Binding == joint.Binding) jointStride = binding.Stride;
        }
        if (positionStride == 0 || jointStride == 0 || (positionStride | jointStride | position.Offset | joint.Offset) % 4 != 0 ||
            position.Binding >= mesh.BindingOrder.Count || joint.Binding >= mesh.BindingOrder.Count) return false;
        positions = mesh.Buffers[mesh.BindingOrder[(int)position.Binding]];
        ids = mesh.Buffers[mesh.BindingOrder[(int)joint.Binding]];
        if (positions == null || ids == null || (positions.Usage & ids.Usage & BufferUsageFlags.StorageBufferBit) == 0 ||
            positions.Size < position.Offset + 12UL || ids.Size < joint.Offset + 4UL) return false;
        ulong available = Math.Min((positions.Size - position.Offset - 12) / positionStride + 1,
            (ids.Size - joint.Offset - 4) / jointStride + 1);
        if (available == 0 || available > 262144) return false;
        count = (uint)available;
        return true;
    }

    private VulkanBuffer? Deform(VulkanMesh mesh, ClientUniformBuffer bones, VulkanBuffer positions, VulkanBuffer ids,
        VertexAttribute position, VertexAttribute joint, uint positionStride, uint jointStride, uint count)
    {
        if (bones.Shadow.Length < 64 || bones.Shadow.Length % 64 != 0) return null;
        var gpuBones = PrepareJointPose(bones);
        if (gpuBones == null) bones.MaterializeJointPose();
        ReadOnlySpan<byte> poseData = bones.SourceJointPose is { } sourcePose ? sourcePose.Bytes : bones.Shadow;
        var hash = new HashCode(); hash.AddBytes(poseData);
        var key = new DeformationKey(mesh, mesh.GeometryRevision, hash.ToHashCode());
        if (_deformedGeometry.TryGetValue(key, out var versions))
            foreach (var geometry in versions)
                if (poseData.SequenceEqual(geometry.Pose))
                {
                    geometry.Frame = _frameCounter;
                    DeformationCacheReuses++;
                    return geometry.Buffer;
                }
        ulong bytes = (ulong)count * 64;
        var limits = _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        ulong retainedBytes = bytes + (ulong)poseData.Length;
        uint boneOffset = 0;
        if (_deformationBytes + worldGeometryBytes + retainedBytes > DeformationBudget || bytes > limits.MaxStorageBufferRange ||
            positions.Size > limits.MaxStorageBufferRange || ids.Size > limits.MaxStorageBufferRange ||
            bones.Shadow.Length > limits.MaxStorageBufferRange ||
            (gpuBones == null && !bones.TrySnapshot(_frameCounter, _frames.Current, out boneOffset))) return null;
        if (_deformationProgram == 0)
        {
            using Stream stream = typeof(VulkanDevice).Assembly.GetManifestResourceStream("shaders-vk/geometry/deform.comp")!;
            using var reader = new StreamReader(stream);
            _deformationProgram = CreateComputeProgram(reader.ReadToEnd(), "shared-deformation",
                [new ComputeSlot(0, ComputeSlotKind.Buffer), new ComputeSlot(1, ComputeSlotKind.Buffer),
                    new ComputeSlot(2, ComputeSlotKind.Buffer), new ComputeSlot(3, ComputeSlotKind.Buffer)], 24, 64, 1);
        }
        var program = _compute.Get(_deformationProgram);
        if (program == null) return null;
        var output = new VulkanBuffer(_context, bytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit |
            BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
        try
        {
            CommandBuffer command = Commands;
            using GpuSection section = BeginGpuSection("shared-deformation");
            _targets.EndRendering(command);
            _uploads.NoteUse(command, positions); _uploads.NoteUse(command, ids);
            _graph.OpenComputePass(new PassSignature
            {
                NameId = _graph.NameId("compute:shared-deformation"), Width = (int)count, Height = 1,
                FormatsId = -1, Reads = [], Attachments = [],
            });
            var set = _computeArenas[CurrentFrameSlot].GetBuffers(program.SetLayout,
                [new DescriptorBufferInfo(positions.Handle, 0, positions.Size), new DescriptorBufferInfo(ids.Handle, 0, ids.Size),
                    gpuBones != null ? new DescriptorBufferInfo(gpuBones.Handle, 0, gpuBones.Size) :
                        new DescriptorBufferInfo(_frames.UniformBuffer, boneOffset, (ulong)bones.Shadow.Length),
                    new DescriptorBufferInfo(output.Handle, 0, bytes)]);
            _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, Array.Empty<uint>()));
            _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
            uint* push = stackalloc uint[6] { count, positionStride / 4, position.Offset / 4, jointStride / 4, joint.Offset / 4,
                (uint)bones.Shadow.Length / 64 };
            _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 24, push);
            _context.Api.CmdDispatch(command, (count + 63) / 64, 1, 1); _graph.NoteDispatch();
            DeformationDispatchesRecorded++;
            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2, SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = output.Handle, Offset = 0, Size = bytes,
            };
            var dependency = new DependencyInfo { SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
            _context.Api.CmdPipelineBarrier2(command, &dependency);
            if (versions == null) _deformedGeometry.Add(key, versions = new());
            versions.Add(new DeformationGeometry(output, poseData.ToArray(), _frameCounter));
            _deformationBytes += retainedBytes;
            return output;
        }
        catch { _frames.DeferDeletion(output); throw; }
    }

    private void RetireDeformationGeometry(bool all = false)
    {
        _deformationRetire.Clear();
        foreach (var pair in _deformedGeometry)
        {
            var versions = pair.Value;
            for (int i = versions.Count - 1; i >= 0; i--)
                if (all || unchecked(_frameCounter - versions[i].Frame) > 2)
                {
                    var geometry = versions[i];
                    _descriptors.Release(geometry.Buffer.Id);
                    _frames.DeferDeletion(geometry.Buffer);
                    _deformationBytes -= geometry.Buffer.Size + (ulong)geometry.Pose.Length;
                    versions.RemoveAt(i);
                }
            if (versions.Count == 0) _deformationRetire.Add(pair.Key);
        }
        foreach (var key in _deformationRetire) _deformedGeometry.Remove(key);
        _drawDeformation = _drawPreviousDeformation = null;
    }

    private void DisposeDeformationGeometryIdle()
    {
        foreach (var versions in _deformedGeometry.Values)
            foreach (var geometry in versions) geometry.Buffer.Dispose();
        _deformedGeometry.Clear(); _deformationBytes = 0;
    }
}
