using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private int particleCentersProgram;
    internal long ParticleSimulationCenterDispatchesRecorded { get; private set; }
    internal long ParticleSimulationCentersRecorded { get; private set; }
    private readonly Dictionary<int, (VulkanMesh Mesh, ulong Revision)> particleCenterRevisions = new();
    private readonly Dictionary<ParticleSimulationRunner.SimulationBuffer, List<ParticleSimulationRenderInput>> particleCenterGroups = new();
    private readonly Stack<List<ParticleSimulationRenderInput>> particleCenterLists = new();

    internal void PrepareParticleSimulationCenters(int meshId, ReadOnlySpan<ParticleSimulationBuffer?> buffers,
        ReadOnlySpan<ParticleSimulationRenderInput> inputs)
    {
        if (_meshes.Get(meshId) is not { } mesh || inputs.Length != buffers.Length ||
            !TryParticleInput(mesh, 4, Format.R32G32B32Sfloat, true, out var destination) ||
            destination.Offset != 0 || destination.Stride != 16 || destination.Buffer.Size < (ulong)inputs.Length * 16 ||
            (particleCenterRevisions.TryGetValue(meshId, out var previous) && ReferenceEquals(previous.Mesh, mesh) && previous.Revision == mesh.GeometryRevision)) return;
        var groups = particleCenterGroups;
        try
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                if (buffers[i] is not ParticleSimulationRunner.SimulationBuffer buffer || !ReferenceEquals(buffer.Owner, particleSimulation)) continue;
                if (!groups.TryGetValue(buffer, out var values))
                    groups.Add(buffer, values = particleCenterLists.Count != 0 ? particleCenterLists.Pop() : new());
                values.Add(inputs[i]);
            }
            if (groups.Count == 0) return;
            if (particleCentersProgram == 0) particleCentersProgram = CreateComputeProgram(ParticleEvolutionShader("particle-centers.comp"), "particle-centers",
                [new ComputeSlot(0, ComputeSlotKind.Buffer), new ComputeSlot(1, ComputeSlotKind.Buffer), new ComputeSlot(2, ComputeSlotKind.Buffer)], 4, 64, 1);
            var program = _compute.Get(particleCentersProgram);
            if (program == null) return;
            var command = Commands;
            using GpuSection section = BeginGpuSection("particle-centers");
            _targets.EndRendering(command);
            _uploads.NoteUse(command, destination.Buffer);
            var before = new BufferMemoryBarrier2 { SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllCommandsBit, SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit, DstAccessMask = AccessFlags2.ShaderStorageWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = destination.Buffer.Handle, Size = destination.Buffer.Size };
            var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &before };
            _context.Api.CmdPipelineBarrier2(command, &dependency);
            _graph.OpenComputePass(new PassSignature { NameId = _graph.NameId("compute:particle-centers"), Width = inputs.Length,
                Height = 1, FormatsId = -1, Reads = [], Attachments = [] });
            _frames.GateNextFrameSubmit(particleSimulation!.Timeline, groups.Keys.Max(buffer => buffer.TimelineValue));
            bool exhausted = false;
            foreach (var pair in groups)
            {
                // A frame reference survives producer-buffer overwrite, live feature
                // switching and both eyes until all recorded graphics reads retire.
                _frames.DeferDeletion(pair.Key.Retain());
                var values = CollectionsMarshal.AsSpan(pair.Value);
                for (int first = 0; first < values.Length; first += 1024)
                {
                    int count = Math.Min(1024, values.Length - first), bytes = count * 64;
                    if (!_frames.Current.TryAllocateUniforms(bytes, out var upload))
                    { exhausted = true; break; } // Unwritten instances retain the exact original CPU array upload.
                    MemoryMarshal.AsBytes(values.Slice(first, count)).CopyTo(new Span<byte>((void*)upload.Pointer, bytes));
                    var set = _computeArenas[CurrentFrameSlot].GetBuffers(program.SetLayout,
                        [new DescriptorBufferInfo(pair.Key.Buffer.Handle, 0, pair.Key.Buffer.Size),
                            new DescriptorBufferInfo(_frames.UniformBuffer, upload.Offset, (ulong)bytes),
                            new DescriptorBufferInfo(destination.Buffer.Handle, 0, destination.Buffer.Size)]);
                    _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, []));
                    _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
                    uint quantity = (uint)count;
                    _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 4, &quantity);
                    _context.Api.CmdDispatch(command, (quantity + 63) / 64, 1, 1); _graph.NoteDispatch();
                    ParticleSimulationCenterDispatchesRecorded++; ParticleSimulationCentersRecorded += count;
                }
                if (exhausted) break;
            }
            var after = new BufferMemoryBarrier2 { SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.VertexInputBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
                DstAccessMask = AccessFlags2.VertexAttributeReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = destination.Buffer.Handle, Size = destination.Buffer.Size };
            dependency.PBufferMemoryBarriers = &after; _context.Api.CmdPipelineBarrier2(command, &dependency);
            particleCenterRevisions[meshId] = (mesh, mesh.GeometryRevision);
        }
        finally
        {
            foreach (var values in groups.Values) { values.Clear(); particleCenterLists.Push(values); }
            groups.Clear(); // Do not retain simulation buffers after the draw.
        }
    }

    private void RetireParticleSimulationCenters(bool all)
    {
        foreach (var id in particleCenterRevisions.Where(pair => all || !ReferenceEquals(_meshes.Get(pair.Key), pair.Value.Mesh)).Select(pair => pair.Key).ToArray())
            particleCenterRevisions.Remove(id);
        // A zero lease count includes completion of every frame-retained read.
        // Invalidate old descriptors before the runner reuses this buffer.
        particleSimulation?.Retire(buffer => _descriptors.Release(buffer.Id));
    }
}
