using System;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

internal readonly record struct EntityVisibilityInput(TerrainDrawBounds Bounds, TerrainFrustumPlane[] Planes,
    double CameraX, double CameraY, double CameraZ, int RigidProgram = 0);

public sealed unsafe partial class VulkanDevice
{
    private const int EntityBatchCapacity = 2048;
    private const ulong EntityBatchBytes = (64 + EntityBatchCapacity * 12) * 4;
    private sealed class EntityDrawBatch(VulkanBuffer input, ulong inputOffset, VulkanBuffer commands,
        ulong commandOffset, ulong serial, int eye, OcclusionCamera camera, TerrainFrustumPlane[] planes)
    {
        internal readonly VulkanBuffer Input = input, Commands = commands;
        internal readonly ulong InputOffset = inputOffset, CommandOffset = commandOffset, Serial = serial;
        internal readonly int Eye = eye;
        internal readonly OcclusionCamera Camera = camera;
        internal readonly TerrainFrustumPlane[] Planes = planes;
        internal int Count;
    }
    // One recorded indirect-count command over consecutive commands of a batch. Its
    // count word is host-written until submission; a following draw joins only when
    // everything its own command would observe is what the first command observes.
    private sealed class EntityDrawRun
    {
        internal bool Open;
        internal EntityDrawBatch? Batch;
        internal int First;
        internal uint Count, MaxCount;
        internal ulong CountOffset, Serial, Command, Pipeline;
        internal long Order, Scope, DynamicCommands;
        internal EntityRecordSegment? Segment;
        internal EntityGeometryArena? Arena;
        internal NativePassDescription? Pass;
        internal VulkanFramebuffer? Target;
        internal DescriptorSet FrameSet;
        internal uint FrameOffset;
    }
    private VulkanBuffer?[] _entityInputs = Array.Empty<VulkanBuffer?>();
    private ulong[] _entityInputOffsets = Array.Empty<ulong>();
    private EntityDrawBatch? _entityDrawBatch;
    private readonly EntityDrawRun _entityDrawRun = new();
    private uint? _entityMaxDrawIndirectCount;
    // Advanced by every recorded draw, in-scope attachment clear and occlusion query
    // boundary. Scope restarts, dispatches, barriers and transfers change ScopesOpened.
    private long _drawOrder;
    internal EntityVisibilityInput? EntityVisibility { get; set; }
    internal long EntityVisibilityDispatchesRecorded { get; private set; }
    internal long EntityIndirectDrawsRecorded { get; private set; }
    internal long EntityCombinedDrawsRecorded { get; private set; }
    internal long EntityRunsOpened { get; private set; }
    internal long EntityRunBreaks { get; private set; }
    internal long EntityRunBreakContiguity { get; private set; }
    internal long EntityRunBreakOrder { get; private set; }
    internal long EntityRunBreakScope { get; private set; }
    internal long EntityRunBreakDynamicState { get; private set; }
    internal long EntityRunBreakFrameSet { get; private set; }
    internal long EntityRunBreakProgram { get; private set; }
    internal long EntityRunBreakReplaced { get; private set; }

    private enum EntityRunBreak : byte { None, Contiguity, Order, Scope, DynamicState, FrameSet, Program, Replaced }

    private void BreakEntityDrawRun(EntityRunBreak reason)
    {
        var run = _entityDrawRun;
        // Submission and frame end are lifetime boundaries, not failed continuations.
        if (run.Open && run.Serial == _frames.Current.RecordingSerial)
        {
            EntityRunBreaks++;
            switch (reason)
            {
                case EntityRunBreak.Contiguity: EntityRunBreakContiguity++; break;
                case EntityRunBreak.Order: EntityRunBreakOrder++; break;
                case EntityRunBreak.Scope: EntityRunBreakScope++; break;
                case EntityRunBreak.DynamicState: EntityRunBreakDynamicState++; break;
                case EntityRunBreak.FrameSet: EntityRunBreakFrameSet++; break;
                case EntityRunBreak.Program: EntityRunBreakProgram++; break;
                case EntityRunBreak.Replaced: EntityRunBreakReplaced++; break;
            }
        }
        run.Open = false;
    }

    private void AdvanceDrawOrder() => _drawOrder++;

    // Admission before the producer writes its final model matrix. This only
    // certifies retained streams/current warp state; the real draw repeats these
    // checks with its final model shadow. No commands or GPU work are produced.
    internal bool CanPrepareRigidEntityMesh(int meshId, int programId) => _meshes.Get(meshId) is { } mesh &&
        _programs.TryGetValue(programId, out var program) && mesh.EntityFlagsWithoutWind &&
        HasRigidEntityStreams(mesh) && HasRigidEntityShaderState(program, mesh);

    private static bool HasRigidEntityStreams(VulkanMesh mesh)
    {
        if (mesh.EntityLocalBounds == null || mesh.Ssbo) return false;
        bool position = false, flags = false;
        foreach (var attribute in mesh.Layout.Attributes)
        {
            if (attribute.Binding >= mesh.BindingOrder.Count || attribute.Offset != 0) continue;
            foreach (var binding in mesh.Layout.Bindings)
            {
                if (binding.Binding != attribute.Binding || binding.PerInstance) continue;
                position |= attribute.Location == 0 && attribute.Format == Format.R32G32B32Sfloat &&
                    binding.Stride == 12 && mesh.BindingOrder[(int)attribute.Binding] == MeshManager.BufferXyz;
                flags |= attribute.Location == 3 && attribute.Format == Format.R32Sint &&
                    binding.Stride == 4 && mesh.BindingOrder[(int)attribute.Binding] == MeshManager.BufferFlags;
            }
        }
        return position && flags;
    }

    private bool HasRigidEntityShaderState(ShaderProgramResources program, VulkanMesh mesh)
    {
        Span<byte> scalar = stackalloc byte[4];
        if (!CopyWorldUniform(program, "extraZOffset", "float", scalar) ||
            MemoryMarshal.Read<float>(scalar) != 0 ||
            !CopyWorldUniform(program, "dontWarpVertices", "int", scalar)) return false;
        int warp = MemoryMarshal.Read<int>(scalar);
        if (warp != 1)
        {
            // Stock dropped items use mode zero. Wind/water meshes and active
            // perception/global deformation retain the original unculled draw.
            if (warp is not (0 or 2) || !mesh.EntityFlagsWithoutWind ||
                !CopyWorldUniform(program, "addRenderFlags", "int", scalar) ||
                (MemoryMarshal.Read<int>(scalar) & (0xf << 25)) != 0 ||
                !TryReadClientFloatUniform(program.ProgramId, "globalWarpIntensity", out float global) ||
                !float.IsFinite(global) || global != 0 ||
                !TryReadClientFloatUniform(program.ProgramId, "glitchWaviness", out float glitch) ||
                !float.IsFinite(glitch) || glitch > 0.1f ||
                !CopyWorldUniform(program, "perceptionEffectId", "int", scalar)) return false;
            if (MemoryMarshal.Read<int>(scalar) == 2 &&
                (!TryReadClientFloatUniform(program.ProgramId, "perceptionEffectIntensity", out float perception) ||
                    !float.IsFinite(perception) || perception > 0)) return false;
        }
        return true;
    }

    private bool TryRigidEntityBounds(ShaderProgramResources program, VulkanMesh mesh,
        EntityVisibilityInput visibility, out TerrainDrawBounds result)
    {
        result = default;
        if (!HasRigidEntityStreams(mesh) || mesh.EntityLocalBounds is not { } local ||
            !HasRigidEntityShaderState(program, mesh)) return false;
        Span<byte> matrixBytes = stackalloc byte[64];
        if (!CopyWorldUniform(program, "modelMatrix", "mat4", matrixBytes)) return false;
        var model = MemoryMarshal.Cast<byte, float>(matrixBytes);
        foreach (float value in model) if (!float.IsFinite(value)) return false;
        if (model[3] != 0 || model[7] != 0 || model[11] != 0 || model[15] != 1) return false;
        double minX = double.PositiveInfinity, minY = minX, minZ = minX;
        double maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX;
        double margin = 0.001;
        // Transform all eight corners using the exact model shadow consumed by
        // this draw, including item origin, scale, rotation, bobbing and offsets.
        for (int corner = 0; corner < 8; corner++)
        {
            double x = (corner & 1) == 0 ? local.MinX : local.MaxX;
            double y = (corner & 2) == 0 ? local.MinY : local.MaxY;
            double z = (corner & 4) == 0 ? local.MinZ : local.MaxZ;
            double tx = model[0] * x + model[4] * y + model[8] * z + model[12];
            double ty = model[1] * x + model[5] * y + model[9] * z + model[13];
            double tz = model[2] * x + model[6] * y + model[10] * z + model[14];
            double magnitude = 0;
            for (int row = 0; row < 3; row++)
                magnitude = Math.Max(magnitude, Math.Abs(model[row] * x) + Math.Abs(model[row + 4] * y) +
                    Math.Abs(model[row + 8] * z) + Math.Abs(model[row + 12]));
            if (!double.IsFinite(magnitude) || magnitude > 1e6) return false;
            margin = Math.Max(margin, magnitude * 1e-5);
            minX = Math.Min(minX, tx); minY = Math.Min(minY, ty); minZ = Math.Min(minZ, tz);
            maxX = Math.Max(maxX, tx); maxY = Math.Max(maxY, ty); maxZ = Math.Max(maxZ, tz);
        }
        result = new(minX + visibility.CameraX - margin, minY + visibility.CameraY - margin,
            minZ + visibility.CameraZ - margin, maxX + visibility.CameraX + margin,
            maxY + visibility.CameraY + margin, maxZ + visibility.CameraZ + margin, true);
        return true;
    }

    private bool PrepareEntityDraw(ShaderProgramResources program, VulkanMesh mesh,
        out VulkanBuffer? commands, out ulong offset, uint firstIndex = 0, uint firstInstance = 0)
    {
        commands = null; offset = 0;
        if (EntityVisibility is not { } visibility || !CanPrepareTerrainDraws ||
            _nativeTarget?.DepthTextureId != OcclusionDepth.SourceDepthTexture) return false;
        var box = visibility.Bounds;
        if (visibility.RigidProgram != 0)
        {
            if (visibility.RigidProgram != program.ProgramId ||
                !TryRigidEntityBounds(program, mesh, visibility, out box)) return false;
        }
        else if (program.LocationOf("vsComputeDeformation") < 0) return false;
        var camera = OcclusionDepth.GpuCamera!;
        var batch = _entityDrawBatch;
        // Inputs may be appended after recording the dispatch, but never after submitting it.
        // A partial submission starts a fresh serial and a disjoint input/output region.
        if (batch == null || batch.Count == EntityBatchCapacity || batch.Serial != _frames.Current.RecordingSerial ||
            batch.Eye != TerrainEyeIndex || !ReferenceEquals(batch.Camera, camera) ||
            !ReferenceEquals(batch.Planes, visibility.Planes))
        {
            batch = BeginEntityDrawBatch(visibility);
            if (batch == null) return false;
            _entityDrawBatch = batch;
        }
        float* header = (float*)((byte*)batch.Input.Mapped + batch.InputOffset);
        float* record = header + 64 + batch.Count * 12;
        double dx = visibility.CameraX - camera.CameraX, dy = visibility.CameraY - camera.CameraY,
            dz = visibility.CameraZ - camera.CameraZ;
        double grow = Math.Sqrt(dx * dx + dy * dy + dz * dz) + 0.5;
        record[0] = (float)(box.MinX - camera.CameraX - grow);
        record[1] = (float)(box.MinY - camera.CameraY - grow);
        record[2] = (float)(box.MinZ - camera.CameraZ - grow); record[3] = 0;
        record[4] = (float)(box.MaxX - camera.CameraX + grow);
        record[5] = (float)(box.MaxY - camera.CameraY + grow);
        record[6] = (float)(box.MaxZ - camera.CameraZ + grow); record[7] = 0;
        ((uint*)record)[8] = (uint)mesh.IndexCount; ((uint*)record)[9] = firstIndex;
        ((uint*)record)[10] = 6 | (firstInstance << 8); ((uint*)record)[11] = 1;
        commands = batch.Commands;
        offset = batch.CommandOffset + (ulong)batch.Count * 20;
        batch.Count++;
        ((uint*)header)[37] = (uint)batch.Count;
        return true;
    }

    private EntityDrawBatch? BeginEntityDrawBatch(EntityVisibilityInput visibility)
    {
        var program = DrawPreparationProgram();
        if (program == null || Culling.OcclusionProjection.From(OcclusionDepth.GpuCamera!.Projection) == null) return null;
        if (_entityInputs.Length == 0)
        {
            _entityInputs = new VulkanBuffer?[_frames.FramesInFlight];
            _entityInputOffsets = new ulong[_frames.FramesInFlight];
        }
        int slot = CurrentFrameSlot;
        var limits = _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        ulong alignment = Math.Max(4UL, limits.MinStorageBufferOffsetAlignment);
        ulong at = (_entityInputOffsets[slot] + alignment - 1) / alignment * alignment;
        ulong capacity = Math.Min(2UL << 20, limits.MaxStorageBufferRange);
        if (at + EntityBatchBytes > capacity) return null;
        // Also the count source of combined draws: the first record's unused word 3.
        var input = _entityInputs[slot] ??= new VulkanBuffer(_context, capacity,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);
        var indirect = AllocateIndirect(EntityBatchCapacity, out ulong indirectOffset, alignment);
        _entityInputOffsets[slot] = at + EntityBatchBytes;
        FillVisibilityHeader((float*)((byte*)input.Mapped + at), OcclusionDepth, visibility.Planes);
        var depth = _textures.Get(OcclusionDepth.GpuTexture)!;
        var command = Commands;
        using GpuSection section = BeginGpuSection("entity-draws");
        _targets.EndRendering(command);
        _graph.OpenComputePass(new PassSignature
        {
            NameId = _graph.NameId("compute:entity-draws"), Width = EntityBatchCapacity, Height = 1,
            FormatsId = -1, Reads = [OcclusionDepth.GpuTexture], Attachments = [],
        });
        _textures.Require(_barriers, command, depth, 0, depth.MipLevels, ResourceUsage.SampleCompute);
        _barriers.Flush(command);
        var set = _computeArenas[slot].GetMixed(program.SetLayout,
            [new ComputeImageWrite(0, DescriptorType.CombinedImageSampler, depth.ViewOfMips(0, depth.MipLevels),
                _textures.Samplers.Get(ComputeNearest), ImageLayout.ShaderReadOnlyOptimal)], 1,
            [new DescriptorBufferInfo(input.Handle, at, EntityBatchBytes),
                new DescriptorBufferInfo(indirect.Handle, indirectOffset, EntityBatchCapacity * 20)]);
        _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, [0u]));
        _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
        uint* push = stackalloc uint[3] { 0, 0, EntityBatchCapacity };
        _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 12, push);
        _context.Api.CmdDispatch(command, EntityBatchCapacity / 64, 1, 1); _graph.NoteDispatch();
        EntityVisibilityDispatchesRecorded++;
        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2, SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit, DstStageMask = PipelineStageFlags2.DrawIndirectBit,
            DstAccessMask = AccessFlags2.IndirectCommandReadBit, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = indirect.Handle, Offset = indirectOffset,
            Size = EntityBatchCapacity * 20,
        };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
        _context.Api.CmdPipelineBarrier2(command, &dependency);
        return new EntityDrawBatch(input, at, indirect, indirectOffset, _frames.Current.RecordingSerial,
            TerrainEyeIndex, OcclusionDepth.GpuCamera!, visibility.Planes);
    }

    /// <summary>
    /// Records a prepared entity command. A shader-record draw opens a run: one
    /// indirect-count command over this and the following commands of its batch,
    /// counted by a host-written word until this recording is submitted.
    /// </summary>
    private void RecordEntityDraw(CommandBuffer command, VulkanBuffer commands, ulong offset,
        EntityRecordSegment? segment, Pipeline pipeline, EntityGeometryArena? arena)
    {
        var batch = _entityDrawBatch;
        var run = _entityDrawRun;
        BreakEntityDrawRun(EntityRunBreak.Replaced);
        uint maxCount = 0;
        int first = -1;
        ulong countOffset = 0;
        if (segment != null && arena != null && _context.DrawIndirectCountEnabled && batch != null &&
            ReferenceEquals(commands, batch.Commands) && offset >= batch.CommandOffset)
        {
            first = (int)((offset - batch.CommandOffset) / 20);
            _entityMaxDrawIndirectCount ??= _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits.MaxDrawIndirectCount;
            maxCount = (uint)Math.Max(0, Math.Min((long)EntityBatchCapacity - first, _entityMaxDrawIndirectCount.Value));
            countOffset = batch.InputOffset + (ulong)(64 + first * 12 + 3) * 4;
        }
        if (maxCount > 1)
        {
            // The command region spans the whole batch, so maxCount never reads past it.
            *(uint*)((byte*)batch!.Input.Mapped + countOffset) = 1;
            _context.Api.CmdDrawIndexedIndirectCount(command, commands.Handle, offset, batch.Input.Handle,
                countOffset, maxCount, 20);
        }
        else _context.Api.CmdDrawIndexedIndirect(command, commands.Handle, offset, 1, 20);
        EntityIndirectDrawsRecorded++;
        NoteNativeDraw(NativeDrawKind.Indirect);
        if (maxCount <= 1) return;
        // Snapshot after this draw's own bindings and counting, so only later work differs.
        run.Open = true; run.Batch = batch; run.First = first; run.Count = 1; run.MaxCount = maxCount;
        EntityRunsOpened++;
        run.CountOffset = countOffset;
        run.Serial = _frames.Current.RecordingSerial; run.Command = (ulong)command.Handle;
        run.Pipeline = pipeline.Handle; run.Segment = segment; run.Arena = arena;
        run.Order = _drawOrder; run.Scope = _targets.ScopesOpened; run.DynamicCommands = _dynamicStateCommands;
        run.Pass = _nativePass; run.Target = _nativeTarget;
        run.FrameSet = _boundFrameSet; run.FrameOffset = _boundFrameOffset;
    }

    /// <summary>
    /// Appends a prepared shader-record draw to the open run instead of recording a
    /// command. Called after BeginNativeDraw, whose state recording for the original
    /// program does not affect the record variant: per-draw uniforms and push values
    /// live in this draw's row. Any other recorded draw, clear, query boundary,
    /// scope restart, dynamic-state change, frame-set change or submission refuses.
    /// </summary>
    private bool TryCombineEntityDraw(CommandBuffer command, in EntityRecordDraw draw, EntityGeometryArena? arena,
        VulkanBuffer commands, ulong offset)
    {
        var run = _entityDrawRun;
        var batch = run.Batch;
        if (!run.Open) return false;
        // Mesh identity and firstIndex deliberately differ: slot preparation packed
        // every available slice into this arena's common bindings before the pass.
        // Classify only the first failed guard, so these counters sum to EntityRunBreaks.
        EntityRunBreak reason;
        if (batch == null || !ReferenceEquals(batch, _entityDrawBatch) ||
            !ReferenceEquals(commands, batch.Commands) || run.Count >= run.MaxCount ||
            offset != batch.CommandOffset + (ulong)(run.First + run.Count) * 20 ||
            (ulong)command.Handle != run.Command || _frames.Current.RecordingSerial != run.Serial)
            reason = EntityRunBreak.Contiguity;
        else if (_drawOrder != run.Order) reason = EntityRunBreak.Order;
        else if (_targets.ScopesOpened != run.Scope || !_targets.RenderingActive ||
            !SameEntityDrawPass(_nativePass, run.Pass) || !ReferenceEquals(_nativeTarget, run.Target))
            reason = EntityRunBreak.Scope;
        else if (_dynamicStateCommands != run.DynamicCommands) reason = EntityRunBreak.DynamicState;
        else if (_boundFrameSet.Handle != run.FrameSet.Handle || _boundFrameOffset != run.FrameOffset)
            reason = EntityRunBreak.FrameSet;
        else if (arena == null || draw.Pipeline.Handle != run.Pipeline ||
            !ReferenceEquals(draw.Segment, run.Segment) || !ReferenceEquals(arena, run.Arena))
            reason = EntityRunBreak.Program;
        else reason = EntityRunBreak.None;
        if (reason != EntityRunBreak.None)
        {
            BreakEntityDrawRun(reason);
            return false;
        }
        // The row is new and the count word is still unsubmitted host memory.
        WriteEntityRecord(draw);
        run.Count++;
        *(uint*)((byte*)batch!.Input.Mapped + run.CountOffset) = run.Count;
        EntityIndirectDrawsRecorded++;
        EntityCombinedDrawsRecorded++;
        EntityPackedDrawsRecorded++;
        EntityRecordDrawsRecorded++;
        return true;
    }

    // The stated route creates a new description for every draw while retaining
    // the same rendering scope. Object identity is not a pass-state change. Keep
    // every description field equal as well as the independent scope/target guards.
    private static bool SameEntityDrawPass(NativePassDescription? a, NativePassDescription? b) =>
        ReferenceEquals(a, b) || (a != null && b != null &&
        a.LinearColorAttachments == b.LinearColorAttachments && a.Name == b.Name &&
        a.FramebufferId == b.FramebufferId && a.ColorSlots == b.ColorSlots &&
        a.Reads.AsSpan().SequenceEqual(b.Reads) && a.TransientSlots == b.TransientSlots &&
        a.Flags == b.Flags && a.ClearSlots == b.ClearSlots &&
        MemoryMarshal.AsBytes(a.ClearValue.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.ClearValue.AsSpan())) &&
        a.ViewportX == b.ViewportX && a.ViewportY == b.ViewportY &&
        a.ViewportWidth == b.ViewportWidth && a.ViewportHeight == b.ViewportHeight &&
        Nullable.Equals(a.Scissor, b.Scissor) && a.Generic == b.Generic);

    private void BeginEntityDrawFrame(int slot)
    {
        _entityDrawBatch = null; EntityVisibility = null; EndEntityDrawRun();
        BeginEntityGeometryFrame(slot);
        BeginEntityRecordFrame(slot);
        if (_entityInputOffsets.Length > slot) _entityInputOffsets[slot] = 0;
    }

    private void DisposeEntityDrawsIdle()
    {
        DisposeEntityGeometryIdle();
        DisposeEntityRecordsIdle();
        foreach (var input in _entityInputs) input?.Dispose();
        _entityInputs = Array.Empty<VulkanBuffer?>();
        _entityInputOffsets = Array.Empty<ulong>();
        _entityDrawBatch = null;
        EndEntityDrawRun();
    }

    private void EndEntityDrawRun()
    {
        var run = _entityDrawRun;
        run.Open = false; run.Batch = null; run.Segment = null; run.Arena = null; run.Pass = null; run.Target = null;
    }
}
