using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private ParticleSimulationRunner? particleSimulation;
    private bool particleSimulationUnavailable;
    internal void PrepareParticleSimulation(bool enabled, float[] table)
    {
        if (!enabled || !_context.ShaderFloat64Enabled ||
            particleSimulation != null || particleSimulationUnavailable || _disposed) return;
        int id = 0;
        try
        {
            id = CreateComputeProgram(ParticleEvolutionShader("particle-simulation.comp"), "particle-simulation",
                Enumerable.Range(0, 9).Select(i => new ComputeSlot((uint)i, ComputeSlotKind.Buffer)).ToArray(), 12, 64, 1);
            var program = _compute.Get(id);
            if (program != null) particleSimulation = new ParticleSimulationRunner(_context, program, _compute.PipelineFor(program, []), table);
            else particleSimulationUnavailable = true;
        }
        catch (Exception failure)
        {
            // Nothing has been submitted during preparation; these private program
            // objects can be released immediately, without renderer retirement.
            if (id != 0) _compute.Remove(id)?.Dispose();
            particleSimulationUnavailable = true;
            RenderLogger?.Warning("VulkanStory: GPU particle simulation unavailable: {0}", failure.Message);
        }
    }
    internal IParticleSimulationBackend? ParticleSimulationBackend => particleSimulation;
    internal bool ParticleSimulationAvailable => particleSimulation?.Available == true;
    internal bool ParticleSimulationSeparateQueue => _context.ParticleComputeQueueSeparate;
    internal long ParticleSimulationDispatchesRecorded => particleSimulation?.Dispatches ?? 0;
    internal long ParticleSimulationStepsRecorded => particleSimulation?.Steps ?? 0;
    internal double ParticleSimulationSubmitMilliseconds => particleSimulation?.SubmitMilliseconds ?? 0;
    internal double? ParticleSimulationGpuMilliseconds => particleSimulation?.GpuMilliseconds;
    internal ulong ParticleSimulationRetainedBytes => particleSimulation?.RetainedBytes ?? 0;
    internal long ParticleSimulationCapacityFallbacks => particleSimulation?.CapacityFallbacks ?? 0;

    /// <summary>Two immutable submissions, shared by pool workers without touching frame command state.</summary>
    private sealed class ParticleSimulationRunner : IParticleSimulationBackend, IDisposable
    {
        private readonly object gate = new();
        private readonly VulkanContext context;
        private readonly ParticleSimulationSlot[] slots;
        private readonly long[] tickets = new long[2];
        private readonly bool[] abandoned = new bool[2];
        private readonly HashSet<SimulationBuffer> retained = new();
        private readonly List<VulkanBuffer> reusable = new();
        private const int ResidentCapacity = 65536;
        private const int BoxCapacity = 262144;
        private const int LiquidCapacity = 262144;
        // Device inputs plus mapped uploads, per-slot seeds/readback/query scratch,
        // and shared device residents all count against the same 128 MiB budget.
        private const ulong WorkspaceBytes = ResidentCapacity * (224UL + 64) +
            2 * (2 * (4096UL * (32 + 64) + BoxCapacity * 48UL + LiquidCapacity * 4UL + 8193UL * 4) + 4096UL * (224 + 64 + 64 + 32));
        private readonly VulkanBuffer configurations, simulationState;
        internal readonly Semaphore Timeline;
        private readonly ConditionalWeakTable<object, Resident> residents = new();
        private readonly Resident?[] residentSlots = new Resident?[ResidentCapacity];
        private readonly Resident[][] pendingResidents = [[], []];
        private readonly ParticleSimulationCommand[] commands = new ParticleSimulationCommand[4096];
        private int nextResident;
        private sealed class Resident(int slot, object spawn)
        {
            internal readonly int Slot = slot;
            internal readonly WeakReference<object> Spawn = new(spawn);
            internal ParticleSimulationInput Configuration;
            internal ParticleSimulationOutput Last;
            internal bool Initialized, Pending;
        }
        private const int MaximumReusableBuffers = 16;
        private const ulong MaximumRetainedBytes = 128UL << 20;
        private ulong retainedBytes;
        private long capacityFallbacks;
        private long admissionTicks;
        private long sequence;
        private bool disposed, failed;
        internal bool Available => !Volatile.Read(ref disposed) && !Volatile.Read(ref failed);
        internal ulong RetainedBytes { get { lock (gate) return retainedBytes; } }
        internal long CapacityFallbacks => Interlocked.Read(ref capacityFallbacks);
        internal long Dispatches => slots.Sum(slot => slot.Dispatches);
        internal long Steps => slots.Sum(slot => slot.Steps);
        internal double SubmitMilliseconds => slots.Sum(slot => slot.SubmitMilliseconds) +
            Interlocked.Read(ref admissionTicks) * 1000.0 / Stopwatch.Frequency;
        internal double? GpuMilliseconds => slots.All(slot => slot.GpuMilliseconds.HasValue) ? slots.Sum(slot => slot.GpuMilliseconds!.Value) : null;

        internal ParticleSimulationRunner(VulkanContext context, ComputeProgram program, Pipeline pipeline, float[] values)
        {
            this.context = context;
            var memory = MemoryPropertyFlags.DeviceLocalBit;
            var usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit;
            configurations = new VulkanBuffer(context, ResidentCapacity * 224UL, usage, memory, MemoryPoolClass.DeviceBuffers);
            try { simulationState = new VulkanBuffer(context, ResidentCapacity * 64UL, usage, memory, MemoryPoolClass.DeviceBuffers); }
            catch { configurations.Dispose(); throw; }
            ParticleSimulationSlot? first = null;
            try
            {
                var type = new SemaphoreTypeCreateInfo { SType = StructureType.SemaphoreTypeCreateInfo, SemaphoreType = SemaphoreType.Timeline };
                var create = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &type };
                Semaphore made;
                VulkanResult.Check(context.Api.CreateSemaphore(context.Device, &create, null, &made), "particle simulation timeline"); Timeline = made;
                first = new ParticleSimulationSlot(context, program, pipeline, values, configurations, simulationState);
                slots = [first, new ParticleSimulationSlot(context, program, pipeline, values, configurations, simulationState)];
                retainedBytes = WorkspaceBytes;
            }
            catch
            {
                first?.Dispose(); configurations.Dispose(); simulationState.Dispose();
                if (Timeline.Handle != 0) context.Api.DestroySemaphore(context.Device, Timeline, null);
                throw;
            }
        }
        private Resident? FindResident(object spawn)
        {
            if (residents.TryGetValue(spawn, out var existing)) return existing;
            int slot = nextResident;
            if (slot == ResidentCapacity)
            {
                slot = Array.FindIndex(residentSlots, value => value is { Pending: false } && !value.Spawn.TryGetTarget(out _));
                if (slot < 0) return null;
            }
            else nextResident++;
            var result = new Resident(slot, spawn); residentSlots[slot] = result; residents.Add(spawn, result);
            return result;
        }
        private static ParticleEvolution ConfigurationEvolution(ParticleEvolution value) =>
            new(value.First, value.Factor, 0, value.Maximum, value.Function, value.Limited != 0);
        private static ParticleSimulationInput Configuration(in ParticleSimulationInput input) => new()
        {
            VelocityGravity = new(0, 0, 0, input.VelocityGravity.W), Timing = new(0, input.Timing.Y, 0, 0),
            Parent = input.Parent, StartingVelocity = input.StartingVelocity,
            Physics = new(input.Physics.X, input.Physics.Y, input.Physics.Z, 0),
            Control = new(0, input.Control.Y, input.Control.Z, input.Control.W),
            VelocityX = ConfigurationEvolution(input.VelocityX), VelocityY = ConfigurationEvolution(input.VelocityY),
            VelocityZ = ConfigurationEvolution(input.VelocityZ),
        };
        public long Submit(ReadOnlySpan<ParticleSimulationInput> input, ReadOnlySpan<object> spawns,
            ReadOnlySpan<ParticleSimulationWorldInput> world, ReadOnlySpan<ParticleSimulationBox> boxes, ReadOnlySpan<uint> liquidCells)
        {
            lock (gate)
            {
                if (disposed || failed || input.IsEmpty || input.Length > 4096 || spawns.Length != input.Length ||
                    world.Length != input.Length || boxes.Length > BoxCapacity || liquidCells.Length > LiquidCapacity) return 0;
                ulong bytes = (ulong)input.Length * 64;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (abandoned[i])
                    {
                        if (!CollectDiscarded(i)) continue;
                    }
                    if (tickets[i] != 0) continue;
                    long admissionStart = Stopwatch.GetTimestamp();
                    var active = new Resident[input.Length];
                    var unique = new HashSet<Resident>();
                    for (int j = 0; j < input.Length; j++)
                    {
                        var resident = FindResident(spawns[j]);
                        if (resident == null || resident.Pending || !unique.Add(resident))
                        {
                            Interlocked.Add(ref admissionTicks, Stopwatch.GetTimestamp() - admissionStart);
                            Interlocked.Increment(ref capacityFallbacks); return 0;
                        }
                        active[j] = resident;
                    }
                    int reusableIndex = reusable.FindIndex(buffer => buffer.Size >= bytes);
                    VulkanBuffer target;
                    if (reusableIndex >= 0)
                    { target = reusable[reusableIndex]; reusable.RemoveAt(reusableIndex); }
                    else
                    {
                        while (retainedBytes > MaximumRetainedBytes - bytes && reusable.Count > 0)
                        {
                            var old = reusable[^1]; reusable.RemoveAt(reusable.Count - 1);
                            retainedBytes -= old.Size; old.Dispose();
                        }
                        if (bytes > MaximumRetainedBytes || retainedBytes > MaximumRetainedBytes - bytes)
                        {
                            Interlocked.Add(ref admissionTicks, Stopwatch.GetTimestamp() - admissionStart);
                            Interlocked.Increment(ref capacityFallbacks); return 0;
                        }
                        target = new VulkanBuffer(context, bytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
                            MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers,
                            [context.ParticleComputeQueueFamily, context.GraphicsQueueFamily]);
                        retainedBytes += bytes;
                    }
                    long ticket = checked(sequence + 1);
                    slots[i].BeginSeeds();
                    for (int j = 0; j < input.Length; j++)
                    {
                        var resident = active[j]; ref readonly var value = ref input[j];
                        var configuration = Configuration(in value);
                        bool configChanged = !resident.Initialized || !MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref configuration, 1)).SequenceEqual(
                            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref resident.Configuration, 1)));
                        if (configChanged) resident.Configuration = configuration;
                        // Only seed a spawn or materialize a real external/CPU event.
                        // Normal ticks retain double position and velocity on the GPU.
                        var last = resident.Last;
                        bool stateChanged = !resident.Initialized || last.X != value.X || last.Y != value.Y || last.Z != value.Z ||
                            last.VelocityAge.X != value.VelocityGravity.X || last.VelocityAge.Y != value.VelocityGravity.Y ||
                            last.VelocityAge.Z != value.VelocityGravity.Z;
                        slots[i].Seed(j, (uint)resident.Slot, configuration, new ParticleSimulationOutput
                            { X = value.X, Y = value.Y, Z = value.Z, VelocityAge = value.VelocityGravity }, configChanged, stateChanged);
                        resident.Initialized = true;
                        commands[j] = new ParticleSimulationCommand { Slot = (uint)resident.Slot, Age = value.Timing.X,
                            LifeDelta = value.Timing.Z, PhysicsDelta = value.Timing.W, Control = value.Control };
                    }
                    Interlocked.Add(ref admissionTicks, Stopwatch.GetTimestamp() - admissionStart);
                    try { if (!slots[i].Submit(commands.AsSpan(0, input.Length), world, boxes, liquidCells, target, Timeline, (ulong)ticket)) { Recycle(target); return 0; } }
                    catch { failed = true; throw; }
                    pendingResidents[i] = active;
                    foreach (var resident in active) resident.Pending = true;
                    sequence = ticket;
                    return tickets[i] = ticket;
                }
                Interlocked.Increment(ref capacityFallbacks);
                return 0;
            }
        }
        public ParticleSimulationBuffer Complete(long ticket, Span<ParticleSimulationOutput> output)
        {
            lock (gate)
            {
                if (disposed || failed || ticket == 0) throw new InvalidOperationException("Particle simulation backend lost an accepted trajectory.");
                int index = Array.IndexOf(tickets, ticket);
                if (index < 0 || abandoned[index]) throw new InvalidOperationException("Particle simulation ticket is no longer owned by its producer.");
                try
                {
                    var buffer = new SimulationBuffer(this, slots[index].Complete(output), (ulong)ticket);
                    for (int j = 0; j < pendingResidents[index].Length; j++)
                    { var resident = pendingResidents[index][j]; resident.Last = output[j]; resident.Pending = false; }
                    pendingResidents[index] = [];
                    retained.Add(buffer); tickets[index] = 0;
                    return buffer;
                }
                catch { failed = true; throw; }
            }
        }
        // Producer buffers, meshes and frame retirements release references.
        // Finalizers only decrement those references; the renderer returns
        // formerly rendered buffers to the bounded reuse list through Retire.
        internal void Retire(Action<VulkanBuffer> invalidate)
        {
            lock (gate)
            {
                foreach (var buffer in retained.Where(value => value.References == 0).ToArray())
                { retained.Remove(buffer); invalidate(buffer.Buffer); Recycle(buffer.Buffer); }
            }
        }
        private void Recycle(VulkanBuffer buffer)
        {
            if (reusable.Count < MaximumReusableBuffers) reusable.Add(buffer);
            else { retainedBytes -= buffer.Size; buffer.Dispose(); }
        }
        internal sealed class SimulationBuffer(ParticleSimulationRunner owner, VulkanBuffer buffer, ulong timelineValue) : ParticleSimulationBuffer
        {
            private int references = 1;
            internal int References => Volatile.Read(ref references);
            internal readonly ParticleSimulationRunner Owner = owner;
            internal readonly VulkanBuffer Buffer = buffer;
            internal readonly ulong TimelineValue = timelineValue;
            public override ParticleSimulationBuffer Retain()
            {
                int count;
                do
                {
                    count = Volatile.Read(ref references);
                    if (count <= 0) throw new ObjectDisposedException(nameof(SimulationBuffer));
                } while (Interlocked.CompareExchange(ref references, count + 1, count) != count);
                return this;
            }
            public override void Dispose() => Interlocked.Decrement(ref references);
        }
        // Called with gate held. Never resets a command pool or reuses mapped
        // buffers until its fence reports completion, including discarded tickets.
        private bool CollectDiscarded(int index)
        {
            VulkanBuffer? buffer;
            try { if (!slots[index].TryDiscard(out buffer)) return false; }
            catch
            {
                failed = true;
                throw;
            }
            Recycle(buffer!);
            // Discarded GPU state has no committed facade: the next accepted
            // command must rebase from the still authoritative game producer.
            foreach (var resident in pendingResidents[index]) { resident.Pending = false; resident.Initialized = false; }
            pendingResidents[index] = [];
            tickets[index] = 0; abandoned[index] = false;
            return true;
        }
        public void Release(long ticket)
        {
            lock (gate)
            {
                if (disposed || ticket == 0) return;
                int index = Array.IndexOf(tickets, ticket);
                if (index < 0) return;
                abandoned[index] = true;
                // Release can run from a weak producer-sidecar finalizer. Mark
                // ownership only; Submit/DiscardPending/Dispose collect on their
                // normal callers and never recycle an uncompleted buffer.
            }
        }
        public void DiscardPending()
        {
            lock (gate)
            {
                if (disposed) return;
                for (int i = 0; i < slots.Length; i++)
                    if (tickets[i] != 0)
                    {
                        abandoned[i] = true;
                        if (!failed) CollectDiscarded(i);
                    }
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                foreach (var slot in slots) slot.Dispose();
                if (Timeline.Handle != 0) context.Api.DestroySemaphore(context.Device, Timeline, null);
                configurations.Dispose(); simulationState.Dispose(); residents.Clear();
                foreach (var buffer in retained) buffer.Buffer.Dispose();
                foreach (var buffer in reusable) buffer.Dispose(); reusable.Clear();
                retained.Clear();
                retainedBytes = 0;
                disposed = true;
                Array.Clear(tickets);
                Array.Clear(abandoned);
            }
        }
    }

    /// <summary>A private command pool and fenced buffers; no frame command state crosses onto particle workers.</summary>
    private sealed class ParticleSimulationSlot : IDisposable
    {
        private const int Capacity = 4096;
        private readonly object gate = new();
        private readonly VulkanContext context;
        private readonly ComputeProgram program;
        private readonly Pipeline pipeline;
        private VulkanBuffer? input, output, table, world, boxes, liquid, queries, readback;
        private VulkanBuffer? inputUpload, worldUpload, boxUpload, liquidUpload, tableUpload, configurationUpload, stateUpload;
        private readonly List<BufferCopy> configurationCopies = new(), stateCopies = new();
        private readonly VulkanBuffer configurations, simulationState;
        private CommandPool pool;
        private CommandBuffer command;
        private DescriptorPool descriptors;
        private DescriptorSet set;
        private Fence fence;
        private QueryPool timestamps;
        private float timestampPeriod;
        private uint timestampBits;
        private bool disposed;
        private bool inFlight;
        private bool tableUploaded;
        private int count;
        private long dispatches, steps;
        private double submitMilliseconds, gpuMilliseconds;
        internal long Dispatches => Interlocked.Read(ref dispatches);
        internal long Steps => Interlocked.Read(ref steps);
        internal double SubmitMilliseconds => Volatile.Read(ref submitMilliseconds);
        internal double? GpuMilliseconds => timestamps.Handle != 0 ? Volatile.Read(ref gpuMilliseconds) : null;
        internal ParticleSimulationSlot(VulkanContext context, ComputeProgram program, Pipeline pipeline, float[] values,
            VulkanBuffer configurations, VulkanBuffer simulationState)
        {
            this.context = context; this.program = program; this.pipeline = pipeline;
            this.configurations = configurations; this.simulationState = simulationState;
            if (values.Length != 8193) throw new ArgumentException("Particle trigonometry table differs from its compute contract.");
            try
            {
                VulkanBuffer Device(ulong bytes) => new(context, bytes,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
                VulkanBuffer Upload(ulong bytes) => new(context, bytes, BufferUsageFlags.TransferSrcBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);
                input = Device(Capacity * 32UL); inputUpload = Upload(input.Size);
                world = Device(Capacity * 64UL); worldUpload = Upload(world.Size);
                boxes = Device(262144 * 48UL); boxUpload = Upload(boxes.Size);
                liquid = Device(262144 * 4UL); liquidUpload = Upload(liquid.Size);
                table = Device((ulong)values.Length * 4); tableUpload = Upload(table.Size);
                queries = Device(Capacity * 32UL);
                configurationUpload = Upload(Capacity * 224UL); stateUpload = Upload(Capacity * 64UL);
                readback = new VulkanBuffer(context, Capacity * 64UL, BufferUsageFlags.TransferDstBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);
                values.AsSpan().CopyTo(new Span<float>((void*)tableUpload.Mapped, values.Length));
                var createPool = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo,
                    QueueFamilyIndex = context.ParticleComputeQueueFamily, Flags = CommandPoolCreateFlags.ResetCommandBufferBit };
                CommandPool madePool;
                VulkanResult.Check(context.Api.CreateCommandPool(context.Device, &createPool, null, &madePool), "particle simulation command pool"); pool = madePool;
                var allocate = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
                CommandBuffer madeCommand;
                VulkanResult.Check(context.Api.AllocateCommandBuffers(context.Device, &allocate, &madeCommand), "particle simulation command buffer"); command = madeCommand;
                var createFence = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
                Fence madeFence;
                VulkanResult.Check(context.Api.CreateFence(context.Device, &createFence, null, &madeFence), "particle simulation fence"); fence = madeFence;
                var size = new DescriptorPoolSize(DescriptorType.StorageBuffer, 9);
                var createDescriptors = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &size };
                DescriptorPool madeDescriptors;
                VulkanResult.Check(context.Api.CreateDescriptorPool(context.Device, &createDescriptors, null, &madeDescriptors), "particle simulation descriptors"); descriptors = madeDescriptors;
                var layout = program.SetLayout;
                var allocateSet = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = descriptors, DescriptorSetCount = 1, PSetLayouts = &layout };
                DescriptorSet madeSet;
                VulkanResult.Check(context.Api.AllocateDescriptorSets(context.Device, &allocateSet, &madeSet), "particle simulation descriptor set"); set = madeSet;
                DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[9]
                { new(input.Handle, 0, input.Size), new(input.Handle, 0, input.Size), new(table.Handle, 0, table.Size),
                    new(configurations.Handle, 0, configurations.Size), new(simulationState.Handle, 0, simulationState.Size),
                    new(world.Handle, 0, world.Size), new(boxes.Handle, 0, boxes.Size), new(queries.Handle, 0, queries.Size), new(liquid.Handle, 0, liquid.Size) };
                WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[9];
                for (uint i = 0; i < 9; i++) writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet,
                    DstSet = set, DstBinding = i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = buffers + i };
                context.Api.UpdateDescriptorSets(context.Device, 9, writes, 0, null);
                uint count = 0; context.Api.GetPhysicalDeviceQueueFamilyProperties(context.PhysicalDevice, &count, null);
                var families = new QueueFamilyProperties[count];
                fixed (QueueFamilyProperties* data = families) context.Api.GetPhysicalDeviceQueueFamilyProperties(context.PhysicalDevice, &count, data);
                timestampBits = families[context.ParticleComputeQueueFamily].TimestampValidBits;
                timestampPeriod = context.Api.GetPhysicalDeviceProperties(context.PhysicalDevice).Limits.TimestampPeriod;
                if (timestampBits != 0 && timestampPeriod > 0)
                {
                    var createQuery = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = 2 };
                    QueryPool madeQuery;
                    // Timing is optional; unavailable query storage must not disable simulation.
                    if (context.Api.CreateQueryPool(context.Device, &createQuery, null, &madeQuery) == Result.Success) timestamps = madeQuery;
                }
            }
            catch { Dispose(); throw; }
        }

        // Runner gate and free ticket guarantee these per-slot uploads are no
        // longer GPU-owned. Pending residents cannot be seeded in another slot.
        internal void BeginSeeds() { configurationCopies.Clear(); stateCopies.Clear(); }
        internal void Seed(int index, uint slot, in ParticleSimulationInput configuration, in ParticleSimulationOutput state,
            bool configChanged, bool stateChanged)
        {
            if (configChanged)
            {
                ((ParticleSimulationInput*)configurationUpload!.Mapped)[index] = configuration;
                configurationCopies.Add(new BufferCopy((ulong)index * 224, slot * 224UL, 224));
            }
            if (stateChanged)
            {
                ((ParticleSimulationOutput*)stateUpload!.Mapped)[index] = state;
                stateCopies.Add(new BufferCopy((ulong)index * 64, slot * 64UL, 64));
            }
        }

        internal bool Submit(ReadOnlySpan<ParticleSimulationCommand> source, ReadOnlySpan<ParticleSimulationWorldInput> worldInput,
            ReadOnlySpan<ParticleSimulationBox> worldBoxes, ReadOnlySpan<uint> liquidCells, VulkanBuffer target, Semaphore timeline, ulong timelineValue)
        {
            if (source.Length == 0 || source.Length > Capacity) return false;
            long start = Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (disposed) return false;
                // A failed wait does not establish that the GPU released these buffers.
                // Preserve them and fail terminally rather than resetting pending work.
                if (inFlight) throw new InvalidOperationException("Particle simulation completion was not established; buffers remain GPU-owned.");
                var api = context.Api;
                output = target;
                var outputInfo = new DescriptorBufferInfo(output.Handle, 0, output.Size);
                var outputWrite = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set,
                    DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &outputInfo };
                api.UpdateDescriptorSets(context.Device, 1, &outputWrite, 0, null);
                MemoryMarshal.AsBytes(source).CopyTo(new Span<byte>((void*)inputUpload!.Mapped, source.Length * 32));
                MemoryMarshal.AsBytes(worldInput).CopyTo(new Span<byte>((void*)worldUpload!.Mapped, worldInput.Length * 64));
                MemoryMarshal.AsBytes(worldBoxes).CopyTo(new Span<byte>((void*)boxUpload!.Mapped, worldBoxes.Length * 48));
                MemoryMarshal.AsBytes(liquidCells).CopyTo(new Span<byte>((void*)liquidUpload!.Mapped, liquidCells.Length * 4));
                VulkanResult.Check(api.ResetCommandPool(context.Device, pool, 0), "particle simulation command reset");
                var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
                VulkanResult.Check(api.BeginCommandBuffer(command, &begin), "particle simulation command begin");
                if (timestamps.Handle != 0)
                { api.CmdResetQueryPool(command, timestamps, 0, 2); api.CmdWriteTimestamp2(command, PipelineStageFlags2.TopOfPipeBit, timestamps, 0); }
                void Barrier(PipelineStageFlags2 from, AccessFlags2 writes, PipelineStageFlags2 to, AccessFlags2 reads)
                {
                    var memory = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2, SrcStageMask = from,
                        SrcAccessMask = writes, DstStageMask = to, DstAccessMask = reads };
                    var dependency = new DependencyInfo { SType = StructureType.DependencyInfo,
                        MemoryBarrierCount = 1, PMemoryBarriers = &memory };
                    api.CmdPipelineBarrier2(command, &dependency);
                }
                void Copy(VulkanBuffer from, VulkanBuffer to, ulong bytes)
                {
                    if (bytes == 0) return;
                    var region = new BufferCopy(0, 0, bytes);
                    api.CmdCopyBuffer(command, from.Handle, to.Handle, 1, &region);
                }
                void CopySeeds(VulkanBuffer from, VulkanBuffer to, List<BufferCopy> regions)
                {
                    if (regions.Count == 0) return;
                    fixed (BufferCopy* copies = CollectionsMarshal.AsSpan(regions))
                        api.CmdCopyBuffer(command, from.Handle, to.Handle, (uint)regions.Count, copies);
                }
                // Same private queue owns all residents. Order earlier shader
                // reads/writes before rebases, and publish mapped slot uploads.
                Barrier(PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit | PipelineStageFlags2.HostBit,
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferReadBit |
                    AccessFlags2.TransferWriteBit | AccessFlags2.HostWriteBit,
                    PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit);
                Copy(inputUpload!, input!, (ulong)source.Length * 32);
                Copy(worldUpload!, world!, (ulong)worldInput.Length * 64);
                Copy(boxUpload!, boxes!, (ulong)worldBoxes.Length * 48);
                Copy(liquidUpload!, liquid!, (ulong)liquidCells.Length * 4);
                if (!tableUploaded) Copy(tableUpload!, table!, table!.Size);
                CopySeeds(configurationUpload!, configurations, configurationCopies);
                CopySeeds(stateUpload!, simulationState, stateCopies);
                Barrier(PipelineStageFlags2.CopyBit | PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.TransferWriteBit | AccessFlags2.ShaderStorageWriteBit,
                    PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
                api.CmdBindPipeline(command, PipelineBindPoint.Compute, pipeline);
                var bound = set;
                api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &bound, 0, null);
                uint* parameters = stackalloc uint[3] { (uint)source.Length, 0, worldInput[0].InitialPopulatedSize };
                for (uint phase = 0; phase < 3; phase++)
                {
                    parameters[1] = phase;
                    api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 12, parameters);
                    api.CmdDispatch(command, phase == 1 ? 1 : (parameters[0] + 63) / 64, 1, 1);
                    if (phase < 2) Barrier(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                        PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
                }
                if (timestamps.Handle != 0) api.CmdWriteTimestamp2(command, PipelineStageFlags2.ComputeShaderBit, timestamps, 1);
                Barrier(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                    PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);
                Copy(output!, readback!, (ulong)source.Length * 64);
                Barrier(PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.HostBit, AccessFlags2.HostReadBit);
                VulkanResult.Check(api.EndCommandBuffer(command), "particle simulation command end");
                var completed = fence;
                VulkanResult.Check(api.ResetFences(context.Device, 1, &completed), "particle simulation fence reset");
                var commandInfo = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = command };
                var signal = new SemaphoreSubmitInfo { SType = StructureType.SemaphoreSubmitInfo, Semaphore = timeline,
                    Value = timelineValue, StageMask = PipelineStageFlags2.AllCommandsBit };
                var submit = new SubmitInfo2 { SType = StructureType.SubmitInfo2, CommandBufferInfoCount = 1, PCommandBufferInfos = &commandInfo,
                    SignalSemaphoreInfoCount = 1, PSignalSemaphoreInfos = &signal };
                lock (context.QueueLock) VulkanResult.Check(api.QueueSubmit2(context.ParticleComputeQueue, 1, &submit, fence), "particle simulation submit");
                tableUploaded = true;
                inFlight = true;
                this.count = source.Length;
                Interlocked.Add(ref dispatches, 3); Interlocked.Add(ref steps, source.Length);
                submitMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                return true;
            }
        }

        internal bool TryDiscard(out VulkanBuffer? buffer)
        {
            buffer = null;
            long start = Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (disposed || !inFlight) return false;
                var api = context.Api;
                var status = api.GetFenceStatus(context.Device, fence);
                if (status == Result.NotReady) return false;
                VulkanResult.Check(status, "particle simulation result fence");
                inFlight = false;
                if (timestamps.Handle != 0)
                {
                    ulong* ticks = stackalloc ulong[2];
                    if (api.GetQueryPoolResults(context.Device, timestamps, 0, 2, 16, ticks, 8, QueryResultFlags.Result64Bit) == Result.Success)
                    {
                        ulong mask = timestampBits >= 64 ? ulong.MaxValue : (1UL << (int)timestampBits) - 1;
                        gpuMilliseconds += ((ticks[1] - ticks[0]) & mask) * (double)timestampPeriod / 1000000;
                    }
                }
                submitMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                // This path only discards abandoned work. Successful consumers
                // transfer output ownership through Complete instead.
                buffer = output; output = null;
                return true;
            }
        }

        internal VulkanBuffer Complete(Span<ParticleSimulationOutput> destination)
        {
            long start = Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (disposed || !inFlight || destination.Length < count)
                    throw new InvalidOperationException("Particle trajectory completion has no matching submission or CPU facade.");
                var completed = fence;
                // Accepted supported work is authoritative: do not compute it a
                // second time on the CPU while its immutable result is pending.
                VulkanResult.Check(context.Api.WaitForFences(context.Device, 1, &completed, true, ulong.MaxValue), "particle simulation completion fence");
                inFlight = false;
                new ReadOnlySpan<ParticleSimulationOutput>((void*)readback!.Mapped, count).CopyTo(destination);
                if (timestamps.Handle != 0)
                {
                    ulong* ticks = stackalloc ulong[2];
                    if (context.Api.GetQueryPoolResults(context.Device, timestamps, 0, 2, 16, ticks, 8, QueryResultFlags.Result64Bit) == Result.Success)
                    {
                        ulong mask = timestampBits >= 64 ? ulong.MaxValue : (1UL << (int)timestampBits) - 1;
                        gpuMilliseconds += ((ticks[1] - ticks[0]) & mask) * (double)timestampPeriod / 1000000;
                    }
                }
                submitMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var result = output; output = null;
                return result;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                if (inFlight)
                {
                    var completed = fence;
                    var result = context.Api.WaitForFences(context.Device, 1, &completed, true, ulong.MaxValue);
                    // Device loss releases pending-use lifetime restrictions, but does
                    // not destroy child objects. Other failures leave the work pending.
                    if (result == Result.ErrorDeviceLost) VulkanResult.DeviceLost = true;
                    else VulkanResult.Check(result, "particle simulation teardown fence");
                    inFlight = false;
                }
                disposed = true;
                if (timestamps.Handle != 0) context.Api.DestroyQueryPool(context.Device, timestamps, null);
                if (descriptors.Handle != 0) context.Api.DestroyDescriptorPool(context.Device, descriptors, null);
                if (fence.Handle != 0) context.Api.DestroyFence(context.Device, fence, null);
                if (pool.Handle != 0) context.Api.DestroyCommandPool(context.Device, pool, null);
                input?.Dispose(); output?.Dispose(); table?.Dispose(); world?.Dispose(); boxes?.Dispose(); liquid?.Dispose(); liquidUpload?.Dispose();
                queries?.Dispose(); readback?.Dispose(); inputUpload?.Dispose(); worldUpload?.Dispose(); boxUpload?.Dispose();
                tableUpload?.Dispose(); configurationUpload?.Dispose(); stateUpload?.Dispose();
            }
        }
    }
}
