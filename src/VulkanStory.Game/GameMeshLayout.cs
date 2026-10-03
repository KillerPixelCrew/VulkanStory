using Vintagestory.API.Client;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

/// <summary>Preserves game allocation metadata without passing game objects into Vulkan.</summary>
internal static class GameMeshLayout
{
    internal static MeshUploadData Capture(MeshData data) => new()
    {
        VerticesCount = data.VerticesCount, IndicesCount = data.IndicesCount,
        xyz = data.xyz, Normals = data.Normals, Uv = data.Uv, Rgba = data.Rgba,
        Flags = data.Flags, Indices = data.Indices,
        XyzOffset = data.XyzOffset, NormalsOffset = data.NormalsOffset, UvOffset = data.UvOffset,
        RgbaOffset = data.RgbaOffset, FlagsOffset = data.FlagsOffset, IndicesOffset = data.IndicesOffset,
        mode = DrawMode(data.mode),
        CustomFloats = Upload(data.CustomFloats, Part(data.CustomFloats)),
        CustomShorts = Upload(data.CustomShorts, Part(data.CustomShorts)),
        CustomInts = Upload(data.CustomInts, Part(data.CustomInts)),
        CustomBytes = Upload(data.CustomBytes, Part(data.CustomBytes)),
    };

    private static MeshCustomUpload<T>? Upload<T>(CustomMeshDataPart<T>? part, MeshCustomPartLayout? layout) =>
        part is null ? null : new(part.Values, part.Count, part.BaseOffset, layout!);

    internal static MeshDrawMode DrawMode(EnumDrawMode mode) => mode switch
    {
        EnumDrawMode.Triangles => MeshDrawMode.Triangles,
        EnumDrawMode.Lines => MeshDrawMode.Lines,
        EnumDrawMode.LineStrip => MeshDrawMode.LineStrip,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static MeshDataConversion Conversion(DataConversion conversion) => conversion switch
    {
        DataConversion.Float => MeshDataConversion.Float,
        DataConversion.NormalizedFloat => MeshDataConversion.NormalizedFloat,
        DataConversion.Integer => MeshDataConversion.Integer,
        _ => throw new ArgumentOutOfRangeException(nameof(conversion)),
    };

    private static MeshCustomPartLayout? Part<T>(CustomMeshDataPart<T>? part, MeshDataConversion conversion) =>
        part is null ? null : new(part.AllocationSize, part.InterleaveSizes, part.InterleaveOffsets,
            part.InterleaveStride, part.Instanced, conversion);

    internal static MeshCustomPartLayout? Part(CustomMeshDataPartFloat? part) =>
        Part(part, MeshDataConversion.Float);
    internal static MeshCustomPartLayout? Part(CustomMeshDataPartShort? part) =>
        Part(part, part is null ? MeshDataConversion.Float : Conversion(part.Conversion));
    internal static MeshCustomPartLayout? Part(CustomMeshDataPartInt? part) =>
        Part(part, part is null ? MeshDataConversion.Integer : Conversion(part.Conversion));
    internal static MeshCustomPartLayout? Part(CustomMeshDataPartByte? part) =>
        Part(part, part is null ? MeshDataConversion.NormalizedFloat : Conversion(part.Conversion));
}
