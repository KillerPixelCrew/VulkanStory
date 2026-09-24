using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    // --------------------------------------------------------------------- meshes

    public int CreateMesh(MeshData data, bool staticDraw)
    {
        // Sized as GL's UploadMesh sizes them. Every part follows the vertex
        // count except flags, which GL allocates at the array's full length -
        // a mesh that later grows within that capacity updates its flags in
        // place there, and would overflow a vertex-count-sized buffer here.
        int vertices = data.VerticesCount;
        int id = _meshes.CreateEmpty(
            data.xyz != null ? vertices * 3 * sizeof(float) : 0,
            data.Normals != null ? vertices * sizeof(int) : 0,
            data.Uv != null ? vertices * 2 * sizeof(float) : 0,
            data.Rgba != null ? vertices * 4 : 0,
            data.Flags != null ? data.Flags.Length * sizeof(int) : 0,
            data.IndicesCount * sizeof(int),
            data.CustomFloats, data.CustomShorts, data.CustomBytes, data.CustomInts,
            data.mode, staticDraw, ssbo: false, signedCustomShorts: true);

        UpdateMesh(id, data);
        return id;
    }

    public int CreateEmptyMesh(
        int xyzSize, int normalsSize, int uvSize, int rgbaSize, int flagsSize, int indicesSize,
        CustomMeshDataPartFloat customFloats, CustomMeshDataPartShort customShorts,
        CustomMeshDataPartByte customBytes, CustomMeshDataPartInt customInts,
        EnumDrawMode drawMode, bool staticDraw, bool ssbo) =>
        _meshes.CreateEmpty(xyzSize, normalsSize, uvSize, rgbaSize, flagsSize, indicesSize,
            customFloats, customShorts, customBytes, customInts, drawMode, staticDraw, ssbo);

    /// <summary>
    /// Writes a mesh's data, honouring the destination offset each part carries.
    ///
    /// Those offsets are the whole point. The game pools chunk meshes: one large
    /// mesh holds many chunks, and each chunk is handed the same mesh with the
    /// byte offset of its own slice in every part. GL's updateVAO takes that
    /// offset as the destination for a glBufferSubData, so writing it at zero
    /// instead stacks every chunk in the world on top of the first one - which
    /// renders as no terrain at all.
    ///
    /// The counts are per part as well, not VerticesCount: a part can be absent
    /// or shorter than the vertex count, and the custom buffers have no fixed
    /// relationship to it.
    /// </summary>
    public void UpdateMesh(int meshId, MeshData data)
    {
        // An SSBO mesh's xyz slot holds packed face records, written through
        // UpdateMeshStorageBuffer; positions never belong there. The game hands
        // the same MeshData to both calls, so without this the positions would
        // land on top of the records - or, depending on order, under them.
        bool ssbo = _meshes.IsSsbo(meshId);

        if (data.xyz != null && data.XyzCount > 0 && !ssbo)
        {
            fixed (float* source = data.xyz)
            {
                _meshes.Write(meshId, MeshManager.BufferXyz, data.XyzOffset,
                    (IntPtr)source, data.XyzCount * sizeof(float));
            }
        }
        // The normals, uv and flags streams have no buffer on an SSBO mesh - the
        // face records carry what the shader needs from them - so GL's SSBO
        // update path never writes them either.
        if (data.Normals != null && data.VerticesCount > 0 && !ssbo)
        {
            fixed (int* source = data.Normals)
            {
                _meshes.Write(meshId, MeshManager.BufferNormals, data.NormalsOffset,
                    (IntPtr)source, data.VerticesCount * sizeof(int));
            }
        }
        if (data.Uv != null && data.UvCount > 0 && !ssbo)
        {
            fixed (float* source = data.Uv)
            {
                _meshes.Write(meshId, MeshManager.BufferUv, data.UvOffset,
                    (IntPtr)source, data.UvCount * sizeof(float));
            }
        }
        if (data.Rgba != null && data.RgbaCount > 0)
        {
            fixed (byte* source = data.Rgba)
            {
                _meshes.Write(meshId, MeshManager.BufferRgba, data.RgbaOffset,
                    (IntPtr)source, data.RgbaCount);
            }
        }
        if (data.Flags != null && data.FlagsCount > 0 && !ssbo)
        {
            fixed (int* source = data.Flags)
            {
                _meshes.Write(meshId, MeshManager.BufferFlags, data.FlagsOffset,
                    (IntPtr)source, data.FlagsCount * sizeof(int));
            }
        }
        if (data.CustomFloats != null && data.CustomFloats.Count > 0)
        {
            fixed (float* source = data.CustomFloats.Values)
            {
                _meshes.Write(meshId, MeshManager.BufferCustomFloat, data.CustomFloats.BaseOffset,
                    (IntPtr)source, data.CustomFloats.Count * sizeof(float));
            }
        }
        if (data.CustomShorts != null && data.CustomShorts.Count > 0)
        {
            fixed (short* source = data.CustomShorts.Values)
            {
                _meshes.Write(meshId, MeshManager.BufferCustomShort, data.CustomShorts.BaseOffset,
                    (IntPtr)source, data.CustomShorts.Count * sizeof(short));
            }
        }
        if (data.CustomInts != null && data.CustomInts.Count > 0)
        {
            if (ssbo)
            {
                WritePrunedCustomInts(meshId, data.CustomInts);
            }
            else
            {
                fixed (int* source = data.CustomInts.Values)
                {
                    _meshes.Write(meshId, MeshManager.BufferCustomInt, data.CustomInts.BaseOffset,
                        (IntPtr)source, data.CustomInts.Count * sizeof(int));
                }
            }
        }
        if (data.CustomBytes != null && data.CustomBytes.Count > 0)
        {
            fixed (byte* source = data.CustomBytes.Values)
            {
                _meshes.Write(meshId, MeshManager.BufferCustomByte, data.CustomBytes.BaseOffset,
                    (IntPtr)source, data.CustomBytes.Count);
            }
        }
        // An SSBO mesh never takes indices from the data: GL draws every such
        // mesh through one shared index buffer holding the fixed quad pattern,
        // filled once at allocation, and its update path leaves indices alone.
        // The mesh here got the same pattern when it was created.
        if (data.Indices != null && data.IndicesCount > 0 && !ssbo)
        {
            fixed (int* source = data.Indices)
            {
                _meshes.Write(meshId, -1, data.IndicesOffset,
                    (IntPtr)source, data.IndicesCount * sizeof(int));
            }
        }
    }

    /// <summary>
    /// Writes the custom ints as the SSBO path stores them: two per vertex go in
    /// and only the second of each pair is kept, the first being the colormap
    /// data that the face record already carries. The destination offset halves
    /// with the stride. A part with a single int per vertex is not bound at all
    /// on this path, so there is nothing to write.
    /// </summary>
    private void WritePrunedCustomInts(int meshId, CustomMeshDataPartInt customInts)
    {
        if (customInts.InterleaveStride <= 4) return;

        int kept = customInts.Count / 2;
        if (kept <= 0) return;

        if (_prunedCustomInts.Length < kept) _prunedCustomInts = new int[kept];

        int[] values = customInts.Values;
        for (int i = 0; i < kept; i++) _prunedCustomInts[i] = values[i * 2 + 1];

        fixed (int* source = _prunedCustomInts)
        {
            _meshes.Write(meshId, MeshManager.BufferCustomInt, customInts.BaseOffset / 2,
                (IntPtr)source, kept * sizeof(int));
        }
    }

    /// <summary>
    /// The SSBO chunk path packs four vertices into one face record and stores
    /// them in the xyz slot, which CreateEmptyMesh gave StorageBufferBit usage
    /// and no vertex-attribute binding when ssbo was set. Writing it is a plain
    /// buffer write; the shader reads it through gl_VertexIndex.
    /// </summary>
    public void UpdateMeshStorageBuffer(int meshId, IntPtr data, int byteOffset, int byteSize) =>
        _meshes.Write(meshId, MeshManager.BufferXyz, byteOffset, data, byteSize);

    public IntPtr GetMappedPointer(int meshId, EnumMeshBufferPart part) => part switch
    {
        EnumMeshBufferPart.Xyz => _meshes.MappedPointer(meshId, MeshManager.BufferXyz),
        EnumMeshBufferPart.Normals => _meshes.MappedPointer(meshId, MeshManager.BufferNormals),
        EnumMeshBufferPart.Uv => _meshes.MappedPointer(meshId, MeshManager.BufferUv),
        EnumMeshBufferPart.Rgba => _meshes.MappedPointer(meshId, MeshManager.BufferRgba),
        EnumMeshBufferPart.Flags => _meshes.MappedPointer(meshId, MeshManager.BufferFlags),
        EnumMeshBufferPart.CustomFloats => _meshes.MappedPointer(meshId, MeshManager.BufferCustomFloat),
        EnumMeshBufferPart.CustomShorts => _meshes.MappedPointer(meshId, MeshManager.BufferCustomShort),
        EnumMeshBufferPart.CustomInts => _meshes.MappedPointer(meshId, MeshManager.BufferCustomInt),
        EnumMeshBufferPart.CustomBytes => _meshes.MappedPointer(meshId, MeshManager.BufferCustomByte),
        _ => _meshes.MappedPointer(meshId, -1),
    };

    public void DeleteMesh(int meshId) => _meshes.Delete(meshId, _frames);
}
