using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;

namespace VulkanStory.Game.Performance;

internal static partial class GpuChunkMeshing
{
    [ThreadStatic] private static bool referenceProducer;
    private static long equalityChecks, equalityMismatches;
    internal static long EqualityChecks => Interlocked.Read(ref equalityChecks);
    internal static long EqualityMismatches => Interlocked.Read(ref equalityMismatches);

    // Replay ONLY admitted stock cubes, at their original call site, using the
    // identical snapshot, revision, textures and TCTCache. Isolated pass/atlas
    // meshes never reach stock publication or its recycler.
    private static void CaptureReference(Work work, object producer, TCTCache vars, bool solid)
    {
        if (work.ReferenceFailed) return;
        var meshes = work.ReferenceMeshes ??= new();
        MeshData[] pools = vars.tct.GetPoolForPass(vars.RenderPass, 1);
        MeshData[] saved = pools.ToArray();
        int flags = vars.drawFaceFlags;
        int[] light = vars.CurrentLightRGBByCorner.ToArray();
        float xMin = vars.xMin, xMax = vars.xMax, yMin = vars.yMin, yMax = vars.yMax, zMin = vars.zMin, zMax = vars.zMax;
        try
        {
            bool edge = vars.lx is 0 or 31 || vars.ly is 0 or 31 || vars.lz is 0 or 31;
            for (int atlas = 0; atlas < pools.Length; atlas++)
            {
                var bin = new ChunkMeshBin(edge, (int)vars.RenderPass, atlas);
                if (!meshes.TryGetValue(bin, out var mesh))
                    meshes.Add(bin, mesh = new MeshData(4, 6, false, true, true, true)
                        { CustomInts = new CustomMeshDataPartInt() });
                pools[atlas] = mesh;
            }
            if (solid && GpuCulled(work, vars.block))
                vars.drawFaceFlags &= CpuFaces(vars.tct, vars.block, vars.extIndex3d, vars.posX, vars.posY, vars.posZ);
            referenceProducer = true;
            ((IBlockTesselator)producer).Tesselate(vars);
        }
        catch (Exception error)
        {
            work.ReferenceFailed = true;
            HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "equality-reference-failed", new { error = error.ToString() });
        }
        finally
        {
            referenceProducer = false;
            saved.CopyTo(pools, 0); vars.drawFaceFlags = flags; light.CopyTo(vars.CurrentLightRGBByCorner, 0);
            vars.xMin = xMin; vars.xMax = xMax; vars.yMin = yMin; vars.yMax = yMax; vars.zMin = zMin; vars.zMax = zMax;
        }
    }

    private static void CompareReference(Work work, ChunkMeshRange[] ranges)
    {
        try
        {
            uint[]? words = backend?.ChunkMeshingEqualityOutput(work.Batch);
            if (work.ReferenceFailed || words == null || work.ReferenceMeshes == null)
            {
                HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "equality-unavailable",
                    new { referenceFailed = work.ReferenceFailed, readbackAvailable = words != null });
                return;
            }
            object? mismatch = null;
            int cpuVertices = work.ReferenceMeshes.Values.Sum(m => m.VerticesCount);
            int gpuVertices = ranges.Sum(r => r.FaceCount * 4);
            if (cpuVertices != gpuVertices)
                mismatch = new { field = "total-vertex-count", cpu = cpuVertices, gpu = gpuVertices };
            for (int binIndex = 0; binIndex < work.Bins.Count && mismatch == null; binIndex++)
            {
                var bin = work.Bins[binIndex];
                work.ReferenceMeshes.TryGetValue(bin, out var mesh);
                var range = ranges.FirstOrDefault(r => r.Bin == binIndex);
                int vertices = mesh?.VerticesCount ?? 0, indices = mesh?.IndicesCount ?? 0;
                if (vertices != range.FaceCount * 4 || indices != range.FaceCount * 6)
                { mismatch = new { bin, field = "counts", cpuVertices = vertices, gpuVertices = range.FaceCount * 4,
                    cpuIndices = indices, gpuIndices = range.FaceCount * 6 }; break; }
                if (vertices == 0) continue;
                // Use the same stock packing as CPU publication, including UV
                // quantization. The GPU payload is this exact 16-word FaceData
                // plus four untouched vertex RGBA/light words per face.
                var packed = GameSsboFacePacking.Pack(mesh!);
                ReadOnlySpan<uint> expected = MemoryMarshal.Cast<FaceData, uint>(packed.AsSpan(0, vertices / 4));
                ReadOnlySpan<uint> rgba = MemoryMarshal.Cast<byte, uint>(mesh!.Rgba.AsSpan(0, vertices * 4));
                string[] fields = ["position.x", "position.y", "position.z", "uv.origin", "edge1.x", "edge1.y", "edge1.z", "uv.size",
                    "flags.0", "flags.1", "flags.2", "flags.3", "edge2.x", "edge2.y", "edge2.z", "colormap"];
                int[] pattern = [0, 1, 2, 0, 2, 3];
                for (int face = 0; face < vertices / 4 && mismatch == null; face++)
                {
                    for (int field = 0; field < 20; field++)
                    {
                        uint cpu = field < 16 ? expected[face * 16 + field] : rgba[face * 4 + field - 16];
                        uint gpu = words[(range.FirstFace + face) * 20 + field];
                        if (cpu == gpu) continue;
                        int vertex = face * 4 + (field >= 16 ? field - 16 : field is >= 8 and <= 11 ? field - 8 :
                            field is >= 4 and <= 6 ? 1 : field is >= 12 and <= 14 ? 3 : 0);
                        int lane = 0;
                        if (field >= 16) while (((cpu >> (lane * 8)) & 255) == ((gpu >> (lane * 8)) & 255)) lane++;
                        mismatch = new { bin, face, vertex, field = field < 16 ? fields[field] : "rgba/light.byte" + lane, cpu, gpu,
                            cpuByte = field >= 16 ? (uint?)((cpu >> (lane * 8)) & 255) : null,
                            gpuByte = field >= 16 ? (uint?)((gpu >> (lane * 8)) & 255) : null }; break;
                    }
                    // GPU SSBO terrain uses the renderer's immutable stock quad
                    // index pattern; compare the CPU's actual emitted indices.
                    for (int i = 0; i < 6 && mismatch == null; i++)
                        if (mesh.Indices[face * 6 + i] != face * 4 + pattern[i])
                            mismatch = new { bin, face, field = "index", index = face * 6 + i,
                                cpu = mesh.Indices[face * 6 + i], gpu = face * 4 + pattern[i] };
                }
            }
            Interlocked.Increment(ref equalityChecks);
            if (mismatch != null) Interlocked.Increment(ref equalityMismatches);
            HeadlessChunkMeshingProbe.Equality(work.ProbeBatch, work.Revision, work.Generation, mismatch,
                cpuVertices, gpuVertices);
        }
        catch (Exception error)
        { HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "equality-failed", new { error = error.ToString() }); }
    }
}
