using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Culling;
using VulkanStory.Render.Vulkan.Graph;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    // Shader ABI: entityanimated.instanced / shadowmapentityanimated.instanced.
    // Binding 0: scalar rows (612/148 bytes), binding 1: current+previous mat4 poses.
    private const int ResidentEntityCapacity = 4096;
    private const int ResidentEntityHeaderWords = 96;
    private const int ResidentEntityBoundsStart = ResidentEntityHeaderWords + ResidentEntityCapacity * 4;
    private const ulong ResidentEntityInputBytes = (ResidentEntityBoundsStart + ResidentEntityCapacity * 8) * 4UL;
    private const ulong ResidentEntityOutputBytes = ResidentEntityCapacity * 24UL;
    private const int ResidentEntityPoseLimit = 4 * 1024 * 1024; // floats, both histories
    private const int ResidentEntityIdentityFloats = 75 * 16;

    private sealed class ResidentModelHistory
    {
        internal ulong Frame;
        internal byte[] Current = new byte[64], Previous = new byte[64];
        internal bool Valid;
    }
    private sealed class ResidentEntityRow(int index)
    {
        internal readonly int Index = index;
        internal ulong PoseFrame, PreviousPoseFrame;
        internal float[] Pose = [], PreviousPose = [];
        internal int PreviousPoseOffset;
        internal bool PoseHistoryValid;
        internal readonly ResidentModelHistory[] Models = [new(), new(), new()];
        internal byte[] Record = [];
        internal NativeTexture[] Textures = [];
        internal int Mesh, PoseOffset;
        internal VulkanMesh? Geometry;
        internal ulong GeometryRevision;
        internal double X, Y, Z;
        internal float Radius;
    }
    private sealed class ResidentEntityTable(ShaderProgramResources source, VulkanBuffer rows)
    {
        internal readonly ShaderProgramResources Source = source;
        internal readonly VulkanBuffer Rows = rows;
        internal DescriptorSet Storage;
        internal ulong PoseBufferId;
        internal readonly HashSet<int> Captured = new();
    }
    private readonly record struct ResidentEntityCapture(int Row, double X, double Y, double Z, float Radius);
    private sealed class ResidentEntityGroup(NativePipeline pipeline, Pipeline handle,
        ResidentEntityTable table, int mesh, VulkanMesh geometry, DescriptorSet frameSet, uint frameOffset, byte[] frameGlobals)
    {
        internal readonly NativePipeline Pipeline = pipeline;
        internal readonly Pipeline Handle = handle;
        internal readonly ResidentEntityTable Table = table;
        internal readonly int Mesh = mesh;
        internal readonly VulkanMesh Geometry = geometry;
        internal readonly ulong Revision = geometry.GeometryRevision;
        internal readonly uint IndexCount = (uint)geometry.IndexCount;
        internal readonly VulkanBuffer IndexBuffer = geometry.Indices!;
        internal readonly VulkanBuffer[] Vertices = CaptureVertices(geometry);
        internal readonly DescriptorSet FrameSet = frameSet;
        internal readonly uint FrameOffset = frameOffset;
        internal readonly byte[] FrameGlobals = frameGlobals;
        internal readonly List<ResidentEntityCapture> Captures = new();
        internal ulong CommandOffset, CountOffset;
        private static VulkanBuffer[] CaptureVertices(VulkanMesh mesh)
        {
            var buffers = new VulkanBuffer[mesh.BindingOrder.Count];
            for (int i = 0; i < buffers.Length; i++) buffers[i] = mesh.Buffers[mesh.BindingOrder[i]]!;
            return buffers;
        }
    }
    private sealed record ResidentEntityDepth(int TextureId, VulkanTexture Texture, OcclusionCamera Camera, int Width, int Height);
    private sealed class ResidentEntityBatch(VulkanBuffer input, VulkanBuffer output)
    {
        internal readonly VulkanBuffer Input = input, Output = output;
        internal readonly Dictionary<ShaderProgramResources, ResidentEntityTable> Tables = new();
        internal readonly List<ResidentEntityGroup> Groups = new();
        internal readonly List<(NativePipeline Pipeline, NativeTexture Texture)> Textures = new();
        internal readonly HashSet<NativeTexture> TextureSet = new();
        internal int Count, Eye;
        internal ResidentEntityDepth? Depth;
        internal VulkanTexture DepthTexture = null!;
        internal ComputeProgram Compute = null!;
        internal Pipeline ComputePipeline;
        internal DescriptorSet ComputeSet;
    }
    private sealed class ResidentEntitySlot
    {
        internal VulkanBuffer? Poses;
        internal readonly List<ResidentEntityBatch> Batches = new();
        internal int Used;
    }
    private readonly ResidentEntityDepth?[] _residentEntityDepths = new ResidentEntityDepth?[3];
    private readonly Dictionary<(long Entity, int SubMesh), ResidentEntityRow> _residentEntityRows = new();
    private readonly Stack<int> _residentEntityFreeRows = new();
    private readonly List<int> _residentEntityReleasedRows = new();
    private readonly Dictionary<int, int> _residentEntityPoseRanges = new();
    private readonly Dictionary<ClientUniformBuffer, (uint Version, int Offset)> _residentEntityCurrentPoses = new();
    private readonly Dictionary<int, float[]> _residentEntityPoseSnapshots = new();
    private readonly Dictionary<float[], int> _residentEntityPreviousPoseOffsets = new();
    private readonly Dictionary<NativePipeline, Pipeline> _residentEntityPipelines = new();
    private ResidentEntitySlot[] _residentEntitySlots = [];
    private ResidentEntityBatch? _residentEntityPending;
    private int _residentEntityNextRow, _residentEntityPoseCursor, _residentEntityCullProgram;
    private ulong _residentEntityFrame;
    internal bool ResidentEntitiesEnabled { get; set; }
    internal long EntityResidentRowsCaptured { get; private set; }
    internal long EntityResidentCaptureRefusals { get; private set; }
    internal void RefuseResidentEntityCapture() => EntityResidentCaptureRefusals++;

    private void BeginEntitySceneFrame(int slot)
    {
        _residentEntityFrame++;
        _residentEntityPending = null;
        _residentEntityPoseCursor = ResidentEntityIdentityFloats;
        _residentEntityPoseRanges.Clear();
        _residentEntityCurrentPoses.Clear();
        _residentEntityPoseSnapshots.Clear(); _residentEntityPreviousPoseOffsets.Clear();
        foreach (int index in _residentEntityReleasedRows) _residentEntityFreeRows.Push(index);
        _residentEntityReleasedRows.Clear();
        if (_residentEntitySlots.Length != 0) _residentEntitySlots[slot].Used = 0;
        Array.Clear(_residentEntityDepths);
        if (!ResidentEntitiesEnabled || !_frames.Current.TryGetPreGraphicsCommands(out var commands)) return;
        SnapshotResidentEntityDepths();
        // These transitions precede recording the terrain reduction as well as
        // its execution, so image-state tracking follows submission order.
        foreach (var depth in _residentEntityDepths)
            if (depth != null) _textures.TransitionTexture(commands, depth.Texture, ResourceUsage.SampleCompute);
        if (_textures.Get(_bindless!.PlaceholderTextureId(TextureKind.Texture2D)) is { } placeholder)
            _textures.TransitionTexture(commands, placeholder, ResourceUsage.SampleCompute);
    }

    // Called just after Collect() publishes this slot's completed images.
    private void SnapshotResidentEntityDepths()
    {
        Save(0, _occlusionDepth); Save(1, _stereoOcclusionDepths[0]); Save(2, _stereoOcclusionDepths[1]);
        void Save(int index, Culling.OcclusionDepth? depth)
        {
            if (depth?.CompletedGpuCamera is { } camera && _textures.Get(depth.CompletedGpuTexture) is { } texture &&
                OcclusionProjection.From(camera.Projection) != null)
                _residentEntityDepths[index] = new(depth.CompletedGpuTexture, texture, camera,
                    depth.CompletedGpuWidth, depth.CompletedGpuHeight);
        }
    }

    private ResidentEntitySlot ResidentEntityCurrentSlot()
    {
        if (_residentEntitySlots.Length == 0)
        {
            _residentEntitySlots = new ResidentEntitySlot[_frames.FramesInFlight];
            for (int i = 0; i < _residentEntitySlots.Length; i++) _residentEntitySlots[i] = new();
        }
        return _residentEntitySlots[CurrentFrameSlot];
    }

    private bool ReserveResidentPoseStorage(int floats)
    {
        if (floats < 0 || floats > ResidentEntityPoseLimit || (ulong)floats * 4 > DrawPreparationLimits.MaxStorageBufferRange) return false;
        var slot = ResidentEntityCurrentSlot();
        ulong bytes = (ulong)floats * 4;
        if (slot.Poses is { } existing && existing.Size >= bytes) return true;
        ulong capacity = slot.Poses?.Size ?? 65536;
        while (capacity < bytes) capacity *= 2;
        capacity = Math.Min(capacity, Math.Min((ulong)ResidentEntityPoseLimit * 4, DrawPreparationLimits.MaxStorageBufferRange));
        var buffer = new VulkanBuffer(_context, capacity, BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);
        if (slot.Poses is { } old)
        {
            System.Buffer.MemoryCopy((void*)old.Mapped, (void*)buffer.Mapped, (long)capacity,
                Math.Min((long)old.Size, (long)_residentEntityPoseCursor * 4));
            _descriptors.Release(old.Id); _frames.DeferDeletion(old);
        }
        else
        {
            var identity = new Span<float>((void*)buffer.Mapped, ResidentEntityIdentityFloats);
            identity.Clear();
            for (int i = 0; i < identity.Length; i += 16)
                identity[i] = identity[i + 5] = identity[i + 10] = identity[i + 15] = 1;
        }
        slot.Poses = buffer;
        return true;
    }

    internal int BeginEntityPose(int floatCount)
    {
        if (!ResidentEntitiesEnabled || !_frameActive || floatCount <= 0 || floatCount % 16 != 0 ||
            floatCount > ResidentEntityPoseLimit - _residentEntityPoseCursor) return -1;
        int offset = _residentEntityPoseCursor;
        if (!ReserveResidentPoseStorage(offset + floatCount)) return -1;
        _residentEntityPoseCursor += floatCount;
        _residentEntityPoseRanges.Add(offset, floatCount);
        return offset;
    }

    internal Span<float> EntityPoseSpan(int offset, int floatCount)
    {
        if (!_frameActive || !_residentEntityPoseRanges.TryGetValue(offset, out int length) || floatCount < 0 || floatCount > length ||
            ResidentEntityCurrentSlot().Poses is not { } buffer) return Span<float>.Empty;
        return new Span<float>((float*)buffer.Mapped + offset, floatCount);
    }

    private ResidentEntityBatch? PrepareResidentEntityBatch()
    {
        if (_residentEntityPending is { } pending) return pending;
        var slot = ResidentEntityCurrentSlot();
        if (slot.Used >= 32 || !_frames.Current.TryGetPreGraphicsCommands(out _)) return null;
        if (_residentEntityCullProgram == 0)
            _residentEntityCullProgram = CreateComputeProgram(Culling.OcclusionDepth.Source("entities-resident.comp"),
                "resident-entities", [new(0, ComputeSlotKind.Sampled), new(1, ComputeSlotKind.Buffer), new(2, ComputeSlotKind.Buffer)], 0, 64, 1);
        if (_compute.Get(_residentEntityCullProgram) is not { } compute) return null;
        Pipeline handle = _compute.PipelineFor(compute, []);
        if (ResidentEntityInputBytes > DrawPreparationLimits.MaxStorageBufferRange ||
            ResidentEntityOutputBytes > DrawPreparationLimits.MaxStorageBufferRange) return null;
        ResidentEntityBatch batch;
        if (slot.Used < slot.Batches.Count) batch = slot.Batches[slot.Used];
        else
        {
            var input = new VulkanBuffer(_context, ResidentEntityInputBytes, BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);
            try
            {
                var output = new VulkanBuffer(_context, ResidentEntityOutputBytes,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit, MemoryPropertyFlags.DeviceLocalBit);
                batch = new(input, output); slot.Batches.Add(batch);
            }
            catch { input.Dispose(); throw; }
        }
        slot.Used++;
        batch.Count = 0; batch.Groups.Clear(); batch.Textures.Clear(); batch.TextureSet.Clear();
        List<ShaderProgramResources> stale = new();
        foreach (var entry in batch.Tables)
            if (!_residentEntityPrograms.ContainsKey(entry.Key)) stale.Add(entry.Key);
        foreach (var source in stale)
        {
            var old = batch.Tables[source];
            _descriptors.Release(old.Rows.Id); _frames.DeferDeletion(old.Rows);
            batch.Tables.Remove(source);
        }
        foreach (var table in batch.Tables.Values)
        {
            table.Captured.Clear();
            // Touch the descriptor cache once per slot cycle; a retained raw
            // set handle alone must not outlive the cache's age eviction.
            table.Storage = default; table.PoseBufferId = 0;
        }
        batch.Compute = compute; batch.ComputePipeline = handle; batch.Eye = TerrainEyeIndex;
        batch.Depth = _residentEntityDepths[TerrainEyeIndex is >= 0 and < 2 ? TerrainEyeIndex + 1 : 0];
        batch.DepthTexture = batch.Depth?.Texture ?? _textures.Get(_bindless!.PlaceholderTextureId(TextureKind.Texture2D))!;
        batch.ComputeSet = _computeArenas[CurrentFrameSlot].GetMixed(compute.SetLayout,
            [new(0, DescriptorType.CombinedImageSampler, batch.DepthTexture.ViewOfMips(0, batch.DepthTexture.MipLevels),
                _textures.Samplers.Get(ComputeNearest), ImageLayout.ShaderReadOnlyOptimal)], 1,
            [new(batch.Input.Handle, 0, batch.Input.Size), new(batch.Output.Handle, 0, batch.Output.Size)]);
        return _residentEntityPending = batch;
    }

    private bool ResolveResidentEntityTextures(ReadOnlySpan<NativeTexture> textures, Span<byte> push)
    {
        foreach (NativeTexture sampled in textures)
        {
            var sampler = sampled.Sampler;
            if (!sampler.IsPresent) continue;
            if (_targets.IsAttachmentOfBound(sampled.TextureId) || _targets.IsBoundDepth(sampled.TextureId)) return false;
            PrepareNativeMutableSampling(sampled.TextureId);
            VulkanTexture? texture = _textures.Get(sampled.TextureId);
            if (texture != null && !BindlessKinds.Suits(TextureShape.Of(texture), sampler.Kind)) texture = null;
            SamplerState sampling = sampled.Sampling ?? texture?.State ?? SamplerState.Default;
            ImageView view = default;
            if (texture != null)
            {
                if (sampled.UseTextureMipRange)
                {
                    if (!texture.TrySamplingView(sampling, sampled.LinearColor, out view, out sampling)) texture = null;
                }
                else
                {
                    view = texture.ViewOfMips(0, texture.MipLevels, sampled.LinearColor, sampledShape: true);
                    sampling = sampling with { BaseLevel = 0, MaxLevel = (int)texture.MipLevels - 1 };
                }
            }
            if (sampler.FrameBinding >= 0)
            {
                var value = texture == null ? default : new SamplerBindingValue((uint)sampler.FrameBinding, view,
                    _textures.Samplers.Get(BindlessKinds.EffectiveState(sampling, sampler.Kind)), texture.Id);
                SetNativeFrameTexture(FrameTextureIndex(sampler.FrameBinding), value, texture == null ? 0 : sampled.TextureId);
            }
            else
            {
                if (sampler.PushOffset < 0 || sampler.PushOffset + 4 > push.Length) return false;
                uint index = _bindless!.Resolve(texture, sampler.Kind, sampling, ImageLayout.ShaderReadOnlyOptimal, view);
                BitConverter.TryWriteBytes(push[sampler.PushOffset..], index);
            }
        }
        return true;
    }

    private bool ResidentEntityFrameSet(ShaderProgramResources program, out DescriptorSet set, out uint offset)
    {
        set = default;
        if (!TrySnapshotFrameGlobals(program, out offset)) return false;
        // Unused slots can change during interleaved compat draws. They cannot
        // split a resident group: the instanced program never samples them.
        for (int i = 0; i < _frameSamplerScratch.Length; i++) _frameSamplerScratch[i] = FrameTexturePlaceholder(i);
        foreach (var sampler in program.Interface.Samplers)
            if (sampler.FrameBinding >= 0)
            {
                int index = FrameTextureIndex(sampler.FrameBinding);
                if (_frameTextureValues[index].View.Handle != 0) _frameSamplerScratch[index] = _frameTextureValues[index];
            }
        _frameBufferScratch[0] = new((uint)SetConvention.FrameGlobalsBinding, _frames.UniformBuffer, 0, (ulong)_frameGlobals.Length);
        set = GetDescriptorSet(new DescriptorSetKey(0, SetConvention.FrameSet, _frameSamplerScratch, _frameBufferScratch),
            _sharedLayout!.FrameSetLayout);
        return true;
    }

    private bool ResidentEntityFrameMatches(ResidentEntityGroup group, ShaderProgramResources program)
    {
        // Ring offsets name allocations, not values. A compat draw can change
        // globals and restore them before the next row of the same mesh.
        foreach (var declaration in program.Interface.FrameMemberDeclaredLengths)
        {
            if (!FrameGlobals.TryGetMember(declaration.Key, out var member)) return false;
            int bytes = member.Type.Size * Math.Max(1, declaration.Value);
            if (!_frameGlobals.AsSpan(member.Offset, bytes).SequenceEqual(group.FrameGlobals.AsSpan(member.Offset, bytes))) return false;
        }
        return true;
    }

    private void PrepareResidentEntityStorage(ResidentEntityTable table)
    {
        var poses = ResidentEntityCurrentSlot().Poses!;
        if (table.Storage.Handle != 0 && table.PoseBufferId == poses.Id) return;
        var placeholder = _placeholderUniforms!;
        for (int i = 0; i < _storageBufferScratch.Length; i++)
            _storageBufferScratch[i] = new((uint)i, placeholder.Handle, 0, placeholder.Size, placeholder.Id);
        _storageBufferScratch[0] = new(0, table.Rows.Handle, 0, table.Rows.Size, table.Rows.Id);
        _storageBufferScratch[1] = new(1, poses.Handle, 0, poses.Size, poses.Id);
        table.Storage = GetDescriptorSet(new DescriptorSetKey(0, SetConvention.StorageSet,
            ReadOnlySpan<SamplerBindingValue>.Empty, _storageBufferScratch), _sharedLayout!.StorageSetLayout);
        table.PoseBufferId = poses.Id;
    }

    internal bool CaptureEntityRow(NativePipeline pipeline, int meshId, ReadOnlySpan<NativeTexture> textures,
        long entityKey, int subMesh, double centerX, double centerY, double centerZ, float radius)
    {
        if (!ResidentEntitiesEnabled || !_frameActive || !_context.DrawIndirectFirstInstanceEnabled ||
            !_context.SampledImageArrayNonUniformIndexingEnabled ||
            !IsNativePipelineLive(pipeline) || !pipeline.Program.IsNative ||
            !_residentEntityPrograms.TryGetValue(pipeline.Program, out var variant) || !NativeMeshIsDrawable(pipeline, meshId, out var geometry) ||
            !double.IsFinite(centerX) || !double.IsFinite(centerY) || !double.IsFinite(centerZ) || !float.IsFinite(radius) || radius < 0) return false;
        var source = pipeline.Program;
        bool shadow = _programNames[source.ProgramId] == "shadowmapentityanimated";
        int recordBytes = shadow ? 64 : 568, pushBytes = shadow ? 76 : 36, stride = recordBytes + pushBytes + 8;
        if (source.UniformShadow.Length != recordBytes || source.Interface.PushConstantSize != pushBytes ||
            (ulong)(stride * ResidentEntityCapacity) > DrawPreparationLimits.MaxStorageBufferRange) return false;
        if (!_residentEntityRows.TryGetValue((entityKey, subMesh), out var row))
        {
            if (_residentEntityFreeRows.Count == 0 && _residentEntityNextRow == ResidentEntityCapacity) return false;
            int id = _residentEntityFreeRows.Count != 0 ? _residentEntityFreeRows.Pop() : _residentEntityNextRow++;
            _residentEntityRows.Add((entityKey, subMesh), row = new(id));
        }
        int poseOffset = 0, poseCount = ResidentEntityIdentityFloats;
        if (_clientUniformBuffers.BoundBlocks.TryGetValue("Animation", out int animation) &&
            _clientUniformBuffers.Buffers.TryGetValue(animation, out var ubo))
        {
            poseCount = ubo.Shadow.Length / 4;
            if (_residentEntityCurrentPoses.TryGetValue(ubo, out var saved) && saved.Version == ubo.Version)
                poseOffset = saved.Offset;
            else
            {
                poseOffset = BeginEntityPose(poseCount);
                if (poseOffset < 0) return false;
                // Copy the stock Animation upload without changing the compat draw's UBO.
                MemoryMarshal.Cast<byte, float>(ubo.Shadow.AsSpan()).CopyTo(EntityPoseSpan(poseOffset, poseCount));
                _residentEntityCurrentPoses[ubo] = (ubo.Version, poseOffset);
            }
        }
        if (!ReserveResidentPoseStorage(_residentEntityPoseCursor)) return false;
        var batch = PrepareResidentEntityBatch();
        if (batch == null || batch.Count == ResidentEntityCapacity || batch.Eye != TerrainEyeIndex) return false;
        if (!batch.Tables.TryGetValue(source, out var table))
        {
            var rows = new VulkanBuffer(_context, (ulong)(stride * ResidentEntityCapacity), BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);
            table = new(source, rows); batch.Tables.Add(source, table);
        }
        if (table.Captured.Contains(row.Index)) return false;
        if (!_residentEntityPipelines.TryGetValue(pipeline, out Pipeline handle))
        {
            var key = pipeline.Key with { ProgramId = variant.ProgramId };
            var request = new GraphicsPipelineCache.PipelineRequest
            {
                Program = variant, VertexLayout = pipeline.Request.VertexLayout, Targets = pipeline.Request.Targets,
                Blend = pipeline.Request.Blend, PolygonMode = pipeline.Request.PolygonMode, Topology = pipeline.Request.Topology,
            };
            handle = _pipelines.Get(key, request); _residentEntityPipelines.Add(pipeline, handle);
        }
        Span<byte> bytes = stackalloc byte[stride];
        bytes.Clear(); source.UniformShadow.AsSpan().CopyTo(bytes);
        if (source.PushShadow is { } sourcePush) sourcePush.AsSpan().CopyTo(bytes[recordBytes..]);
        if (!ResolveResidentEntityTextures(textures, bytes.Slice(recordBytes, pushBytes)) ||
            !ResidentEntityFrameSet(variant, out DescriptorSet frameSet, out uint frameOffset)) return false;
        bool newPose = row.PoseFrame != _residentEntityFrame;
        bool sameGeometry = ReferenceEquals(row.Geometry, geometry) && row.GeometryRevision == geometry!.GeometryRevision;
        bool poseHistory = sameGeometry && (newPose ? row.PoseFrame + 1 == _residentEntityFrame && row.Pose.Length == poseCount : row.PoseHistoryValid);
        int previousPose = poseOffset;
        if (poseHistory)
        {
            if (row.PreviousPoseFrame == _residentEntityFrame) previousPose = row.PreviousPoseOffset;
            else
            {
                var previous = newPose ? row.Pose : row.PreviousPose;
                if (!_residentEntityPreviousPoseOffsets.TryGetValue(previous, out previousPose))
                {
                    previousPose = BeginEntityPose(previous.Length);
                    if (previousPose < 0) return false;
                    previous.AsSpan().CopyTo(EntityPoseSpan(previousPose, previous.Length));
                    _residentEntityPreviousPoseOffsets.Add(previous, previousPose);
                }
            }
        }
        // Growth preserves every offset; pending tables move to the new buffer.
        // Earlier flushed batches keep their original, retired-at-fence buffer.
        foreach (var pendingTable in batch.Tables.Values) PrepareResidentEntityStorage(pendingTable);
        if (newPose)
        {
            row.PreviousPose = row.Pose;
            row.PoseHistoryValid = poseHistory; row.PoseFrame = _residentEntityFrame;
        }
        // Main-camera history follows the pose actually rendered by the body,
        // even if an earlier shadow pass captured another stock pose this frame.
        if (newPose || !shadow)
        {
            if (!_residentEntityPoseSnapshots.TryGetValue(poseOffset, out var snapshot))
            {
                snapshot = new ReadOnlySpan<float>((float*)ResidentEntityCurrentSlot().Poses!.Mapped + poseOffset, poseCount).ToArray();
                _residentEntityPoseSnapshots.Add(poseOffset, snapshot);
            }
            row.Pose = snapshot;
        }
        row.PreviousPoseFrame = _residentEntityFrame; row.PreviousPoseOffset = previousPose;
        BitConverter.TryWriteBytes(bytes[(recordBytes + pushBytes)..], (uint)poseOffset);
        BitConverter.TryWriteBytes(bytes[(recordBytes + pushBytes + 4)..], (uint)previousPose);
        if (!shadow)
        {
            var history = row.Models[TerrainEyeIndex is >= 0 and < 2 ? TerrainEyeIndex + 1 : 0];
            int modelOffset = source.Interface.MembersByName["modelMatrix"].Offset;
            int previousOffset = source.Interface.MembersByName["prevModelMatrix"].Offset;
            if (history.Frame != _residentEntityFrame)
            {
                history.Valid = sameGeometry && history.Frame + 1 == _residentEntityFrame;
                (history.Current, history.Previous) = (history.Previous, history.Current);
                bytes.Slice(modelOffset, 64).CopyTo(history.Current); history.Frame = _residentEntityFrame;
            }
            if (history.Valid && poseHistory) history.Previous.AsSpan().CopyTo(bytes[previousOffset..]);
            else BitConverter.TryWriteBytes(bytes[(recordBytes + 12)..], 0); // taaHistoryValid
        }
        if (row.Record.Length < stride) row.Record = new byte[stride];
        bytes.CopyTo(row.Record);
        if (row.Textures.Length != textures.Length) row.Textures = new NativeTexture[textures.Length];
        textures.CopyTo(row.Textures); row.Mesh = meshId; row.PoseOffset = poseOffset;
        row.Geometry = geometry; row.GeometryRevision = geometry!.GeometryRevision;
        row.X = centerX; row.Y = centerY; row.Z = centerZ; row.Radius = radius;
        bytes.CopyTo(new Span<byte>((byte*)table.Rows.Mapped + row.Index * stride, stride));
        table.Captured.Add(row.Index);
        ResidentEntityGroup? group = null;
        foreach (var candidate in batch.Groups)
            if (ReferenceEquals(candidate.Pipeline, pipeline) && candidate.Mesh == meshId &&
                ReferenceEquals(candidate.Geometry, geometry) && candidate.Revision == geometry.GeometryRevision &&
                candidate.FrameSet.Handle == frameSet.Handle && ResidentEntityFrameMatches(candidate, variant) &&
                (uint)candidate.Captures.Count < DrawPreparationLimits.MaxDrawIndirectCount) { group = candidate; break; }
        if (group == null)
        {
            group = new(pipeline, handle, table, meshId, geometry, frameSet, frameOffset, (byte[])_frameGlobals.Clone()); batch.Groups.Add(group);
        }
        group.Captures.Add(new(row.Index, centerX, centerY, centerZ, radius));
        foreach (NativeTexture texture in textures)
            if (texture.Sampler.IsPresent && batch.TextureSet.Add(texture)) batch.Textures.Add((pipeline, texture));
        batch.Count++;
        EntityResidentRowsCaptured++;
        return true;
    }

    internal void ReleaseEntityRows(long entityKey)
    {
        List<(long Entity, int SubMesh)> removed = new();
        foreach (var entry in _residentEntityRows) if (entry.Key.Entity == entityKey) removed.Add(entry.Key);
        foreach (var key in removed)
        {
            _residentEntityReleasedRows.Add(_residentEntityRows[key].Index); _residentEntityRows.Remove(key);
        }
    }

    internal void ResetEntityScene()
    {
        _residentEntityRows.Clear(); _residentEntityFreeRows.Clear(); _residentEntityReleasedRows.Clear();
        _residentEntityNextRow = 0; _residentEntityPending = null; _residentEntityPipelines.Clear();
        _residentEntityCurrentPoses.Clear();
        _residentEntityPoseSnapshots.Clear(); _residentEntityPreviousPoseOffsets.Clear();
        Array.Clear(_residentEntityDepths);
    }

    private void FillResidentEntityCull(ResidentEntityBatch batch, int passKind, in TerrainView view)
    {
        var words = new Span<uint>((void*)batch.Input.Mapped, (int)(ResidentEntityInputBytes / 4));
        words[..ResidentEntityHeaderWords].Clear();
        var floats = MemoryMarshal.Cast<uint, float>(words);
        Matrix4x4 matrix = view.ViewProjection;
        MemoryMarshal.Write(MemoryMarshal.AsBytes(floats[..16]), in matrix);
        if (batch.Depth is { } depth)
        {
            depth.Camera.View.AsSpan(0, 16).CopyTo(floats[16..]); depth.Camera.Projection.AsSpan(0, 16).CopyTo(floats[32..]);
            var projection = OcclusionProjection.From(depth.Camera.Projection)!.Value;
            floats[48] = depth.Width; floats[49] = depth.Height;
            floats[50] = projection.DepthUnpackMul; floats[51] = projection.DepthUnpackAdd;
            double dx = view.CameraX - depth.Camera.CameraX, dy = view.CameraY - depth.Camera.CameraY, dz = view.CameraZ - depth.Camera.CameraZ;
            floats[54] = MathF.BitIncrement((float)(Math.Sqrt(dx * dx + dy * dy + dz * dz) + 0.5));
            words[53] = passKind == 0 && !view.Shadow ? 1u : 0u;
        }
        words[52] = 1; words[55] = (uint)batch.Groups.Count;
        TerrainPlane[] planes = [view.Plane0, view.Plane1, view.Plane2, view.Plane3, view.Plane4, view.Plane5];
        bool suppliedPlanes = false;
        for (int i = 0; i < planes.Length; i++)
        {
            var plane = planes[i]; suppliedPlanes |= plane.A != 0 || plane.B != 0 || plane.C != 0;
            int at = 60 + i * 4;
            floats[at] = (float)plane.A; floats[at + 1] = (float)plane.B; floats[at + 2] = (float)plane.C;
            floats[at + 3] = (float)((plane.A * view.CameraX + plane.B * view.CameraY) + plane.C * view.CameraZ + plane.D);
        }
        words[59] = suppliedPlanes ? 6u : 0u;
        int index = 0, outputWord = 0;
        for (int g = 0; g < batch.Groups.Count; g++)
        {
            var group = batch.Groups[g]; int header = ResidentEntityHeaderWords + g * 4;
            words[header] = (uint)index; words[header + 1] = (uint)group.Captures.Count; words[header + 2] = (uint)outputWord;
            words[header + 3] = group.IndexCount;
            group.CommandOffset = (ulong)outputWord * 4;
            group.CountOffset = group.CommandOffset + (ulong)group.Captures.Count * 20;
            outputWord += group.Captures.Count * 5 + 1;
            foreach (var capture in group.Captures)
            {
                int at = ResidentEntityBoundsStart + index++ * 8;
                floats[at] = (float)(capture.X - view.CameraX); floats[at + 1] = (float)(capture.Y - view.CameraY);
                floats[at + 2] = (float)(capture.Z - view.CameraZ); floats[at + 3] = capture.Radius;
                floats[at + 4] = (float)(capture.X - (batch.Depth?.Camera.CameraX ?? view.CameraX));
                floats[at + 5] = (float)(capture.Y - (batch.Depth?.Camera.CameraY ?? view.CameraY));
                floats[at + 6] = (float)(capture.Z - (batch.Depth?.Camera.CameraZ ?? view.CameraZ)); words[at + 7] = (uint)capture.Row;
                // The stock frustum center is narrowed to float by the game.
                // Enclose its possible world-center rounding for depth tests.
                float magnitude = (float)Math.Max(Math.Abs(capture.X), Math.Max(Math.Abs(capture.Y), Math.Abs(capture.Z)));
                floats[56] = Math.Max(floats[56], MathF.BitIncrement(magnitude) - magnitude);
            }
        }
    }

    internal bool DrawEntityPass(int passKind, in TerrainView view)
    {
        var batch = _residentEntityPending;
        if (batch == null || batch.Count == 0) return ResidentEntitiesEnabled;
        // Admission reserved every buffer, descriptor and PSO. Invalid stage
        // ownership is an adapter error, never permission to drop accepted rows.
        if (passKind is < 0 or > 3 || !_frameActive || _nativePass == null || _nativeTarget == null || batch.Eye != view.Eye ||
            !_frames.Current.TryGetPreGraphicsCommands(out CommandBuffer computeCommands))
            throw new InvalidOperationException("Accepted resident entities require their stage's native pass.");
        FillResidentEntityCull(batch, passKind, view);
        var api = _context.Api; DescriptorSet set = batch.ComputeSet;
        api.CmdBindPipeline(computeCommands, PipelineBindPoint.Compute, batch.ComputePipeline);
        api.CmdBindDescriptorSets(computeCommands, PipelineBindPoint.Compute, batch.Compute.Layout, 0, 1, &set, 0, null);
        api.CmdDispatch(computeCommands, (uint)batch.Groups.Count, 1, 1);
        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.DrawIndirectBit, DstAccessMask = AccessFlags2.IndirectCommandReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = batch.Output.Handle, Offset = 0, Size = batch.Output.Size,
        };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
        api.CmdPipelineBarrier2(computeCommands, &dependency);
        CommandBuffer command = Commands;
        EnsureBindlessPlaceholdersReadable(command);
        foreach (var material in batch.Textures)
            if (_textures.Get(material.Texture.TextureId) is { } texture)
            {
                _targets.FlushPendingClears(command, texture);
                RequireSamplerStages(command, material.Pipeline.SamplerStageUsages(material.Texture.Sampler.Index), texture);
            }
        foreach (var group in batch.Groups) PrepareUnnamedFrameTextures(command, group.Pipeline, []);
        if (_barriers.Pending != 0) _targets.EndRendering(command);
        _barriers.Flush(command); _targets.SetDepthReadOnly(false); _targets.EnsureRendering(command);
        ForgetBoundDescriptors();
        DescriptorSet textureSet = _bindless!.Set;
        api.CmdBindDescriptorSets(command, PipelineBindPoint.Graphics, _sharedLayout!.Layout, SetConvention.TextureSet, 1, &textureSet, 0, null);
        ulong boundPipeline = 0;
        Silk.NET.Vulkan.Buffer* vertexBuffers = stackalloc Silk.NET.Vulkan.Buffer[MeshManager.MaxBuffers];
        ulong* vertexOffsets = stackalloc ulong[MeshManager.MaxBuffers];
        batch.Groups.Sort((left, right) => left.Handle.Handle.CompareTo(right.Handle.Handle));
        foreach (var group in batch.Groups)
        {
            if (boundPipeline != group.Handle.Handle)
            {
                api.CmdBindPipeline(command, PipelineBindPoint.Graphics, group.Handle); boundPipeline = group.Handle.Handle;
            }
            DescriptorSet frameSet = group.FrameSet, storage = group.Table.Storage;
            uint frameOffset = group.FrameOffset, recordOffset = 0;
            api.CmdBindDescriptorSets(command, PipelineBindPoint.Graphics, _sharedLayout.Layout, 0, 1, &frameSet, 1, &frameOffset);
            api.CmdBindDescriptorSets(command, PipelineBindPoint.Graphics, _sharedLayout.Layout, 2, 1, &storage, 1, &recordOffset);
            EmitNativeDynamicState(command, _nativeTarget, _nativePass, group.Pipeline);
            var layout = group.Pipeline.Request.VertexLayout;
            if (layout.Bindings.Length > 0 && layout.Bindings[^1].Binding == VertexLayoutDescription.DefaultAttributeBinding && _defaultAttributes is { } defaults)
            {
                var defaultBuffer = defaults.Handle; ulong zero = 0;
                api.CmdBindVertexBuffers(command, VertexLayoutDescription.DefaultAttributeBinding, 1, &defaultBuffer, &zero);
            }
            // Bind captured buffer identities, even if a later stock upload
            // replaced the mesh handle before this stage flushed.
            int count = group.Vertices.Length;
            for (int i = 0; i < count; i++)
            {
                _uploads.NoteUse(command, group.Vertices[i]);
                vertexBuffers[i] = group.Vertices[i].Handle; vertexOffsets[i] = 0;
            }
            if (count != 0) api.CmdBindVertexBuffers(command, 0, (uint)count, vertexBuffers, vertexOffsets);
            _uploads.NoteUse(command, group.IndexBuffer);
            api.CmdBindIndexBuffer(command, group.IndexBuffer.Handle, 0, IndexType.Uint32);
            if (_context.DrawIndirectCountEnabled)
                api.CmdDrawIndexedIndirectCount(command, batch.Output.Handle, group.CommandOffset, batch.Output.Handle, group.CountOffset, (uint)group.Captures.Count, 20);
            else api.CmdDrawIndexedIndirect(command, batch.Output.Handle, group.CommandOffset, (uint)group.Captures.Count, 20);
            _meshes.NoteSceneDrawCommand(); NoteNativeDraw(NativeDrawKind.Indirect);
        }
        _nativeBoundPipelineSerial = 0; _nativeBoundDefaultsSerial = 0; ForgetBoundDescriptors();
        _residentEntityPending = null;
        return true;
    }

    private void DisposeEntitySceneIdle()
    {
        foreach (var slot in _residentEntitySlots)
        {
            slot.Poses?.Dispose();
            foreach (var batch in slot.Batches)
            {
                batch.Input.Dispose(); batch.Output.Dispose();
                foreach (var table in batch.Tables.Values) table.Rows.Dispose();
            }
        }
        foreach (var variant in _residentEntityPrograms.Values) variant.Dispose();
        _residentEntityPrograms.Clear(); _residentEntitySlots = []; ResetEntityScene();
    }
}
