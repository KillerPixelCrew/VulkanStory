namespace VulkanStory.Contracts;

/// <summary>Stored scalar type of a raw vertex attribute.</summary>
public enum RawVertexElementType
{
    Byte = 0x1400, UnsignedByte = 0x1401, Short = 0x1402, UnsignedShort = 0x1403,
    Int = 0x1404, UnsignedInt = 0x1405, Float = 0x1406,
}

/// <summary>A borrowed byte stream and its explicit vertex attribute description.</summary>
/// <param name="Location">Linked shader attribute location.</param>
/// <param name="Components">Scalar components per attribute, from one to four.</param>
/// <param name="ElementType">Stored scalar representation.</param>
/// <param name="Normalized">Whether integral components convert to the normalized floating-point range.</param>
/// <param name="Integer">Whether the shader consumes an integer attribute.</param>
/// <param name="Stride">Bytes between successive vertices; zero means tightly packed.</param>
/// <param name="Offset">Byte offset of the first attribute in Data.</param>
/// <param name="Data">Source bytes consumed synchronously before mesh creation returns.</param>
public sealed record RawVertexStream(int Location, int Components, RawVertexElementType ElementType,
    bool Normalized, bool Integer, int Stride, int Offset, byte[] Data);

/// <summary>Explicit vertex streams and decoded uint32 indices, synchronously copied into owned GPU storage.</summary>
public sealed class RawMeshDefinition
{
    /// <summary>One attribute per stream; at most ten retained vertex-buffer bindings.</summary>
    public RawVertexStream[] VertexStreams { get; init; } = [];
    /// <summary>Unsigned 32-bit element values stored in int words; base vertex must already be applied.</summary>
    public int[] Indices { get; init; } = [];
    /// <summary>Primitive topology of the decoded indices.</summary>
    public MeshDrawMode Mode { get; init; } = MeshDrawMode.Triangles;
}
