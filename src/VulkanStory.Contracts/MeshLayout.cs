namespace VulkanStory.Contracts;

// Values retain the game-facing draw/conversion contract; no game types cross it.
public enum MeshDrawMode { Triangles = 0, Lines = 1, LineStrip = 2 }
public enum MeshDataConversion { Float = 0, NormalizedFloat = 1, Integer = 2 }
public enum MeshBufferSlot
{
    Indices = -1, Xyz = 0, Normals = 1, Uv = 2, Rgba = 3, Flags = 4,
    CustomFloats = 5, CustomShorts = 6, CustomInts = 7, CustomBytes = 8,
}

/// <summary>Metadata consumed synchronously by retained mesh allocation/layout code.</summary>
public sealed record MeshCustomPartLayout(
    int AllocationSize, int[]? InterleaveSizes, int[]? InterleaveOffsets,
    int InterleaveStride, bool Instanced, MeshDataConversion Conversion);
