using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private TerrainNoiseRunner? terrainNoise;
    private bool terrainNoiseUnavailable;
    private string terrainNoiseAvailabilityReason = "not-prepared";
    internal bool TerrainNoiseAvailable => terrainNoise?.Available == true;
    internal string TerrainNoiseAvailabilityReason => TerrainNoiseAvailable ? "available" : terrainNoiseAvailabilityReason;
    internal long TerrainNoiseDispatchesRecorded => terrainNoise?.Dispatches ?? 0;
    internal ITerrainNoiseBatch? SubmitTerrainNoise(ReadOnlySpan<TerrainNoiseInput> input) => terrainNoise?.Submit(input);

    internal void PrepareTerrainNoise(bool enabled, float[] gradients)
    {
        PrepareTerrainDensity(enabled, gradients);
        if (!enabled || terrainNoise != null || terrainNoiseUnavailable || _disposed) return;
        int id = 0;
        try
        {
            // The raw kernel rounds binary32 using uint operations, including subnormals
            // and signed zero. No optional native float-control capability is required.
            string source = ReadWorldShader("worldgen/terrain-noise.comp").Replace(
                "#include \"terrain-noise-kernel.glsl\"", ReadWorldShader("worldgen/terrain-noise-kernel.glsl"), StringComparison.Ordinal).Replace(
                "#include \"terrain-binary32.glsl\"", ReadWorldShader("worldgen/terrain-binary32.glsl"), StringComparison.Ordinal);
            var compiled = _shaderCompiler.CompileCompute(source, "terrain-noise");
            if (!compiled.Success) throw new InvalidOperationException(compiled.Error);
            id = CreateComputeProgram(new ComputeProgramDescription
            {
                Name = "terrain-noise", Spirv = compiled.Spirv, PushConstantBytes = 4,
                Slots = [new(0, ComputeSlotKind.Buffer), new(1, ComputeSlotKind.Buffer), new(2, ComputeSlotKind.Buffer)],
                LocalSizeX = 64, LocalSizeY = 1,
            });
            var program = _compute.Get(id)!;
            terrainNoise = new TerrainNoiseRunner(_context, program, _compute.PipelineFor(program, []), gradients);
        }
        catch (Exception error)
        {
            if (id != 0) _compute.Remove(id)?.Dispose();
            terrainNoiseUnavailable = true;
            terrainNoiseAvailabilityReason = error.Message;
            RenderLogger?.Warning("VulkanStory: GPU terrain noise unavailable: {0}", error.Message);
        }
    }

    /// <summary>Two bounded host-visible tiles, independent of all frame command/descriptor state.</summary>
    private sealed class TerrainNoiseRunner : IDisposable
    {
        private const int Capacity = 32768;
        private readonly object gate = new();
        private readonly VulkanContext context;
        private readonly ComputeProgram program;
        private readonly Pipeline pipeline;
        private readonly bool density;
        private readonly Tile[] tiles = new Tile[2];
        private VulkanBuffer? gradients;
        private bool disposed;
        private long dispatches;
        internal long Dispatches => Interlocked.Read(ref dispatches);
        internal bool Available => !Volatile.Read(ref disposed);

        private sealed class Tile
        {
            internal VulkanBuffer? Input, Output;
            internal CommandPool Pool;
            internal CommandBuffer Command;
            internal DescriptorPool Descriptors;
            internal DescriptorSet Set;
            internal Fence Fence;
            internal bool Leased, InFlight;
            internal int Count;
        }
        private sealed class Batch(TerrainNoiseRunner owner, Tile tile) : ITerrainNoiseBatch, ITerrainDensityBatch
        {
            private bool released;
            public bool Complete(Span<uint> results) => Complete(MemoryMarshal.Cast<uint, float>(results));
            public bool Complete(Span<float> results)
            {
                lock (owner.gate)
                {
                    if (released || owner.disposed || results.Length < tile.Count) return false;
                    owner.Wait(tile, false);
                    // Shader SSBO stores uint IEEE encodings; transfer bytes without
                    // a native floating-point conversion that could flush subnormals.
                    new ReadOnlySpan<byte>((void*)tile.Output!.Mapped, tile.Count * sizeof(float))
                        .CopyTo(MemoryMarshal.AsBytes(results[..tile.Count]));
                    // The caller now owns a full CPU snapshot. This batch must never
                    // touch the tile again, even if it is reused before Dispose.
                    released = true;
                    tile.Leased = false;
                    return true;
                }
            }
            public void Dispose()
            {
                lock (owner.gate)
                {
                    if (released) return;
                    if (!owner.disposed) owner.Wait(tile, true);
                    released = true; tile.Leased = false;
                }
            }
        }

        internal TerrainNoiseRunner(VulkanContext context, ComputeProgram program, Pipeline pipeline, float[] table, bool density = false)
        {
            this.context = context; this.program = program; this.pipeline = pipeline; this.density = density;
            if (table.Length != 1024) throw new ArgumentException("Stock terrain gradient table changed.");
            try
            {
                var memory = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                gradients = new VulkanBuffer(context, 4096, BufferUsageFlags.StorageBufferBit, memory, MemoryPoolClass.DeviceBuffers);
                MemoryMarshal.AsBytes(table.AsSpan()).CopyTo(new Span<byte>((void*)gradients.Mapped, 4096));
                for (int index = 0; index < tiles.Length; index++)
                {
                    var tile = tiles[index] = new Tile();
                    tile.Input = new VulkanBuffer(context, density ? 2 * 1024 * 1024UL : Capacity * 48UL, BufferUsageFlags.StorageBufferBit, memory, MemoryPoolClass.DeviceBuffers);
                    tile.Output = new VulkanBuffer(context, density ? 65536 * 4UL : Capacity * 4UL, BufferUsageFlags.StorageBufferBit, memory, MemoryPoolClass.DeviceBuffers);
                    var poolInfo = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo,
                        QueueFamilyIndex = context.ParticleComputeQueueFamily, Flags = CommandPoolCreateFlags.ResetCommandBufferBit };
                    CommandPool pool;
                    VulkanResult.Check(context.Api.CreateCommandPool(context.Device, &poolInfo, null, &pool), "terrain command pool"); tile.Pool = pool;
                    var allocate = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo,
                        CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
                    CommandBuffer command;
                    VulkanResult.Check(context.Api.AllocateCommandBuffers(context.Device, &allocate, &command), "terrain command buffer"); tile.Command = command;
                    var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
                    Fence fence;
                    VulkanResult.Check(context.Api.CreateFence(context.Device, &fenceInfo, null, &fence), "terrain fence"); tile.Fence = fence;
                    var size = new DescriptorPoolSize(DescriptorType.StorageBuffer, 3);
                    var descriptorInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo,
                        MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &size };
                    DescriptorPool descriptors;
                    VulkanResult.Check(context.Api.CreateDescriptorPool(context.Device, &descriptorInfo, null, &descriptors), "terrain descriptors"); tile.Descriptors = descriptors;
                    var layout = program.SetLayout;
                    var setInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo,
                        DescriptorPool = descriptors, DescriptorSetCount = 1, PSetLayouts = &layout };
                    DescriptorSet set;
                    VulkanResult.Check(context.Api.AllocateDescriptorSets(context.Device, &setInfo, &set), "terrain descriptor set"); tile.Set = set;
                    DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[3]
                    { new(tile.Input.Handle, 0, tile.Input.Size), new(tile.Output.Handle, 0, tile.Output.Size), new(gradients.Handle, 0, gradients.Size) };
                    WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[3];
                    for (uint binding = 0; binding < 3; binding++) writes[binding] = new WriteDescriptorSet
                    { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = binding, DescriptorCount = 1,
                        DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = buffers + binding };
                    context.Api.UpdateDescriptorSets(context.Device, 3, writes, 0, null);
                }
            }
            catch { Dispose(); throw; }
        }

        internal ITerrainNoiseBatch? Submit(ReadOnlySpan<TerrainNoiseInput> input)
        {
            if (input.Length == 0 || input.Length > Capacity) return null;
            return SubmitBytes(MemoryMarshal.AsBytes(input), input.Length, (uint)((input.Length + 63) / 64));
        }

        internal ITerrainDensityBatch? SubmitDensity(ReadOnlySpan<TerrainDensityColumn> columns,
            ReadOnlySpan<TerrainDensityOctave> octaves, ReadOnlySpan<TerrainDensityRequest> requests)
        {
            if (!density || columns.Length is < 1 or > 32 || octaves.Length > 1024 || requests.Length is < 1 or > 65536) return null;
            foreach (TerrainDensityColumn column in columns)
                if (column.OctaveCount > 32 || (ulong)column.OctaveStart + column.OctaveCount > (ulong)octaves.Length ||
                    (ulong)column.RequestStart + column.RequestCount > (ulong)requests.Length) return null;
            int columnBytes = columns.Length * 16, octaveBytes = octaves.Length * 64;
            byte[] packed = new byte[16 + columnBytes + octaveBytes + requests.Length * 16];
            Span<uint> header = MemoryMarshal.Cast<byte, uint>(packed.AsSpan(0, 16));
            header[0] = (uint)columns.Length;
            header[1] = (uint)((16 + columnBytes) / 4);
            header[2] = (uint)((16 + columnBytes + octaveBytes) / 4);
            MemoryMarshal.AsBytes(columns).CopyTo(packed.AsSpan(16));
            MemoryMarshal.AsBytes(octaves).CopyTo(packed.AsSpan(16 + columnBytes));
            MemoryMarshal.AsBytes(requests).CopyTo(packed.AsSpan(16 + columnBytes + octaveBytes));
            return SubmitBytes(packed, requests.Length, (uint)((columns.Length + 63) / 64));
        }

        private Batch? SubmitBytes(ReadOnlySpan<byte> input, int resultCount, uint groups)
        {
            lock (gate)
            {
                if (disposed) return null;
                Tile? tile = tiles.FirstOrDefault(t => !t.Leased);
                if (tile == null) return null;
                // No queue of CPU requests: full tiles retain the original producer immediately.
                tile.Leased = true; tile.Count = resultCount;
                try
                {
                    var api = context.Api;
                    if ((ulong)input.Length > tile.Input!.Size || (ulong)resultCount * 4 > tile.Output!.Size)
                    { tile.Leased = false; return null; }
                    input.CopyTo(new Span<byte>((void*)tile.Input.Mapped, input.Length));
                    VulkanResult.Check(api.ResetCommandPool(context.Device, tile.Pool, 0), "terrain command reset");
                    var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
                    VulkanResult.Check(api.BeginCommandBuffer(tile.Command, &begin), "terrain command begin");
                    api.CmdBindPipeline(tile.Command, PipelineBindPoint.Compute, pipeline);
                    var set = tile.Set;
                    api.CmdBindDescriptorSets(tile.Command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
                    uint count = (uint)resultCount;
                    api.CmdPushConstants(tile.Command, program.Layout, ShaderStageFlags.ComputeBit, 0, 4, &count);
                    api.CmdDispatch(tile.Command, groups, 1, 1);
                    var barrier = new BufferMemoryBarrier2 { SType = StructureType.BufferMemoryBarrier2,
                        SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                        DstStageMask = PipelineStageFlags2.HostBit, DstAccessMask = AccessFlags2.HostReadBit,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Buffer = tile.Output!.Handle, Size = count * 4UL };
                    var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
                    api.CmdPipelineBarrier2(tile.Command, &dependency);
                    VulkanResult.Check(api.EndCommandBuffer(tile.Command), "terrain command end");
                    var fence = tile.Fence;
                    VulkanResult.Check(api.ResetFences(context.Device, 1, &fence), "terrain fence reset");
                    var command = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = tile.Command };
                    var submit = new SubmitInfo2 { SType = StructureType.SubmitInfo2, CommandBufferInfoCount = 1, PCommandBufferInfos = &command };
                    lock (context.QueueLock) VulkanResult.Check(api.QueueSubmit2(context.ParticleComputeQueue, 1, &submit, fence), "terrain submit");
                    tile.InFlight = true;
                    Interlocked.Increment(ref dispatches);
                    return new Batch(this, tile);
                }
                catch
                {
                    // Failure after a successful submit (including batch allocation)
                    // still owns the fence. Drain it before making the tile reusable.
                    try { Wait(tile, true); }
                    finally { if (!tile.InFlight) tile.Leased = false; }
                    throw;
                }
            }
        }

        private void Wait(Tile tile, bool teardown)
        {
            if (!tile.InFlight) return;
            var fence = tile.Fence;
            var result = context.Api.WaitForFences(context.Device, 1, &fence, true, ulong.MaxValue);
            if (teardown && result == Result.ErrorDeviceLost) VulkanResult.DeviceLost = true;
            else VulkanResult.Check(result, "terrain result fence");
            tile.InFlight = false;
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                foreach (Tile? tile in tiles) if (tile != null) Wait(tile, true);
                disposed = true;
                foreach (Tile? tile in tiles)
                {
                    if (tile == null) continue;
                    if (tile.Descriptors.Handle != 0) context.Api.DestroyDescriptorPool(context.Device, tile.Descriptors, null);
                    if (tile.Fence.Handle != 0) context.Api.DestroyFence(context.Device, tile.Fence, null);
                    if (tile.Pool.Handle != 0) context.Api.DestroyCommandPool(context.Device, tile.Pool, null);
                    tile.Input?.Dispose(); tile.Output?.Dispose();
                }
                gradients?.Dispose();
            }
        }
    }
}
