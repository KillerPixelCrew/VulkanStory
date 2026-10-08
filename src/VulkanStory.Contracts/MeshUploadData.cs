namespace VulkanStory.Contracts;

/// <summary>A borrowed custom mesh stream and the layout needed to upload it synchronously.</summary>
/// <typeparam name="T">Stored component type.</typeparam>
/// <param name="Values">Source components, or null when this stream has no upload data.</param>
/// <param name="Count">Number of source components in the upload.</param>
/// <param name="BaseOffset">Retained destination stream byte offset used by the upload path.</param>
/// <param name="Layout">Allocation and vertex-input metadata for the stream.</param>
public sealed record MeshCustomUpload<T>(T[]? Values, int Count, int BaseOffset, MeshCustomPartLayout Layout)
{
    /// <summary>Byte stride supplied by the custom attribute layout.</summary>
    public int InterleaveStride => Layout.InterleaveStride;
}

/// <summary>Borrowed arrays consumed synchronously by retained allocation/upload operations.</summary>
public sealed class MeshUploadData
{
    /// <summary>Number of vertices represented by the standard vertex streams.</summary>
    public int VerticesCount { get; init; }
    /// <summary>Number of element indices to upload.</summary>
    public int IndicesCount { get; init; }
    /// <summary>Three position components per vertex; borrowed from the game mesh.</summary>
    public float[]? xyz { get; init; }
    /// <summary>Packed normal values in the retained game mesh representation.</summary>
    public int[]? Normals { get; init; }
    /// <summary>Two texture-coordinate components per vertex.</summary>
    public float[]? Uv { get; init; }
    /// <summary>Four color bytes per vertex.</summary>
    public byte[]? Rgba { get; init; }
    /// <summary>Packed game vertex flags, one value per vertex.</summary>
    public int[]? Flags { get; init; }
    /// <summary>Element indices in the retained mesh topology.</summary>
    public int[]? Indices { get; init; }
    /// <summary>Destination byte offset in the position buffer.</summary>
    public int XyzOffset { get; init; }
    /// <summary>Destination byte offset in the packed-normal buffer.</summary>
    public int NormalsOffset { get; init; }
    /// <summary>Destination byte offset in the texture-coordinate buffer.</summary>
    public int UvOffset { get; init; }
    /// <summary>Destination byte offset in the color buffer.</summary>
    public int RgbaOffset { get; init; }
    /// <summary>Destination byte offset in the vertex-flag buffer.</summary>
    public int FlagsOffset { get; init; }
    /// <summary>Destination byte offset in the element-index buffer.</summary>
    public int IndicesOffset { get; init; }
    /// <summary>Position component count derived from the vertex count.</summary>
    public int XyzCount => VerticesCount * 3;
    /// <summary>Texture-coordinate component count derived from the vertex count.</summary>
    public int UvCount => VerticesCount * 2;
    /// <summary>Color byte count derived from the vertex count.</summary>
    public int RgbaCount => VerticesCount * 4;
    /// <summary>Flag element count derived from the vertex count.</summary>
    public int FlagsCount => VerticesCount;
    /// <summary>Primitive topology; triangles are used when the caller does not specify one.</summary>
    public MeshDrawMode mode { get; init; } = MeshDrawMode.Triangles;
    /// <summary>Optional custom float attribute stream.</summary>
    public MeshCustomUpload<float>? CustomFloats { get; init; }
    /// <summary>Optional custom short attribute stream.</summary>
    public MeshCustomUpload<short>? CustomShorts { get; init; }
    /// <summary>Optional custom integer attribute stream.</summary>
    public MeshCustomUpload<int>? CustomInts { get; init; }
    /// <summary>Optional custom byte attribute stream.</summary>
    public MeshCustomUpload<byte>? CustomBytes { get; init; }
}
