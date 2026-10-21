using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Culling;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

/// <summary>A stock world-space plane retained by the resident entity culler.</summary>
internal readonly record struct TerrainFrustumPlane(double X, double Y, double Z, double D);

internal readonly record struct TerrainPartDesc(int PoolMesh, int RenderPass, int Atlas,
    uint FirstIndex, uint IndexCount, double CenterX, double CenterY, double CenterZ,
    float Radius, float RadiusY, float RadiusZ, int LodLevel, int ChunkSlot)
{
    public bool Hidden { get; init; }
    public double PoolOriginX { get; init; }
    public double PoolOriginY { get; init; }
    public double PoolOriginZ { get; init; }
}

internal readonly record struct TerrainPlane(double A, double B, double C, double D);

internal readonly record struct TerrainParityPart(bool StockVisible, bool StockDepthVisible, TerrainPartDesc Inputs, bool ChunkVisible);

internal readonly record struct TerrainView(int Eye, double CameraX, double CameraY, double CameraZ,
    double PlayerX, double PlayerY, double PlayerZ,
    System.Numerics.Matrix4x4 ViewProjection /* camera-relative */, float ViewDistance,
    double Lod0BiasSq, double Lod2BiasSq,
    int CullMode /* stock EnumFrustumCullMode */, bool Shadow)
{
    public int ViewDistanceSq { get; init; }
    public int PlayerBlockX { get; init; }
    public int PlayerBlockZ { get; init; }
    public double ShadowRangeX { get; init; }
    public double ShadowRangeZ { get; init; }
    public TerrainPlane Plane0 { get; init; }
    public TerrainPlane Plane1 { get; init; }
    public TerrainPlane Plane2 { get; init; }
    public TerrainPlane Plane3 { get; init; }
    public TerrainPlane Plane4 { get; init; }
    public TerrainPlane Plane5 { get; init; }
    public string OriginUniform { get; init; } = "";
}

public sealed unsafe partial class VulkanDevice
{
    internal bool ResidentTerrainEnabled { get; set; }
    private PhysicalDeviceLimits? _terrainLimits;
    private PhysicalDeviceLimits DrawPreparationLimits
    {
        get
        {
            _terrainLimits ??= _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
            return _terrainLimits.Value;
        }
    }
    private readonly HashSet<int> _terrainOccluderMeshes = new();

    private void InvalidateTerrainGeometry(int meshId, bool deleted = false)
    {
        if (_terrainOccluderMeshes.Contains(meshId)) ResetTerrainOcclusion();
        if (deleted) _terrainOccluderMeshes.Remove(meshId);
    }

    // Rows are rebuilt only when producer state changes, never by walking parts at draw time.
    // GPU row (16 uints): float xyz/radii xyz, first/count, lod, chunk, Hide,
    // pool, render pass, atlas, part handle, and a reserved word. Stock float centers are
    // promoted to double by the cull shader before the stock arithmetic.
    private const int TerrainSceneRowWords = 16, TerrainSceneHeaderWords = 144;
    private readonly object _terrainSceneLock = new();
    private readonly Dictionary<int, TerrainPartDesc> _terrainSceneParts = new();
    private readonly Dictionary<int, ulong[]> _terrainSceneVisibility = new();
    private int _terrainSceneNextHandle;
    private long _terrainSceneVersion, _terrainSceneUploadedVersion = -1, _terrainSceneFrameVersion;
    private bool _terrainSceneFrozen;
    private uint[]? _terrainScenePendingUpload;
    private VulkanBuffer? _terrainSceneTable;
    private int _terrainSceneCapacity;
    private int _terrainSceneProgram;
    private BarrierBatcher? _terrainSceneBarriers;
    private VulkanBuffer?[] _terrainSceneInputs = [];
    private ulong[] _terrainSceneInputCursors = [];
    private TerrainScenePool[] _terrainScenePools = [];
    private KeyValuePair<int, TerrainPartDesc>[] _terrainSceneRows = [];
    private readonly record struct TerrainScenePool(int Mesh, int Pass, int Atlas, int First, int Count,
        double OriginX, double OriginY, double OriginZ)
    {
        public double MinX { get; init; }
        public double MinY { get; init; }
        public double MinZ { get; init; }
        public double MaxX { get; init; }
        public double MaxY { get; init; }
        public double MaxZ { get; init; }
        public ulong[] ChunkSlots { get; init; } = [];
    }
    private readonly record struct TerrainSceneDepth(int Texture, int Width, int Height, OcclusionCamera? Camera);
    private readonly TerrainSceneDepth[] _terrainSceneDepths = new TerrainSceneDepth[3];
    private readonly HashSet<int> _terrainSceneDrawnPools = new();
    private readonly record struct TerrainSceneCullKey(int Pass, int Atlas, TerrainView View, ulong[] Visibility, bool Depth, bool Parity);
    private readonly record struct TerrainSceneIndirect(TerrainScenePool Pool, VulkanBuffer? Buffer, ulong Offset, ulong CountOffset);
    private readonly Dictionary<TerrainSceneCullKey, TerrainSceneIndirect[]> _terrainSceneCulls = new();
    private sealed record TerrainSceneParityResult(int Site, int Pass, int Atlas, TerrainView View, ulong Frame,
        TerrainSceneIndirect[] Draws, KeyValuePair<int, TerrainPartDesc>[] Rows, IReadOnlyDictionary<int, TerrainParityPart> Expected,
        bool UseDepth);
    private readonly Dictionary<int, List<TerrainSceneParityResult>> _terrainSceneParityResults = new();
    private readonly Dictionary<int, List<VulkanBuffer>> _terrainReadbackBuffers = new();
    private int[] _terrainReadbackBufferCursors = [];
    private bool _terrainParityMismatchLogged;
    public long TerrainParityChecks { get; private set; }
    public long TerrainParityMismatches { get; private set; }

    private static ulong TerrainSceneOutputBytes(TerrainScenePool pool, bool parity) =>
        (ulong)(pool.Count * (parity ? 8L : 7L) + (pool.Count + 63) / 64 + 1) * 4;

    private VulkanBuffer AllocateTerrainOutput(ulong bytes, bool readback, out ulong offset)
    {
        if (!readback) return AllocateIndirect(checked((int)((bytes + 19) / 20)), out offset,
            Math.Max(4UL, DrawPreparationLimits.MinStorageBufferOffsetAlignment));
        // Generic indirect overflow buffers can retire before this slot returns.
        // Resident parity outputs need slot ownership through CPU collection,
        // beyond GPU completion alone.
        offset = 0;
        if (_terrainReadbackBufferCursors.Length == 0)
            _terrainReadbackBufferCursors = new int[_frames.FramesInFlight];
        int slot = CurrentFrameSlot, index = _terrainReadbackBufferCursors[slot]++;
        if (!_terrainReadbackBuffers.TryGetValue(slot, out var buffers))
            _terrainReadbackBuffers.Add(slot, buffers = new());
        if (index == buffers.Count || buffers[index].Size < bytes)
        {
            var replacement = new VulkanBuffer(_context, bytes,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit,
                MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                MemoryPoolClass.ReBar);
            if (index == buffers.Count) buffers.Add(replacement);
            else { buffers[index].Dispose(); buffers[index] = replacement; }
        }
        return buffers[index];
    }

    internal bool IsTerrainPoolResident(int poolMesh) => _terrainSceneDrawnPools.Contains(poolMesh);

    internal int AddTerrainPart(in TerrainPartDesc part)
    {
        if (!ValidTerrainPart(part)) return 0;
        lock (_terrainSceneLock)
        {
            if (_terrainSceneNextHandle == int.MaxValue ||
                (ulong)(_terrainSceneParts.Count + 1) * TerrainSceneRowWords * 4 > DrawPreparationLimits.MaxStorageBufferRange)
                return 0;
            int handle = ++_terrainSceneNextHandle;
            _terrainSceneParts.Add(handle, part);
            _terrainSceneVersion++;
            return handle;
        }
    }

    internal void UpdateTerrainPart(int handle, in TerrainPartDesc part)
    {
        lock (_terrainSceneLock)
            if (_terrainSceneParts.TryGetValue(handle, out var previous) && previous != part)
            {
                _terrainSceneParts[handle] = part;
                _terrainSceneVersion++;
            }
    }

    internal void RemoveTerrainPart(int handle)
    {
        lock (_terrainSceneLock)
            if (_terrainSceneParts.Remove(handle)) _terrainSceneVersion++;
    }

    internal void RemoveTerrainPool(int poolMesh)
    {
        lock (_terrainSceneLock)
        {
            foreach (int handle in _terrainSceneParts.Where(pair => pair.Value.PoolMesh == poolMesh).Select(pair => pair.Key).ToArray())
                _terrainSceneParts.Remove(handle);
            _terrainSceneVersion++;
        }
    }

    internal void SetTerrainChunkVisibility(ReadOnlySpan<ulong> visibleChunkSlots)
    {
        // Each cull call gets an immutable copy in its frame-slot input arena. A later eye
        // or ChunkCuller result can therefore never change commands already recorded.
        _terrainSceneVisibility[TerrainEyeIndex] = visibleChunkSlots.ToArray();
    }

    internal bool DrawTerrainPass(NativePipeline pipeline, ReadOnlySpan<NativeTexture> textures,
        int stockCallSite /* 0..13 */, int renderPass, int atlas, in TerrainView view,
        IReadOnlyDictionary<int, TerrainParityPart>? parity = null)
    {
        _terrainSceneDrawnPools.Clear();
        if (!ResidentTerrainEnabled || !_context.ShaderFloat64Enabled || !_frameActive ||
            _nativePass == null || _nativeTarget == null || !pipeline.Program.IsNative ||
            !IsNativePipelineLive(pipeline) || stockCallSite is < 0 or > 13 || view.Eye != TerrainEyeIndex ||
            !_frames.Current.TryGetPreGraphicsCommands(out var producer)) return false;
        if (view.CullMode is >= 1 and <= 4 && !_terrainSceneVisibility.ContainsKey(view.Eye)) return false;
        if (!FreezeTerrainScene()) return false;
        lock (_terrainSceneLock)
            if (_terrainSceneFrameVersion != _terrainSceneVersion) return false;
        TerrainScenePool[] pools = _terrainScenePools.Where(pool => pool.Pass == renderPass && pool.Atlas == atlas).ToArray();
        if (pools.Length == 0)
        {
            QueueTerrainSceneParity(stockCallSite, renderPass, atlas, view, false, [], parity);
            return true;
        }
        ulong[] visibility = _terrainSceneVisibility.GetValueOrDefault(view.Eye) ?? Array.Empty<ulong>();
        var skipped = new bool[pools.Length];
        for (int i = 0; i < pools.Length; i++) skipped[i] = SkipTerrainScenePool(pools[i], view, visibility);
        TerrainSceneDepth depth = _terrainSceneDepths[view.Eye is >= 0 and < 2 ? view.Eye : 2];
        bool useDepth = !view.Shadow && view.CullMode == 1 && stockCallSite is >= 5 and <= 9 && pipeline.Description.DepthWrite &&
            depth.Camera != null && _textures.Get(depth.Texture) != null;
        var key = new TerrainSceneCullKey(renderPass, atlas, view with { ViewProjection = default, OriginUniform = "" },
            visibility, useDepth, parity != null);
        if (skipped.All(skip => skip))
        {
            if (!_terrainSceneCulls.TryGetValue(key, out var empty))
            {
                empty = pools.Select(pool => new TerrainSceneIndirect(pool, null, 0, 0)).ToArray();
                _terrainSceneCulls.Add(key, empty);
            }
            foreach (var pool in pools)
            {
                _terrainOccluderMeshes.Add(pool.Mesh);
                _terrainSceneDrawnPools.Add(pool.Mesh);
            }
            QueueTerrainSceneParity(stockCallSite, renderPass, atlas, view, useDepth, empty, parity);
            return true;
        }
        NativeUniform origin = pipeline.Uniform(string.IsNullOrEmpty(view.OriginUniform) ? "origin" : view.OriginUniform);
        // Every maintained native chunk program puts its per-pool origin in push
        // constants. This keeps the preflighted record/frame snapshots unchanged.
        if (origin.Block != NativeUniformBlock.Push || origin.Size != 12) return false;
        for (int i = 0; i < pools.Length; i++)
            if (!skipped[i] && (!NativeMeshIsDrawable(pipeline, pools[i].Mesh, out _) ||
                (uint)pools[i].Count > DrawPreparationLimits.MaxDrawIndirectCount ||
                TerrainSceneOutputBytes(pools[i], parity != null) > DrawPreparationLimits.MaxStorageBufferRange))
                return false;
        if (!PreflightTerrainSceneDraw(pipeline, textures)) return false;
        ComputeProgram? program = TerrainSceneProgram();
        if (program == null) return false;
        if (!_terrainSceneCulls.TryGetValue(key, out var prepared))
        {
            VulkanTexture sampled = _textures.Get(useDepth ? depth.Texture : _bindless!.PlaceholderTextureId(TextureKind.Texture2D))!;
            // Completed pyramids stay immutable throughout this logical frame,
            // including any partial submissions before a later eye or replay.
            var input = TerrainSceneInput(view, useDepth ? depth : default, parity != null, out ulong inputOffset);
            if (input == null) return false;
            var cullPipeline = _compute.PipelineFor(program, [0u]);
            var scatterPipeline = _compute.PipelineFor(program, [1u]);
            prepared = new TerrainSceneIndirect[pools.Length];
            var sets = new DescriptorSet[pools.Length];
            for (int poolIndex = 0; poolIndex < pools.Length; poolIndex++)
            {
                var pool = pools[poolIndex];
                if (skipped[poolIndex])
                {
                    prepared[poolIndex] = new(pool, null, 0, 0);
                    continue;
                }
                int groups = (pool.Count + 63) / 64;
                ulong bytes = TerrainSceneOutputBytes(pool, parity != null);
                VulkanBuffer output = AllocateTerrainOutput(bytes, parity != null, out ulong outputOffset);
                sets[poolIndex] = _computeArenas[CurrentFrameSlot].GetMixed(program.SetLayout,
                    [new ComputeImageWrite(0, DescriptorType.CombinedImageSampler, sampled.ViewOfMips(0, sampled.MipLevels),
                        _textures.Samplers.Get(ComputeNearest), ImageLayout.ShaderReadOnlyOptimal)], 1,
                    [new DescriptorBufferInfo(input.Handle, 0, input.Size), new DescriptorBufferInfo(output.Handle, outputOffset, bytes),
                        new DescriptorBufferInfo(_terrainSceneTable!.Handle, 0, _terrainSceneTable.Size)]);
                prepared[poolIndex] = new(pool, output, outputOffset, outputOffset + (ulong)(pool.Count * 7 + groups) * 4);
            }
            // All optional admission and capacity checks are now complete. Everything
            // below records the whole call, with no partial-success fallback.
            if (_terrainScenePendingUpload is { } rows)
            {
                fixed (uint* source = rows) _uploads.UploadToBuffer(_terrainSceneTable!, 0, (IntPtr)source, (ulong)rows.Length * 4);
                _terrainSceneUploadedVersion = _terrainSceneFrameVersion;
                _terrainScenePendingUpload = null;
            }
            var barriers = _terrainSceneBarriers ??= _textures.CreateBatcher();
            _textures.Require(barriers, producer, sampled, 0, sampled.MipLevels, ResourceUsage.SampleCompute);
            barriers.Flush(producer);
            uint* push = stackalloc uint[4];
            for (int poolIndex = 0; poolIndex < prepared.Length; poolIndex++)
            {
                var draw = prepared[poolIndex];
                if (draw.Buffer == null) continue;
                var pool = draw.Pool;
                int groups = (pool.Count + 63) / 64;
                DescriptorSet set = sets[poolIndex];
                _context.Api.CmdBindDescriptorSets(producer, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
                push[0] = (uint)(inputOffset / 4); push[1] = 0; push[2] = (uint)pool.Count; push[3] = (uint)pool.First;
                _context.Api.CmdPushConstants(producer, program.Layout, ShaderStageFlags.ComputeBit, 0, 16, push);
                _context.Api.CmdBindPipeline(producer, PipelineBindPoint.Compute, cullPipeline);
                _context.Api.CmdDispatch(producer, (uint)groups, 1, 1);
                _graph.NoteDispatch();
            }
            TerrainSceneBarrier(producer, prepared, parity != null, PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            for (int poolIndex = 0; poolIndex < prepared.Length; poolIndex++)
            {
                var draw = prepared[poolIndex];
                if (draw.Buffer == null) continue;
                var pool = draw.Pool;
                int groups = (pool.Count + 63) / 64;
                if (groups <= 1) continue;
                DescriptorSet set = sets[poolIndex];
                _context.Api.CmdBindDescriptorSets(producer, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
                push[0] = (uint)(inputOffset / 4); push[1] = 0; push[2] = (uint)pool.Count; push[3] = (uint)pool.First;
                _context.Api.CmdPushConstants(producer, program.Layout, ShaderStageFlags.ComputeBit, 0, 16, push);
                _context.Api.CmdBindPipeline(producer, PipelineBindPoint.Compute, scatterPipeline);
                _context.Api.CmdDispatch(producer, (uint)groups, 1, 1);
                _graph.NoteDispatch();
            }
            TerrainSceneBarrier(producer, prepared, parity != null,
                PipelineStageFlags2.DrawIndirectBit | (parity != null ? PipelineStageFlags2.HostBit : 0),
                AccessFlags2.IndirectCommandReadBit | (parity != null ? AccessFlags2.HostReadBit : 0));
            _terrainSceneCulls.Add(key, prepared);
        }
        // Liquid-motion replay uses these very same compacted lists. A distinct eye,
        // cascade, cull policy or visibility snapshot gets a separate cull.
        foreach (var draw in prepared)
        {
            var pool = draw.Pool;
            if (draw.Buffer != null)
            {
                WriteNative(pipeline, origin, (float)(pool.OriginX - view.CameraX),
                    (float)(pool.OriginY - view.CameraY), (float)(pool.OriginZ - view.CameraZ));
                if (!BeginNativeDraw(pipeline, textures, pool.Mesh, out var command, out _, requirePipeline: true))
                    throw new InvalidOperationException("Preflighted resident terrain draw was invalidated: " + NativeDrawRefusal);
                _meshes.Bind(command, _meshes.Get(pool.Mesh)!);
                if (_context.DrawIndirectCountEnabled)
                    _context.Api.CmdDrawIndexedIndirectCount(command, draw.Buffer.Handle, draw.Offset, draw.Buffer.Handle,
                        draw.CountOffset, (uint)pool.Count, 20);
                else _context.Api.CmdDrawIndexedIndirect(command, draw.Buffer.Handle, draw.Offset, (uint)pool.Count, 20);
                _meshes.NoteSceneDrawCommand();
                NoteNativeDraw(NativeDrawKind.Indirect);
            }
            _terrainOccluderMeshes.Add(pool.Mesh);
            _terrainSceneDrawnPools.Add(pool.Mesh);
        }
        QueueTerrainSceneParity(stockCallSite, renderPass, atlas, view, useDepth, prepared, parity);
        return true;
    }

    private static bool SkipTerrainScenePool(in TerrainScenePool pool, in TerrainView view, ulong[] visibility)
    {
        if (pool.ChunkSlots.Length == 0) return true;
        if (view.CullMode is < 1 or > 4) return false;
        bool chunkVisible = false;
        // Two low-first uint header words per ulong; slots beyond the header count are clear.
        for (int i = 0, count = Math.Min(pool.ChunkSlots.Length, visibility.Length); i < count; i++)
            if ((pool.ChunkSlots[i] & visibility[i]) != 0) { chunkVisible = true; break; }
        if (!chunkVisible) return true;
        for (int i = 0, count = view.CullMode == 1 ? 5 : 6; i < count; i++)
        {
            TerrainPlane plane = i switch
            {
                0 => view.Plane0, 1 => view.Plane1, 2 => view.Plane2,
                3 => view.Plane3, 4 => view.Plane4, _ => view.Plane5
            };
            double x = plane.A > 0 ? pool.MaxX : pool.MinX;
            double y = plane.B > 0 ? pool.MaxY : pool.MinY;
            double z = plane.C > 0 ? pool.MaxZ : pool.MinZ;
            // The pool vertex dominates each row's positive vertex; allow for double rounding order.
            if (((x * plane.A + y * plane.B) + z * plane.C) + plane.D < -1e-3) return true;
        }
        return false;
    }

    private void QueueTerrainSceneParity(int site, int pass, int atlas, in TerrainView view, bool useDepth,
        TerrainSceneIndirect[] draws, IReadOnlyDictionary<int, TerrainParityPart>? expected)
    {
        if (expected == null) return;
        if (!_terrainSceneParityResults.TryGetValue(CurrentFrameSlot, out var results))
            _terrainSceneParityResults.Add(CurrentFrameSlot, results = new());
        // Record every pass, including a liquid replay that reuses the same cull output.
        results.Add(new(site, pass, atlas, view, _frameCounter, draws, _terrainSceneRows, expected, useDepth));
    }

    // Called only after this slot's fence completes, before its mapped/coherent
    // diagnostic outputs can be reused. No wait or readback submit.
    private void CollectTerrainSceneParity(int slot)
    {
        if (!_terrainSceneParityResults.TryGetValue(slot, out var results)) return;
        foreach (var result in results)
        {
            var seen = new HashSet<int>();
            foreach (var draw in result.Draws)
            {
                uint* handles = draw.Buffer == null ? null : (uint*)((byte*)draw.Buffer.Mapped + draw.CountOffset + 4);
                HashSet<uint>? depthKept = null;
                if (result.UseDepth && draw.Buffer != null)
                {
                    uint count = *(uint*)((byte*)draw.Buffer.Mapped + draw.CountOffset);
                    uint* commands = (uint*)((byte*)draw.Buffer.Mapped + draw.Offset);
                    depthKept = new HashSet<uint>();
                    for (uint i = 0; i < count; i++) depthKept.Add(commands[i * 5 + 2]);
                }
                for (int i = 0; i < draw.Pool.Count; i++)
                {
                    var row = result.Rows[draw.Pool.First + i];
                    seen.Add(row.Key);
                    bool found = result.Expected.TryGetValue(row.Key, out var expected);
                    uint gpu = handles == null ? 0u : handles[i];
                    TerrainParityChecks++;
                    if (!found || gpu != (expected.StockVisible ? (uint)row.Key : 0u))
                        NoteTerrainParityMismatch(result, row.Key, found ? expected : null, gpu, row.Value);
                    if (depthKept != null)
                    {
                        bool gpuDepthKept = depthKept.Contains(row.Value.FirstIndex);
                        bool stockDepthKept = found && expected.StockDepthVisible;
                        TerrainParityChecks++;
                        if (gpuDepthKept != stockDepthKept)
                            NoteTerrainParityMismatch(result, row.Key, found ? expected : null,
                                gpuDepthKept ? (uint)row.Key : 0u, row.Value, depth: true);
                    }
                }
            }
            foreach (var expected in result.Expected)
                if (!seen.Contains(expected.Key))
                {
                    TerrainParityChecks++;
                    NoteTerrainParityMismatch(result, expected.Key, expected.Value, 0, null);
                }
        }
        results.Clear();
    }

    private void NoteTerrainParityMismatch(TerrainSceneParityResult result, int handle, TerrainParityPart? stock,
        uint gpuHandle, TerrainPartDesc? gpuInputs, bool depth = false)
    {
        TerrainParityMismatches++;
        if (_terrainParityMismatchLogged) return;
        _terrainParityMismatchLogged = true;
        RenderLogger?.Warning("VulkanStory: terrain parity mismatch: frame={0} site={1} pass={2} atlas={3} eye={4} part={5} " +
            "stock={6} GPU={7} GPUHandle={8} stockInputs={9} GPUInputs={10} view={11} stage={12}", result.Frame,
            result.Site, result.Pass, result.Atlas, result.View.Eye, handle, depth ? stock?.StockDepthVisible : stock?.StockVisible,
            gpuHandle != 0, gpuHandle, stock, gpuInputs, result.View, depth ? "depth" : "stock");
    }

    private bool PreflightTerrainSceneDraw(NativePipeline pipeline, ReadOnlySpan<NativeTexture> textures)
    {
        if (!ReferenceEquals(_targets.Bound, _nativeTarget) ||
            !_targets.ScopeFormatsMatch(_nativeTarget!, pipeline.Description.Targets)) return false;
        foreach (var sampled in textures)
            if (sampled.Sampler.IsPresent && _targets.IsBoundDepth(sampled.TextureId) &&
                !pipeline.Description.SamplesBoundDepth) return false;
        _ = GetNativeDrawPipeline(pipeline);
        var program = pipeline.Program;
        if ((program.Interface.UsesFrameBlock || program.Interface.UsesFrameTextures) &&
            !TrySnapshotFrameGlobals(program, out _)) return false;
        if (program.Interface.HasUniformBlock && !program.HasSnapshotFor(_frameCounter))
        {
            if (!_frames.Current.TryAllocateUniforms(program.UniformShadow.Length, out var allocation)) return false;
            fixed (byte* source = program.UniformShadow)
                System.Buffer.MemoryCopy(source, (void*)allocation.Pointer, program.UniformShadow.Length, program.UniformShadow.Length);
            program.NoteSnapshot(_frameCounter, allocation.Offset);
        }
        foreach (var block in program.Interface.UniformBlocks)
            if (_clientUniformBuffers.BoundBlocks.TryGetValue(block.BlockName, out int handle) &&
                _clientUniformBuffers.Buffers.TryGetValue(handle, out var ubo) &&
                !TrySnapshotClientBlock(ubo, program, out _)) return false;
        return true;
    }

    private static bool ValidTerrainPart(in TerrainPartDesc part) => part.PoolMesh > 0 && part.IndexCount > 0 &&
        float.IsFinite((float)part.CenterX) && float.IsFinite((float)part.CenterY) && float.IsFinite((float)part.CenterZ) &&
        double.IsFinite(part.PoolOriginX) && double.IsFinite(part.PoolOriginY) && double.IsFinite(part.PoolOriginZ) &&
        float.IsFinite(part.Radius) && float.IsFinite(part.RadiusY) && float.IsFinite(part.RadiusZ) &&
        part.Radius >= 0 && part.RadiusY >= 0 && part.RadiusZ >= 0 && part.ChunkSlot >= 0;

    private bool FreezeTerrainScene()
    {
        if (_terrainSceneFrozen) return true;
        lock (_terrainSceneLock)
        {
            _terrainSceneFrameVersion = _terrainSceneVersion;
            if (_terrainSceneUploadedVersion != _terrainSceneVersion)
            {
                if (_terrainSceneParts.Values.Any(part => !ValidTerrainPart(part))) return false;
                var parts = _terrainSceneParts.OrderBy(pair => pair.Value.PoolMesh).ThenBy(pair => pair.Value.FirstIndex).ToArray();
                int capacity = Math.Max(64, _terrainSceneCapacity);
                while (capacity < parts.Length) capacity = checked(capacity * 2);
                ulong bytes = (ulong)capacity * TerrainSceneRowWords * 4;
                if (bytes > DrawPreparationLimits.MaxStorageBufferRange) return false;
                if (_terrainSceneTable == null || capacity != _terrainSceneCapacity)
                {
                    VulkanBuffer replacement;
                    try
                    {
                        replacement = new VulkanBuffer(_context, bytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                            MemoryPropertyFlags.DeviceLocalBit);
                    }
                    catch (VulkanMemoryAllocationException error) when (error.Result == Result.ErrorOutOfDeviceMemory) { return false; }
                    if (_terrainSceneTable != null) _frames.DeferDeletion(_terrainSceneTable);
                    _terrainSceneTable = replacement;
                    _terrainSceneCapacity = capacity;
                }
                uint[] rows = new uint[Math.Max(1, parts.Length) * TerrainSceneRowWords];
                var pools = new List<TerrainScenePool>();
                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i].Value;
                    Span<uint> row = rows.AsSpan(i * TerrainSceneRowWords, TerrainSceneRowWords);
                    row[0] = BitConverter.SingleToUInt32Bits((float)part.CenterX);
                    row[1] = BitConverter.SingleToUInt32Bits((float)part.CenterY);
                    row[2] = BitConverter.SingleToUInt32Bits((float)part.CenterZ);
                    row[3] = BitConverter.SingleToUInt32Bits(part.Radius); row[4] = BitConverter.SingleToUInt32Bits(part.RadiusY);
                    row[5] = BitConverter.SingleToUInt32Bits(part.RadiusZ); row[6] = part.FirstIndex; row[7] = part.IndexCount;
                    row[8] = (uint)part.LodLevel; row[9] = (uint)part.ChunkSlot; row[10] = part.Hidden ? 1u : 0u;
                    row[11] = (uint)part.PoolMesh; row[12] = (uint)part.RenderPass; row[13] = (uint)part.Atlas;
                    row[14] = (uint)parts[i].Key;
                    if (pools.Count == 0 || pools[^1].Mesh != part.PoolMesh)
                        pools.Add(new(part.PoolMesh, part.RenderPass, part.Atlas, i, 1, part.PoolOriginX, part.PoolOriginY, part.PoolOriginZ));
                    else pools[^1] = pools[^1] with { Count = pools[^1].Count + 1 };
                }
                for (int poolIndex = 0; poolIndex < pools.Count; poolIndex++)
                {
                    var pool = pools[poolIndex];
                    double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
                    double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
                    int words = 0;
                    for (int i = pool.First; i < pool.First + pool.Count; i++)
                    {
                        var part = parts[i].Value;
                        if (part.Hidden) continue;
                        // Mirror the row's float centers and float division before widening to double.
                        double x = (float)part.CenterX, y = (float)part.CenterY, z = (float)part.CenterZ;
                        double rx = part.Radius / 1.7320508f, ry = part.RadiusY / 1.7320508f, rz = part.RadiusZ / 1.7320508f;
                        minX = Math.Min(minX, x - rx); minY = Math.Min(minY, y - ry); minZ = Math.Min(minZ, z - rz);
                        maxX = Math.Max(maxX, x + rx); maxY = Math.Max(maxY, y + ry); maxZ = Math.Max(maxZ, z + rz);
                        words = Math.Max(words, (part.ChunkSlot >> 6) + 1);
                    }
                    var chunks = new ulong[words];
                    for (int i = pool.First; i < pool.First + pool.Count; i++)
                    {
                        var part = parts[i].Value;
                        if (!part.Hidden) chunks[part.ChunkSlot >> 6] |= 1UL << (part.ChunkSlot & 63);
                    }
                    pools[poolIndex] = pool with { MinX = minX, MinY = minY, MinZ = minZ,
                        MaxX = maxX, MaxY = maxY, MaxZ = maxZ, ChunkSlots = chunks };
                }
                _terrainScenePendingUpload = rows;
                _terrainSceneRows = parts;
                _terrainScenePools = pools.ToArray();
            }
            _terrainSceneFrozen = true;
            for (int eye = 0; eye < 3; eye++)
            {
                var depth = eye == 2 ? _occlusionDepth : _stereoOcclusionDepths[eye];
                _terrainSceneDepths[eye] = depth == null ? default : new(depth.CompletedGpuTexture,
                    depth.CompletedGpuWidth, depth.CompletedGpuHeight, depth.CompletedGpuCamera);
            }
        }
        return true;
    }

    private ComputeProgram? TerrainSceneProgram()
    {
        if (_terrainSceneProgram == 0)
        {
            string source = Culling.OcclusionDepth.Source("draws.comp");
            _terrainSceneProgram = CreateComputeProgram(source, "resident-terrain-draws",
                [new(0, ComputeSlotKind.Sampled), new(1, ComputeSlotKind.Buffer), new(2, ComputeSlotKind.Buffer), new(3, ComputeSlotKind.Buffer)], 16, 64, 1);
        }
        return _compute.Get(_terrainSceneProgram);
    }

    private VulkanBuffer? TerrainSceneInput(in TerrainView view, TerrainSceneDepth depth, bool parity, out ulong offset)
    {
        _terrainSceneVisibility.TryGetValue(view.Eye, out var visibility);
        visibility ??= [];
        ulong bytes = (ulong)TerrainSceneHeaderWords * 4 + (ulong)visibility.Length * 8;
        offset = 0;
        if (bytes > DrawPreparationLimits.MaxStorageBufferRange) return null;
        if (_terrainSceneInputs.Length == 0)
        {
            _terrainSceneInputs = new VulkanBuffer?[_frames.FramesInFlight];
            _terrainSceneInputCursors = new ulong[_frames.FramesInFlight];
        }
        int slot = CurrentFrameSlot;
        offset = _terrainSceneInputCursors[slot];
        VulkanBuffer? input = _terrainSceneInputs[slot];
        if (input == null || input.Size - Math.Min(offset, input.Size) < bytes)
        {
            ulong capacity = Math.Max(65536UL, input?.Size ?? 0);
            while (capacity < bytes || capacity < offset + bytes) capacity *= 2;
            capacity = Math.Min(capacity, DrawPreparationLimits.MaxStorageBufferRange);
            if (capacity < bytes) return null;
            VulkanBuffer replacement;
            try
            {
                replacement = new VulkanBuffer(_context, capacity, BufferUsageFlags.StorageBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);
            }
            catch (VulkanMemoryAllocationException error) when (error.Result == Result.ErrorOutOfDeviceMemory) { return null; }
            if (input != null) _frames.DeferDeletion(input);
            input = _terrainSceneInputs[slot] = replacement;
            offset = 0;
        }
        _terrainSceneInputCursors[slot] = offset + bytes;
        uint* words = (uint*)((byte*)input.Mapped + offset);
        new Span<uint>(words, TerrainSceneHeaderWords).Clear();
        float* values = (float*)words;
        if (depth.Camera is { } camera && OcclusionProjection.From(camera.Projection) is { } projection)
        {
            camera.View.AsSpan(0, 16).CopyTo(new Span<float>(values, 16));
            camera.Projection.AsSpan(0, 16).CopyTo(new Span<float>(values + 16, 16));
            values[32] = depth.Width; values[33] = depth.Height;
            values[34] = projection.DepthUnpackMul; values[35] = projection.DepthUnpackAdd;
            double dx = view.CameraX - camera.CameraX, dy = view.CameraY - camera.CameraY, dz = view.CameraZ - camera.CameraZ;
            values[38] = MathF.BitIncrement((float)(Math.Sqrt(dx * dx + dy * dy + dz * dz) + 0.5));
            *(double*)(words + 136) = camera.CameraX; *(double*)(words + 138) = camera.CameraY; *(double*)(words + 140) = camera.CameraZ;
            words[85] = 1;
        }
        *(double*)(words + 64) = view.CameraX; *(double*)(words + 66) = view.CameraY; *(double*)(words + 68) = view.CameraZ;
        *(double*)(words + 70) = view.PlayerBlockX; *(double*)(words + 72) = view.PlayerBlockZ;
        *(double*)(words + 74) = view.Lod0BiasSq; *(double*)(words + 76) = view.Lod2BiasSq;
        words[78] = (uint)view.ViewDistanceSq; words[79] = (uint)view.CullMode;
        *(double*)(words + 80) = view.ShadowRangeX; *(double*)(words + 82) = view.ShadowRangeZ;
        words[86] = (uint)visibility.Length * 2;
        words[87] = parity ? 1u : 0u;
        TerrainPlane[] planes = [view.Plane0, view.Plane1, view.Plane2, view.Plane3, view.Plane4, view.Plane5];
        for (int i = 0; i < planes.Length; i++)
        {
            double* plane = (double*)(words + 88 + i * 8);
            plane[0] = planes[i].A; plane[1] = planes[i].B; plane[2] = planes[i].C; plane[3] = planes[i].D;
        }
        visibility.AsSpan().CopyTo(new Span<ulong>(words + TerrainSceneHeaderWords, visibility.Length));
        return input;
    }

    private void TerrainSceneBarrier(CommandBuffer command, ReadOnlySpan<TerrainSceneIndirect> draws, bool parity,
        PipelineStageFlags2 stage, AccessFlags2 access)
    {
        Span<BufferMemoryBarrier2> barriers = draws.Length <= 64
            ? stackalloc BufferMemoryBarrier2[draws.Length] : new BufferMemoryBarrier2[draws.Length];
        int count = 0;
        foreach (var draw in draws)
        {
            if (draw.Buffer == null) continue;
            // Keep each exact range, including outputs in separate overflow/readback buffers,
            // in one dependency so the whole stage has just one synchronization boundary.
            barriers[count++] = new BufferMemoryBarrier2 { SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = stage, DstAccessMask = access, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = draw.Buffer.Handle, Offset = draw.Offset,
                Size = TerrainSceneOutputBytes(draw.Pool, parity) };
        }
        if (count == 0) return;
        fixed (BufferMemoryBarrier2* ranges = barriers)
        {
            var dependency = new DependencyInfo { SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = (uint)count, PBufferMemoryBarriers = ranges };
            _context.Api.CmdPipelineBarrier2(command, &dependency);
        }
    }

    private void BeginTerrainSceneFrame()
    {
        _terrainSceneFrozen = false;
        _terrainSceneDrawnPools.Clear();
        _terrainSceneCulls.Clear();
        _terrainSceneVisibility.Clear();
        if (_terrainSceneInputCursors.Length > CurrentFrameSlot) _terrainSceneInputCursors[CurrentFrameSlot] = 0;
        // Collected later by PrepareGraphicsSlot; the completed GPU identities are read lazily
        // when the first resident draw of the frame freezes the table.
        Array.Clear(_terrainSceneDepths);
    }

    internal void ResetTerrainScene()
    {
        lock (_terrainSceneLock)
        {
            _terrainSceneParts.Clear(); _terrainScenePools = []; _terrainSceneRows = []; _terrainSceneVersion++;
            _terrainSceneFrozen = false;
        }
        _terrainSceneVisibility.Clear();
        _terrainSceneDrawnPools.Clear();
        _terrainSceneCulls.Clear();
        if (_terrainSceneTable != null) _frames.DeferDeletion(_terrainSceneTable);
        _terrainSceneTable = null; _terrainSceneCapacity = 0; _terrainSceneUploadedVersion = -1;
        _terrainScenePendingUpload = null;
    }

    private void DisposeTerrainSceneIdle()
    {
        _terrainSceneTable?.Dispose(); _terrainSceneTable = null;
        foreach (var input in _terrainSceneInputs) input?.Dispose();
        _terrainSceneInputs = []; _terrainSceneInputCursors = []; _terrainSceneParts.Clear(); _terrainScenePools = [];
        _terrainSceneRows = []; _terrainSceneParityResults.Clear();
        foreach (var buffers in _terrainReadbackBuffers.Values)
            foreach (var buffer in buffers) buffer.Dispose();
        _terrainReadbackBuffers.Clear(); _terrainReadbackBufferCursors = [];
    }
}
