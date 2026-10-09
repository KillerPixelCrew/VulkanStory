namespace VulkanStory.Contracts;

// Values retain the game-facing draw/conversion contract; no game types cross it.
/// <summary>Primitive topology tokens retained at the game-to-renderer mesh boundary.</summary>
public enum MeshDrawMode { Triangles = 0, Lines = 1, LineStrip = 2 }
/// <summary>How a custom vertex attribute's stored components are interpreted by the shader.</summary>
public enum MeshDataConversion { Float = 0, NormalizedFloat = 1, Integer = 2 }

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
