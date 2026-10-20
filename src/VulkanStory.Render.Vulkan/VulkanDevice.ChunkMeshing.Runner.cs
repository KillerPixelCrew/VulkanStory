using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    /// <summary>Two generation workspaces and separately byte-bounded retained geometry, with no CPU request queue.</summary>
    private sealed class ChunkMeshingRunner : IDisposable
    {
        private const int Capacity = 6 * 32 * 32 * 32, BinCapacity = 128;
        private readonly object gate = new();
        private readonly VulkanContext context;
        private readonly ComputeProgram program;
        private readonly Pipeline pipeline;
        private readonly Tile?[] tiles = new Tile?[2];
        // Separate from the two immutable snapshot/generation workspaces. VMA
        // suballocates these exact-sized, device-local payload buffers.
        private const long PayloadByteLimit = 64L * 1024 * 1024;
        private readonly HashSet<VulkanBuffer> payloadStorage = new();
        private long payloadStorageBytes, payloadsDetached;
        private bool disposed;
        private long jobsCompleted;
        internal bool Available => !Volatile.Read(ref disposed);
        internal long JobsCompleted => Interlocked.Read(ref jobsCompleted);
        internal long PayloadStorageBytes => Interlocked.Read(ref payloadStorageBytes);
        internal long PayloadsDetached => Interlocked.Read(ref payloadsDetached);

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
            private Tile? tile;
            private VulkanBuffer? storage;
            private int references = 1;
            private int gpuReads;
            private readonly long started = Stopwatch.GetTimestamp();
            internal readonly int X, Y, Z, ChunkIdentity, Generation;
            internal readonly long Revision;
            internal readonly bool EdgeOnly;
            private ChunkMeshRange[]? ranges;
            private int stage, faceCount, binCount;
            private VulkanBuffer? readback;
            internal bool ProbeRequested;
            internal uint[]? ProbeWords;
            internal VulkanBuffer Output => storage ?? tile!.Output!;
            public int FaceCapacity => Capacity;
            internal Batch(ChunkMeshingRunner owner, Tile tile, int x, int y, int z, int chunkIdentity,
                long revision, bool edgeOnly, int generation)
            { Owner = owner; this.tile = tile; X = x; Y = y; Z = z; ChunkIdentity = chunkIdentity;
                Revision = revision; EdgeOnly = edgeOnly; Generation = generation; }
            internal object Describe() => new { X, Y, Z, ChunkIdentity, Revision, EdgeOnly, Generation,
                leaseIdentity = RuntimeHelpers.GetHashCode(this),
                ageMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                references, gpuReads, producerOrPayloadReferences = references - gpuReads,
                generated = ranges != null, detached = storage != null, InFlight = tile?.InFlight ?? false };
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
            public void Submit(ReadOnlySpan<ChunkMeshVoxel> voxels, ReadOnlySpan<ChunkMeshFace> faces,
                ReadOnlySpan<ChunkMeshAoNeighbour> neighbours, int binCount)
            {
                lock (Owner.gate)
                {
                    if (references == 0 || Owner.disposed || stage != 0) throw new InvalidOperationException("Invalid chunk mesh lease.");
                    Owner.Submit(tile!, voxels, faces, neighbours, binCount);
                    faceCount = faces.Length; this.binCount = binCount; stage = 1;
                }
            }
            public bool TryComplete(out ChunkMeshRange[] result)
            {
                lock (Owner.gate)
                {
                    result = [];
                    if (references == 0 || Owner.disposed || stage == 0) throw new InvalidOperationException("Invalid chunk mesh lease.");
                    if (stage == 4) { result = ranges!; return true; }
                    Tile workspace = tile!;
                    if (!Owner.Ready(workspace)) return false;
                    if (stage == 1)
                    {
                        ranges = Owner.Pack(workspace, faceCount, binCount, this);
                        stage = 2;
                        return false;
                    }
                    if (stage == 2)
                    {
                        Owner.BindOutput(workspace, workspace.Output!);
                        if (ProbeRequested && ranges!.Length != 0)
                        {
                            // The copy is another polled stage, never a fence wait.
                            try
                            {
                                readback = Owner.SubmitProbe(workspace, Output, ranges.Sum(r => r.FaceCount));
                                stage = 3; return false;
                            }
                            catch { ProbeWords = null; }
                        }
                        else if (ProbeRequested) ProbeWords = [];
                    }
                    if (stage == 3)
                    {
                        ProbeWords = new ReadOnlySpan<uint>((void*)readback!.Mapped, checked((int)readback.Size / 4)).ToArray();
                        readback.Dispose(); readback = null;
                    }
                    stage = 4;
                    if (storage != null || ranges!.Length == 0) ReleaseTile();
                    Interlocked.Add(ref Owner.payloadsDetached, storage != null ? ranges!.Length : 0);
                    Interlocked.Increment(ref Owner.jobsCompleted);
                    result = ranges!; return true;
                }
            }
            internal void RequireRange(ChunkMeshRange range)
            {
                lock (Owner.gate)
                    if (references == 0 || Owner.disposed || stage != 4 || ranges == null || !ranges.Contains(range))
                        throw new InvalidOperationException("Uncompleted chunk mesh range.");
            }
            public void Dispose()
            {
                lock (Owner.gate)
                {
                    if (references == 0) return;
                    if (--references != 0) return;
                    // Cancellation never blocks an unload or producer. The tile
                    // owns abandoned resources until a later status poll retires them.
                    if (!Owner.disposed && tile != null && !Owner.Ready(tile)) return;
                    ReleaseResources();
                }
            }
            internal void Reap()
            {
                if (references == 0 && tile != null && Owner.Ready(tile)) ReleaseResources();
            }
            internal void DeviceDrained() { readback?.Dispose(); readback = null; }
            private void ReleaseResources()
            {
                if (!Owner.disposed && storage != null) Owner.ReleaseStorage(storage);
                storage = null; readback?.Dispose(); readback = null;
                ReleaseTile();
            }
            private void ReleaseTile()
            {
                if (tile == null) return;
                // Its descriptor may still name payload storage after cancellation.
                if (!Owner.disposed) Owner.BindOutput(tile, tile.Output!);
                tile.Leased = false; tile.Lease = null; tile = null;
            }
            internal void OwnStorage(VulkanBuffer buffer) => storage = buffer;
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
                    tiles[i]?.Lease?.Reap();
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
                tile.Output = new VulkanBuffer(context, Capacity * 80UL, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
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
        private void Submit(Tile tile, ReadOnlySpan<ChunkMeshVoxel> voxels, ReadOnlySpan<ChunkMeshFace> faces,
            ReadOnlySpan<ChunkMeshAoNeighbour> neighbours, int binCount)
        {
            if (voxels.Length != 39304 || faces.Length > Capacity || neighbours.Length != 54 || binCount > BinCapacity || binCount < 0)
                throw new ArgumentOutOfRangeException(nameof(faces));
            foreach (var face in faces) if (face.Bin >= binCount || face.Side > 5 || face.Index < 1191 || face.Index > 38112)
                throw new ArgumentException("Invalid chunk mesh input.");
            Copy(voxels, tile.Voxels!); Copy(faces, tile.Input!); Copy(neighbours, tile.Neighbours!);
            var bins = new Span<uint>((void*)tile.Bins!.Mapped, BinCapacity * 3); bins.Clear();
            if (faces.Length == 0) return;
            // Also drain debug poison or host writes left by an aborted rank pass
            // before the GPU overwrites these cache lines.
            OrderCache(tile, invalidate: false);
            Dispatch(tile, faces.Length, 0);
        }
        private ChunkMeshRange[] Pack(Tile tile, int faceCount, int binCount, Batch batch)
        {
            if (faceCount == 0) return [];
            OrderCache(tile, invalidate: true);
            // Rank visible faces within their bin in input order. Inputs arrive
            // in the stock producer's emission order, so every bin, including
            // blended passes, keeps the order of the original CPU mesh.
            var bins = new Span<uint>((void*)tile.Bins!.Mapped, BinCapacity * 3);
            var faces = new ReadOnlySpan<ChunkMeshFace>((void*)tile.Input!.Mapped, faceCount);
            var order = new Span<uint>((void*)tile.Order!.Mapped, faceCount);
            for (int i = 0; i < faceCount; i++)
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
            // The visibility pass already established the exact output size.
            // Generate directly into payload-owned storage instead of copying
            // it after completion: identical offsets/order, no extra submission.
            VulkanBuffer output = tile.Output!;
            long bytes = first * 80L;
            if (bytes > 0 && bytes <= PayloadByteLimit - payloadStorageBytes)
            {
                try
                {
                    output = new VulkanBuffer(context, (ulong)bytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
                        MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
                    payloadStorage.Add(output);
                    Interlocked.Add(ref payloadStorageBytes, bytes);
                    batch.OwnStorage(output);
                }
                catch (VulkanMemoryAllocationException) { output = tile.Output!; }
            }
            // The preceding pass's fence has retired this descriptor set.
            BindOutput(tile, output);
            Dispatch(tile, faceCount, 1);
            return ranges.ToArray();
        }
        private VulkanBuffer SubmitProbe(Tile tile, VulkanBuffer output, int faces)
        {
            var readback = new VulkanBuffer(context, (ulong)faces * 80, BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);
            try
            {
            var api = context.Api;
            VulkanResult.Check(api.ResetCommandPool(context.Device, tile.Pool, 0), "chunk equality reset");
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            VulkanResult.Check(api.BeginCommandBuffer(tile.Command, &begin), "chunk equality begin");
            var barrier = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.TransferBit, DstAccessMask = AccessFlags2.TransferReadBit };
            var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
            api.CmdPipelineBarrier2(tile.Command, &dependency);
            var copy = new BufferCopy(0, 0, readback.Size);
            api.CmdCopyBuffer(tile.Command, output.Handle, readback.Handle, 1, &copy);
            ReadbackManager.RecordHostReadBarrier(context, tile.Command, readback, 0, readback.Size);
            VulkanResult.Check(api.EndCommandBuffer(tile.Command), "chunk equality end");
            var fence = tile.Fence;
            VulkanResult.Check(api.ResetFences(context.Device, 1, &fence), "chunk equality fence");
            var command = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = tile.Command };
            var submit = new SubmitInfo2 { SType = StructureType.SubmitInfo2, CommandBufferInfoCount = 1, PCommandBufferInfos = &command };
            lock (context.QueueLock) VulkanResult.Check(api.QueueSubmit2(context.GraphicsQueue, 1, &submit, fence), "chunk equality submit");
            tile.InFlight = true;
            return readback;
            }
            catch { readback.Dispose(); throw; }
        }
        private void BindOutput(Tile tile, VulkanBuffer output)
        {
            var info = new DescriptorBufferInfo(output.Handle, 0, output.Size);
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = tile.Set,
                DstBinding = 4, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &info };
            context.Api.UpdateDescriptorSets(context.Device, 1, &write, 0, null);
        }
        private void ReleaseStorage(VulkanBuffer buffer)
        {
            if (!payloadStorage.Remove(buffer)) return;
            Interlocked.Add(ref payloadStorageBytes, -checked((long)buffer.Size));
            buffer.Dispose();
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
        private bool Ready(Tile tile)
        {
            if (!tile.InFlight) return true;
            Result status = context.Api.GetFenceStatus(context.Device, tile.Fence);
            if (status == Result.NotReady) return false;
            VulkanResult.Check(status, "chunk mesh status");
            tile.InFlight = false;
            return true;
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
                foreach (Tile? tile in tiles) if (tile != null) { Wait(tile); tile.Lease?.Reap(); }
                disposed = true;
                foreach (Tile? tile in tiles) if (tile != null) { tile.Lease?.DeviceDrained(); Destroy(tile); }
                foreach (VulkanBuffer buffer in payloadStorage) buffer.Dispose();
                payloadStorage.Clear(); Interlocked.Exchange(ref payloadStorageBytes, 0);
            }
        }
    }
}
