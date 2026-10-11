using System.Numerics;
using System.Runtime.InteropServices;

namespace VulkanStory.Contracts;

/// <summary>Compact surface data shared by acceleration builds and ray-hit shading.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct RayTracingVertex
{
    public Vector3 Position;
    public uint Flags;
    public Vector2 Uv;
    public uint Color;
    public uint MaterialData;
    public Vector2 SecondaryUv;
    public uint AuxiliaryData;
    public uint PackedNormal;
    public Vector3 PreviousPosition;
    public uint JointIndex;
}

/// <summary>Material behavior independent of the game's render-pass numbering.</summary>
public enum RayTracingSurface : uint
{
    Opaque,
    Cutout,
    Transparent,
    Liquid,
    Topsoil,
    AlphaBlend,
}

/// <summary>Owned upload arrays, consumed synchronously; positions are local to the double-precision origin.</summary>
public sealed record RayTracingGeometryUpload(
    ulong WorldIdentity, RayTracingVertex[] Vertices, uint[] Indices,
    double OriginX, double OriginY, double OriginZ,
    int Dimension, int AtlasPage, RayTracingSurface Surface, int LodLevel, byte VisibilityMask = byte.MaxValue);

/// <summary>Column-major shader matrices, represented with the adapter's transposed row-vector Matrix4x4 storage.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct RayTracingJoint
{
    public Matrix4x4 Current;
    public Matrix4x4 Previous;
    public Matrix4x4 Normal;
}

/// <summary>Borrowed bind-pose geometry and palette, copied before the producer advances.</summary>
public readonly record struct RayTracingSkinUpload(RayTracingGeometryUpload Geometry, RayTracingJoint[] Joints,
    long GeometryVersion, Vector3 PreviousOriginDelta, bool Reset);
