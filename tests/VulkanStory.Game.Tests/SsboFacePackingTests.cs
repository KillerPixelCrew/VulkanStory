using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Xunit;

namespace VulkanStory.Game.Tests;

public sealed class SsboFacePackingTests
{
    [Fact]
    public void QuadPackingRetainsGpuRecordLayoutAndPerCornerFlags()
    {
        var data = new MeshData
        {
            VerticesCount = 4,
            xyz = [0, 0, 0, 2, 0, 0, 2, 2, 0, 0, 2, 0],
            Uv = [0, 0, 0, 1, 1, 1, 1, 0],
            Flags = [1, 2, 3, 4],
        };
        FaceData face = GameSsboFacePacking.Pack(data)[0];
        Assert.Equal(64, Marshal.SizeOf<FaceData>());
        Assert.Equal(1f, face.dx1); Assert.Equal(0f, face.dy1);
        Assert.Equal(0f, face.dx2); Assert.Equal(1f, face.dy2);
        Assert.Equal(0, face.uv);
        Assert.Equal(unchecked((int)0x80008000), face.uvSize);
        Assert.Equal(1, face.renderFlags0); Assert.Equal(2, face.renderFlags1);
        Assert.Equal(3, face.renderFlags2); Assert.Equal(4, face.renderFlags3);
        Assert.Equal(0, face.colormapData);
    }
}
