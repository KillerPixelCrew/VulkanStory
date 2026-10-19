using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private ChunkMeshingRunner? chunkMeshing;
    private bool chunkMeshingUnavailable;
    private int chunkPublishProgram;
    private long chunkMeshingFacesPublished;
    internal bool ChunkMeshingAvailable => chunkMeshing?.Available == true;
    internal long ChunkMeshingJobsCompleted => chunkMeshing?.JobsCompleted ?? 0;
    /// <summary>Generated faces whose real pool publication commands were recorded (not GPU execution proof).</summary>
    internal long ChunkMeshingFacesPublished => Interlocked.Read(ref chunkMeshingFacesPublished);
    internal IChunkMeshBatch? LeaseChunkMeshing() => chunkMeshing?.Lease();

    internal void PrepareChunkMeshing(bool enabled)
    {
        if (!enabled || chunkMeshing != null || chunkMeshingUnavailable || _disposed) return;
        int id = 0;
        try
        {
            var compiled = _shaderCompiler.CompileCompute(ReadWorldShader("terrain-meshing/faces.comp"), "terrain-meshing");
            if (!compiled.Success) throw new InvalidOperationException(compiled.Error);
            id = CreateComputeProgram(new ComputeProgramDescription
            {
                Name = "terrain-meshing", Spirv = compiled.Spirv, PushConstantBytes = 8,
                Slots = [new(0, ComputeSlotKind.Buffer), new(1, ComputeSlotKind.Buffer), new(2, ComputeSlotKind.Buffer),
                    new(3, ComputeSlotKind.Buffer), new(4, ComputeSlotKind.Buffer)], LocalSizeX = 64, LocalSizeY = 1,
            });
            chunkPublishProgram = CreateComputeProgram(ReadWorldShader("terrain-meshing/publish.comp"), "terrain-mesh-publish",
                [new(0, ComputeSlotKind.Buffer), new(1, ComputeSlotKind.Buffer), new(2, ComputeSlotKind.Buffer)], 32, 64, 1);
            var program = _compute.Get(id)!;
            chunkMeshing = new ChunkMeshingRunner(_context, program, _compute.PipelineFor(program, []));
        }
        catch (Exception error)
        {
            if (id != 0) _compute.Remove(id)?.Dispose();
            if (chunkPublishProgram != 0) { _compute.Remove(chunkPublishProgram)?.Dispose(); chunkPublishProgram = 0; }
            chunkMeshingUnavailable = true;
            RenderLogger?.Warning("VulkanStory: GPU chunk meshing unavailable: {0}", error.Message);
        }
    }

    /// <summary>Writes generated geometry directly into the original SSBO pool's reserved range.</summary>
    internal void PublishChunkMeshing(IChunkMeshBatch batch, ChunkMeshRange range, int meshId,
        int vertexPosition, float dx, float dy, float dz)
    {
        if (batch is not ChunkMeshingRunner.Batch job || !ReferenceEquals(job.Owner, chunkMeshing) ||
            !_meshes.IsSsbo(meshId) || vertexPosition < 0 || vertexPosition % 4 != 0)
            throw new InvalidOperationException("Chunk mesh publication lost its SSBO pool owner.");
        job.RequireRange(range);
        ulong first = (ulong)vertexPosition / 4, count = (uint)range.FaceCount;
        var faces = _meshes.GpuWriteRange(meshId, MeshManager.BufferXyz, first * 64, count * 64);
        var colors = _meshes.GpuWriteRange(meshId, MeshManager.BufferRgba, first * 16, count * 16);
        InvalidateTerrainGeometry(meshId);
        CommandBuffer command = Commands;
        _targets.EndRendering(command);
        ComputeProgram program = _compute.Get(chunkPublishProgram)!;
        DescriptorSet set = _computeArenas[CurrentFrameSlot].GetMixed(program.SetLayout, [], 0,
            [new DescriptorBufferInfo(job.Output.Handle, 0, job.Output.Size),
             new DescriptorBufferInfo(faces.Handle, 0, faces.Size), new DescriptorBufferInfo(colors.Handle, 0, colors.Size)]);
        // Pool buffers can have reads already recorded in this very command buffer.
        // Preserve their placement and order the range update after those readers.
        BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[2];
        for (int i = 0; i < 2; i++) barriers[i] = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2, Buffer = i == 0 ? faces.Handle : colors.Handle,
            Offset = first * (i == 0 ? 64UL : 16UL), Size = count * (i == 0 ? 64UL : 16UL),
            SrcStageMask = PipelineStageFlags2.AllCommandsBit, SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit, DstAccessMask = AccessFlags2.ShaderStorageWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 2, PBufferMemoryBarriers = barriers };
        _context.Api.CmdPipelineBarrier2(command, &dependency);
        _uploads.NoteUse(command, faces);
        _uploads.NoteUse(command, colors);
        // Each recorded copy holds a lease until this frame's submitted timeline
        // has retired. Disposing the game's count-only MeshData cannot recycle it.
        job.Retain();
        try { _frames.DeferDeletion(new ChunkMeshGpuRead(job)); }
        catch { job.Dispose(); throw; }
        _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, []));
        _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
        uint* push = stackalloc uint[8] { (uint)range.FirstFace, (uint)range.FaceCount, (uint)first, 0,
            BitConverter.SingleToUInt32Bits(dx), BitConverter.SingleToUInt32Bits(dy), BitConverter.SingleToUInt32Bits(dz), 0 };
        _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 32, push);
        _context.Api.CmdDispatch(command, ((uint)range.FaceCount + 63) / 64, 1, 1);
        for (int i = 0; i < 2; i++)
        {
            barriers[i].SrcStageMask = PipelineStageFlags2.ComputeShaderBit;
            barriers[i].SrcAccessMask = AccessFlags2.ShaderStorageWriteBit;
            // Subsequent CPU-produced chunks can update this very pool through
            // UploadManager's transfer path before any draw reads the range.
            barriers[i].DstStageMask = PipelineStageFlags2.AllCommandsBit;
            barriers[i].DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
        }
        _context.Api.CmdPipelineBarrier2(command, &dependency);
        Interlocked.Add(ref chunkMeshingFacesPublished, range.FaceCount);
    }

    private sealed class ChunkMeshGpuRead(ChunkMeshingRunner.Batch job) : IDisposable
    { public void Dispose() => job.Dispose(); }

    // Called after device idle, before compute programs and mesh owners are destroyed.
    internal void DisposeChunkMeshing() { chunkMeshing?.Dispose(); chunkMeshing = null; }
}
