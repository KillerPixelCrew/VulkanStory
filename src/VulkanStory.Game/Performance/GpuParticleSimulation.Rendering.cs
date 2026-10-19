using System.Numerics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game.Performance;

internal static partial class GpuParticleSimulation
{
    private readonly struct RenderState : IDisposable
    {
        internal readonly ParticleSimulationBuffer Buffer;
        internal readonly int Index;
        internal readonly ParticleSimulationOutput Result;
        internal readonly object Spawn;
        internal RenderState(ParticleGeneric particle, ParticleSimulationBuffer buffer, int index, in ParticleSimulationOutput result)
        { Buffer = buffer.Retain(); Index = index; Result = result; Spawn = ParticleMotionHistory.SpawnToken(particle); }
        public void Dispose() => Buffer.Dispose();
    }

    // These sidecars follow the original five-buffer worker handoff. Each owner
    // retains immutable trajectories before the preceding owner can release them.
    private sealed class RenderRecords
    {
        internal ParticleSimulationBuffer?[] Buffers = [];
        internal ParticleSimulationRenderInput[] Inputs = [];
        internal uint[] Produced = [];
        internal int Count;
        internal void Reset(int count)
        {
            Clear();
            // Particle counts vary every frame; keep the worker handoff storage
            // instead of allocating three exact-sized arrays after every change.
            if (Buffers.Length < count)
            {
                int capacity = Math.Max(count, Math.Max(16, Buffers.Length * 2));
                Array.Resize(ref Buffers, capacity); Array.Resize(ref Inputs, capacity);
                Array.Resize(ref Produced, capacity * 3);
            }
            Count = count;
        }
        internal void Clear()
        {
            for (int i = 0; i < Count; i++) { Buffers[i]?.Dispose(); Buffers[i] = null; }
        }
        ~RenderRecords() => Clear(); // Release references only; Vulkan destruction stays on the owner thread.
    }
    private static readonly ConditionalWeakTable<float[], RenderRecords> producedCenters = new();
    private static readonly ConditionalWeakTable<MeshRef, RenderRecords> uploadedCenters = new();

    private static void ClearRenderRecords()
    {
        ClearInstanceHandoffs();
        foreach (var value in producedCenters) value.Value.Clear();
        foreach (var value in uploadedCenters) value.Value.Clear();
        producedCenters.Clear(); uploadedCenters.Clear();
    }

    internal static void Produced(ParticleGeneric particle, MeshData data, Vec3d camera, int position)
    {
        if (!Enabled) return;
        if (position < 4 || position % 4 != 0 || data.CustomFloats?.Values is not { } floats) return;
        int index = position / 4 - 1;
        if ((uint)index >= floats.Length / 4) return;
        if (!producedCenters.TryGetValue(floats, out var records))
        {
            if (current is not { } firstScope || !firstScope.State.Rendered.ContainsKey(particle)) return;
            records = producedCenters.GetValue(floats, _ => new RenderRecords());
            records.Reset(floats.Length / 4);
        }
        if (index == 0) records.Clear();
        records.Buffers[index]?.Dispose(); records.Buffers[index] = null;
        if (current is not { } scope || !scope.State.Rendered.TryGetValue(particle, out var state)) return;
        if (!ReferenceEquals(state.Spawn, ParticleMotionHistory.SpawnToken(particle))) return;
        // An external producer edit after simulation belongs to the original
        // instance upload. Never reconstruct a different GPU trajectory for it.
        if (particle.Position.X != state.Result.X || particle.Position.Y != state.Result.Y || particle.Position.Z != state.Result.Z) return;
        records.Buffers[index] = state.Buffer.Retain();
        float remainder = 1 - particle.prevPosAdvance;
        records.Inputs[index] = new ParticleSimulationRenderInput
        {
            Source = (uint)state.Index, Target = (uint)index,
            CameraX = camera.X, CameraY = camera.Y, CameraZ = camera.Z,
            Interpolation = new Vector4(particle.prevPosDeltaX * remainder, particle.prevPosDeltaY * remainder, particle.prevPosDeltaZ * remainder, 0),
        };
        for (int axis = 0; axis < 3; axis++) records.Produced[index * 3 + axis] = BitConverter.SingleToUInt32Bits(floats[index * 4 + axis]);
    }

    internal static void Uploaded(MeshRef mesh, MeshData data)
    {
        if (data.CustomFloats?.Values is not { } floats || data.CustomFloats.BaseOffset != 0 ||
            !producedCenters.TryGetValue(floats, out var produced))
        {
            if (uploadedCenters.TryGetValue(mesh, out var old)) old.Clear();
            uploadedCenters.Remove(mesh); return;
        }
        int count = Math.Min(data.CustomFloats.Count / 4, produced.Count);
        var uploaded = uploadedCenters.GetValue(mesh, _ => new RenderRecords()); uploaded.Reset(count);
        for (int i = 0; i < count; i++)
        {
            if (produced.Buffers[i] is not { } buffer) continue;
            uploaded.Buffers[i] = buffer.Retain();
            var input = produced.Inputs[i];
            // Stock worker interpolation mutates the CPU instance array in
            // place, with float rounding at every frame. Replay its final bit
            // delta instead of replacing that ordered handoff with new maths.
            input.UploadDeltaX = unchecked(BitConverter.SingleToUInt32Bits(floats[i * 4]) - produced.Produced[i * 3]);
            input.UploadDeltaY = unchecked(BitConverter.SingleToUInt32Bits(floats[i * 4 + 1]) - produced.Produced[i * 3 + 1]);
            input.UploadDeltaZ = unchecked(BitConverter.SingleToUInt32Bits(floats[i * 4 + 2]) - produced.Produced[i * 3 + 2]);
            uploaded.Inputs[i] = input;
        }
    }

    internal static void PrepareRender(VulkanDevice renderer, MeshRef mesh, int handle)
    {
        if (!Enabled || !uploadedCenters.TryGetValue(mesh, out var records)) return;
        renderer.PrepareParticleSimulationCenters(handle, records.Buffers.AsSpan(0, records.Count), records.Inputs.AsSpan(0, records.Count));
    }
}
