using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Vintagestory.API.Client;

/// <summary>
/// Issue #73: recycles the per-clone <see cref="CustomMeshDataPart{T}"/> buffers
/// that chunk mesh finalization allocates. Profiling (200 chunks, greedy meshing
/// off, VD256) showed ~45 KB/chunk of allocation, 99.4% of it in the clone step,
/// and a per-clone extra-array dump attributed all of it to CustomInts (always),
/// CustomShorts (TopSoil) and CustomFloats (Liquid) - the CustomMeshDataPart&lt;T&gt;
/// clones. The big xyz/uv/rgba/flags/indices arrays are already pooled by
/// MeshDataRecycler; these were the last unpooled per-chunk buffers.
///
/// This type is Cecil-injected into the game API assembly (it cannot add methods
/// to the sealed vanilla MeshData; instead ChunkTesselator, which is fully
/// transplanted, calls OptimumMeshPartPool.CloneChunkMesh and OptimumMeshPartPool
/// reclaims the parts after upload). It touches only public MeshData /
/// CustomMeshDataPart members.
///
/// Lifecycle (verified against the decompiled 1.22.7 client, not assumed):
///  - CloneChunkMesh runs on the single "tesselateterrain" thread in
///    ChunkTesselator.populateTesselatedChunkPart (the only caller of the chunk
///    clone; entity/item/GUI meshes never touch this pool).
///  - The cloned mesh's Custom* buffers are consumed read-only on the render
///    thread in MeshDataPool.AddModel (upload), then reclaimed via Reclaim right
///    after upload in TesselatedChunkPart.AddToPools. Same lifecycle as the basic
///    arrays MeshDataRecycler already pools.
///
/// Threading discipline mirrors MeshDataRecycler exactly: the only cross-thread
/// operation is Reclaim (render thread enqueues to a ConcurrentQueue). Pulls and
/// draining that queue happen only on the tessellation thread, so the
/// size-bucketed free lists are single-thread-owned and lock-free. Gated on
/// OptimumConfig.MeshPartPoolActive, which additionally requires a single
/// tessellation worker; with more than one worker the pool is bypassed (fresh
/// clone) so raising the worker count can never race the free lists.
/// </summary>
public static class OptimumMeshPartPool
{
    private static readonly ConcurrentQueue<CustomMeshDataPartInt> _returnInt = new();
    private static readonly ConcurrentQueue<CustomMeshDataPartShort> _returnShort = new();
    private static readonly ConcurrentQueue<CustomMeshDataPartFloat> _returnFloat = new();

    private const int MaxPerType = 512;
    private static readonly List<CustomMeshDataPartInt> _freeInt = new();
    private static readonly List<CustomMeshDataPartShort> _freeShort = new();
    private static readonly List<CustomMeshDataPartFloat> _freeFloat = new();

    // ---- Render thread: offer parts back (enqueue only, never touch free lists). ----
    public static void Reclaim(MeshData mesh)
    {
        if (mesh == null) return;
        if (mesh.CustomInts != null) _returnInt.Enqueue(mesh.CustomInts);
        if (mesh.CustomShorts != null) _returnShort.Enqueue(mesh.CustomShorts);
        if (mesh.CustomFloats != null) _returnFloat.Enqueue(mesh.CustomFloats);
    }

    private static void Drain()
    {
        while (_returnInt.TryDequeue(out var p)) { if (_freeInt.Count < MaxPerType) _freeInt.Add(p); }
        while (_returnShort.TryDequeue(out var p)) { if (_freeShort.Count < MaxPerType) _freeShort.Add(p); }
        while (_returnFloat.TryDequeue(out var p)) { if (_freeFloat.Count < MaxPerType) _freeFloat.Add(p); }
    }

    /// <summary>
    /// Tess thread only. Clones a chunk mesh the same way MeshData.CloneUsingRecycler
    /// does (recycled basic arrays via MeshDataRecycler, fresh extra arrays), but
    /// pulls the CustomMeshDataPart clones from the pool. Returns null when the mesh
    /// does not qualify for recycling (caller falls back to src.CloneUsingRecycler()).
    /// Output renders identically to CloneUsingRecycler: same counts, same copied
    /// Values[0..Count), same interleave metadata.
    /// </summary>
    public static MeshData CloneChunkMesh(MeshData src)
    {
        // Same qualification test as MeshData.CloneUsingRecycler.
        int recyclableSizeInBytes = src.VerticesCount * MeshData.BaseSizeInBytes;
        if (recyclableSizeInBytes < MeshDataRecycler.MinimumSizeForRecycling ||
            src.Uv == null || src.Rgba == null || src.Flags == null ||
            src.VerticesPerFace != MeshData.StandardVerticesPerFace ||
            src.IndicesPerFace != MeshData.StandardIndicesPerFace)
        {
            return null;
        }
        int requiredSize = Math.Max(src.VerticesCount,
            ((src.IndicesCount + MeshData.StandardIndicesPerFace - 1) / MeshData.StandardIndicesPerFace) * MeshData.StandardVerticesPerFace);
        if (requiredSize > src.VerticesCount * 1.05f ||
            requiredSize * MeshData.StandardIndicesPerFace / MeshData.StandardVerticesPerFace > src.IndicesCount * 1.2f)
        {
            return null;
        }

        Drain();

        MeshData dst = MeshData.Recycler.GetOrCreateMesh(requiredSize);

        // --- basic data (mirrors CopyBasicData; dst arrays sized by recycler) ---
        dst.VerticesPerFace = src.VerticesPerFace;
        dst.IndicesPerFace = src.IndicesPerFace;
        dst.SetVerticesCount(src.VerticesCount);
        dst.SetIndicesCount(src.IndicesCount);
        Array.Copy(src.xyz, dst.xyz, src.VerticesCount * 3);
        Array.Copy(src.Uv, dst.Uv, src.VerticesCount * 2);
        Array.Copy(src.Rgba, dst.Rgba, src.VerticesCount * 4);
        Array.Copy(src.Flags, dst.Flags, src.VerticesCount);
        Array.Copy(src.Indices, dst.Indices, src.IndicesCount);

        // --- extra data (mirrors CloneExtraData; Custom* routed through the pool) ---
        if (src.Normals != null) dst.Normals = FastCopyInt(src.Normals, src.NormalsCount);
        if (src.XyzFaces != null) { dst.XyzFaces = FastCopyByte(src.XyzFaces, src.XyzFacesCount); dst.XyzFacesCount = src.XyzFacesCount; }
        if (src.TextureIndices != null) { dst.TextureIndices = FastCopyByte(src.TextureIndices, src.TextureIndicesCount); dst.TextureIndicesCount = src.TextureIndicesCount; dst.TextureIds = (int[])src.TextureIds.Clone(); }
        if (src.ClimateColorMapIds != null) { dst.ClimateColorMapIds = FastCopyByte(src.ClimateColorMapIds, src.ColorMapIdsCount); dst.ColorMapIdsCount = src.ColorMapIdsCount; }
        if (src.SeasonColorMapIds != null) { dst.SeasonColorMapIds = FastCopyByte(src.SeasonColorMapIds, src.ColorMapIdsCount); dst.ColorMapIdsCount = src.ColorMapIdsCount; }
        if (src.RenderPassesAndExtraBits != null) { dst.RenderPassesAndExtraBits = FastCopyShort(src.RenderPassesAndExtraBits, src.RenderPassCount); dst.RenderPassCount = src.RenderPassCount; }

        dst.CustomFloats = src.CustomFloats == null ? null : ClonePartFloat(src.CustomFloats);
        dst.CustomShorts = src.CustomShorts == null ? null : ClonePartShort(src.CustomShorts);
        dst.CustomBytes = src.CustomBytes == null ? null : src.CustomBytes.Clone();
        dst.CustomInts = src.CustomInts == null ? null : ClonePartInt(src.CustomInts);

        return dst;
    }

    // Only pool parts with default (auto) allocation size, so the reused part
    // reproduces the source exactly (AllocationSize == Count). Chunk-mesh parts
    // are always auto (never call SetAllocationSize); anything else falls back
    // to the vanilla per-type Clone(), which is always correct.
    private static CustomMeshDataPartInt ClonePartInt(CustomMeshDataPartInt src)
    {
        if (src.AllocationSize != src.Count) { Vintagestory.API.Config.OptimumDiagnostics.MeshPartPool.Skip(); return src.Clone(); }
        CustomMeshDataPartInt reuse = TakeFit(_freeInt, src.Count);
        if (reuse == null) { Vintagestory.API.Config.OptimumDiagnostics.MeshPartPool.Skip(); reuse = new CustomMeshDataPartInt(); }
        else Vintagestory.API.Config.OptimumDiagnostics.MeshPartPool.Hit();
        CopyInt(src, reuse);
        return reuse;
    }
    private static CustomMeshDataPartShort ClonePartShort(CustomMeshDataPartShort src)
    {
        if (src.AllocationSize != src.Count) { Vintagestory.API.Config.OptimumDiagnostics.MeshPartPool.Skip(); return src.Clone(); }
        CustomMeshDataPartShort reuse = TakeFit(_freeShort, src.Count);
        if (reuse == null) { Vintagestory.API.Config.OptimumDiagnostics.MeshPartPool.Skip(); reuse = new CustomMeshDataPartShort(); }
        else Vintagestory.API.Config.OptimumDiagnostics.MeshPartPool.Hit();
        CopyShort(src, reuse);
        return reuse;
    }
    private static CustomMeshDataPartFloat ClonePartFloat(CustomMeshDataPartFloat src)
    {
        if (src.AllocationSize != src.Count) { Vintagestory.API.Config.OptimumDiagnostics.MeshPartPool.Skip(); return src.Clone(); }
        CustomMeshDataPartFloat reuse = TakeFit(_freeFloat, src.Count);
        if (reuse == null) { Vintagestory.API.Config.OptimumDiagnostics.MeshPartPool.Skip(); reuse = new CustomMeshDataPartFloat(); }
        else Vintagestory.API.Config.OptimumDiagnostics.MeshPartPool.Hit();
        CopyFloat(src, reuse);
        return reuse;
    }

    private static void CopyInt(CustomMeshDataPartInt src, CustomMeshDataPartInt dst)
    {
        dst.Count = src.Count;
        if (dst.Values == null || dst.Values.Length < src.Count) dst.Values = new int[src.Count];
        Array.Copy(src.Values, dst.Values, src.Count);
        CopyInterleave(src, dst);
    }
    private static void CopyShort(CustomMeshDataPartShort src, CustomMeshDataPartShort dst)
    {
        dst.Count = src.Count;
        if (dst.Values == null || dst.Values.Length < src.Count) dst.Values = new short[src.Count];
        Array.Copy(src.Values, dst.Values, src.Count);
        CopyInterleave(src, dst);
    }
    private static void CopyFloat(CustomMeshDataPartFloat src, CustomMeshDataPartFloat dst)
    {
        dst.Count = src.Count;
        if (dst.Values == null || dst.Values.Length < src.Count) dst.Values = new float[src.Count];
        Array.Copy(src.Values, dst.Values, src.Count);
        CopyInterleave(src, dst);
    }
    private static void CopyInterleave<T>(CustomMeshDataPart<T> src, CustomMeshDataPart<T> dst)
    {
        dst.InterleaveSizes = src.InterleaveSizes == null ? null : (int[])src.InterleaveSizes.Clone();
        dst.InterleaveOffsets = src.InterleaveOffsets == null ? null : (int[])src.InterleaveOffsets.Clone();
        dst.InterleaveStride = src.InterleaveStride;
        dst.Instanced = src.Instanced;
        dst.StaticDraw = src.StaticDraw;
        dst.BaseOffset = src.BaseOffset;
    }

    private static T TakeFit<T>(List<T> free, int needCount) where T : class
    {
        for (int i = free.Count - 1; i >= 0; i--)
        {
            int len = BufLen(free[i]);
            if (len >= needCount)
            {
                T hit = free[i];
                free[i] = free[free.Count - 1];
                free.RemoveAt(free.Count - 1);
                return hit;
            }
        }
        return null;
    }

    private static int BufLen(object p)
    {
        switch (p)
        {
            case CustomMeshDataPartInt i: return i.Values?.Length ?? 0;
            case CustomMeshDataPartShort s: return s.Values?.Length ?? 0;
            case CustomMeshDataPartFloat f: return f.Values?.Length ?? 0;
            default: return 0;
        }
    }

    private static int[] FastCopyInt(int[] a, int count) { var b = new int[count]; Array.Copy(a, b, count); return b; }
    private static byte[] FastCopyByte(byte[] a, int count) { var b = new byte[count]; Array.Copy(a, b, count); return b; }
    private static short[] FastCopyShort(short[] a, int count) { var b = new short[count]; Array.Copy(a, b, count); return b; }

    /// <summary>Drop all pooled buffers (world unload / recycler reset).</summary>
    public static void Clear()
    {
        while (_returnInt.TryDequeue(out _)) { }
        while (_returnShort.TryDequeue(out _)) { }
        while (_returnFloat.TryDequeue(out _)) { }
        _freeInt.Clear();
        _freeShort.Clear();
        _freeFloat.Clear();
    }

    // ---- internal test seams (no MeshDataRecycler/game instance needed) ----
    internal static CustomMeshDataPartInt TestClonePartInt(CustomMeshDataPartInt src) => ClonePartInt(src);
    internal static int TestFreeIntCount => _freeInt.Count;
    internal static void TestReclaimInt(CustomMeshDataPartInt p) { _returnInt.Enqueue(p); }
    internal static void TestDrain() => Drain();
}
