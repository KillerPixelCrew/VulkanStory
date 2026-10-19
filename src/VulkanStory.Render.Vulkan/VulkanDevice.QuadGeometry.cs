using System;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private int quadGeometryProgram;
    internal long QuadGeometryDispatchesRecorded { get; private set; }

    private int TryPrepareQuadGeometry(ShaderProgramResources program, int meshId, int instances)
    {
        if (!ComputeParticleGeometryEnabled || !program.ComputeWarpCompatible || instances <= 0 || program.WorldWarpWaving is not (0 or 1) ||
            _meshes.Get(meshId) is not { } mesh || !program.Interface.StorageBlocks.Exists(block => block.BlockName == "VulkanParticleGeometry") ||
            !TryParticleInput(mesh, 0, Format.R32G32B32Sfloat, false, out var vertices) ||
            !TryParticleInput(mesh, 4, Format.R32G32B32Sfloat, true, out var current) ||
            !TryParticleInput(mesh, 5, Format.R32Sfloat, true, out var scale) ||
            !TryParticleInput(mesh, 3, Format.R32Sint, true, out var flags) ||
            !TryParticleInput(mesh, 6, Format.R8G8B8A8Unorm, true, out var direction) ||
            !ReferenceEquals(current.Buffer, scale.Buffer) || scale.Offset != current.Offset + 12 || scale.Stride != current.Stride ||
            vertices.Buffer.Size < vertices.Offset + 12UL ||
            current.Buffer.Size < current.Offset + (ulong)(instances - 1) * current.Stride + 16 ||
            flags.Buffer.Size < flags.Offset + (ulong)(instances - 1) * flags.Stride + 4 ||
            direction.Buffer.Size < direction.Offset + (ulong)(instances - 1) * direction.Stride + 4) return 0;
        uint vertexCount = (uint)((vertices.Buffer.Size - vertices.Offset - 12) / vertices.Stride + 1);
        if (vertexCount is 0 or > 128 || (ulong)vertexCount * (uint)instances > 1048576) return 0;
        current = VisualParticleInput(meshId, 4, current);
        Span<byte> parameters = stackalloc byte[172]; parameters.Clear();
        if (!CopyWorldUniform(program, "modelViewMatrix", "mat4", parameters[..64]) ||
            !ReadGeometryWarpState(program, false, parameters.Slice(64, 64))) return 0;
        foreach (float value in MemoryMarshal.Cast<byte, float>(parameters[..64])) if (!float.IsFinite(value)) return 0;
        var push = MemoryMarshal.Cast<byte, uint>(parameters[128..]);
        push[0] = vertexCount; push[1] = (uint)instances; push[2] = vertices.Stride / 4; push[3] = vertices.Offset / 4;
        push[4] = current.Stride / 4; push[5] = current.Offset / 4; push[6] = flags.Stride / 4; push[7] = flags.Offset / 4;
        push[8] = direction.Stride / 4; push[9] = direction.Offset / 4; push[10] = (uint)program.WorldWarpWaving!.Value;
        var hash = new HashCode(); hash.AddBytes(parameters);
        var key = new ParticleGeometryKey(mesh, mesh.GeometryRevision, instances, hash.ToHashCode(), true);
        if (particleGeometry.TryGetValue(key, out var versions))
            foreach (var geometry in versions)
                if (parameters.SequenceEqual(geometry.Parameters))
                { geometry.Frame = _frameCounter; ParticleGeometryCacheReuses++; drawParticleGeometry = geometry.Buffer; return (int)vertexCount; }
        ulong size = (ulong)vertexCount * (uint)instances * 96;
        var limits = _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        if (particleGeometryBytes + size + (ulong)parameters.Length > 32UL << 20 || size > limits.MaxStorageBufferRange ||
            vertices.Buffer.Size > limits.MaxStorageBufferRange || current.Buffer.Size > limits.MaxStorageBufferRange ||
            flags.Buffer.Size > limits.MaxStorageBufferRange || direction.Buffer.Size > limits.MaxStorageBufferRange ||
            !_frames.Current.TryAllocateUniforms(128, out var state)) return 0;
        if (quadGeometryProgram == 0)
        {
            string source = ReadWorldShader("geometry/quads.comp");
            foreach (string include in new[] { "vertexflagbits.glsl", "noise3d.glsl", "warpstate.glsl" })
                source = source.Replace("#include \"../include/" + include + "\"", ReadWorldShader("include/" + include), StringComparison.Ordinal);
            quadGeometryProgram = CreateComputeProgram(source, "quad-geometry",
                [new ComputeSlot(0, ComputeSlotKind.Buffer), new ComputeSlot(1, ComputeSlotKind.Buffer), new ComputeSlot(2, ComputeSlotKind.Buffer),
                    new ComputeSlot(3, ComputeSlotKind.Buffer), new ComputeSlot(4, ComputeSlotKind.Buffer), new ComputeSlot(5, ComputeSlotKind.Buffer)], 44, 64, 1);
        }
        var compute = _compute.Get(quadGeometryProgram);
        if (compute == null) return 0;
        parameters[..128].CopyTo(new Span<byte>((void*)state.Pointer, 128));
        var output = new VulkanBuffer(_context, size, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
        try
        {
            var command = Commands; using GpuSection section = BeginGpuSection("quad-geometry"); _targets.EndRendering(command);
            foreach (var input in new[] { vertices, current, flags, direction }) _uploads.NoteUse(command, input.Buffer);
            _graph.OpenComputePass(new PassSignature { NameId = _graph.NameId("compute:quad-geometry"), Width = (int)vertexCount * instances,
                Height = 1, FormatsId = -1, Reads = [], Attachments = [] });
            var set = _computeArenas[CurrentFrameSlot].GetBuffers(compute.SetLayout,
                [new DescriptorBufferInfo(vertices.Buffer.Handle, 0, vertices.Buffer.Size), new DescriptorBufferInfo(current.Buffer.Handle, 0, current.Buffer.Size),
                    new DescriptorBufferInfo(flags.Buffer.Handle, 0, flags.Buffer.Size), new DescriptorBufferInfo(direction.Buffer.Handle, 0, direction.Buffer.Size),
                    new DescriptorBufferInfo(_frames.UniformBuffer, state.Offset, 128), new DescriptorBufferInfo(output.Handle, 0, size)]);
            _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(compute, []));
            _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, compute.Layout, 0, 1, &set, 0, null);
            fixed (byte* data = parameters) _context.Api.CmdPushConstants(command, compute.Layout, ShaderStageFlags.ComputeBit, 0, 44, data + 128);
            _context.Api.CmdDispatch(command, (vertexCount * (uint)instances + 63) / 64, 1, 1); _graph.NoteDispatch();
            QuadGeometryDispatchesRecorded++; ParticleGeometryDispatchesRecorded++;
            var barrier = new BufferMemoryBarrier2 { SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = output.Handle, Size = size };
            var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
            _context.Api.CmdPipelineBarrier2(command, &dependency);
            if (versions == null) particleGeometry.Add(key, versions = new());
            versions.Add(new ParticleGeometry(output, parameters.ToArray(), _frameCounter)); particleGeometryBytes += size + (ulong)parameters.Length;
            drawParticleGeometry = output; return (int)vertexCount;
        }
        catch { _frames.DeferDeletion(output); throw; }
    }
}
