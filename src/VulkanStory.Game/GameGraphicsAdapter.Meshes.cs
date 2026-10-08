using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private sealed record MeshOwner(GameGraphicsAdapter Adapter, int Handle);
    private static readonly ConditionalWeakTable<VAO, MeshOwner> Meshes = new();

    private static PrimitiveType DrawMode(EnumDrawMode mode) => mode switch
    {
        EnumDrawMode.Triangles => PrimitiveType.Triangles,
        EnumDrawMode.Lines => PrimitiveType.Lines,
        EnumDrawMode.LineStrip => PrimitiveType.LineStrip,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    private VAO MeshReference(int handle, int indices, EnumDrawMode mode, bool persistent)
    {
        var mesh = new VAO { VaoId = handle, IndicesCount = indices, drawMode = DrawMode(mode), Persistent = persistent };
        Meshes.Add(mesh, new MeshOwner(this, handle));
        return mesh;
    }
    internal MeshRef UploadMesh(MeshData data) =>
        MeshReference(RequireDevice().CreateMesh(GameMeshLayout.Capture(data), true), data.IndicesCount, data.mode, false);
    internal MeshRef AllocateEmptyMesh(int xyz, int normals, int uv, int rgba, int flags, int indices,
        CustomMeshDataPartFloat? floats, CustomMeshDataPartShort? shorts, CustomMeshDataPartByte? bytes,
        CustomMeshDataPartInt? ints, EnumDrawMode mode, bool staticDraw) =>
        MeshReference(RequireDevice().CreateEmptyMesh(xyz, normals, uv, rgba, flags, indices,
            GameMeshLayout.Part(floats), GameMeshLayout.Part(shorts), GameMeshLayout.Part(bytes), GameMeshLayout.Part(ints),
            GameMeshLayout.DrawMode(mode), staticDraw, false), indices, mode, !staticDraw);
    internal void UpdateMesh(MeshRef mesh, MeshData data)
    {
        ParticleMotionHistory.Uploaded(mesh, data);
        RequireDevice().UpdateMesh(MeshHandle((VAO)mesh), GameMeshLayout.Capture(data));
    }
    private int MeshHandle(VAO mesh)
    {
        if (!Meshes.TryGetValue(mesh, out var owner) || !ReferenceEquals(owner.Adapter, this))
            throw new InvalidOperationException("Active mesh routing has no renderer owner.");
        return owner.Handle;
    }
    internal static GameGraphicsAdapter MeshAdapter(VAO mesh) => Meshes.TryGetValue(mesh, out var owner)
        ? owner.Adapter : throw new InvalidOperationException("Active mesh routing has no renderer owner.");
    internal void RequireOwnedMesh(VAO mesh) { RequireDevice(); MeshHandle(mesh); }
    internal void ReleaseMesh(VAO mesh) => RequireDevice().DeleteMesh(MeshHandle(mesh));
    internal void DeleteMesh(MeshRef? mesh)
    {
        RequireDevice();
        if (mesh is not null) ((VAO)mesh).Dispose();
    }
    internal MeshRef AllocateEmptySsboMesh(int xyz, int normals, int uv, int rgba, int flags, int indices,
        CustomMeshDataPartFloat? floats, CustomMeshDataPartShort? shorts, CustomMeshDataPartByte? bytes,
        CustomMeshDataPartInt? ints, EnumDrawMode mode, bool staticDraw) =>
        MeshReference(RequireDevice().CreateEmptyMesh(xyz, normals, uv, rgba, flags, indices,
            GameMeshLayout.Part(floats), GameMeshLayout.Part(shorts), GameMeshLayout.Part(bytes), GameMeshLayout.Part(ints),
            GameMeshLayout.DrawMode(mode), staticDraw, true), indices, mode, !staticDraw);

    /// <summary>Packs the supplied original mesh into the retained SSBO layout and updates its adapter-owned backend mesh.</summary>
    /// <param name="mesh">Owned original mesh receiving the update.</param>
    /// <param name="data">Original geometry to pack into the retained SSBO layout.</param>
    internal void UpdateSsboMesh(MeshRef mesh, MeshData data)
    {
        var renderer = RequireDevice();
        if (data.xyz is null) return;
        int handle = MeshHandle((VAO)mesh);
        FaceData[] faces = GameSsboFacePacking.Pack(data);
        int byteOffset = data.XyzOffset / 12 * 16;
        // Retained order: ordinary streams first, packed xyz-slot records last.
        renderer.UpdateMesh(handle, GameMeshLayout.Capture(data));
        GCHandle pin = GCHandle.Alloc(faces, GCHandleType.Pinned);
        try { renderer.UpdateMeshStorageBuffer(handle, pin.AddrOfPinnedObject(), byteOffset, 16 * data.VerticesCount); }
        finally { pin.Free(); }
    }
}
