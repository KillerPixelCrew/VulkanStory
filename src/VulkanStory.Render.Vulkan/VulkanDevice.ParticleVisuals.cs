using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private static readonly bool CheckParticleVisuals = Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS") == "1";
    private sealed class ParticleVisualSource(int id)
    {
        internal readonly int MeshId = id;
        internal ParticleEvolution[] Properties = [], PreviousSizes = [];
        internal float[] Floats = [];
        internal byte[] Bytes = [];
        internal ulong Version, HistoryVersion;
        internal int Count;
        internal int PreviousSizeCount;
        internal float[] HistoryScratch = [];
        internal bool Materialized;
    }
    private readonly record struct ParticleVisualKey(VulkanMesh Mesh, ulong Revision, ulong Version, ulong History, int Count);
    private sealed class ParticleVisualResult(VulkanBuffer buffer, uint frame)
    { internal readonly VulkanBuffer Buffer = buffer; internal uint Frame = frame; }
    private readonly Dictionary<VulkanMesh, ParticleVisualSource> particleVisualSources = new();
    private readonly Dictionary<ParticleVisualKey, ParticleVisualResult> particleVisualResults = new();
    private readonly List<VulkanMesh> oldParticleVisualSources = new();
    private readonly List<ParticleVisualKey> oldParticleVisualResults = new();
    private float[] particleTrigonometry = [];
    private VulkanBuffer? particleTrigonometryBuffer, drawParticleVisuals;
    private int particleVisualProgram, drawParticleVisualMesh, drawParticleVisualCount;
    private ulong particleVisualBytes;
    internal bool ComputeParticleVisualsEnabled { get; set; }
    internal long ParticleVisualDispatchesRecorded { get; private set; }
    private VulkanBuffer?[] particleVisualReadbacks = [];
    private ParticleVisualCheck?[] pendingParticleVisualChecks = [];
    private sealed record ParticleVisualCheck(float[] Current, uint[] Colors, float[] Previous, int Functions);
    internal long ParticleVisualChecks { get; private set; }
    internal double ParticleVisualMaximumError { get; private set; }
    internal int ParticleVisualMaximumColorError { get; private set; }
    internal int ParticleVisualFunctionCoverage { get; private set; }
    internal bool ParticleVisualChecksPassed => ParticleVisualChecks > 0 && ParticleVisualMaximumError <= 0.00001 && ParticleVisualMaximumColorError <= 1;

    internal bool SetParticleVisuals(int meshId, ReadOnlySpan<ParticleEvolution> properties, float[] floats, byte[] bytes, int count, float[] table)
    {
        if (_meshes.Get(meshId) is not { } mesh) return false;
        if (!ComputeParticleVisualsEnabled || count <= 0 || count > 65536 || properties.Length != count * 5 || floats.Length < count * 4 ||
            bytes.Length < count * 12 || table.Length != 8193 || !TryParticleInput(mesh, 4, Format.R32G32B32Sfloat, true, out var positions) ||
            positions.Offset != 0 || positions.Stride != 16 || !TryParticleInput(mesh, 5, Format.R32Sfloat, true, out var scale) ||
            !ReferenceEquals(scale.Buffer, positions.Buffer) || scale.Offset != 12 || scale.Stride != 16 ||
            !TryParticleInput(mesh, 6, Format.R8G8B8A8Unorm, true, out var colors) || colors.Offset != 0 || colors.Stride != 12 ||
            !TryParticleInput(mesh, 7, Format.R8G8B8A8Unorm, true, out var light) || light.Offset != 4 || light.Stride != 12 ||
            !TryParticleInput(mesh, 8, Format.R8G8B8A8Unorm, true, out var rgba) || rgba.Offset != 8 || rgba.Stride != 12 ||
            !ReferenceEquals(colors.Buffer, light.Buffer) || !ReferenceEquals(colors.Buffer, rgba.Buffer))
        { particleVisualSources.Remove(mesh); return false; }
        if (particleTrigonometry.Length == 0) particleTrigonometry = (float[])table.Clone();
        if (!particleVisualSources.TryGetValue(mesh, out var source)) particleVisualSources.Add(mesh, source = new ParticleVisualSource(meshId));
        if (source.Properties.Length < properties.Length) Array.Resize(ref source.Properties, Math.Max(properties.Length, source.Properties.Length * 2));
        properties.CopyTo(source.Properties);
        if (source.Floats.Length < count * 4) Array.Resize(ref source.Floats, Math.Max(count * 4, source.Floats.Length * 2));
        floats.AsSpan(0, count * 4).CopyTo(source.Floats);
        if (source.Bytes.Length < count * 12) Array.Resize(ref source.Bytes, Math.Max(count * 12, source.Bytes.Length * 2));
        bytes.AsSpan(0, count * 12).CopyTo(source.Bytes);
        source.Count = count; source.Materialized = false; source.Version++;
        return true;
    }

    internal void ClearParticleVisuals(int meshId)
    { if (_meshes.Get(meshId) is { } mesh) particleVisualSources.Remove(mesh); }
    internal bool CanPrepareParticleVisuals(int meshId, int count) => ComputeParticleVisualsEnabled && _meshes.Get(meshId) is { } mesh &&
        particleVisualSources.TryGetValue(mesh, out var source) && !source.Materialized && count > 0 && count <= source.Count;
    internal void SetParticleVisualHistory(int meshId, ReadOnlySpan<ParticleEvolution> sizes)
    {
        if (_meshes.Get(meshId) is not { } mesh || !particleVisualSources.TryGetValue(mesh, out var source)) return;
        if (MemoryMarshal.AsBytes(sizes).SequenceEqual(MemoryMarshal.AsBytes(source.PreviousSizes.AsSpan(0, source.PreviousSizeCount)))) return;
        if (source.PreviousSizes.Length < sizes.Length) Array.Resize(ref source.PreviousSizes, Math.Max(sizes.Length, source.PreviousSizes.Length * 2));
        sizes.CopyTo(source.PreviousSizes); source.PreviousSizeCount = sizes.Length; source.HistoryVersion++;
    }

    private void MaterializeParticleVisuals(VulkanMesh mesh, ParticleVisualSource source)
    {
        if (!source.Materialized)
        {
            for (int i = 0; i < source.Count; i++)
            {
                int at = i * 5, rgba = i * 12 + 8;
                source.Floats[i * 4 + 3] = source.Properties[at].Evaluate(particleTrigonometry);
                source.Bytes[rgba] = unchecked((byte)(source.Bytes[rgba] + source.Properties[at + 4].Evaluate(particleTrigonometry)));
                source.Bytes[rgba + 1] = unchecked((byte)(source.Bytes[rgba + 1] + source.Properties[at + 3].Evaluate(particleTrigonometry)));
                source.Bytes[rgba + 2] = unchecked((byte)(source.Bytes[rgba + 2] + source.Properties[at + 2].Evaluate(particleTrigonometry)));
                source.Bytes[rgba + 3] = (byte)Math.Clamp(source.Properties[at + 1].Evaluate(particleTrigonometry), 0, 255);
            }
            fixed (float* values = source.Floats) _meshes.Write(source.MeshId, MeshManager.BufferCustomFloat, 0, (IntPtr)values, source.Count * 16);
            fixed (byte* values = source.Bytes) _meshes.Write(source.MeshId, MeshManager.BufferCustomByte, 0, (IntPtr)values, source.Count * 12);
            source.Materialized = true;
        }
        int count = source.PreviousSizeCount;
        if (count == 0 || mesh.ParticleHistoryCount < count * 11) return;
        if (source.HistoryScratch.Length < mesh.ParticleHistoryCount) Array.Resize(ref source.HistoryScratch, mesh.ParticleHistoryCount);
        var history = source.HistoryScratch.AsSpan(0, mesh.ParticleHistoryCount);
        mesh.ParticleHistoryShadow.AsSpan(0, mesh.ParticleHistoryCount).CopyTo(history);
        for (int i = 0; i < count; i++)
            history[i * 11 + 3] = history[i * 11 + 4] = history[i * 11 + 5] = source.PreviousSizes[i].Evaluate(particleTrigonometry);
        _meshes.UpdateParticleHistory(source.MeshId, history);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PrepareParticleVisuals(int meshId, int count)
    {
        drawParticleVisuals = null; drawParticleVisualMesh = 0; drawParticleVisualCount = 0;
        if (_meshes.Get(meshId) is not { } mesh || !particleVisualSources.TryGetValue(mesh, out var source)) return;
        if (!ComputeParticleVisualsEnabled || source.Materialized || count <= 0 || count > source.Count)
        { MaterializeParticleVisuals(mesh, source); return; }
        var key = new ParticleVisualKey(mesh, mesh.GeometryRevision, source.Version, source.HistoryVersion, count);
        if (particleVisualResults.TryGetValue(key, out var cached))
        { cached.Frame = _frameCounter; SelectParticleVisuals(meshId, count, cached.Buffer); return; }
        if (!TryParticleInput(mesh, 4, Format.R32G32B32Sfloat, true, out var positions) ||
            !TryParticleInput(mesh, 6, Format.R8G8B8A8Unorm, true, out var colors))
        { MaterializeParticleVisuals(mesh, source); return; }
        bool withHistory = source.PreviousSizeCount == count && TryParticleInput(mesh, 9, Format.R32G32B32Sfloat, true, out _);
        ParticleInput history = positions;
        if (withHistory) TryParticleInput(mesh, 9, Format.R32G32B32Sfloat, true, out history);
        ulong bytes = (ulong)count * (withHistory ? 72UL : 28UL);
        int propertyBytes = count * 5 * 32, historyBytes = withHistory ? count * 32 : 32;
        var limits = _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        if (particleVisualBytes + bytes > 32UL << 20 || bytes > limits.MaxStorageBufferRange ||
            positions.Buffer.Size > limits.MaxStorageBufferRange || colors.Buffer.Size > limits.MaxStorageBufferRange ||
            history.Buffer.Size > limits.MaxStorageBufferRange || propertyBytes > limits.MaxStorageBufferRange ||
            !_frames.Current.TryAllocateUniforms(propertyBytes, out var properties) || !_frames.Current.TryAllocateUniforms(historyBytes, out var previous))
        { MaterializeParticleVisuals(mesh, source); return; }
        MemoryMarshal.AsBytes(source.Properties.AsSpan(0, count * 5)).CopyTo(new Span<byte>((void*)properties.Pointer, propertyBytes));
        var old = new Span<byte>((void*)previous.Pointer, historyBytes); old.Clear();
        if (withHistory) MemoryMarshal.AsBytes(source.PreviousSizes.AsSpan(0, count)).CopyTo(old);
        if (particleVisualProgram == 0) particleVisualProgram = CreateComputeProgram(ParticleEvolutionShader("particle-visuals.comp"), "particle-visuals",
            [new ComputeSlot(0, ComputeSlotKind.Buffer), new ComputeSlot(1, ComputeSlotKind.Buffer), new ComputeSlot(2, ComputeSlotKind.Buffer),
                new ComputeSlot(3, ComputeSlotKind.Buffer), new ComputeSlot(4, ComputeSlotKind.Buffer), new ComputeSlot(5, ComputeSlotKind.Buffer),
                new ComputeSlot(6, ComputeSlotKind.Buffer)], 8, 64, 1);
        var program = _compute.Get(particleVisualProgram);
        if (program == null) { MaterializeParticleVisuals(mesh, source); return; }
        if (particleTrigonometryBuffer == null)
        {
            particleTrigonometryBuffer = new VulkanBuffer(_context, (ulong)particleTrigonometry.Length * 4, BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.DeviceBuffers);
            particleTrigonometry.AsSpan().CopyTo(new Span<float>((void*)particleTrigonometryBuffer.Mapped, particleTrigonometry.Length));
        }
        var output = RentComputeOutput(bytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferSrcBit);
        try
        {
            var command = Commands; using GpuSection section = BeginGpuSection("particle-visuals"); _targets.EndRendering(command);
            _uploads.NoteUse(command, positions.Buffer); _uploads.NoteUse(command, colors.Buffer); if (withHistory) _uploads.NoteUse(command, history.Buffer);
            _graph.OpenComputePass(new PassSignature { NameId = _graph.NameId("compute:particle-visuals"), Width = count, Height = 1,
                FormatsId = -1, Reads = [], Attachments = [] });
            var set = _computeArenas[CurrentFrameSlot].GetBuffers(program.SetLayout,
                [new DescriptorBufferInfo(positions.Buffer.Handle, 0, positions.Buffer.Size), new DescriptorBufferInfo(colors.Buffer.Handle, 0, colors.Buffer.Size),
                    new DescriptorBufferInfo(_frames.UniformBuffer, properties.Offset, (ulong)propertyBytes), new DescriptorBufferInfo(history.Buffer.Handle, 0, history.Buffer.Size),
                    new DescriptorBufferInfo(_frames.UniformBuffer, previous.Offset, (ulong)historyBytes),
                    new DescriptorBufferInfo(particleTrigonometryBuffer.Handle, 0, particleTrigonometryBuffer.Size), new DescriptorBufferInfo(output.Handle, 0, bytes)]);
            _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, []));
            _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
            uint* push = stackalloc uint[2] { (uint)count, withHistory ? 1u : 0u };
            _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 8, push);
            _context.Api.CmdDispatch(command, ((uint)count + 63) / 64, 1, 1); _graph.NoteDispatch(); ParticleVisualDispatchesRecorded++;
            var barrier = new BufferMemoryBarrier2 { SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.VertexInputBit | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
                DstAccessMask = AccessFlags2.VertexAttributeReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = output.Handle, Size = bytes };
            var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
            _context.Api.CmdPipelineBarrier2(command, &dependency);
            CaptureParticleVisualCheck(command, output, mesh, source, count, withHistory);
            particleVisualResults.Add(key, new ParticleVisualResult(output, _frameCounter)); particleVisualBytes += bytes;
            SelectParticleVisuals(meshId, count, output);
        }
        catch { RetireComputeOutput(output); throw; }
    }

    private void SelectParticleVisuals(int meshId, int count, VulkanBuffer buffer)
    { drawParticleVisualMesh = meshId; drawParticleVisualCount = count; drawParticleVisuals = buffer; }
    private static string ParticleEvolutionShader(string file) => ReadWorldShader("geometry/" + file).Replace(
        "#include \"../include/evolution.glsl\"", ReadWorldShader("include/evolution.glsl"), StringComparison.Ordinal);
    private ParticleInput VisualParticleInput(int meshId, uint location, ParticleInput original) => drawParticleVisualMesh == meshId && drawParticleVisuals is { } buffer
        ? location == 4 ? new ParticleInput(buffer, 16, 0) : location == 9 ? new ParticleInput(buffer, 44, (uint)drawParticleVisualCount * 28) : original : original;
    private void BindParticleVisuals(CommandBuffer command, int meshId)
    {
        if (drawParticleVisualMesh != meshId || drawParticleVisuals is not { } output || _meshes.Get(meshId) is not { } mesh) return;
        foreach (var attribute in mesh.Layout.Attributes)
        {
            if (attribute.Location is not (4 or 6 or 9)) continue;
            ulong offset = attribute.Location == 6 ? (ulong)drawParticleVisualCount * 16 : attribute.Location == 9 ? (ulong)drawParticleVisualCount * 28 : 0;
            if (offset >= output.Size) continue;
            var buffer = output.Handle;
            _context.Api.CmdBindVertexBuffers(command, attribute.Binding, 1, &buffer, &offset);
        }
    }
    private void RetireParticleVisuals(bool all = false)
    {
        oldParticleVisualResults.Clear();
        foreach (var pair in particleVisualResults)
            if (all || unchecked(_frameCounter - pair.Value.Frame) > 2)
            { _descriptors.Release(pair.Value.Buffer.Id); RetireComputeOutput(pair.Value.Buffer); particleVisualBytes -= pair.Value.Buffer.Size; oldParticleVisualResults.Add(pair.Key); }
        foreach (var key in oldParticleVisualResults) particleVisualResults.Remove(key);
        oldParticleVisualSources.Clear();
        foreach (var pair in particleVisualSources) if (all || !ReferenceEquals(_meshes.Get(pair.Value.MeshId), pair.Key)) oldParticleVisualSources.Add(pair.Key);
        foreach (var mesh in oldParticleVisualSources) particleVisualSources.Remove(mesh);
        drawParticleVisuals = null; drawParticleVisualMesh = 0;
    }
    private void DisposeParticleVisualsIdle()
    {
        foreach (var result in particleVisualResults.Values) result.Buffer.Dispose(); particleVisualResults.Clear(); particleVisualSources.Clear();
        particleTrigonometryBuffer?.Dispose(); particleTrigonometryBuffer = null; particleVisualBytes = 0;
        foreach (var readback in particleVisualReadbacks) readback?.Dispose();
        particleVisualReadbacks = []; pendingParticleVisualChecks = [];
    }

    private void CaptureParticleVisualCheck(CommandBuffer command, VulkanBuffer output, VulkanMesh mesh, ParticleVisualSource source, int count, bool withHistory)
    {
        if (!CheckParticleVisuals) return;
        if (particleVisualReadbacks.Length == 0)
        {
            particleVisualReadbacks = new VulkanBuffer?[_frames.FramesInFlight];
            pendingParticleVisualChecks = new ParticleVisualCheck?[_frames.FramesInFlight];
        }
        int slot = CurrentFrameSlot;
        if (pendingParticleVisualChecks[slot] != null) return;
        int sampleCount = Math.Min(count, 128), functions = 0;
        var current = source.Floats.AsSpan(0, sampleCount * 4).ToArray();
        var colors = new uint[sampleCount * 3]; MemoryMarshal.Cast<byte, uint>(source.Bytes.AsSpan(0, sampleCount * 12)).CopyTo(colors);
        var history = withHistory ? mesh.ParticleHistoryShadow.AsSpan(0, sampleCount * 11).ToArray() : [];
        for (int i = 0; i < sampleCount; i++)
        {
            int at = i * 5;
            current[i * 4 + 3] = source.Properties[at].Evaluate(particleTrigonometry);
            uint packed = colors[i * 3 + 2];
            byte blue = unchecked((byte)((packed & 255) + source.Properties[at + 4].Evaluate(particleTrigonometry)));
            byte green = unchecked((byte)(((packed >> 8) & 255) + source.Properties[at + 3].Evaluate(particleTrigonometry)));
            byte red = unchecked((byte)(((packed >> 16) & 255) + source.Properties[at + 2].Evaluate(particleTrigonometry)));
            byte alpha = (byte)Math.Clamp(source.Properties[at + 1].Evaluate(particleTrigonometry), 0, 255);
            colors[i * 3 + 2] = (uint)(blue | green << 8 | red << 16 | alpha << 24);
            if (withHistory) history[i * 11 + 3] = history[i * 11 + 4] = history[i * 11 + 5] = source.PreviousSizes[i].Evaluate(particleTrigonometry);
            for (int j = 0; j < 5; j++) if (source.Properties[at + j].Function is >= 0 and < 12) functions |= 1 << source.Properties[at + j].Function;
        }
        ulong bytes = (ulong)sampleCount * (withHistory ? 72UL : 28UL);
        if (particleVisualReadbacks[slot] is not { } readback || readback.Size < bytes)
        {
            if (particleVisualReadbacks[slot] is { } old) _frames.DeferDeletion(old);
            readback = particleVisualReadbacks[slot] = CreateReadbackBuffer(bytes);
        }
        BufferCopy* regions = stackalloc BufferCopy[3];
        regions[0] = new BufferCopy { Size = (ulong)sampleCount * 16 };
        regions[1] = new BufferCopy { SrcOffset = (ulong)count * 16, DstOffset = (ulong)sampleCount * 16, Size = (ulong)sampleCount * 12 };
        regions[2] = new BufferCopy { SrcOffset = (ulong)count * 28, DstOffset = (ulong)sampleCount * 28, Size = (ulong)sampleCount * 44 };
        _context.Api.CmdCopyBuffer(command, output.Handle, readback.Handle, withHistory ? 3u : 2u, regions);
        ReadbackManager.RecordHostReadBarrier(_context, command, readback, 0, bytes);
        pendingParticleVisualChecks[slot] = new ParticleVisualCheck(current, colors, history, functions);
    }

    private void CollectParticleVisualCheck(int slot)
    {
        if (slot >= pendingParticleVisualChecks.Length || pendingParticleVisualChecks[slot] is not { } check || particleVisualReadbacks[slot] is not { } readback) return;
        pendingParticleVisualChecks[slot] = null;
        var actual = new ReadOnlySpan<float>((void*)readback.Mapped, check.Current.Length);
        void Compare(ReadOnlySpan<float> expected, ReadOnlySpan<float> observed)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                double error = Math.Abs((double)expected[i] - observed[i]) / Math.Max(1, Math.Abs((double)expected[i]));
                ParticleVisualMaximumError = Math.Max(ParticleVisualMaximumError, double.IsFinite(error) ? error : double.MaxValue);
            }
        }
        Compare(check.Current, actual);
        var colors = new ReadOnlySpan<uint>((byte*)readback.Mapped + check.Current.Length * 4, check.Colors.Length);
        for (int i = 0; i < colors.Length; i++)
            for (int shift = 0; shift < 32; shift += 8)
                ParticleVisualMaximumColorError = Math.Max(ParticleVisualMaximumColorError, Math.Abs((int)((check.Colors[i] >> shift) & 255) - (int)((colors[i] >> shift) & 255)));
        Compare(check.Previous, new ReadOnlySpan<float>((byte*)readback.Mapped + (check.Current.Length + check.Colors.Length) * 4, check.Previous.Length));
        ParticleVisualFunctionCoverage |= check.Functions; ParticleVisualChecks++;
    }
}
