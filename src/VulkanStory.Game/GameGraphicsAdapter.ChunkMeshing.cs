using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    internal void PublishChunkMeshing(VAO mesh, IChunkMeshBatch batch, ChunkMeshRange range,
        int vertexPosition, float dx, float dy, float dz)
    {
        var renderer = RequireDevice();
        int handle = MeshHandle(mesh);
        Meshes.GetValue(mesh, _ => throw new InvalidOperationException("Chunk pool lost its mesh owner.")).GeometryRevision++;
        renderer.PublishChunkMeshing(batch, range, handle, vertexPosition, dx, dy, dz);
    }
}
