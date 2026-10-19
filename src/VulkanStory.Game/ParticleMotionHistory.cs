using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VulkanStory.Contracts;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Tracks rendered particle transforms by spawn identity, independently of pool order and worker-buffer reuse.</summary>
/// <remarks>
/// Producer postfixes associate each pooled particle spawn with a fresh token and write that
/// token beside its original instance-buffer slot. Mesh upload copies tokens and current
/// position/scale/direction before the producer buffer is reused. Render preparation advances
/// history by temporal frame identity and returns a separate GPU instance stream. Weak tables
/// follow particle, producer-array and mesh lifetimes; per-mesh samples retain spawn tokens
/// only until they expire from rendered history. Mutable entries have no independent locking:
/// callers must preserve the original producer-to-upload handoff, and upload/render access to
/// one mesh must remain serialized by the session owner.
/// </remarks>
internal static class ParticleMotionHistory
{
    /// <summary>Identity of the current spawn occupying one pooled particle object; respawn replaces the token.</summary>
    private sealed class Spawn { internal object Token = new(); }
    /// <summary>Producer-slot identities stored beside one original four-float instance array.</summary>
    /// <param name="count">Number of complete four-float instance records in the original array.</param>
    private sealed class BufferTokens(int count) { internal readonly object?[] Tokens = new object?[count]; }
    /// <summary>Current and previous transforms plus captured size evolution for one spawn token.</summary>
    /// <remarks>Frame stamps rotate once per rendered frame, so repeated draws compare with the preceding frame rather than an earlier draw in the same frame.</remarks>
    private sealed class Sample
    {
        internal float[] State = new float[18], Previous = new float[18];
        internal long Frame = -1, PreviousFrame = -1;
        internal int Flags, PreviousFlags;
    }
    /// <summary>Copied upload data, token-keyed rendered samples and reusable GPU payload owned by one original mesh.</summary>
    /// <remarks>
    /// Current contains ten transform floats (position, scale and direction) followed by a
    /// captured size-evolution descriptor. Payload retains eleven vertex-input floats; the
    /// separate SizePayload supplies deferred previous-size evaluation to compute.
    /// These arrays are CPU storage; the renderer copies Payload into its separately owned
    /// previous-particle vertex buffer before the next preparation can overwrite it.
    /// </remarks>
    private sealed class MeshHistory
    {
        internal float[] Current = [];
        internal object?[] Tokens = [];
        internal int[] Flags = [];
        internal readonly Dictionary<object, Sample> Samples = new();
        internal readonly List<object> Expired = new();
        internal long Frame = -1;
        internal float[] Payload = [];
        internal ParticleEvolution[] SizePayload = [];
        internal int ValidCount;
    }
    private static readonly ConditionalWeakTable<ParticleGeneric, Spawn> Spawns = new();
    private static readonly ConditionalWeakTable<float[], BufferTokens> Buffers = new();
    private static readonly ConditionalWeakTable<MeshRef, MeshHistory> Meshes = new();

    /// <summary>Changes identity when a pooled particle is spawned again.</summary>
    /// <param name="particle">Original pooled particle whose new spawn must not inherit the previous occupant's motion.</param>
    /// <remarks>Called after the original spawn operation. The weak association retains no particle beyond its original lifetime.</remarks>
    internal static void Spawned(ParticleGeneric particle) => Spawns.GetValue(particle, _ => new Spawn()).Token = new object();

    internal static object SpawnToken(ParticleGeneric particle) => Spawns.GetValue(particle, _ => new Spawn()).Token;

    /// <summary>Records the identity paired with one original producer's four-float instance record.</summary>
    /// <param name="particle">Original particle contributing the just-written instance.</param>
    /// <param name="buffer">Original producer mesh buffer containing position xyz and scalar scale in CustomFloats.</param>
    /// <param name="position">Float cursor after the original producer advanced past its four-float record; the recorded slot is position divided by four minus one.</param>
    /// <remarks>Missing float storage, cursors before the first complete record and slots outside the array are ignored. The caller retains producer-buffer ownership.</remarks>
    internal static void Produced(ParticleGeneric particle, MeshData buffer, int position)
    {
        if (buffer.CustomFloats?.Values is not { } values || position < 4) return;
        var tokens = Buffers.GetValue(values, value => new BufferTokens(value.Length / 4));
        int index = position / 4 - 1;
        if ((uint)index < tokens.Tokens.Length) tokens.Tokens[index] = Spawns.GetValue(particle, _ => new Spawn()).Token;
    }

    /// <summary>Copies producer identities and actual uploaded position/scale/direction before a worker can reuse its buffer.</summary>
    /// <param name="mesh">Original mesh receiving the instance update; its weak association owns the copied history.</param>
    /// <param name="data">Producer output with four floats and twelve custom bytes per instance; the first four bytes encode normalized direction.</param>
    /// <remarks>
    /// Called at the mesh-update boundary before backend upload. Copies only records supported
    /// by the float count, token capacity and byte-array capacity. Scalar scale is repeated for
    /// all three GPU scale components. Missing/unrecognized producer storage discards existing
    /// mesh history. The copied arrays no longer depend on subsequent worker writes;
    /// the original handoff must prevent concurrent mutation while the copy is in progress.
    /// </remarks>
    internal static void Uploaded(MeshRef mesh, MeshData data)
    {
        Performance.GpuParticleSimulation.Uploaded(mesh, data);
        if (data.CustomFloats?.Values is not { } floats || !Buffers.TryGetValue(floats, out var identities) ||
            data.CustomBytes?.Values is not { } bytes)
        {
            Meshes.Remove(mesh);
            return;
        }
        int count = Math.Min(data.CustomFloats.Count / 4, Math.Min(identities.Tokens.Length, bytes.Length / 12));
        var history = Meshes.GetValue(mesh, _ => new MeshHistory());
        Array.Resize(ref history.Current, count * 18);
        Array.Resize(ref history.Tokens, count);
        Array.Resize(ref history.Flags, count);
        Array.Copy(identities.Tokens, history.Tokens, count);
        for (int i = 0; i < count; i++)
        {
            int target = i * 18;
            history.Flags[i] = data.Flags is { } flags && i < flags.Length ? flags[i] : 0;
            Array.Copy(floats, i * 4, history.Current, target, 3);
            history.Current[target + 3] = history.Current[target + 4] = history.Current[target + 5] = floats[i * 4 + 3];
            for (int j = 0; j < 4; j++) history.Current[target + 6 + j] = bytes[i * 12 + j] / 255f;
            var size = Performance.GpuParticleVisuals.SizeFor(data, i, floats[i * 4 + 3]);
            MemoryMarshal.Write(MemoryMarshal.AsBytes(history.Current.AsSpan(target + 10, 8)), in size);
        }
    }

    /// <summary>Returns previous rendered instance state, invalidating new/revived/missing/reset particles.</summary>
    /// <param name="mesh">Original uploaded mesh whose copied particle identities and state should be consumed.</param>
    /// <param name="quantity">Nonnegative instance count for this draw; missing uploaded records invalidate the entire payload.</param>
    /// <param name="frame">Session temporal state supplying the current rendered-frame index and history reset flag.</param>
    /// <returns>
    /// Borrowed array of eleven floats per instance, with all records invalid when mesh
    /// history is absent or the requested count exceeds it. Each record contains previous position xyz, previous
    /// scale xyz, previous direction xyzw and a final validity value of zero or one.
    /// </returns>
    /// <remarks>
    /// Render-thread use only. A valid token must have been sampled in the immediately preceding
    /// rendered frame and the current temporal frame must not reset history. Invalid records
    /// repeat current uploaded state with validity zero. Samples older than the preceding frame
    /// are removed once per new frame. Consume or copy the returned array before another Prepare
    /// call for this mesh; repeated draws reuse its storage while retaining the same previous frame.
    /// GPU allocation, upload and retirement belong to the renderer, not this CPU history owner.
    /// </remarks>
    internal static float[] Prepare(MeshRef mesh, int quantity, TemporalFrameState frame, bool deferSize = false)
    {
        var history = Meshes.GetValue(mesh, _ => new MeshHistory());
        Array.Resize(ref history.Payload, checked(quantity * 11));
        Array.Resize(ref history.SizePayload, quantity);
        history.ValidCount = 0;
        if (quantity > history.Tokens.Length)
        {
            Array.Clear(history.Payload);
            Array.Fill(history.SizePayload, ParticleEvolution.Constant(0));
            return history.Payload;
        }
        if (history.Frame != frame.FrameIndex)
        {
            history.Expired.Clear();
            foreach (var entry in history.Samples)
                if (entry.Value.Frame < frame.FrameIndex - 1) history.Expired.Add(entry.Key);
            foreach (var key in history.Expired) history.Samples.Remove(key);
            history.Frame = frame.FrameIndex;
        }
        for (int i = 0; i < quantity; i++)
        {
            object? token = history.Tokens[i];
            Sample? sample = null;
            if (token != null && !history.Samples.TryGetValue(token, out sample))
                history.Samples[token] = sample = new Sample();
            if (sample != null && sample.Frame != frame.FrameIndex)
            {
                (sample.State, sample.Previous) = (sample.Previous, sample.State);
                sample.PreviousFrame = sample.Frame;
                sample.PreviousFlags = sample.Flags;
                sample.Frame = frame.FrameIndex;
            }
            bool valid = !frame.Reset && sample is { PreviousFrame: >= 0 } && sample.PreviousFrame == frame.FrameIndex - 1 &&
                sample.PreviousFlags == history.Flags[i];
            if (sample != null) sample.Flags = history.Flags[i];
            if (valid) history.ValidCount++;
            if (sample != null) Array.Copy(history.Current, i * 18, sample.State, 0, 18);
            float[] selected = valid ? sample!.Previous : sample?.State ?? history.Current;
            int sourceOffset = sample == null ? i * 18 : 0;
            Array.Copy(selected, sourceOffset, history.Payload, i * 11, 10);
            history.Payload[i * 11 + 10] = valid ? 1f : 0f;
            var size = MemoryMarshal.Read<ParticleEvolution>(MemoryMarshal.AsBytes(selected.AsSpan(sourceOffset + 10, 8)));
            history.SizePayload[i] = size;
            if (!deferSize)
                history.Payload[i * 11 + 3] = history.Payload[i * 11 + 4] = history.Payload[i * 11 + 5] = Performance.GpuParticleVisuals.Evaluate(size);
        }
        return history.Payload;
    }
    internal static ReadOnlySpan<ParticleEvolution> PreviousVisualSizes(MeshRef mesh) => Meshes.TryGetValue(mesh, out var history) ? history.SizePayload : [];
    internal static int ValidInstances(MeshRef mesh) => Meshes.TryGetValue(mesh, out var history) ? history.ValidCount : 0;
}
