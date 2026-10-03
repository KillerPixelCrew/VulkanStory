using Vintagestory.API.Client;
using VulkanStory.Contracts;
using Xunit;

namespace VulkanStory.Game.Tests;

public sealed class GameMeshLayoutTests
{
    [Fact]
    public void CaptureKeepsBorrowedArraysCapacityAndPerPartByteOffsets()
    {
        var data = new MeshData(false)
        {
            VerticesCount = 2, IndicesCount = 3,
            xyz = [0, 0, 0, 1, 1, 1], Indices = [0, 1, 0], Flags = [1, 2, 3, 4],
            XyzOffset = 128, NormalsOffset = 8, UvOffset = 32, RgbaOffset = 16,
            FlagsOffset = 40, IndicesOffset = 20,
            CustomInts = new CustomMeshDataPartInt
            {
                Values = [10, 20, 30, 40], Count = 4, BaseOffset = 36,
                InterleaveSizes = [1, 1], InterleaveOffsets = [0, 4], InterleaveStride = 8,
            },
        };
        MeshUploadData captured = GameMeshLayout.Capture(data);
        Assert.Same(data.xyz, captured.xyz);
        Assert.Same(data.Flags, captured.Flags);
        Assert.Same(data.Indices, captured.Indices);
        Assert.Equal(6, captured.XyzCount);
        Assert.Equal(2, captured.FlagsCount);
        Assert.Equal(4, captured.Flags!.Length); // Capacity is distinct from the write count.
        Assert.Equal((128, 8, 32, 16, 40, 20), (captured.XyzOffset, captured.NormalsOffset,
            captured.UvOffset, captured.RgbaOffset, captured.FlagsOffset, captured.IndicesOffset));
        Assert.Same(data.CustomInts.Values, captured.CustomInts!.Values);
        Assert.Equal(36, captured.CustomInts.BaseOffset);
        Assert.Equal(4, captured.CustomInts.Count);
        Assert.Equal(8, captured.CustomInts.InterleaveStride);
    }

    [Fact]
    public void CustomAllocationAndEmptyAttributeMetadataSurviveTheBoundary()
    {
        var part = new CustomMeshDataPartShort
        {
            Count = 0, InterleaveSizes = [2, 1], InterleaveOffsets = [0, 4],
            InterleaveStride = 6, Instanced = true, Conversion = DataConversion.Integer,
        };
        part.SetAllocationSize(12);
        MeshCustomPartLayout mapped = GameMeshLayout.Part(part)!;
        Assert.Equal(12, mapped.AllocationSize);
        Assert.Same(part.InterleaveSizes, mapped.InterleaveSizes);
        Assert.Same(part.InterleaveOffsets, mapped.InterleaveOffsets);
        Assert.Equal(6, mapped.InterleaveStride);
        Assert.True(mapped.Instanced);
        Assert.Equal(MeshDataConversion.Integer, mapped.Conversion);
        Assert.Null(GameMeshLayout.Part((CustomMeshDataPartShort?)null));
    }

    [Theory]
    // Game enum types must be resolved after the official resolver initializes,
    // rather than while xUnit decodes method attributes during discovery.
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void DrawModePreservesSupportedTopology(int game, int neutral) =>
        Assert.Equal((MeshDrawMode)neutral, GameMeshLayout.DrawMode((EnumDrawMode)game));
}
