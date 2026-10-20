using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    /// <summary>Two whole-chunk leases, with no pending CPU request queue.</summary>
    private sealed class ChunkMeshingRunner : IDisposable
    {
        private const int Capacity = 6 * 32 * 32 * 32, BinCapacity = 128;
        private readonly object gate = new();
        private readonly VulkanContext context;
        private readonly ComputeProgram program;
        private readonly Pipeline pipeline;
        private readonly Tile?[] tiles = new Tile?[2];
        private bool disposed;
        private long jobsCompleted;
        internal bool Available => !Volatile.Read(ref disposed);
        internal long JobsCompleted => Interlocked.Read(ref jobsCompleted);

        internal sealed class Tile
        {
            internal VulkanBuffer? Voxels, Input, Neighbours, Bins, Order, Output;
            internal bool CachedOrder;
            internal CommandPool Pool;
            internal CommandBuffer Command;
            internal DescriptorPool Descriptors;
            internal DescriptorSet Set;
            internal Fence Fence;
            internal bool Leased, InFlight;
            internal Batch? Lease;
        }
        internal sealed class Batch : IChunkMeshBatch
        {
            internal readonly ChunkMeshingRunner Owner;
            private readonly Tile tile;
            private int references = 1;
            private int gpuReads;
            private readonly long started = Stopwatch.GetTimestamp();
            internal readonly int X, Y, Z, ChunkIdentity, Generation;
            internal readonly long Revision;
            internal readonly bool EdgeOnly;
            private ChunkMeshRange[]? ranges;
            internal VulkanBuffer Output => tile.Output!;
            public int FaceCapacity => Capacity;
            internal Batch(ChunkMeshingRunner owner, Tile tile, int x, int y, int z, int chunkIdentity,
                long revision, bool edgeOnly, int generation)
            { Owner = owner; this.tile = tile; X = x; Y = y; Z = z; ChunkIdentity = chunkIdentity;
                Revision = revision; EdgeOnly = edgeOnly; Generation = generation; }
            internal object Describe() => new { X, Y, Z, ChunkIdentity, Revision, EdgeOnly, Generation,
                leaseIdentity = RuntimeHelpers.GetHashCode(this),
                ageMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                references, gpuReads, producerOrPayloadReferences = references - gpuReads,
                generated = ranges != null, tile.InFlight };
            internal void RetainGpuRead() { lock (Owner.gate) { Retain(); gpuReads++; } }
            internal void ReleaseGpuRead() { lock (Owner.gate) { gpuReads--; Dispose(); } }
            public void Retain()
            {
                lock (Owner.gate)
                {
                    if (references == 0 || Owner.disposed) throw new ObjectDisposedException(nameof(Batch));
                    references++;
                }
            }
            public ChunkMeshRange[] Generate(ReadOnlySpan<ChunkMeshVoxel> voxels, ReadOnlySpan<ChunkMeshFace> faces,
                ReadOnlySpan<ChunkMeshAoNeighbour> neighbours, int binCount)
            {
                lock (Owner.gate)
                {
                    if (references == 0 || Owner.disposed || ranges != null) throw new InvalidOperationException("Invalid chunk mesh lease.");
                    ranges = Owner.Generate(tile, voxels, faces, neighbours, binCount);
                    Interlocked.Increment(ref Owner.jobsCompleted);
                    return ranges;
                }
            }
            internal void RequireRange(ChunkMeshRange range)
            {
                lock (Owner.gate)
                    if (references == 0 || Owner.disposed || ranges == null || !ranges.Contains(range))
                        throw new InvalidOperationException("Uncompleted chunk mesh range.");
            }
            public void Dispose()
            {
                lock (Owner.gate)
                {
                    if (references == 0) return;
                    if (--references != 0) return;
                    if (!Owner.disposed) Owner.Wait(tile);
                    tile.Leased = false;
                    tile.Lease = null;
                }
            }
        }
        internal ChunkMeshingRunner(VulkanContext context, ComputeProgram program, Pipeline pipeline)
        { this.context = context; this.program = program; this.pipeline = pipeline; }
        internal IChunkMeshBatch? Lease(int x, int y, int z, int chunkIdentity, long revision,
            bool edgeOnly, int generation, out string? refusal, out object? owners)
        {
            lock (gate)
            {
                owners = null; refusal = null;
                if (disposed) { refusal = "backend-disposed"; return null; }
                for (int i = 0; i < tiles.Length; i++)
                {
                    if (tiles[i]?.Leased == true) continue;
                    Tile tile = tiles[i] ??= Allocate();
                    tile.Leased = true;
                    return tile.Lease = new Batch(this, tile, x, y, z, chunkIdentity, revision, edgeOnly, generation);
                }
                refusal = "lease-capacity-saturated";
                owners = tiles.Select((tile, slot) => new { slot, owner = tile?.Lease?.Describe() }).ToArray();
                return null;
            }
        }
        private Tile Allocate()
        {
            var tile = new Tile();
            try
            {
                var memory = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                tile.Voxels = new VulkanBuffer(context, 39304UL * 32, BufferUsageFlags.StorageBufferBit, memory, MemoryPoolClass.Staging);
                tile.Input = new VulkanBuffer(context, Capacity * 64UL, BufferUsageFlags.StorageBufferBit, memory, MemoryPoolClass.Staging);
                tile.Neighbours = new VulkanBuffer(context, 54UL * 20, BufferUsageFlags.StorageBufferBit, memory, MemoryPoolClass.Staging);
                tile.Bins = new VulkanBuffer(context, BinCapacity * 12UL, BufferUsageFlags.StorageBufferBit, memory, MemoryPoolClass.Staging);
                try
                {
                    // Dedicated memory makes the whole mapped cache range ours,
                    // including non-coherent atom padding. Keep one rank buffer per lease.
                    tile.Order = new VulkanBuffer(context, Capacity * 4UL, BufferUsageFlags.StorageBufferBit,
                        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit, MemoryPoolClass.Dedicated);
                    tile.CachedOrder = true;
                }
                catch (VulkanMemoryAllocationException error) when
                    (error.Result is Result.ErrorFeatureNotPresent or Result.ErrorOutOfDeviceMemory)
                {
                    tile.Order = new VulkanBuffer(context, Capacity * 4UL, BufferUsageFlags.StorageBufferBit, memory, MemoryPoolClass.Staging);
                }
                // Graphics queue ownership is retained throughout production/publication.
                // No optional async queue capability or queue-family transfers are needed.
                tile.Output = new VulkanBuffer(context, Capacity * 80UL, BufferUsageFlags.StorageBufferBit,
                    MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
                var poolInfo = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo,
                    QueueFamilyIndex = context.GraphicsQueueFamily, Flags = CommandPoolCreateFlags.ResetCommandBufferBit };
                CommandPool pool;
                VulkanResult.Check(context.Api.CreateCommandPool(context.Device, &poolInfo, null, &pool), "chunk mesh pool"); tile.Pool = pool;
                var allocate = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
                CommandBuffer command;
                VulkanResult.Check(context.Api.AllocateCommandBuffers(context.Device, &allocate, &command), "chunk mesh command"); tile.Command = command;
                var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
                Fence fence;
                VulkanResult.Check(context.Api.CreateFence(context.Device, &fenceInfo, null, &fence), "chunk mesh fence"); tile.Fence = fence;
                var size = new DescriptorPoolSize(DescriptorType.StorageBuffer, 6);
                var descriptorInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &size };
                DescriptorPool descriptors;
                VulkanResult.Check(context.Api.CreateDescriptorPool(context.Device, &descriptorInfo, null, &descriptors), "chunk mesh descriptors"); tile.Descriptors = descriptors;
                var layout = program.SetLayout;
                var setInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = descriptors, DescriptorSetCount = 1, PSetLayouts = &layout };
                DescriptorSet set;
                VulkanResult.Check(context.Api.AllocateDescriptorSets(context.Device, &setInfo, &set), "chunk mesh set"); tile.Set = set;
                DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[6]
                { new(tile.Voxels.Handle,0,tile.Voxels.Size),new(tile.Input.Handle,0,tile.Input.Size),
                  new(tile.Neighbours.Handle,0,tile.Neighbours.Size),new(tile.Bins.Handle,0,tile.Bins.Size),new(tile.Output.Handle,0,tile.Output.Size),
                  new(tile.Order.Handle,0,tile.Order.Size) };
                WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[6];
                for (uint i = 0; i < 6; i++) writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet,
                    DstSet = set, DstBinding = i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = buffers + i };
                context.Api.UpdateDescriptorSets(context.Device, 6, writes, 0, null);
                return tile;
            }
            catch { Destroy(tile); throw; }
        }
        private ChunkMeshRange[] Generate(Tile tile, ReadOnlySpan<ChunkMeshVoxel> voxels, ReadOnlySpan<ChunkMeshFace> faces,
            ReadOnlySpan<ChunkMeshAoNeighbour> neighbours, int binCount)
        {
            if (voxels.Length != 39304 || faces.Length > Capacity || neighbours.Length != 54 || binCount > BinCapacity || binCount < 0)
                throw new ArgumentOutOfRangeException(nameof(faces));
            foreach (var face in faces) if (face.Bin >= binCount || face.Side > 5 || face.Index < 1191 || face.Index > 38112)
                throw new ArgumentException("Invalid chunk mesh input.");
            Copy(voxels, tile.Voxels!); Copy(faces, tile.Input!); Copy(neighbours, tile.Neighbours!);
            var bins = new Span<uint>((void*)tile.Bins!.Mapped, BinCapacity * 3); bins.Clear();
            if (faces.Length == 0) return [];
            // Also drain debug poison or host writes left by an aborted rank pass
            // before the GPU overwrites these cache lines.
            OrderCache(tile, invalidate: false);
            Dispatch(tile, faces.Length, 0); Wait(tile);
            OrderCache(tile, invalidate: true);
            // Rank visible faces within their bin in input order. Inputs arrive
            // in the stock producer's emission order, so every bin, including
            // blended passes, keeps the order of the original CPU mesh.
            var order = new Span<uint>((void*)tile.Order!.Mapped, faces.Length);
            for (int i = 0; i < faces.Length; i++)
            {
                uint bin = faces[i].Bin;
                order[i] = order[i] switch { 0 => uint.MaxValue, 1 => bins[(int)bin * 3]++,
                    _ => throw new InvalidOperationException("Invalid GPU chunk face visibility.") };
            }
            var ranges = new List<ChunkMeshRange>(); int first = 0;
            for (int i = 0; i < binCount; i++)
            {
                int count = checked((int)bins[i * 3]);
                if (count > faces.Length - first) throw new InvalidOperationException("GPU chunk mesh count exceeds capacity.");
                bins[i * 3 + 1] = (uint)first;
                if (count > 0) ranges.Add(new(i, first, count));
                first += count;
            }
            OrderCache(tile, invalidate: false);
            Dispatch(tile, faces.Length, 1); Wait(tile);
            return ranges.ToArray();
        }
        private void OrderCache(Tile tile, bool invalidate)
        {
            if (!tile.CachedOrder) return; // The fallback is host coherent.
            var range = new MappedMemoryRange { SType = StructureType.MappedMemoryRange,
                Memory = tile.Order!.Allocation.Memory, Offset = 0, Size = Vk.WholeSize };
            VulkanResult.Check(invalidate
                ? context.Api.InvalidateMappedMemoryRanges(context.Device, 1, &range)
                : context.Api.FlushMappedMemoryRanges(context.Device, 1, &range), "chunk mesh order cache");
        }
        private static void Copy<T>(ReadOnlySpan<T> source, VulkanBuffer buffer) where T : unmanaged =>
            MemoryMarshal.AsBytes(source).CopyTo(new Span<byte>((void*)buffer.Mapped, checked((int)buffer.Size)));
        private void Dispatch(Tile tile, int count, uint mode)
        {
            var api = context.Api;
            VulkanResult.Check(api.ResetCommandPool(context.Device, tile.Pool, 0), "chunk mesh reset");
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            VulkanResult.Check(api.BeginCommandBuffer(tile.Command, &begin), "chunk mesh begin");
            api.CmdBindPipeline(tile.Command, PipelineBindPoint.Compute, pipeline);
            var set = tile.Set;
            api.CmdBindDescriptorSets(tile.Command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
            uint* push = stackalloc uint[2] { (uint)count, mode };
            api.CmdPushConstants(tile.Command, program.Layout, ShaderStageFlags.ComputeBit, 0, 8, push);
            api.CmdDispatch(tile.Command, ((uint)count + 63) / 64, 1, 1);
            var barrier = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.HostBit | PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.HostReadBit | AccessFlags2.ShaderStorageReadBit };
            var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
            api.CmdPipelineBarrier2(tile.Command, &dependency);
            VulkanResult.Check(api.EndCommandBuffer(tile.Command), "chunk mesh end");
            var fence = tile.Fence;
            VulkanResult.Check(api.ResetFences(context.Device, 1, &fence), "chunk mesh fence reset");
            var command = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = tile.Command };
            var submit = new SubmitInfo2 { SType = StructureType.SubmitInfo2, CommandBufferInfoCount = 1, PCommandBufferInfos = &command };
            lock (context.QueueLock) VulkanResult.Check(api.QueueSubmit2(context.GraphicsQueue, 1, &submit, fence), "chunk mesh submit");
            tile.InFlight = true;
        }
        private void Wait(Tile tile)
        {
            if (!tile.InFlight) return;
            var fence = tile.Fence;
            VulkanResult.Check(context.Api.WaitForFences(context.Device, 1, &fence, true, ulong.MaxValue), "chunk mesh result");
            tile.InFlight = false;
        }
        private void Destroy(Tile tile)
        {
            if (tile.Descriptors.Handle != 0) context.Api.DestroyDescriptorPool(context.Device, tile.Descriptors, null);
            if (tile.Fence.Handle != 0) context.Api.DestroyFence(context.Device, tile.Fence, null);
            if (tile.Pool.Handle != 0) context.Api.DestroyCommandPool(context.Device, tile.Pool, null);
            tile.Voxels?.Dispose(); tile.Input?.Dispose(); tile.Neighbours?.Dispose(); tile.Bins?.Dispose(); tile.Order?.Dispose(); tile.Output?.Dispose();
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                foreach (Tile? tile in tiles) if (tile != null) Wait(tile);
                disposed = true;
                foreach (Tile? tile in tiles) if (tile != null) Destroy(tile);
            }
        }
    }
}
