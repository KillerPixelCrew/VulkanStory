using System;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

internal readonly record struct TerrainPartDesc(int PoolMesh, int RenderPass, int Atlas,
    uint FirstIndex, uint IndexCount, double CenterX, double CenterY, double CenterZ,
    float Radius, float RadiusY, float RadiusZ, int LodLevel, int ChunkSlot)
{
    public bool Hidden { get; init; }
    public double PoolOriginX { get; init; }
    public double PoolOriginY { get; init; }
    public double PoolOriginZ { get; init; }
}

internal readonly record struct TerrainView(int Eye, double CameraX, double CameraY, double CameraZ,
    double PlayerX, double PlayerY, double PlayerZ,
    System.Numerics.Matrix4x4 ViewProjection /* camera-relative */, float ViewDistance,
    float Lod0BiasSq, float Lod2BiasSq,
    int CullMode /* stock EnumFrustumCullMode */, bool Shadow)
{
    public float ViewDistanceSq { get; init; }
    public int PlayerBlockX { get; init; }
    public int PlayerBlockZ { get; init; }
    public float ShadowRangeX { get; init; }
    public float ShadowRangeZ { get; init; }
    public System.Numerics.Vector4 Plane0 { get; init; }
    public System.Numerics.Vector4 Plane1 { get; init; }
    public System.Numerics.Vector4 Plane2 { get; init; }
    public System.Numerics.Vector4 Plane3 { get; init; }
    public System.Numerics.Vector4 Plane4 { get; init; }
    public System.Numerics.Vector4 Plane5 { get; init; }
}

public sealed unsafe partial class VulkanDevice
{
    internal bool ResidentTerrainEnabled { get; set; }

    internal int AddTerrainPart(in TerrainPartDesc part)
    {
        // TODO(T-render): admit the part into the resident table; zero retains the stock pool path.
        return 0;
    }

    internal void UpdateTerrainPart(int handle, in TerrainPartDesc part)
    {
        // TODO(T-render): update the resident part.
    }

    internal void RemoveTerrainPart(int handle)
    {
        // TODO(T-render): remove the resident part.
    }

    internal void RemoveTerrainPool(int poolMesh)
    {
        // TODO(T-render): remove all resident parts owned by this pool.
    }

    internal void SetTerrainChunkVisibility(ReadOnlySpan<ulong> visibleChunkSlots)
    {
        // TODO(T-render): upload the stock chunk visibility bitset for this frame and eye.
    }

    internal bool DrawTerrainPass(NativePipeline pipeline, int stockCallSite /* 0..13 */,
        int renderPass, in TerrainView view)
    {
        // TODO(T-render): cull and draw the resident pools inside the current native pass.
        return false;
    }
}
