using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game.Performance;

internal static partial class GpuParticleSimulation
{
    private const long InstanceCpuBudget = 128L << 20;
    private static long instanceCpuBytes;
    private static readonly object instanceBudgetGate = new();
    // Every stock MeshData (including all five worker buffers) owns a separate
    // immutable producer generation. Only the original read/write handoff publishes it.
    private sealed class InstanceHandoff
    {
        internal ParticleSimulationBuffer?[] Buffers = [];
        internal ParticleInstanceInput[] Inputs = [];
        internal ParticleSimulationOutput[] Results = [];
        internal ParticleEvolution[] Properties = [];
        internal object[] Tokens = [];
        internal readonly List<Vector4> Steps = new();
        internal int Count;
        internal long Generation;
        internal bool Deferred;
        private long charge;
        internal void Release()
        {
            for (int i = 0; i < Count; i++) { Buffers[i]?.Dispose(); Buffers[i] = null; }
        }
        internal void Begin()
        {
            Release(); Count = 0; Steps.Clear(); Deferred = true; Generation++;
        }
        internal bool Grow(int index)
        {
            if (index < Inputs.Length) return true;
            int capacity = Math.Min(65536, Math.Max(index + 1, Math.Max(16, Inputs.Length * 2)));
            long nextCharge = capacity * (320L + ParticleSimulationOutput.ByteSize - 64) + 65536; // arrays plus at most 4096 ordered extrapolation commands
            lock (instanceBudgetGate)
            {
                if (instanceCpuBytes + nextCharge - charge > InstanceCpuBudget) return false;
                instanceCpuBytes += nextCharge - charge; charge = nextCharge;
            }
            Array.Resize(ref Inputs, capacity); Array.Resize(ref Buffers, capacity);
            Array.Resize(ref Results, capacity); Array.Resize(ref Properties, capacity * 5); Array.Resize(ref Tokens, capacity);
            return true;
        }
        internal void Clear()
        {
            Release(); Count = 0; Deferred = false; Buffers = []; Inputs = []; Results = []; Properties = []; Tokens = []; Steps.Clear(); Steps.Capacity = 0;
            lock (instanceBudgetGate) { instanceCpuBytes -= charge; charge = 0; }
        }
        ~InstanceHandoff() => Clear();
    }
    private static readonly ConditionalWeakTable<MeshData, InstanceHandoff> instanceHandoffs = new();
    private static readonly ConditionalWeakTable<MeshRef, MeshData> instanceUploads = new();
    private static readonly System.Reflection.MethodInfo InstanceUpdate = AccessTools.Method(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.UpdateMesh), [typeof(MeshRef), typeof(MeshData)])!;
    internal static bool InstanceUploadBoundarySafe(ParticlePoolQuads pool) => Game(pool).Platform.GetType() == typeof(ClientPlatformWindows) && InstanceUpdate != null &&
        Harmony.GetPatchInfo(InstanceUpdate)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) != true;
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, MeshData[]> WorkerBuffers = AccessTools.FieldRefAccess<ParticlePoolQuads, MeshData[]>("updateBuffers");
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, Vec3d[]> WorkerCameras = AccessTools.FieldRefAccess<ParticlePoolQuads, Vec3d[]>("cameraPos");
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, float[]> WorkerTicks = AccessTools.FieldRefAccess<ParticlePoolQuads, float[]>("tickTimes");
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, float[][]> WorkerVelocities = AccessTools.FieldRefAccess<ParticlePoolQuads, float[][]>("velocities");
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, int> WorkerRead = AccessTools.FieldRefAccess<ParticlePoolQuads, int>("readPosition");
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, int> WorkerAdvance = AccessTools.FieldRefAccess<ParticlePoolQuads, int>("advanceCount");
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, object> WorkerLock = AccessTools.FieldRefAccess<ParticlePoolQuads, object>("advanceCountLock");
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, float> WorkerAccum = AccessTools.FieldRefAccess<ParticlePoolQuads, float>("accumPhysics");

    internal static bool InstanceDeferred(MeshData data, int index) => instanceHandoffs.TryGetValue(data, out var handoff) && handoff.Deferred && index >= 0 && index < handoff.Count;

    internal static void BeginInstanceProduction(ParticleBase particle, MeshData data, int position)
    {
        if (!Enabled || current is not { InstanceCallbacksSafe: true } scope) { MaterializeInstances(data); return; }
        if (!scope.InstanceProductionStarted)
        {
            scope.InstanceProductionStarted = true;
            var first = instanceHandoffs.GetValue(data, _ => new InstanceHandoff()); first.Begin(); GpuParticleVisuals.Forget(data);
            if (position != 0) first.Deferred = false;
        }
        // The stock call is virtual on ParticleBase. A custom producer must
        // see the captured prefix materialized before it observes or edits it.
        if (particle is not ParticleGeneric) MaterializeInstances(data);
    }

    internal static bool TryProduceInstance(ParticleGeneric particle, MeshData data, Vec3d camera, ref int position, ref int rgba, ref int flags)
    {
        if (!Enabled || current is not { InstanceCallbacksSafe: true }) { MaterializeInstances(data); return false; }
        int index = position / 4;
        if (position < 0 || position % 4 != 0 || data.CustomFloats?.Values is not { } floats || data.CustomBytes?.Values is not { } bytes ||
            data.Flags is not { } flagValues || index >= Math.Min(65536, Math.Min(floats.Length / 4, Math.Min(bytes.Length / 12, flagValues.Length))))
        { MaterializeInstances(data); return false; }
        if (!instanceHandoffs.TryGetValue(data, out var handoff)) return false;
        if (!handoff.Deferred) return false;
        if (index != handoff.Count || !handoff.Grow(index) || current is not { } scope || rgba != index * 12 || flags != index ||
            !scope.State.Rendered.TryGetValue(particle, out var state) || !ReferenceEquals(state.Spawn, ParticleMotionHistory.SpawnToken(particle)) ||
            particle.Position.X != state.Result.X || particle.Position.Y != state.Result.Y || particle.Position.Z != state.Result.Z ||
            !GpuParticleVisuals.CaptureInstance(particle, handoff.Properties.AsSpan(index * 5, 5)))
        { MaterializeInstances(data); handoff.Deferred = false; return false; }
        handoff.Buffers[index] = state.Buffer.Retain(); handoff.Results[index] = state.Result; handoff.Tokens[index] = state.Spawn;
        handoff.Inputs[index] = new ParticleInstanceInput
        {
            CameraX = camera.X, CameraY = camera.Y, CameraZ = camera.Z, Source = (uint)state.Index, Target = (uint)index,
            Interpolation = new Vector4(particle.prevPosDeltaX, particle.prevPosDeltaY, particle.prevPosDeltaZ, particle.prevPosAdvance),
            VelocityPhase = new Vector4(particle.prevPosDeltaX, particle.prevPosDeltaY, particle.prevPosDeltaZ, particle.SecondsAlive / particle.LifeLength),
            Direction = (Direction(particle) & 0x00ffffffu) | (uint)bytes[index * 12 + 3] << 24,
            Light = unchecked((uint)particle.lightrgbs),
            Color = (uint)(particle.ColorBlue | particle.ColorGreen << 8 | particle.ColorRed << 16 | particle.ColorAlpha << 24),
            Flags = unchecked((uint)particle.VertexFlags),
        };
        handoff.Count = index + 1; position += 4; rgba += 12; flags++;
        return true;
    }

    internal static void MaterializeInstances(MeshData data)
    {
        if (!instanceHandoffs.TryGetValue(data, out var handoff)) return;
        lock (handoff)
        {
            if (!handoff.Deferred) return;
            var floats = data.CustomFloats.Values; var bytes = data.CustomBytes.Values;
            for (int i = 0; i < handoff.Count; i++)
            {
                ref readonly var input = ref handoff.Inputs[i]; ref readonly var result = ref handoff.Results[i];
                // Reproduce each float store, including every accumulated worker
                // extrapolation. This runs only at a real CPU fallback boundary.
                float remainder = 1 - input.Interpolation.W;
                float x = (float)(result.X - (double)(input.Interpolation.X * remainder) - input.CameraX);
                float y = (float)(result.Y - (double)(input.Interpolation.Y * remainder) - input.CameraY);
                float z = (float)(result.Z - (double)(input.Interpolation.Z * remainder) - input.CameraZ);
                foreach (var step in handoff.Steps)
                { x += step.X + input.VelocityPhase.X * step.W; y += step.Y + input.VelocityPhase.Y * step.W; z += step.Z + input.VelocityPhase.Z * step.W; }
                floats[i * 4] = x; floats[i * 4 + 1] = y; floats[i * 4 + 2] = z;
                floats[i * 4 + 3] = GpuParticleVisuals.Evaluate(handoff.Properties[i * 5]);
                uint color = input.Color;
                byte b = unchecked((byte)((color & 255) + GpuParticleVisuals.Evaluate(handoff.Properties[i * 5 + 4])));
                byte g = unchecked((byte)(((color >> 8) & 255) + GpuParticleVisuals.Evaluate(handoff.Properties[i * 5 + 3])));
                byte r = unchecked((byte)(((color >> 16) & 255) + GpuParticleVisuals.Evaluate(handoff.Properties[i * 5 + 2])));
                byte a = (byte)Math.Clamp(GpuParticleVisuals.Evaluate(handoff.Properties[i * 5 + 1]), 0, 255);
                var packed = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(i * 12, 12));
                packed[0] = input.Direction; packed[1] = input.Light; packed[2] = (uint)(b | g << 8 | r << 16 | a << 24);
                data.Flags[i] = unchecked((int)input.Flags);
                ParticleMotionHistory.ProducedToken(data, i, handoff.Tokens[i]);
            }
            handoff.Deferred = false;
            handoff.Release();
        }
    }

    internal static bool TryUploadInstances(VulkanDevice renderer, MeshRef mesh, int handle, MeshData data, TemporalFrameState frame)
    {
        if (!Enabled || !instanceHandoffs.TryGetValue(data, out var handoff) || !handoff.Deferred ||
            handoff.Count == 0 || handoff.Count != data.VerticesCount || handoff.Count != data.CustomFloats?.Count / 4 || data.CustomBytes?.Count != handoff.Count * 12 ||
            data.CustomFloats.BaseOffset != 0 || data.CustomBytes.BaseOffset != 0 || data.FlagsOffset != 0 || !data.FlagsInstanced ||
            data.xyz != null || data.Normals != null || data.Uv != null || data.Rgba != null || data.Indices != null ||
            data.CustomInts != null || data.CustomShorts != null) { MaterializeInstances(data); return false; }
        bool accepted = renderer.UpdateParticleInstances(handle, handoff, handoff.Generation,
            handoff.Buffers.AsSpan(0, handoff.Count), handoff.Inputs.AsSpan(0, handoff.Count), handoff.Properties.AsSpan(0, handoff.Count * 5),
            handoff.Tokens.AsSpan(0, handoff.Count), CollectionsMarshal.AsSpan(handoff.Steps), frame.FrameIndex, frame.Reset, GpuParticleVisuals.Trigonometry);
        if (!accepted) MaterializeInstances(data);
        else { instanceUploads.Remove(mesh); instanceUploads.Add(mesh, data); }
        return accepted;
    }

    internal static void CpuInstanceUpload(MeshRef mesh) => instanceUploads.Remove(mesh);
    internal static bool TryMaterializeUploadedInstances(MeshRef mesh, out MeshData data)
    {
        data = null!;
        if (Enabled && GpuParticleVisuals.InstanceRenderingEnabled || !instanceUploads.TryGetValue(mesh, out data!)) return false;
        instanceUploads.Remove(mesh); MaterializeInstances(data); return true;
    }

    private static void ClearInstanceHandoffs()
    {
        foreach (var pair in instanceHandoffs) lock (pair.Value) { MaterializeInstances(pair.Key); pair.Value.Clear(); }
        instanceHandoffs.Clear();
    }

    // The concrete stock method is final on IParticlePool. Patch the method;
    // never subclass it or alter the official game assembly's type contract.
    private static bool BeforeWorkerRender(ParticlePoolQuads __instance, float __0, Vec3d __1)
    {
        var pool = __instance;
        if (pool.GetType() != typeof(ParticlePoolQuads) && pool.GetType() != typeof(ParticlePoolCubes)) return true;
        var buffers = WorkerBuffers(pool);
        if (buffers == null || !buffers.Any(data => instanceHandoffs.TryGetValue(data, out var value) && value.Deferred)) return true;
        bool safeBoundary = InstanceUploadBoundarySafe(pool);
        WorkerAccum(pool) += __0;
        int read = WorkerRead(pool); var ticks = WorkerTicks(pool); float tick = ticks[read];
        if (WorkerAccum(pool) >= tick)
        {
            lock (WorkerLock(pool))
                if (WorkerAdvance(pool) > 0)
                { read = WorkerRead(pool) = (read + 1) % buffers.Length; WorkerAdvance(pool)--; WorkerAccum(pool) -= tick; tick = ticks[read]; }
            if (WorkerAccum(pool) > 1) WorkerAccum(pool) = 0;
        }
        float ratio = __0 / tick;
        var data = buffers[read]; var velocities = WorkerVelocities(pool)[read]; var camera = WorkerCameras(pool)[read];
        int count = pool.QuantityAlive = data.VerticesCount;
        float dx = (float)(camera.X - __1.X), dy = (float)(camera.Y - __1.Y), dz = (float)(camera.Z - __1.Z);
        camera.X -= dx; camera.Y -= dy; camera.Z -= dz;
        if (Enabled && safeBoundary && instanceHandoffs.TryGetValue(data, out var handoff) && handoff.Deferred && handoff.Count == count && handoff.Steps.Count < 4096)
            handoff.Steps.Add(new Vector4(dx, dy, dz, ratio));
        else
        {
            MaterializeInstances(data); var values = data.CustomFloats.Values;
            for (int i = 0; i < count; i++)
            { values[i * 4] += dx + velocities[i * 3] * ratio; values[i * 4 + 1] += dy + velocities[i * 3 + 1] * ratio; values[i * 4 + 2] += dz + velocities[i * 3 + 2] * ratio; }
        }
        var game = Game(pool); game.Platform.UpdateMesh(pool.Model, data);
        bool quad = pool.GetType() == typeof(ParticlePoolQuads);
        string key = quad ? "asyncquadparticlepool" : "asynccubeparticlepool";
        game.DebugScreenInfo[key] = game.extendedDebugInfo
            ? (quad ? "Async Quad Particle pool: " : "Async Cube Particle pool: ") + pool.ParticlesPool.AliveCount + " / " + (int)(PoolCapacity(pool) * (float)game.particleLevel / 100f) : "";
        ((IWorldAccessor)game).FrameProfiler.Mark("otparticles-tick");
        return false;
    }
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, int> PoolCapacity = AccessTools.FieldRefAccess<ParticlePoolQuads, int>("poolSize");
}
