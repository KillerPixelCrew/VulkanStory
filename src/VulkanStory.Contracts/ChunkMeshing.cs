using System.Runtime.InteropServices;

namespace VulkanStory.Contracts;

// All snapshots are owned values. No block, atlas or game references cross this boundary.
[StructLayout(LayoutKind.Sequential)]
public struct ChunkMeshVoxel
{
    public int Identity, Material;
    public uint Opaque, Solid, EmitAo;
    public int Absorption, FluidAbsorption;
    public uint Light;
}

[StructLayout(LayoutKind.Sequential)]
public struct ChunkMeshFace
{
    public float X, Y, Z, Height;
    public float U1, V1, U2, V2;
    public uint Index, Side, Bin, Flags;
    public uint ColorMap, CullMode, Smooth, Padding;
}

[StructLayout(LayoutKind.Sequential)]
public struct ChunkMeshAoNeighbour
{
    public int Offset, Facing, Opposite, UpperOrLeft, LowerOrRight;
}

public readonly record struct ChunkMeshBin(bool Edge, int Pass, int Atlas);
public readonly record struct ChunkMeshRange(int Bin, int FirstFace, int FaceCount);

/// <summary>A bounded lease, retained until CPU owners and submitted GPU readers release it.</summary>
public interface IChunkMeshBatch : IDisposable
{
    int FaceCapacity { get; }
    void Retain();
    void Submit(ReadOnlySpan<ChunkMeshVoxel> voxels, ReadOnlySpan<ChunkMeshFace> faces,
        ReadOnlySpan<ChunkMeshAoNeighbour> neighbours, int binCount);
    /// <summary>Advance completed stages without waiting; ranges are valid only when true.</summary>
    bool TryComplete(out ChunkMeshRange[] ranges);
}
