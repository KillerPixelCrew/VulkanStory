using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private readonly record struct ParticleInput(VulkanBuffer Buffer, uint Stride, uint Offset);
    private readonly record struct ParticleGeometryKey(VulkanMesh Mesh, ulong Revision, int Instances, int Hash, bool Quad);
    private sealed class ParticleGeometry(VulkanBuffer buffer, byte[] parameters, uint frame)
    {
        internal readonly VulkanBuffer Buffer = buffer;
        internal readonly byte[] Parameters = parameters;
        internal uint Frame = frame;
    }
    private readonly Dictionary<ParticleGeometryKey, List<ParticleGeometry>> particleGeometry = new();
    private readonly List<ParticleGeometryKey> oldParticleGeometry = new();
    private ulong particleGeometryBytes;
    private int particleGeometryProgram;
    private VulkanBuffer? drawParticleGeometry;
    internal bool ComputeParticleGeometryEnabled { get; set; }
    internal long ParticleGeometryDispatchesRecorded { get; private set; }
    internal long ParticleGeometryCacheReuses { get; private set; }

    private void PrepareParticleGeometry(ShaderProgramResources program, int meshId, int instances)
    {
        drawParticleGeometry = null;
        bool quad = program.LocationOf("vsComputeQuadGeometry") >= 0;
        int location = program.LocationOf(quad ? "vsComputeQuadGeometry" : "vsComputeParticleGeometry");
        if (location < 0) return;
        int count = quad ? TryPrepareQuadGeometry(program, meshId, instances) : TryPrepareParticleGeometry(program, meshId, instances);
        ReadOnlySpan<byte> bytes = new(&count, 4);
        if (ShaderProgramResources.IsPushLocation(location)) program.SetPushUniform(location, bytes);
        else program.SetUniform(location, bytes);
    }

    private static bool TryParticleInput(VulkanMesh mesh, uint location, Format format, bool instanced, out ParticleInput input)
    {
        input = default;
        foreach (var attribute in mesh.Layout.Attributes)
        {
            if (attribute.Location != location || attribute.Format != format || attribute.Offset % 4 != 0 ||
                attribute.Binding >= mesh.BindingOrder.Count) continue;
            foreach (var binding in mesh.Layout.Bindings)
                if (binding.Binding == attribute.Binding && binding.PerInstance == instanced && binding.Stride > 0 && binding.Stride % 4 == 0 &&
                    mesh.Buffers[mesh.BindingOrder[(int)attribute.Binding]] is { } buffer && (buffer.Usage & BufferUsageFlags.StorageBufferBit) != 0)
                { input = new ParticleInput(buffer, binding.Stride, attribute.Offset); return true; }
        }
        return false;
    }

    private int TryPrepareParticleGeometry(ShaderProgramResources program, int meshId, int instances)
    {
        if (!ComputeParticleGeometryEnabled || !program.ComputeWarpCompatible || instances <= 0 || program.WorldWarpWaving is not (0 or 1) ||
            _meshes.Get(meshId) is not { } mesh || !program.Interface.StorageBlocks.Exists(block => block.BlockName == "VulkanParticleGeometry") ||
            !TryParticleInput(mesh, 0, Format.R32G32B32Sfloat, false, out var vertices) ||
            !TryParticleInput(mesh, 4, Format.R32G32B32Sfloat, true, out var current) ||
            !TryParticleInput(mesh, 5, Format.R32Sfloat, true, out var scale) ||
            !TryParticleInput(mesh, 3, Format.R32Sint, true, out var flags) ||
            !TryParticleInput(mesh, 9, Format.R32G32B32Sfloat, true, out var history) ||
            !TryParticleInput(mesh, 10, Format.R32G32B32Sfloat, true, out var oldScale) ||
            !TryParticleInput(mesh, 12, Format.R32Sfloat, true, out var valid) ||
            !ReferenceEquals(current.Buffer, scale.Buffer) || scale.Offset != current.Offset + 12 || scale.Stride != current.Stride ||
            !ReferenceEquals(history.Buffer, oldScale.Buffer) || !ReferenceEquals(history.Buffer, valid.Buffer) ||
            oldScale.Offset != history.Offset + 12 || valid.Offset != history.Offset + 40 || oldScale.Stride != history.Stride || valid.Stride != history.Stride ||
            vertices.Buffer.Size < vertices.Offset + 12UL ||
            current.Buffer.Size < current.Offset + (ulong)(instances - 1) * current.Stride + 16 ||
            flags.Buffer.Size < flags.Offset + (ulong)(instances - 1) * flags.Stride + 4 ||
            history.Buffer.Size < history.Offset + (ulong)(instances - 1) * history.Stride + 44) return 0;
        uint vertexCount = (uint)((vertices.Buffer.Size - vertices.Offset - 12) / vertices.Stride + 1);
        if (vertexCount is 0 or > 128 || (ulong)vertexCount * (uint)instances > 1048576) return 0;
        current = VisualParticleInput(meshId, 4, current);
        history = VisualParticleInput(meshId, 9, history);
        Span<byte> parameters = stackalloc byte[172]; parameters.Clear();
        if (!ReadGeometryWarpState(program, false, parameters[..64]) || !ReadGeometryWarpState(program, true, parameters.Slice(64, 64))) return 0;
        var push = MemoryMarshal.Cast<byte, uint>(parameters[128..]);
        push[0] = vertexCount; push[1] = (uint)instances; push[2] = vertices.Stride / 4; push[3] = vertices.Offset / 4;
        push[4] = current.Stride / 4; push[5] = current.Offset / 4; push[6] = flags.Stride / 4; push[7] = flags.Offset / 4;
        push[8] = history.Stride / 4; push[9] = history.Offset / 4; push[10] = (uint)program.WorldWarpWaving!.Value;
        var hash = new HashCode(); hash.AddBytes(parameters);
        var key = new ParticleGeometryKey(mesh, mesh.GeometryRevision, instances, hash.ToHashCode(), false);
        if (particleGeometry.TryGetValue(key, out var versions))
            foreach (var geometry in versions)
                if (parameters.SequenceEqual(geometry.Parameters))
                { geometry.Frame = _frameCounter; ParticleGeometryCacheReuses++; drawParticleGeometry = geometry.Buffer; return (int)vertexCount; }
        ulong size = (ulong)vertexCount * (uint)instances * 32;
        var limits = _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        if (particleGeometryBytes + size + (ulong)parameters.Length > 32UL << 20 || size > limits.MaxStorageBufferRange ||
            vertices.Buffer.Size > limits.MaxStorageBufferRange || current.Buffer.Size > limits.MaxStorageBufferRange ||
            flags.Buffer.Size > limits.MaxStorageBufferRange || history.Buffer.Size > limits.MaxStorageBufferRange ||
            !_frames.Current.TryAllocateUniforms(128, out var state)) return 0;
        if (particleGeometryProgram == 0)
        {
            string source = ReadWorldShader("geometry/particles.comp");
            foreach (string include in new[] { "vertexflagbits.glsl", "noise3d.glsl", "warpstate.glsl" })
                source = source.Replace("#include \"../include/" + include + "\"", ReadWorldShader("include/" + include), StringComparison.Ordinal);
            particleGeometryProgram = CreateComputeProgram(source, "particle-geometry",
                [new ComputeSlot(0, ComputeSlotKind.Buffer), new ComputeSlot(1, ComputeSlotKind.Buffer), new ComputeSlot(2, ComputeSlotKind.Buffer),
                    new ComputeSlot(3, ComputeSlotKind.Buffer), new ComputeSlot(4, ComputeSlotKind.Buffer), new ComputeSlot(5, ComputeSlotKind.Buffer)], 44, 64, 1);
        }
        var compute = _compute.Get(particleGeometryProgram);
        if (compute == null) return 0;
        parameters[..128].CopyTo(new Span<byte>((void*)state.Pointer, 128));
        var output = new VulkanBuffer(_context, size, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
        try
        {
            var command = Commands;
            using GpuSection section = BeginGpuSection("particle-geometry");
            _targets.EndRendering(command);
            foreach (var input in new[] { vertices, current, flags, history }) _uploads.NoteUse(command, input.Buffer);
            _graph.OpenComputePass(new PassSignature { NameId = _graph.NameId("compute:particle-geometry"),
                Width = (int)vertexCount * instances, Height = 1, FormatsId = -1, Reads = [], Attachments = [] });
            var set = _computeArenas[CurrentFrameSlot].GetBuffers(compute.SetLayout,
                [new DescriptorBufferInfo(vertices.Buffer.Handle, 0, vertices.Buffer.Size), new DescriptorBufferInfo(current.Buffer.Handle, 0, current.Buffer.Size),
                    new DescriptorBufferInfo(flags.Buffer.Handle, 0, flags.Buffer.Size), new DescriptorBufferInfo(history.Buffer.Handle, 0, history.Buffer.Size),
                    new DescriptorBufferInfo(_frames.UniformBuffer, state.Offset, 128), new DescriptorBufferInfo(output.Handle, 0, size)]);
            _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(compute, []));
            _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, compute.Layout, 0, 1, &set, 0, null);
            fixed (byte* data = parameters) _context.Api.CmdPushConstants(command, compute.Layout, ShaderStageFlags.ComputeBit, 0, 44, data + 128);
            _context.Api.CmdDispatch(command, (vertexCount * (uint)instances + 63) / 64, 1, 1); _graph.NoteDispatch(); ParticleGeometryDispatchesRecorded++;
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

    private void RetireParticleGeometry(bool all = false)
    {
        oldParticleGeometry.Clear();
        foreach (var pair in particleGeometry)
        {
            for (int i = pair.Value.Count - 1; i >= 0; i--)
                if (all || unchecked(_frameCounter - pair.Value[i].Frame) > 2)
                {
                    var geometry = pair.Value[i]; _descriptors.Release(geometry.Buffer.Id); _frames.DeferDeletion(geometry.Buffer);
                    particleGeometryBytes -= geometry.Buffer.Size + (ulong)geometry.Parameters.Length; pair.Value.RemoveAt(i);
                }
            if (pair.Value.Count == 0) oldParticleGeometry.Add(pair.Key);
        }
        foreach (var key in oldParticleGeometry) particleGeometry.Remove(key);
        drawParticleGeometry = null;
    }

    private void DisposeParticleGeometryIdle()
    {
        foreach (var versions in particleGeometry.Values) foreach (var geometry in versions) geometry.Buffer.Dispose();
        particleGeometry.Clear(); particleGeometryBytes = 0;
    }
}
