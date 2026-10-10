using System.Runtime.InteropServices;
using VulkanStory.Contracts;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Checks retained native allocation and mapped writes; does not render.</summary>
public static unsafe class VulkanMeshPreflight
{
    /// <summary>Runs the explicit mesh allocation/upload and release preflight.</summary>
    /// <remarks>This method creates native resources and may submit GPU work; it is an opt-in validation entry point.</remarks>
    /// <returns>Null on successful completion, otherwise the failure detail.</returns>
    public static string? Check()
    {
        try
        {
            if (!VulkanContext.TryCreate(new VulkanContextOptions { Headless = true },
                    out VulkanContext? context, out string? reason)) return reason ?? "Vulkan initialization failed";
            using (context)
            using (var meshes = new MeshManager(context!))
            {
                var emptyShort = new MeshCustomPartLayout(0, [2], [0], 4, false, MeshDataConversion.NormalizedFloat);
                var uploads = new MeshUploads(meshes);
                float[] vertices = [-1, -1, 0, 1, -1, 0, 0, 1, 0];
                int triangle = uploads.CreateMesh(new MeshUploadData
                {
                    VerticesCount = 3, IndicesCount = 3, xyz = vertices,
                    Uv = new float[6], Rgba = new byte[12], Flags = new int[3], Indices = [0, 1, 2],
                    CustomShorts = new MeshCustomUpload<short>([], 0, 0, emptyShort),
                }, staticDraw: false);
                VulkanMesh mesh = meshes.Get(triangle)!;
                if (mesh.LayoutId == MeshManager.EmptyLayoutId || mesh.IndexCount != 3 ||
                    mesh.Buffers[MeshManager.BufferCustomShort]?.Size != 4)
                    return "Triangle or empty custom-part allocation did not preserve its layout";
                var copied = new float[vertices.Length];
                Marshal.Copy(meshes.MappedPointer(triangle, MeshManager.BufferXyz), copied, 0, copied.Length);
                if (!vertices.SequenceEqual(copied)) return "Mapped mesh write did not preserve its bytes";
                uploads.UpdateMesh(triangle, new MeshUploadData
                {
                    VerticesCount = 1, xyz = [0.25f, 0.5f, 0.75f], XyzOffset = 12,
                });
                vertices[3] = 0.25f; vertices[4] = 0.5f; vertices[5] = 0.75f;
                Marshal.Copy(meshes.MappedPointer(triangle, MeshManager.BufferXyz), copied, 0, copied.Length);
                if (!vertices.SequenceEqual(copied)) return "Per-part mesh upload offset changed neighbouring vertices";

                int ssbo = meshes.CreateEmpty(48, 16, 32, 16, 16, 24,
                    null, null, null, null, MeshDrawMode.Triangles, false, true);
                VulkanMesh packed = meshes.Get(ssbo)!;
                if (packed.Buffers[MeshManager.BufferXyz]?.Size != 64 ||
                    packed.Buffers[MeshManager.BufferNormals] != null ||
                    packed.Buffers[MeshManager.BufferUv] != null ||
                    packed.Buffers[MeshManager.BufferFlags] != null ||
                    !packed.BindingOrder.SequenceEqual(new[] { MeshManager.BufferRgba }))
                    return "SSBO face-record sizing or vertex bindings changed";
                var indices = new int[6];
                Marshal.Copy(meshes.BufferOf(ssbo, -1)!.Mapped, indices, 0, indices.Length);
                if (!indices.SequenceEqual(new[] { 0, 1, 2, 0, 2, 3 })) return "SSBO quad index pattern changed";
                meshes.Delete(triangle);
                meshes.Delete(ssbo);
                if (meshes.Count != 0) return "Deleted meshes remain registered";
            }
            return null;
        }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
}
