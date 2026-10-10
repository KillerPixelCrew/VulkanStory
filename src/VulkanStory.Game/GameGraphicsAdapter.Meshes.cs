using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Threading;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private sealed class MeshOwner(GameGraphicsAdapter adapter, int handle)
    {
        internal GameGraphicsAdapter Adapter { get; } = adapter;
        internal int Handle { get; } = handle;
        private int released;
        internal bool ClaimRelease() => Interlocked.Exchange(ref released, 1) == 0;

        ~MeshOwner()
        {
            if (!ClaimRelease()) return;
            // The original VAO finalizer only logs. Its weak sidecar can enqueue
            // the stable renderer handle, but must never touch Vulkan or its table.
            try { Adapter.QueueFinalizedMesh(Handle); }
            catch { /* During process teardown/OOM the device still owns the mesh. */ }
        }
    }
    private static readonly ConditionalWeakTable<VAO, MeshOwner> Meshes = new();
    private readonly ConcurrentQueue<int> finalizedMeshHandles = new();

    private void QueueFinalizedMesh(int handle)
    {
        // A detached adapter must not reacquire a disposed device. A shutdown
        // race may enqueue a handle after teardown; no consumer then runs and
        // MeshManager.Dispose has already released all remaining GPU meshes.
        if (Volatile.Read(ref device) != null) finalizedMeshHandles.Enqueue(handle);
    }

    /// <summary>Retires abandoned game mesh handles on the frame thread through the existing GPU timeline.</summary>
    internal void DrainFinalizedMeshes()
    {
        var renderer = RequireDevice();
        while (finalizedMeshHandles.TryDequeue(out int handle)) renderer.DeleteMesh(handle);
    }

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
    internal void ReleaseMesh(VAO mesh)
    {
        var renderer = RequireDevice();
        if (!Meshes.TryGetValue(mesh, out var owner) || !ReferenceEquals(owner.Adapter, this))
            throw new InvalidOperationException("Active mesh routing has no renderer owner.");
        if (owner.ClaimRelease()) renderer.DeleteMesh(owner.Handle);
        GC.SuppressFinalize(owner);
    }
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
