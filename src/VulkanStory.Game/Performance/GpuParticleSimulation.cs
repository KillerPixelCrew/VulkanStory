using System.Numerics;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game.Performance;

/// <summary>Executes due stock physics on resident GPU state, retaining immutable render results and ordered CPU events.</summary>
internal static partial class GpuParticleSimulation
{
    private const int MaximumBatch = 4096;
    internal const int BenchmarkMarker = 0x4000;
    private static readonly bool Benchmark = HeadlessHarnessOptions.Enabled && Environment.GetEnvironmentVariable("VULKANSTORY_PARTICLE_BENCHMARK") == "1";
    private readonly record struct TickMeasurement(long Start, bool Requested, bool Gpu);
    private readonly record struct PoolMeasurement(Scope? Previous, long Start, bool Requested);
    private sealed class Batch
    {
        internal readonly List<ParticleGeneric> Particles = new();
        internal ParticleSimulationInput[] Input = [];
        internal ParticleSimulationOutput[] Output = [];
        internal object[] Spawns = [];
        internal ParticleSimulationWorldInput[] World = [];
        internal readonly List<ParticleSimulationBox> Boxes = new();
        internal readonly List<Block> BoxBlocks = new();
        internal readonly Dictionary<WorldRegion, (uint Start, uint Count)> Regions = new();
        internal readonly List<uint> LiquidCells = new();
        internal readonly Dictionary<WorldRegion, uint> LiquidRegions = new();
        internal IParticleSimulationBackend? Backend;
        internal long Ticket;
        internal ParticleSimulationBuffer? Buffer;
        internal int Remaining;
        internal bool Ready;
        internal void Clear()
        {
            if (Ticket != 0) { Backend!.Release(Ticket); Ticket = 0; }
            Buffer?.Dispose(); Buffer = null;
            Array.Clear(Spawns, 0, Particles.Count);
            Particles.Clear(); Boxes.Clear(); BoxBlocks.Clear(); Regions.Clear(); LiquidCells.Clear(); LiquidRegions.Clear(); Backend = null; Remaining = 0; Ready = false;
        }
    }
    private sealed class PendingStep(Batch batch, int index, object spawn)
    {
        internal readonly Batch Batch = batch;
        internal readonly int Index = index;
        internal readonly object Spawn = spawn;
    }
    private sealed class PoolState
    {
        internal readonly object Gate = new();
        internal readonly List<Batch> Batches = new();
        internal readonly Dictionary<ParticleGeneric, PendingStep> Pending = new(ReferenceEqualityComparer.Instance);
        // A callback can kill and revive the same pooled object before its next
        // tick. Advice belongs to the observed spawn, never its next occupant.
        internal readonly HashSet<object> CpuOnly = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<ParticleGeneric, RenderState> Rendered = new(ReferenceEqualityComparer.Instance);
        internal readonly BlockPos Position = new(0);
        internal readonly BlockPos WorldMinimum = new(0), WorldMaximum = new(0);
        internal readonly Cuboidf CollisionBox = new();
        internal long Epoch = -1;
        ~PoolState() => Clear();
        internal void InvalidatePending()
        {
            // A run of CPU-only actors has no speculation left after its first
            // boundary. Do not repeatedly clear every retained batch capacity.
            if (Pending.Count == 0) return;
            foreach (var batch in Batches)
                if (batch.Remaining != 0) batch.Clear();
            Pending.Clear();
        }
        internal void DiscardPending() { InvalidatePending(); CpuOnly.Clear(); }
        internal void Clear()
        {
            DiscardPending();
            foreach (var value in Rendered.Values) value.Dispose();
            Rendered.Clear();
        }
        internal void Remove(ParticleGeneric particle)
        {
            if (Pending.Remove(particle, out var pending) && --pending.Batch.Remaining == 0)
                pending.Batch.Clear();
        }
    }
    private sealed record Scope(ParticlePoolQuads Pool, PoolState State, float Delta, float GameSpeed, bool Worker, long Epoch, bool InstanceCallbacksSafe)
    { internal bool InstanceProductionStarted; }
    private delegate void CollisionStep(ParticleBase particle, float dt, ICoreClientAPI api, ParticlePhysics physics, float height);
    private static readonly ConditionalWeakTable<ParticlePoolQuads, PoolState> pools = new();
    private static readonly object poolRegistryGate = new();
    private static readonly List<WeakReference<PoolState>> poolRegistry = new();
    private static readonly MethodInfo Tick = AccessTools.Method(typeof(ParticleGeneric), nameof(ParticleGeneric.TickNow))!;
    private static readonly MethodInfo Main = AccessTools.Method(typeof(ParticlePoolQuads), nameof(ParticlePoolQuads.OnNewFrame))!;
    private static readonly MethodInfo Worker = AccessTools.Method(typeof(ParticlePoolQuads), nameof(ParticlePoolQuads.OnNewFrameOffThread))!;
    private static readonly MethodInfo Collision = AccessTools.Method(typeof(ParticleBase), "updatePositionWithCollision")!;
    private static readonly CollisionStep Collide = Collision.CreateDelegate<CollisionStep>();
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, bool> OffThread = AccessTools.FieldRefAccess<ParticlePoolQuads, bool>("offthread");
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, ClientMain> Game = AccessTools.FieldRefAccess<ParticlePoolQuads, ClientMain>("game");
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, ParticlePhysics> Physics = AccessTools.FieldRefAccess<ParticlePoolQuads, ParticlePhysics>("partPhysics");
    private static readonly AccessTools.FieldRef<ParticleBase, Vec3f> Motion = AccessTools.FieldRefAccess<ParticleBase, Vec3f>("motion");
    private static readonly AccessTools.FieldRef<ParticleBase, float> Drag = AccessTools.FieldRefAccess<ParticleBase, float>("tdragnow");
    private static readonly AccessTools.FieldRef<ParticleBase, byte> TickCount = AccessTools.FieldRefAccess<ParticleBase, byte>("tickCount");
    private static readonly AccessTools.FieldRef<ParticleBase, float> RandomDrag = AccessTools.FieldRefAccess<ParticleBase, float>("rnddrag");
    private static readonly AccessTools.FieldRef<ParticleBase, EnumCollideFlags> CollisionFlags = AccessTools.FieldRefAccess<ParticleBase, EnumCollideFlags>("flags");
    private static readonly AccessTools.FieldRef<ParticleGeneric, uint> Direction = AccessTools.FieldRefAccess<ParticleGeneric, uint>("dirNormalized");
    [ThreadStatic] private static Scope? current;
    private static bool armed, ready;
    private static IParticleSimulationBackend? backend;
    private static VulkanDevice? backendDevice;
    private static bool backendEnabled;
    private static long epoch;
    private static long stepsApplied, checks;
    private static long liquidPolicyChecks, airPolicyChecks, liquidDeathPolicyChecks, wetPolicyChecks;
    private static long liquidPolicyApplied, airPolicyApplied, liquidDeathPolicyApplied, wetPolicyApplied;
    internal static long LiquidPolicyChecks => Interlocked.Read(ref liquidPolicyChecks);
    internal static long AirPolicyChecks => Interlocked.Read(ref airPolicyChecks);
    internal static long LiquidDeathPolicyChecks => Interlocked.Read(ref liquidDeathPolicyChecks);
    internal static long WetPolicyChecks => Interlocked.Read(ref wetPolicyChecks);
    internal static long LiquidPolicyApplied => Interlocked.Read(ref liquidPolicyApplied);
    internal static long AirPolicyApplied => Interlocked.Read(ref airPolicyApplied);
    internal static long LiquidDeathPolicyApplied => Interlocked.Read(ref liquidDeathPolicyApplied);
    internal static long WetPolicyApplied => Interlocked.Read(ref wetPolicyApplied);
    private static long cpuBenchmarkSteps, cpuBenchmarkTicks, gpuBenchmarkSteps, gpuBenchmarkTicks, fallbackBenchmarkSteps, fallbackBenchmarkTicks;
    private static long cpuBenchmarkPools, cpuBenchmarkPoolTicks, gpuBenchmarkPools, gpuBenchmarkPoolTicks;
    internal static long CpuBenchmarkPools => Interlocked.Read(ref cpuBenchmarkPools);
    internal static long GpuBenchmarkPools => Interlocked.Read(ref gpuBenchmarkPools);
    internal static double CpuBenchmarkPoolMilliseconds => Interlocked.Read(ref cpuBenchmarkPoolTicks) * 1000.0 / Stopwatch.Frequency;
    internal static double GpuBenchmarkPoolMilliseconds => Interlocked.Read(ref gpuBenchmarkPoolTicks) * 1000.0 / Stopwatch.Frequency;
    internal static long CpuBenchmarkSteps => Interlocked.Read(ref cpuBenchmarkSteps);
    internal static long GpuBenchmarkSteps => Interlocked.Read(ref gpuBenchmarkSteps);
    internal static long FallbackBenchmarkSteps => Interlocked.Read(ref fallbackBenchmarkSteps);
    internal static double CpuBenchmarkMilliseconds => Interlocked.Read(ref cpuBenchmarkTicks) * 1000.0 / Stopwatch.Frequency;
    internal static double GpuBenchmarkMilliseconds => Interlocked.Read(ref gpuBenchmarkTicks) * 1000.0 / Stopwatch.Frequency;
    internal static double FallbackBenchmarkMilliseconds => Interlocked.Read(ref fallbackBenchmarkTicks) * 1000.0 / Stopwatch.Frequency;
    private static double maximumFloatError, maximumPositionError;
    private static readonly object checkGate = new();
    internal static long StepsApplied => Interlocked.Read(ref stepsApplied);
    internal static long Checks => Interlocked.Read(ref checks);
    internal static double MaximumFloatError => Volatile.Read(ref maximumFloatError);
    internal static double MaximumPositionError => Volatile.Read(ref maximumPositionError);
    internal static bool ChecksPassed => Checks > 0 && MaximumFloatError <= 0.00001 && MaximumPositionError <= 0.0001;
    private static bool Enabled => armed && ready && PerformancePatchSet.Current?.GpuParticleSimulation == true && Volatile.Read(ref backend) != null;

    internal static void Configure(VulkanDevice device, bool enabled)
    {
        enabled &= device.ParticleSimulationAvailable;
        if (ReferenceEquals(backendDevice, device) && backendEnabled == enabled) return;
        ResetPools(detach: true);
        backendDevice = device; backendEnabled = enabled;
        Volatile.Write(ref backend, enabled ? device.ParticleSimulationBackend : null);
    }
    internal static PerformanceFeature Create() => new("gpu-particle-simulation", settings => settings.GpuParticleSimulation,
        () => { _ = Tick; _ = Main; _ = Worker; _ = Collide; }, harmony =>
        {
            foreach (var root in new[] { Main, Worker }) harmony.Patch(root,
                prefix: new HarmonyMethod(typeof(GpuParticleSimulation), nameof(BeforePool)),
                finalizer: new HarmonyMethod(typeof(GpuParticleSimulation), nameof(AfterPool)));
            harmony.Patch(Tick, prefix: new HarmonyMethod(typeof(GpuParticleSimulation), nameof(BeforeTick)),
                postfix: new HarmonyMethod(typeof(GpuParticleSimulation), nameof(AfterTick)));
            harmony.Patch(AccessTools.Method(typeof(ParticlePoolQuads), "ProcessParticlesFromOffThread"),
                prefix: new HarmonyMethod(typeof(GpuParticleSimulation), nameof(BeforeWorkerRender)));
            armed = true;
        }, Uninstall: () => { armed = ready = false; ResetPools(detach: true); backendDevice = null; }, WorldReady: _ =>
        {
            ready = false;
            lock (stockBoxTypes) stockBoxTypes.Clear();
            lock (stockFlowTypes) stockFlowTypes.Clear();
            lock (stockLiquidTypes) stockLiquidTypes.Clear();
            foreach (var method in new[] { Main, Worker, Tick, Collision,
                AccessTools.Method(typeof(ParticlePoolQuads), "ProcessParticlesFromOffThread")!,
                AccessTools.Method(typeof(ParticleBase), nameof(ParticleBase.TickFixedStep))!,
                AccessTools.Method(typeof(ParticleGeneric), nameof(ParticleGeneric.UpdateBuffers))!,
                AccessTools.Method(typeof(Cuboidd), nameof(Cuboidd.pushOutX))!,
                AccessTools.Method(typeof(Cuboidd), nameof(Cuboidd.pushOutY))!,
                AccessTools.Method(typeof(Cuboidd), nameof(Cuboidd.pushOutZ))!,
                AccessTools.Method(typeof(Vintagestory.Common.BlockAccessorReadLockfree), nameof(IBlockAccessor.WalkBlocks))!,
                AccessTools.Method(typeof(Vintagestory.API.Datastructures.CachedCuboidList), "Add",
                    [typeof(Cuboidf[]), typeof(int), typeof(int), typeof(int), typeof(Block)])!,
                AccessTools.Method(typeof(Vintagestory.API.Datastructures.CachedCuboidList), "Add",
                    [typeof(Cuboidf), typeof(int), typeof(int), typeof(int), typeof(Block)])!,
                StockBoxes, AccessTools.Method(typeof(ParticlePhysics), nameof(ParticlePhysics.UpdateMotion))!,
                AccessTools.Method(typeof(Block), nameof(Block.IsLiquid))!,
                AccessTools.Method(typeof(Vintagestory.Common.BlockAccessorReadLockfree), nameof(IBlockAccessor.GetBlockRaw))!,
                AccessTools.Method(typeof(ParticlePhysics), nameof(ParticlePhysics.HandleBoyancy))!,
                AccessTools.Method(typeof(EvolvingNatFloat), nameof(EvolvingNatFloat.nextFloat))!,
                AccessTools.Method(typeof(GameMath), nameof(GameMath.FastSin))!, AccessTools.Method(typeof(GameMath), nameof(GameMath.FastCos))! })
                if (Harmony.GetPatchInfo(method)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) == true) return;
            ready = true;
        }, WorldLeft: () => { ready = false; ResetPools(); });

    private static PoolState NewPool(ParticlePoolQuads _)
    {
        var state = new PoolState();
        lock (poolRegistryGate) poolRegistry.Add(new WeakReference<PoolState>(state));
        return state;
    }
    private static void ResetPools(bool detach = false)
    {
        var previous = Interlocked.Exchange(ref backend, null);
        Interlocked.Increment(ref epoch);
        PoolState[] active;
        lock (poolRegistryGate)
        {
            poolRegistry.RemoveAll(value => !value.TryGetTarget(out _));
            active = poolRegistry.Select(value => value.TryGetTarget(out var state) ? state : null).OfType<PoolState>().ToArray();
        }
        // Finish the owner boundary before releasing accepted tickets. Clearing a
        // worker's ticket concurrently with Complete would lose authoritative work.
        foreach (var state in active) lock (state.Gate) state.Clear();
        ClearRenderRecords();
        previous?.DiscardPending();
        if (!detach) Volatile.Write(ref backend, previous);
    }

    private static void BeforePool(ParticlePoolQuads __instance, float __0, MethodBase __originalMethod, out PoolMeasurement __state)
    {
        __state = new PoolMeasurement(current, 0, PerformancePatchSet.Current?.GpuParticleSimulation == true); current = null;
        bool worker = __originalMethod == Worker;
        if (OffThread(__instance) != worker || Game(__instance).IsPaused ||
            (__instance.GetType() != typeof(ParticlePoolQuads) && __instance.GetType() != typeof(ParticlePoolCubes))) return;
        if (Benchmark) __state = __state with { Start = Stopwatch.GetTimestamp() };
        if (!Enabled)
        {
            if (pools.TryGetValue(__instance, out var previous)) lock (previous.Gate) previous.Clear();
            return;
        }
        var state = pools.GetValue(__instance, NewPool);
        Monitor.Enter(state.Gate);
        long generation = Interlocked.Read(ref epoch);
        current = new Scope(__instance, state, __0, Game(__instance).Calendar.SpeedOfTime / 60f, worker, generation, InstanceUploadBoundarySafe(__instance));
        if (state.Epoch != generation) { state.Clear(); state.Epoch = generation; }
        state.CpuOnly.Clear();
        if (!worker && __instance.ParticlesPool.FirstAlive is { } first)
        {
            var physics = Physics(__instance);
            Submit(current, first, physics.PhysicsTickTime, physics.PhysicsTickTime, physics, lookahead: true);
        }
    }
    private static Exception? AfterPool(PoolMeasurement __state, Exception? __exception)
    {
        var held = current;
        try
        {
            if (current is { } scope)
            {
                if (__exception != null || scope.Epoch != Interlocked.Read(ref epoch) || !Enabled) scope.State.Clear();
                else scope.State.DiscardPending();
            }
        }
        finally
        {
            if (held != null) Monitor.Exit(held.State.Gate);
            current = __state.Previous;
            if (__state.Start != 0)
            {
                long ticks = Stopwatch.GetTimestamp() - __state.Start;
                if (__state.Requested) { Interlocked.Increment(ref gpuBenchmarkPools); Interlocked.Add(ref gpuBenchmarkPoolTicks, ticks); }
                else { Interlocked.Increment(ref cpuBenchmarkPools); Interlocked.Add(ref cpuBenchmarkPoolTicks, ticks); }
            }
        }
        return __exception;
    }

    private static bool Input(ParticleGeneric particle, float lifeDt, float dt, ParticlePhysics physics, PoolState state, out ParticleSimulationInput input)
    {
        input = default;
        if (physics.GetType() != typeof(ParticlePhysics) || physics.BlockAccess.GetType() != typeof(Vintagestory.Common.BlockAccessorReadLockfree) ||
            particle.SecondaryParticles is { Length: > 0 } || particle.DeathParticles is { Length: > 0 } || particle.RandomVelocityChange ||
            !double.IsFinite(particle.Position.X) || !double.IsFinite(particle.Position.Y) || !double.IsFinite(particle.Position.Z) ||
            Math.Abs(particle.Position.X) > int.MaxValue - 8 || Math.Abs(particle.Position.Y) > int.MaxValue - 8 || Math.Abs(particle.Position.Z) > int.MaxValue - 8 ||
            !float.IsFinite(lifeDt) || !float.IsFinite(dt) || lifeDt <= 0 || dt <= 0 || dt > 1 || lifeDt > 1 ||
            !float.IsFinite(particle.SecondsAlive) || !float.IsFinite(particle.LifeLength) || particle.LifeLength <= 0 ||
            !float.IsFinite(particle.GravityStrength) || !float.IsFinite(particle.SizeMultiplier) || !float.IsFinite(particle.ParticleHeight)) return false;
        var velocity = particle.Velocity;
        input.X = particle.Position.X; input.Y = particle.Position.Y; input.Z = particle.Position.Z;
        input.VelocityGravity = new Vector4(velocity.X, velocity.Y, velocity.Z, particle.GravityStrength);
        input.Timing = new Vector4(particle.SecondsAlive, particle.LifeLength, lifeDt, dt);
        bool parent = particle.ParentVelocity is { } inherited && (inherited.X != 0 || inherited.Y != 0 || inherited.Z != 0);
        if (parent) input.Parent = new Vector4(particle.ParentVelocity.X, particle.ParentVelocity.Y, particle.ParentVelocity.Z, particle.ParentVelocityWeight);
        input.StartingVelocity = new Vector4(particle.StartingVelocity.X, particle.StartingVelocity.Y, particle.StartingVelocity.Z,
            particle.TerrainCollision && particle.SelfPropelled ? 1 : 0);
        input.Control = new Vector4(Drag(particle), parent ? 1 : 0, particle.VelocityEvolve != null ? 1 : 0, particle.TerrainCollision ? 1 : 0);
        input.Physics = new Vector4(particle.ParticleHeight * particle.SizeMultiplier, particle.SwimOnLiquid ? 1 : 0,
            (particle.DieInAir ? 1 : 0) | (particle.DieInLiquid ? 2 : 0), TickCount(particle));
        foreach (float value in MemoryMarshal.Cast<byte, float>(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref input, 1))[32..128]))
            if (!float.IsFinite(value) || Math.Abs(value) > 1000000) return false;
        if (particle.VelocityEvolve is { } evolution)
        {
            float phase = (particle.SecondsAlive + lifeDt) / particle.LifeLength;
            if (evolution.Length < 3 || !GpuParticleVisuals.CaptureVelocity(evolution[0], phase, out input.VelocityX) ||
                !GpuParticleVisuals.CaptureVelocity(evolution[1], phase, out input.VelocityY) ||
                !GpuParticleVisuals.CaptureVelocity(evolution[2], phase, out input.VelocityZ)) return false;
        }
        return true;
    }
    private static bool Matches(ParticleGeneric particle, float lifeDt, float dt, ParticlePhysics physics, PoolState state,
        in ParticleSimulationInput expected)
    {
        // Validate the original snapshot without constructing/validating another
        // 224-byte shader record or recapturing the three evolution functions.
        // Stock collision boxes can call mod code, so later candidates still need
        // this mutation check at their actual TickNow boundary.
        if (particle.SecondaryParticles is { Length: > 0 } || particle.DeathParticles is { Length: > 0 } || particle.RandomVelocityChange ||
            particle.Position.X != expected.X || particle.Position.Y != expected.Y || particle.Position.Z != expected.Z ||
            particle.Velocity.X != expected.VelocityGravity.X || particle.Velocity.Y != expected.VelocityGravity.Y ||
            particle.Velocity.Z != expected.VelocityGravity.Z ||
            particle.GravityStrength != expected.VelocityGravity.W ||
            particle.SecondsAlive != expected.Timing.X ||
            particle.LifeLength != expected.Timing.Y || lifeDt != expected.Timing.Z || dt != expected.Timing.W ||
            particle.StartingVelocity.X != expected.StartingVelocity.X || particle.StartingVelocity.Y != expected.StartingVelocity.Y ||
            particle.StartingVelocity.Z != expected.StartingVelocity.Z || (particle.TerrainCollision && particle.SelfPropelled) != (expected.StartingVelocity.W > 0.5f) ||
            particle.TerrainCollision != (expected.Control.W > 0.5f) ||
            Drag(particle) != expected.Control.X ||
            particle.ParticleHeight * particle.SizeMultiplier != expected.Physics.X ||
            particle.SwimOnLiquid != (expected.Physics.Y > 0.5f) ||
            ((particle.DieInAir ? 1 : 0) | (particle.DieInLiquid ? 2 : 0)) != (int)expected.Physics.Z || TickCount(particle) != expected.Physics.W ||
            !float.IsFinite(particle.SizeMultiplier) || !float.IsFinite(particle.ParticleHeight)) return false;
        var parent = particle.ParentVelocity;
        bool inherited = parent is { } && (parent.X != 0 || parent.Y != 0 || parent.Z != 0);
        if (inherited != (expected.Control.Y > 0.5f) || (inherited && (parent!.X != expected.Parent.X || parent.Y != expected.Parent.Y ||
            parent.Z != expected.Parent.Z || particle.ParentVelocityWeight != expected.Parent.W))) return false;
        var evolution = particle.VelocityEvolve;
        if ((evolution != null) != (expected.Control.Z > 0.5f)) return false;
        if (evolution != null)
        {
            if (evolution.Length < 3 || !MatchesEvolution(evolution[0], expected.VelocityX) ||
                !MatchesEvolution(evolution[1], expected.VelocityY) || !MatchesEvolution(evolution[2], expected.VelocityZ)) return false;
        }
        if (Math.Abs(particle.Position.X) > int.MaxValue || Math.Abs(particle.Position.Y) > int.MaxValue || Math.Abs(particle.Position.Z) > int.MaxValue) return false;
        return true;
    }
    private static bool MatchesEvolution(EvolvingNatFloat value, ParticleEvolution expected) => expected.Function == -1
        ? value.Transform == EvolvingNatFloat.NoValueSet.Transform && value.Factor == EvolvingNatFloat.NoValueSet.Factor
        : (int)value.Transform == expected.Function && value.Factor == expected.Factor &&
            value.MaxValue.HasValue == (expected.Limited != 0) && (value.MaxValue ?? 0) == expected.Maximum;

    private static bool BeforeTick(ParticleGeneric __instance, float __0, float __1, ICoreClientAPI __2, ParticlePhysics __3, out TickMeasurement __state)
    {
        bool track = Benchmark && __instance.VertexFlags == BenchmarkMarker;
        __state = new TickMeasurement(track ? Stopwatch.GetTimestamp() : 0, PerformancePatchSet.Current?.GpuParticleSimulation == true, false);
        bool original = TryGpuTick(__instance, __0, __1, __2, __3);
        if (track && !original) __state = __state with { Gpu = true };
        return original;
    }
    private static void AfterTick(ParticleGeneric __instance, TickMeasurement __state)
    {
        if (!__instance.Alive && current is { } scope)
        {
            scope.State.Remove(__instance);
            if (scope.State.Rendered.Remove(__instance, out var rendered)) rendered.Dispose();
        }
        if (__state.Start == 0) return;
        long ticks = Stopwatch.GetTimestamp() - __state.Start;
        if (__state.Gpu) { Interlocked.Increment(ref gpuBenchmarkSteps); Interlocked.Add(ref gpuBenchmarkTicks, ticks); }
        else if (!__state.Requested) { Interlocked.Increment(ref cpuBenchmarkSteps); Interlocked.Add(ref cpuBenchmarkTicks, ticks); }
        else { Interlocked.Increment(ref fallbackBenchmarkSteps); Interlocked.Add(ref fallbackBenchmarkTicks, ticks); }
    }
    private static bool TryGpuTick(ParticleGeneric __instance, float __0, float __1, ICoreClientAPI __2, ParticlePhysics __3)
    {
        if (!Enabled || current is not { } scope || scope.Epoch != Interlocked.Read(ref epoch)) return true;
        if (Benchmark && __instance.VertexFlags != BenchmarkMarker) return true;
        var state = scope.State;
        if (state.Rendered.Remove(__instance, out var lastRender)) lastRender.Dispose();
        if (state.CpuOnly.Contains(ParticleMotionHistory.SpawnToken(__instance))) { state.InvalidatePending(); return true; }
        if (!state.Pending.TryGetValue(__instance, out var pending))
        {
            // A repeated due step or CPU callback changes the cache prefix before
            // later speculative actors. Rebuild in actual TickNow order.
            state.InvalidatePending();
            if (!Submit(scope, __instance, __0, __1, __3) || !state.Pending.TryGetValue(__instance, out pending)) return true;
        }
        var batch = pending.Batch;
        if (!ReferenceEquals(pending.Spawn, ParticleMotionHistory.SpawnToken(__instance)))
        { state.InvalidatePending(); return TryGpuTick(__instance, __0, __1, __2, __3); }
        if (!batch.Ready)
        {
            batch.Buffer = batch.Backend!.Complete(batch.Ticket, batch.Output.AsSpan(0, batch.Particles.Count));
            batch.Ticket = 0; batch.Ready = true;
            // Remember every actual custom-query boundary before consuming the
            // first one. Otherwise each CPU callback discards the same suffix,
            // and the next actor submits that entire suffix again. Advice lives
            // only for this pool call; a callback may still change later inputs,
            // which keep their stock CPU tick at its original position.
            for (int i = 0; i < batch.Particles.Count; i++)
                if ((batch.Output[i].CollisionFlags & ParticleSimulationOutput.RequiresCpuCollision) != 0 &&
                    ReferenceEquals(batch.Spawns[i], ParticleMotionHistory.SpawnToken(batch.Particles[i])))
                    state.CpuOnly.Add(batch.Spawns[i]);
            if (HeadlessHarnessOptions.Enabled && !Benchmark)
                Check(batch.Input.AsSpan(0, batch.Particles.Count), batch.World.AsSpan(0, batch.Particles.Count),
                    CollectionsMarshal.AsSpan(batch.Boxes), CollectionsMarshal.AsSpan(batch.LiquidCells), batch.Output.AsSpan(0, batch.Particles.Count));
        }
        ref readonly var expected = ref batch.Input[pending.Index];
        int resultIndex = pending.Index;
        var result = batch.Output[resultIndex];
        if ((result.CollisionFlags & ParticleSimulationOutput.RequiresCpuCollision) != 0)
        {
            // Only the actual stock query needs this custom callback. Keep its
            // original tick/event order and rebase all later speculative work.
            state.InvalidatePending();
            return true;
        }
        if (!Matches(__instance, __0, __1, __3, state, in expected))
        {
            // An earlier stock callback changed this producer: rebase supported work
            // at its actual stock tick boundary rather than recomputing it on CPU.
            state.InvalidatePending();
            return TryGpuTick(__instance, __0, __1, __2, __3);
        }
        if (!Enabled || scope.Epoch != Interlocked.Read(ref epoch)) { state.Remove(__instance); return true; }
        Apply(__instance, in result, __1, __2, __3, batch, resultIndex, state); Interlocked.Increment(ref stepsApplied);
        state.Rendered[__instance] = new RenderState(__instance, batch.Buffer!, resultIndex, in result);
        state.Remove(__instance);
        return false;
    }

    private static bool Submit(Scope scope, ParticleBase first, float lifeDt, float dt, ParticlePhysics physics, bool lookahead = false)
    {
        var state = scope.State;
        var batch = state.Batches.FirstOrDefault(value => value.Remaining == 0);
        if (batch == null)
        {
            state.Batches.Add(batch = new Batch());
        }
        try
        {
            int visited = 0;
            ParticleBase? node = first;
            for (; node != null && visited++ < MaximumBatch; node = node.Next)
            {
                if (node is not ParticleGeneric particle || (Benchmark && particle.VertexFlags != BenchmarkMarker)) break;
                if (state.Pending.ContainsKey(particle)) continue;
                if ((!ReferenceEquals(particle, first) || lookahead) && !scope.Worker &&
                    particle.accum + scope.Delta * scope.GameSpeed < physics.PhysicsTickTime) continue;
                // A known due CPU tick is an ordering barrier, not a hole that
                // speculative GPU work can cross and later discard again.
                if (state.CpuOnly.Contains(ParticleMotionHistory.SpawnToken(particle))) break;
                if (!Input(particle, lifeDt, dt, physics, state, out var snapshot) ||
                    !CaptureWorld(particle, physics, state, batch, out var world))
                { state.CpuOnly.Add(ParticleMotionHistory.SpawnToken(particle)); break; }
                int index = batch.Particles.Count;
                if (batch.Input.Length <= index) Array.Resize(ref batch.Input, Math.Min(MaximumBatch, Math.Max(1, batch.Input.Length * 2)));
                if (batch.World.Length <= index) Array.Resize(ref batch.World, batch.Input.Length);
                if (batch.Spawns.Length <= index) Array.Resize(ref batch.Spawns, batch.Input.Length);
                object spawn = ParticleMotionHistory.SpawnToken(particle);
                batch.Particles.Add(particle);
                batch.World[index] = world; batch.Spawns[index] = spawn;
                batch.Input[index] = snapshot;
            }
            // Do not speculate across an unsupported producer: its callbacks own the
            // order of spawn/death events and may mutate subsequent particles.
            batch.Backend = Volatile.Read(ref backend);
            int count = batch.Particles.Count;
            if (batch.Output.Length < count) Array.Resize(ref batch.Output, count);
            if (count > 0 && batch.Backend != null)
                batch.Ticket = batch.Backend.Submit(batch.Input.AsSpan(0, count), batch.Spawns.AsSpan(0, count),
                    batch.World.AsSpan(0, count), CollectionsMarshal.AsSpan(batch.Boxes), CollectionsMarshal.AsSpan(batch.LiquidCells));
            if (batch.Ticket != 0)
            {
                batch.Remaining = count;
                for (int i = 0; i < count; i++) state.Pending.Add(batch.Particles[i],
                    new PendingStep(batch, i, batch.Spawns[i]));
                return true;
            }
            // No speculative retry or waiting when both backend tiles are occupied.
            // Stock work continues and admission can try again at the next pool boundary.
            for (int i = 0; i < batch.Particles.Count; i++) state.CpuOnly.Add(batch.Spawns[i]);
            if (first is ParticleGeneric unsupported) state.CpuOnly.Add(ParticleMotionHistory.SpawnToken(unsupported));
            batch.Clear();
            return false;
        }
        catch
        {
            // Preparation can fail before Remaining or Pending is populated.
            // The idle-clear shortcut cannot discover this partial owner.
            batch.Clear();
            state.InvalidatePending();
            throw;
        }
    }

    private static void Apply(ParticleGeneric particle, in ParticleSimulationOutput result, float dt, ICoreClientAPI api, ParticlePhysics physics,
        Batch batch, int index, PoolState state)
    {
        particle.SecondsAlive = result.VelocityAge.W;
        particle.Velocity.Set(result.VelocityAge.X, result.VelocityAge.Y, result.VelocityAge.Z);
        Motion(particle).Set(result.MotionAlive.X, result.MotionAlive.Y, result.MotionAlive.Z);
        particle.Position.Set(result.X, result.Y, result.Z);
        if (particle.TerrainCollision)
        {
            MaterializeCollisionList(physics, batch, index, in result, state.CollisionBox);
            ApplyCollisionResponse(particle, dt, api, result.CollisionFlags);
        }
        bool liquidPolicy = (result.QueryBounds & ParticleSimulationOutput.LiquidPolicyApplied) != 0;
        if (liquidPolicy)
        {
            uint policy = batch.World[index].LiquidPolicy;
            Interlocked.Increment(ref liquidPolicyApplied);
            if ((policy & ParticleSimulationWorldInput.DieInAir) != 0) Interlocked.Increment(ref airPolicyApplied);
            if ((policy & ParticleSimulationWorldInput.DieInLiquid) != 0) Interlocked.Increment(ref liquidDeathPolicyApplied);
            if ((result.QueryBounds & ParticleSimulationOutput.LiquidCell) != 0) Interlocked.Increment(ref wetPolicyApplied);
        }
        particle.Alive = result.MotionAlive.W > 0.5f && (liquidPolicy || !particle.DieInAir || physics.BlockAccess.GetBlockRaw((int)particle.Position.X,
            (int)(particle.Position.Y + 0.15000000596046448), (int)particle.Position.Z, 2).IsLiquid());
        byte ticks = unchecked((byte)(TickCount(particle) + 1)); TickCount(particle) = ticks;
        if (ticks <= 2) return;
        particle.lightrgbs = particle.LightEmission == int.MaxValue ? particle.LightEmission : physics.BlockAccess.GetLightRGBsAsInt(
            (int)particle.Position.X, (int)particle.Position.Y, (int)particle.Position.Z);
        if (particle.LightEmission != 0) particle.lightrgbs = Math.Max(particle.lightrgbs & 0xff, particle.LightEmission & 0xff) |
            Math.Max(particle.lightrgbs & 0xff00, particle.LightEmission & 0xff00) | Math.Max(particle.lightrgbs & 0xff0000, particle.LightEmission & 0xff0000);
        if (particle.DieOnRainHeightmap)
        {
            float remainder = 1 - particle.prevPosAdvance;
            double x = particle.prevPosDeltaX * remainder * dt * 8f, y = particle.prevPosDeltaY * remainder * dt * 12f,
                z = particle.prevPosDeltaZ * remainder * dt * 8f;
            particle.Alive &= physics.BlockAccess.GetRainMapHeightAt((int)(particle.Position.X + x), (int)(particle.Position.Z + z)) - y < particle.Position.Y;
        }
        particle.Alive &= !particle.DieInLiquid || (liquidPolicy ? (result.QueryBounds & ParticleSimulationOutput.LiquidDeath) == 0 :
            !physics.BlockAccess.GetBlockRaw((int)particle.Position.X, (int)(particle.Position.Y + 0.15000000596046448), (int)particle.Position.Z, 2).IsLiquid());
        TickCount(particle) = 0;
        float length = particle.Velocity.Length();
        Direction(particle) = (uint)(unchecked((byte)(particle.Velocity.X / length * 128f)) |
            unchecked((byte)(particle.Velocity.Y / length * 128f)) << 8 | unchecked((byte)(particle.Velocity.Z / length * 128f)) << 16);
    }

    private static void Check(ReadOnlySpan<ParticleSimulationInput> input, ReadOnlySpan<ParticleSimulationWorldInput> world,
        ReadOnlySpan<ParticleSimulationBox> boxes, ReadOnlySpan<uint> liquidCells, ReadOnlySpan<ParticleSimulationOutput> output)
    {
        double floatError = 0, positionError = 0;
        int checkedCount = 0;
        uint populatedSize = world.IsEmpty ? 0 : world[0].InitialPopulatedSize;
        for (int i = 0; i < input.Length; i++)
        {
            var state = input[i];
            var result = output[i];
            var velocity = new Vector3(state.VelocityGravity.X, state.VelocityGravity.Y, state.VelocityGravity.Z);
            if (state.StartingVelocity.W > 0.5f) velocity += (new Vector3(state.StartingVelocity.X, state.StartingVelocity.Y, state.StartingVelocity.Z) - velocity) * 0.02f;
            velocity.Y -= state.VelocityGravity.W * state.Timing.W;
            ReferenceBuoyancy(ref velocity, in state, world[i]);
            float age = state.Timing.X + state.Timing.Z, phase = age / state.Timing.Y;
            var motion = velocity;
            if (state.Control.Z > 0.5f) motion *= new Vector3(GpuParticleVisuals.Evaluate(AtPhase(state.VelocityX, phase)),
                GpuParticleVisuals.Evaluate(AtPhase(state.VelocityY, phase)), GpuParticleVisuals.Evaluate(AtPhase(state.VelocityZ, phase)));
            motion *= state.Timing.W;
            if (state.Control.Y > 0.5f) motion += ((new Vector3(state.Parent.X, state.Parent.Y, state.Parent.Z) * state.Parent.W) * state.Timing.W) * state.Control.X;
            uint queryBounds = 0;
            uint flags = state.Control.W > 0.5f ? ReferenceCollision(in state, world[i], boxes, ref motion, ref populatedSize, out queryBounds) : 0;
            // Establish the boundary independently from the CPU query, rather
            // than trusting the GPU's flag to exclude a result from checking.
            // No numeric result beyond this opaque callback is authoritative.
            if ((flags & ParticleSimulationOutput.RequiresCpuCollision) != 0)
            {
                if (result.CollisionFlags != ParticleSimulationOutput.RequiresCpuCollision || result.QueryBounds != queryBounds)
                    floatError = double.MaxValue;
                break;
            }
            double factor = state.Control.W > 0.5f ? 0.999 : 1;
            double x = state.X + (double)motion.X * factor, y = state.Y + (double)motion.Y * factor, z = state.Z + (double)motion.Z * factor;
            bool alive = age < state.Timing.Y;
            if (world[i].LiquidPolicy != 0)
            {
                var snapshot = world[i];
                int Offset(int shift) => (int)((snapshot.LiquidBounds >> shift) & 15) - 8;
                int minX = (int)state.X + Offset(0), minY = (int)state.Y + Offset(4), minZ = (int)state.Z + Offset(8),
                    maxX = (int)state.X + Offset(12), maxY = (int)state.Y + Offset(16), maxZ = (int)state.Z + Offset(20);
                int cellX = (int)x, cellY = (int)(y + 0.15000000596046448), cellZ = (int)z;
                if (cellX < minX || cellX > maxX || cellY < minY || cellY > maxY || cellZ < minZ || cellZ > maxZ)
                { floatError = double.MaxValue; break; }
                int address = (int)snapshot.LiquidStart + ((cellZ - minZ) * (maxY - minY + 1) + cellY - minY) * (maxX - minX + 1) + cellX - minX;
                if ((uint)address >= liquidCells.Length) { floatError = double.MaxValue; break; }
                // Stock short circuit avoids the IsLiquid callback after age
                // expiry unless the third-tick liquid policy also requires it.
                bool needsCell = (snapshot.LiquidPolicy & ParticleSimulationWorldInput.DieInLiquid) != 0 ||
                    ((snapshot.LiquidPolicy & ParticleSimulationWorldInput.DieInAir) != 0 && alive);
                if (needsCell && liquidCells[address] == 2)
                {
                    if (result.CollisionFlags != ParticleSimulationOutput.RequiresCpuCollision || result.QueryBounds != queryBounds)
                        floatError = double.MaxValue;
                    break;
                }
                bool liquid = liquidCells[address] == 1;
                queryBounds |= ParticleSimulationOutput.LiquidPolicyApplied | (liquid ? ParticleSimulationOutput.LiquidCell : 0);
                if ((snapshot.LiquidPolicy & ParticleSimulationWorldInput.DieInLiquid) != 0 && liquid) queryBounds |= ParticleSimulationOutput.LiquidDeath;
                if ((snapshot.LiquidPolicy & ParticleSimulationWorldInput.DieInAir) != 0) alive = alive && liquid;
                Interlocked.Increment(ref liquidPolicyChecks);
                if ((snapshot.LiquidPolicy & ParticleSimulationWorldInput.DieInAir) != 0) Interlocked.Increment(ref airPolicyChecks);
                if ((snapshot.LiquidPolicy & ParticleSimulationWorldInput.DieInLiquid) != 0) Interlocked.Increment(ref liquidDeathPolicyChecks);
                if (liquid) Interlocked.Increment(ref wetPolicyChecks);
            }
            var delta = Vector4.Abs(result.VelocityAge - new Vector4(velocity, age));
            var motionDelta = Vector4.Abs(result.MotionAlive - new Vector4(motion, alive ? 1 : 0));
            double error = Math.Max(Math.Max(delta.X, delta.Y), Math.Max(delta.Z, delta.W));
            error = Math.Max(error, Math.Max(Math.Max(motionDelta.X, motionDelta.Y), Math.Max(motionDelta.Z, motionDelta.W)));
            if (flags != result.CollisionFlags || queryBounds != result.QueryBounds) error = double.MaxValue;
            floatError = Math.Max(floatError, double.IsFinite(error) ? error : double.MaxValue);
            error = Math.Max(Math.Abs(result.X - (state.X + (double)motion.X * factor)), Math.Max(
                Math.Abs(result.Y - (state.Y + (double)motion.Y * factor)), Math.Abs(result.Z - (state.Z + (double)motion.Z * factor))));
            positionError = Math.Max(positionError, double.IsFinite(error) ? error : double.MaxValue);
            checkedCount++;
        }
        lock (checkGate) { maximumFloatError = Math.Max(maximumFloatError, floatError); maximumPositionError = Math.Max(maximumPositionError, positionError); }
        Interlocked.Add(ref checks, checkedCount);
    }
    private static ParticleEvolution AtPhase(ParticleEvolution value, float phase) =>
        new(value.First, value.Factor, phase, value.Maximum, value.Function, value.Limited != 0);
}
