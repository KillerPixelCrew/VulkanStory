namespace VulkanStory.Contracts;

// Values retain the game-facing draw/conversion contract; no game types cross it.
/// <summary>Primitive topology tokens retained at the game-to-renderer mesh boundary.</summary>
public enum MeshDrawMode { Triangles = 0, Lines = 1, LineStrip = 2 }
/// <summary>How a custom vertex attribute's stored components are interpreted by the shader.</summary>
public enum MeshDataConversion { Float = 0, NormalizedFloat = 1, Integer = 2 }
/// <summary>Stable mesh stream slots; the negative index slot identifies the element-index buffer.</summary>
public enum MeshBufferSlot
{
    Indices = -1, Xyz = 0, Normals = 1, Uv = 2, Rgba = 3, Flags = 4,
    CustomFloats = 5, CustomShorts = 6, CustomInts = 7, CustomBytes = 8,
}

/// <summary>Metadata consumed synchronously by retained mesh allocation/layout code.</summary>
/// <param name="AllocationSize">Requested custom-stream capacity in stored components, converted to bytes by the allocator.</param>
/// <param name="InterleaveSizes">Component counts for interleaved attributes, when supplied.</param>
/// <param name="InterleaveOffsets">Byte offsets for the interleaved attributes, when supplied.</param>
/// <param name="InterleaveStride">Distance in bytes between successive attribute records.</param>
/// <param name="Instanced">Whether this stream advances per instance rather than per vertex.</param>
/// <param name="Conversion">Shader input conversion applied to the stored components.</param>
public sealed record MeshCustomPartLayout(
    int AllocationSize, int[]? InterleaveSizes, int[]? InterleaveOffsets,
    int InterleaveStride, bool Instanced, MeshDataConversion Conversion);
