namespace VulkanStory.Contracts;

public sealed record MeshCustomUpload<T>(T[]? Values, int Count, int BaseOffset, MeshCustomPartLayout Layout)
{
    public int InterleaveStride => Layout.InterleaveStride;
}

/// <summary>Borrowed arrays consumed synchronously by retained allocation/upload operations.</summary>
public sealed class MeshUploadData
{
    public int VerticesCount { get; init; }
    public int IndicesCount { get; init; }
    public float[]? xyz { get; init; }
    public int[]? Normals { get; init; }
    public float[]? Uv { get; init; }
    public byte[]? Rgba { get; init; }
    public int[]? Flags { get; init; }
    public int[]? Indices { get; init; }
    public int XyzOffset { get; init; }
    public int NormalsOffset { get; init; }
    public int UvOffset { get; init; }
    public int RgbaOffset { get; init; }
    public int FlagsOffset { get; init; }
    public int IndicesOffset { get; init; }
    public int XyzCount => VerticesCount * 3;
    public int UvCount => VerticesCount * 2;
    public int RgbaCount => VerticesCount * 4;
    public int FlagsCount => VerticesCount;
    public MeshDrawMode mode { get; init; } = MeshDrawMode.Triangles;
    public MeshCustomUpload<float>? CustomFloats { get; init; }
    public MeshCustomUpload<short>? CustomShorts { get; init; }
    public MeshCustomUpload<int>? CustomInts { get; init; }
    public MeshCustomUpload<byte>? CustomBytes { get; init; }
}
