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
    private const ulong ParticleInstanceBudget = 128UL << 20;
    // Charges survive deferred destruction: allocations still in flight consume
    // the same bounded budget as current handoffs, descriptors and history.
    private sealed class InstanceStorage(VulkanDevice owner, VulkanBuffer buffer, ulong charge) : IDisposable
    {
        internal readonly VulkanBuffer Buffer = buffer;
        private int references = 1;
        internal InstanceStorage Retain() { references++; return this; }
        internal void Release() { if (--references == 0) { owner._descriptors.Release(buffer.Id); owner._frames.DeferDeletion(this); } }
        public void Dispose() { buffer.Dispose(); owner.particleInstanceBytes -= charge; }
    }
    private sealed record InstanceGroup(ParticleSimulationRunner.SimulationBuffer Simulation, int First, int Count);
    private sealed class InstanceCharge(VulkanDevice owner, ulong bytes) : IDisposable
    { public void Dispose() => owner.particleInstanceBytes -= bytes; }
    private sealed class InstanceResident
    {
        internal required InstanceStorage Inputs, Configuration;
        internal required ParticleEvolution[] Curves;
        internal required object[] Tokens;
        internal readonly List<InstanceGroup> Groups = new();
        internal long Generation;
        internal InstanceStorage? Last;
        internal int StepCount;
        internal uint Frame;
    }
    private sealed class InstanceRendered
    {
        internal required VulkanMesh Mesh;
        internal required InstanceStorage Current;
        internal required object[] Tokens;
        internal required uint[] Flags;
        internal required InstanceResident Resident;
        internal InstanceRendered? Previous;
        internal long Frame;
        internal bool Drawn;
        internal int Valid;
    }
    private readonly Dictionary<object, InstanceResident> particleInstanceResidents = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, InstanceRendered> particleInstances = new();
    private readonly Dictionary<VulkanMesh, (int MeshId, VulkanBuffer Buffer, ulong Bytes)> particleInstanceHistory = new();
    private ulong particleInstanceTableCharge;
    private ulong particleInstanceBytes;
    private int particleInstanceProgram;
    private bool particleInstanceFailureLogged;
    internal long ParticleInstanceDispatchesRecorded { get; private set; }
    internal long ParticleInstancesRecorded { get; private set; }
    internal long ParticleInstanceHistoriesRecorded { get; private set; }
    internal bool HasParticleInstances(int meshId) => particleInstances.TryGetValue(meshId, out var value) && ReferenceEquals(_meshes.Get(meshId), value.Mesh);
    internal int ParticleInstanceValidHistory(int meshId) => particleInstances.TryGetValue(meshId, out var value) ? value.Valid : 0;

    private InstanceStorage? AllocateInstanceStorage(ulong bytes, bool mapped, ulong shadow = 0)
    {
        ulong charge = bytes + shadow;
        if (charge > ParticleInstanceBudget || particleInstanceBytes > ParticleInstanceBudget - charge) return null;
        VulkanBuffer buffer;
        try
        {
            buffer = new VulkanBuffer(_context, Math.Max(4UL, bytes), BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                mapped ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit : MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
        }
        catch (InvalidOperationException) when (!VulkanResult.DeviceLost) { return null; }
        particleInstanceBytes += charge;
        return new InstanceStorage(this, buffer, charge);
    }

    internal bool UpdateParticleInstances(int meshId, object handoff, long generation,
        ReadOnlySpan<ParticleSimulationBuffer?> simulations, ReadOnlySpan<ParticleInstanceInput> inputs,
        ReadOnlySpan<ParticleEvolution> curves, ReadOnlySpan<object> tokens, ReadOnlySpan<System.Numerics.Vector4> steps, long frame, bool reset, float[] table)
    {
        try { return UpdateParticleInstancesCore(meshId, handoff, generation, simulations, inputs, curves, tokens, steps, frame, reset, table); }
        catch (InvalidOperationException failure) when (!VulkanResult.DeviceLost)
        {
            if (!particleInstanceFailureLogged) RenderLogger?.Warning("VulkanStory: particle instance preparation returned to CPU: {0}", failure.Message);
            particleInstanceFailureLogged = true;
            return false;
        }
    }

    private bool UpdateParticleInstancesCore(int meshId, object handoff, long generation,
        ReadOnlySpan<ParticleSimulationBuffer?> simulations, ReadOnlySpan<ParticleInstanceInput> inputs,
        ReadOnlySpan<ParticleEvolution> curves, ReadOnlySpan<object> tokens, ReadOnlySpan<System.Numerics.Vector4> steps, long frame, bool reset, float[] table)
    {
        int count = inputs.Length;
        if (!ParticleSimulationAvailable || count is <= 0 or > 65536 || curves.Length != count * 5 || tokens.Length != count || simulations.Length != count || table.Length != 8193 ||
            _meshes.Get(meshId) is not { } mesh || !TryParticleInstanceDestinations(mesh, count, out var centers, out var attributes, out var flags)) return false;
        var limits = _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        if (limits.MaxPerStageDescriptorStorageBuffers < 13 || limits.MaxDescriptorSetStorageBuffers < 13 || (ulong)curves.Length * 32 > limits.MaxStorageBufferRange ||
            centers.Buffer.Size > limits.MaxStorageBufferRange || attributes.Buffer.Size > limits.MaxStorageBufferRange || flags.Buffer.Size > limits.MaxStorageBufferRange) return false;
        if (mesh.Layout.Attributes.Any(attribute => attribute.Location >= 9) &&
            (!TryParticleInput(mesh, 9, Format.R32G32B32Sfloat, true, out var existingHistory) || existingHistory.Stride != 44 || existingHistory.Offset != 0 ||
                mesh.Layout.Attributes.Count(attribute => attribute.Location >= 9) != 4)) return false;
        for (int i = 0; i < count; i++)
            if (simulations[i] is not ParticleSimulationRunner.SimulationBuffer simulation || !ReferenceEquals(simulation.Owner, particleSimulation) ||
                (ulong)inputs[i].Source * 64 + 64 > simulation.Buffer.Size || inputs[i].Target != (uint)i) return false;
        if (particleInstanceProgram == 0)
            particleInstanceProgram = CreateComputeProgram(ParticleEvolutionShader("particle-instances.comp"), "particle-instances",
                Enumerable.Range(0, 13).Select(i => new ComputeSlot((uint)i, ComputeSlotKind.Buffer)).ToArray(), 16, 64, 1);
        var program = _compute.Get(particleInstanceProgram);
        if (program == null) return false;
        if (particleTrigonometryBuffer == null)
        {
            ulong tableCharge = (ulong)table.Length * 8;
            if (particleInstanceBytes + tableCharge > ParticleInstanceBudget) return false;
            particleTrigonometry = (float[])table.Clone();
            particleTrigonometryBuffer = new VulkanBuffer(_context, (ulong)table.Length * 4, BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.DeviceBuffers);
            table.AsSpan().CopyTo(new Span<float>((void*)particleTrigonometryBuffer.Mapped, table.Length));
            particleInstanceBytes += tableCharge; particleInstanceTableCharge = tableCharge;
        }
        particleInstanceResidents.TryGetValue(handoff, out var resident);
        bool fresh = resident == null || resident.Generation != generation;
        if (fresh)
        {
            var normalized = new ParticleEvolution[curves.Length];
            for (int i = 0; i < curves.Length; i++)
            { var c = curves[i]; normalized[i] = new ParticleEvolution(c.First, c.Factor, 0, c.Maximum, c.Function, c.Limited != 0); }
            bool dirty = resident == null || resident.Curves.Length < normalized.Length ||
                !MemoryMarshal.AsBytes(normalized.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(resident.Curves.AsSpan(0, normalized.Length)));
            ulong configurationBytes = Math.Max((ulong)normalized.Length * 32, resident?.Configuration.Buffer.Size ?? 0);
            var configuration = dirty ? AllocateInstanceStorage(configurationBytes, false, configurationBytes * 2) : resident!.Configuration.Retain();
            var commands = AllocateInstanceStorage((ulong)count * 80, true, (ulong)count * 80);
            if (configuration == null || commands == null) { configuration?.Release(); commands?.Release(); return false; }
            InstanceResident? next = null;
            bool committed = false;
            try
            {
                MemoryMarshal.AsBytes(inputs).CopyTo(new Span<byte>((void*)commands.Buffer.Mapped, count * 80));
                if (dirty)
                {
                    // Keep unchanged descriptors on the GPU even when births/deaths
                    // resize a handoff. Only changed slots cross the upload boundary.
                    var copyCommands = Commands; _targets.EndRendering(copyCommands);
                    _uploads.NoteUse(copyCommands, configuration.Buffer);
                    if (resident != null)
                    {
                        var copy = new BufferCopy { Size = resident.Configuration.Buffer.Size };
                        _context.Api.CmdCopyBuffer(copyCommands, resident.Configuration.Buffer.Handle, configuration.Buffer.Handle, 1, &copy);
                    }
                    for (int first = 0; first < normalized.Length;)
                    {
                        if (resident != null && SameInstanceCurve(normalized, resident.Curves, first)) { first++; continue; }
                        int end = first + 1;
                        while (end < normalized.Length && (resident == null || !SameInstanceCurve(normalized, resident.Curves, end))) end++;
                        fixed (ParticleEvolution* values = normalized)
                            _uploads.UploadToBuffer(configuration.Buffer, (ulong)first * 32, (IntPtr)(values + first), (ulong)(end - first) * 32);
                        first = end;
                    }
                }
                next = new InstanceResident { Inputs = commands, Configuration = configuration, Curves = normalized, Tokens = tokens.ToArray(), Generation = generation, Frame = _frameCounter };
                for (int first = 0; first < count;)
                {
                    var simulation = (ParticleSimulationRunner.SimulationBuffer)simulations[first]!;
                    int end = first + 1; while (end < count && ReferenceEquals(simulations[end], simulation)) end++;
                    next.Groups.Add(new InstanceGroup((ParticleSimulationRunner.SimulationBuffer)simulation.Retain(), first, end - first)); first = end;
                }
                if (resident != null) ReleaseInstanceResident(resident);
                particleInstanceResidents[handoff] = resident = next;
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    configuration.Release(); commands.Release();
                    if (next != null) foreach (var group in next.Groups) group.Simulation.Dispose();
                }
            }
        }
        resident!.Frame = _frameCounter;
        // The producer generation is immutable. A worker read buffer can be
        // consumed repeatedly, but each ordered float addition executes once.
        int firstStep = resident.Last == null ? 0 : resident.StepCount;
        if (steps.Length < firstStep) return false;
        int stepCount = steps.Length - firstStep;
        var output = AllocateInstanceStorage((ulong)count * 32, false, (ulong)count * 64);
        var mapping = AllocateInstanceStorage((ulong)count * 4, true);
        var stepBuffer = AllocateInstanceStorage((ulong)Math.Max(1, stepCount) * 16, true);
        if (output == null || mapping == null || stepBuffer == null)
        { output?.Release(); mapping?.Release(); stepBuffer?.Release(); return false; }
        try
        {
            InstanceRendered? previous = null;
            if (particleInstances.TryGetValue(meshId, out var prior) && ReferenceEquals(prior.Mesh, mesh))
                previous = prior.Frame == frame ? prior.Previous : prior.Drawn ? prior : null;
            var indices = new Span<uint>((void*)mapping.Buffer.Mapped, count); indices.Fill(uint.MaxValue);
            int valid = 0;
            if (!reset && previous != null && previous.Frame == frame - 1)
            {
                var old = new Dictionary<object, uint>(previous.Tokens.Length, ReferenceEqualityComparer.Instance);
                for (int i = 0; i < previous.Tokens.Length; i++) old[previous.Tokens[i]] = (uint)i;
                for (int i = 0; i < count; i++)
                    if (old.TryGetValue(tokens[i], out uint index) && previous.Flags[index] == inputs[i].Flags) { indices[i] = index; valid++; }
            }
            MemoryMarshal.AsBytes(steps[firstStep..]).CopyTo(new Span<byte>((void*)stepBuffer.Buffer.Mapped, stepCount * 16));
            // GPU allocation/layout only: there is no CPU history array, initial
            // zero upload or equality shadow on the admitted path.
            bool grew = !TryParticleInput(mesh, 9, Format.R32G32B32Sfloat, true, out var history) || history.Buffer.Size < (ulong)count * 44;
            ulong historyCharge = grew ? (ulong)count * 44 : history.Buffer.Size;
            bool chargeHistory = !particleInstanceHistory.TryGetValue(mesh, out var chargedHistory) || grew || !ReferenceEquals(chargedHistory.Buffer, history.Buffer);
            if (chargeHistory && particleInstanceBytes + historyCharge > ParticleInstanceBudget) return false;
            if (grew)
            {
                var buffer = _meshes.EnsureParticleHistoryStorage(meshId, checked(count * 44));
                history = new ParticleInput(buffer, 44, 0);
            }
            if (chargeHistory)
            {
                if (chargedHistory.Buffer != null) _frames.DeferDeletion(new InstanceCharge(this, chargedHistory.Bytes));
                particleInstanceBytes += historyCharge; particleInstanceHistory[mesh] = (meshId, history.Buffer, historyCharge);
            }
            var command = Commands;
            using GpuSection section = BeginGpuSection("particle-instances");
            _targets.EndRendering(command);
            var targets = new[] { centers.Buffer, attributes.Buffer, flags.Buffer, history.Buffer };
            foreach (var target in targets) { _uploads.NoteUse(command, target); ParticleInstanceBarrier(command, target, false); }
            _graph.OpenComputePass(new PassSignature { NameId = _graph.NameId("compute:particle-instances"), Width = count, Height = 1,
                FormatsId = -1, Reads = [], Attachments = [] });
            _frames.GateNextFrameSubmit(particleSimulation!.Timeline, resident.Groups.Max(group => group.Simulation.TimelineValue));
            uint* push = stackalloc uint[4];
            foreach (var group in resident.Groups)
            {
                _frames.DeferDeletion(group.Simulation.Retain());
                var set = _computeArenas[CurrentFrameSlot].GetBuffers(program.SetLayout,
                    [Info(group.Simulation.Buffer), Info(resident.Inputs.Buffer), Info(resident.Configuration.Buffer), Info(particleTrigonometryBuffer),
                        Info(resident.Last?.Buffer ?? output.Buffer), Info(previous?.Current.Buffer ?? output.Buffer), Info(mapping.Buffer), Info(stepBuffer.Buffer),
                        Info(centers.Buffer), Info(attributes.Buffer), Info(flags.Buffer), Info(history.Buffer), Info(output.Buffer)]);
                _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, []));
                _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
                push[0] = (uint)group.First; push[1] = (uint)group.Count; push[2] = resident.Last == null ? 0u : 1u; push[3] = (uint)stepCount;
                _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 16, push);
                _context.Api.CmdDispatch(command, ((uint)group.Count + 63) / 64, 1, 1); _graph.NoteDispatch();
                ParticleInstanceDispatchesRecorded++; ParticleInstancesRecorded += group.Count;
            }
            foreach (var target in targets) ParticleInstanceBarrier(command, target, true);
            ParticleInstanceBarrier(command, output.Buffer, true);
            resident.Last?.Release(); resident.Last = output.Retain(); resident.StepCount = steps.Length;
            var saved = previous == null ? null : new InstanceRendered { Mesh = mesh, Current = previous.Current.Retain(), Tokens = previous.Tokens,
                Flags = previous.Flags, Resident = previous.Resident, Frame = previous.Frame, Drawn = previous.Drawn };
            var flagValues = new uint[count]; for (int i = 0; i < count; i++) flagValues[i] = inputs[i].Flags;
            ClearParticleInstances(meshId);
            particleInstances[meshId] = new InstanceRendered { Mesh = mesh, Current = output.Retain(), Tokens = tokens.ToArray(), Flags = flagValues,
                Resident = resident, Previous = saved, Frame = frame, Valid = valid };
            particleVisualSources.Remove(mesh); // No CPU visual/history shadow may overwrite this stream.
            mesh.ParticleHistoryShadow = []; // GPU writes invalidate the CPU upload equality cache.
            mesh.GeometryRevision++;
            return true;
        }
        finally { output.Release(); mapping.Release(); stepBuffer.Release(); }
    }

    private static DescriptorBufferInfo Info(VulkanBuffer buffer) => new(buffer.Handle, 0, buffer.Size);
    private static bool SameInstanceCurve(ParticleEvolution[] current, ParticleEvolution[] previous, int index) => index < previous.Length &&
        MemoryMarshal.AsBytes(current.AsSpan(index, 1)).SequenceEqual(MemoryMarshal.AsBytes(previous.AsSpan(index, 1)));
    private static bool TryParticleInstanceDestinations(VulkanMesh mesh, int count, out ParticleInput centers, out ParticleInput attributes, out ParticleInput flags)
    {
        centers = attributes = flags = default;
        return TryParticleInput(mesh, 4, Format.R32G32B32Sfloat, true, out centers) && centers.Offset == 0 && centers.Stride == 16 && centers.Buffer.Size >= (ulong)count * 16 &&
            TryParticleInput(mesh, 5, Format.R32Sfloat, true, out var size) && ReferenceEquals(size.Buffer, centers.Buffer) && size.Offset == 12 && size.Stride == 16 &&
            TryParticleInput(mesh, 6, Format.R8G8B8A8Unorm, true, out attributes) && attributes.Offset == 0 && attributes.Stride == 12 && attributes.Buffer.Size >= (ulong)count * 12 &&
            TryParticleInput(mesh, 7, Format.R8G8B8A8Unorm, true, out var light) && ReferenceEquals(light.Buffer, attributes.Buffer) && light.Offset == 4 && light.Stride == 12 &&
            TryParticleInput(mesh, 8, Format.R8G8B8A8Unorm, true, out var color) && ReferenceEquals(color.Buffer, attributes.Buffer) && color.Offset == 8 && color.Stride == 12 &&
            TryParticleInput(mesh, 3, Format.R32Sint, true, out flags) && flags.Offset == 0 && flags.Stride == 4 && flags.Buffer.Size >= (ulong)count * 4;
    }
    private void ParticleInstanceBarrier(CommandBuffer command, VulkanBuffer buffer, bool after)
    {
        var barrier = new BufferMemoryBarrier2 { SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = after ? PipelineStageFlags2.ComputeShaderBit : PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = after ? AccessFlags2.ShaderStorageWriteBit : AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            DstStageMask = after ? PipelineStageFlags2.AllCommandsBit : PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = after ? AccessFlags2.MemoryReadBit : AccessFlags2.ShaderStorageWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = buffer.Handle, Size = buffer.Size };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
        _context.Api.CmdPipelineBarrier2(command, &dependency);
    }
    internal void ClearParticleInstances(int meshId)
    {
        if (!particleInstances.Remove(meshId, out var previous)) return;
        previous.Current.Release(); previous.Previous?.Current.Release();
    }
    internal void PrepareParticleInstanceFrame(int meshId, long frame, bool reset)
    {
        if (!particleInstances.TryGetValue(meshId, out var current) || current.Frame == frame ||
            !ReferenceEquals(_meshes.Get(meshId), current.Mesh) || _compute.Get(particleInstanceProgram) is not { } program ||
            !TryParticleInput(current.Mesh, 9, Format.R32G32B32Sfloat, true, out var history)) return;
        // Paused pools do not upload. Their history still rotates once per
        // temporal frame, and both eyes reuse the same published instance state.
        var resident = current.Resident; resident.Frame = _frameCounter;
        var command = Commands; _targets.EndRendering(command);
        _uploads.NoteUse(command, history.Buffer); ParticleInstanceBarrier(command, history.Buffer, false);
        _graph.OpenComputePass(new PassSignature { NameId = _graph.NameId("compute:particle-instance-history"), Width = current.Tokens.Length,
            Height = 1, FormatsId = -1, Reads = [], Attachments = [] });
        var snapshot = current.Current.Buffer;
        var set = _computeArenas[CurrentFrameSlot].GetBuffers(program.SetLayout,
            [Info(snapshot), Info(resident.Inputs.Buffer), Info(resident.Configuration.Buffer), Info(particleTrigonometryBuffer!),
                Info(snapshot), Info(snapshot), Info(snapshot), Info(snapshot), Info(snapshot), Info(snapshot), Info(snapshot), Info(history.Buffer), Info(snapshot)]);
        _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, []));
        _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
        bool valid = !reset && current.Drawn && current.Frame == frame - 1;
        uint* push = stackalloc uint[4] { 0, (uint)current.Tokens.Length, valid ? 2u : 3u, 0 };
        _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 16, push);
        _context.Api.CmdDispatch(command, ((uint)current.Tokens.Length + 63) / 64, 1, 1); _graph.NoteDispatch();
        ParticleInstanceDispatchesRecorded++; ParticleInstanceHistoriesRecorded += current.Tokens.Length;
        ParticleInstanceBarrier(command, history.Buffer, true);
        current.Previous?.Current.Release();
        current.Previous = new InstanceRendered { Mesh = current.Mesh, Current = current.Current.Retain(), Tokens = current.Tokens, Flags = current.Flags,
            Resident = resident, Frame = current.Frame, Drawn = current.Drawn };
        current.Frame = frame; current.Drawn = false; current.Valid = valid ? current.Tokens.Length : 0;
        current.Mesh.ParticleHistoryShadow = []; current.Mesh.GeometryRevision++;
    }
    private void ParticleInstancesDrawn(int meshId) { if (particleInstances.TryGetValue(meshId, out var current)) current.Drawn = true; }
    private void ReleaseInstanceResident(InstanceResident resident)
    {
        resident.Inputs.Release(); resident.Configuration.Release(); resident.Last?.Release();
        foreach (var group in resident.Groups) group.Simulation.Dispose();
    }
    private void RetireParticleInstances(bool all)
    {
        foreach (int id in particleInstances.Where(pair => all || !ReferenceEquals(_meshes.Get(pair.Key), pair.Value.Mesh)).Select(pair => pair.Key).ToArray()) ClearParticleInstances(id);
        foreach (var key in particleInstanceResidents.Where(pair => all || unchecked(_frameCounter - pair.Value.Frame) > 120 &&
            !particleInstances.Values.Any(value => ReferenceEquals(value.Resident, pair.Value))).Select(pair => pair.Key).ToArray())
        { ReleaseInstanceResident(particleInstanceResidents[key]); particleInstanceResidents.Remove(key); }
        foreach (var mesh in particleInstanceHistory.Where(pair => all || !ReferenceEquals(_meshes.Get(pair.Value.MeshId), pair.Key)).Select(pair => pair.Key).ToArray())
        { _frames.DeferDeletion(new InstanceCharge(this, particleInstanceHistory[mesh].Bytes)); particleInstanceHistory.Remove(mesh); }
    }
}
