using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

/// <summary>Checks CPU texture decoding across RGBA/BGRA, half-float color, depth, and short-input rejection.</summary>
/// <remarks>Synthetic byte payloads do not establish GPU attachment content.</remarks>
public sealed class TextureCaptureTests
{
    [Fact]
    public void RgbaAndBgraDecodeToTheSameColourWithoutReorderingRows()
    {
        byte[] rgba = [255, 17, 0, 200, 0, 34, 255, 100];
        byte[] bgra = [0, 17, 255, 200, 255, 34, 0, 100];
        var first = TextureDump.ToParityReadback(Format.R8G8B8A8Unorm, 0x8058, 1, 2, rgba)!;
        var second = TextureDump.ToParityReadback(Format.B8G8R8A8Unorm, 0x8058, 1, 2, bgra)!;
        Assert.Equal(rgba, first.Bytes);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.NotSame(rgba, first.Bytes);
        Assert.Equal((1, 2, 0x8058), (first.Width, first.Height, first.GlInternalFormat));
        Assert.Null(first.Floats);
    }

    [Fact]
    public void SingleChannelHalfColourKeepsHdrValuesAndSuppliesOpaqueAlpha()
    {
        Half[] halves = [(Half)2.5f, (Half)(-0.5f)];
        var captured = TextureDump.ToParityReadback(Format.R16Sfloat, 0x822D, 2, 1,
            MemoryMarshal.AsBytes(halves.AsSpan()))!;
        Assert.Equal(new float[] { 2.5f, 0, 0, 1, -0.5f, 0, 0, 1 }, captured.Floats);
        Assert.Null(captured.Bytes);
    }

    [Fact]
    public void DepthIsOneFloatPerTexelAndShortInputIsRejected()
    {
        ushort[] depth = [0, ushort.MaxValue];
        var captured = TextureDump.ToParityReadback(Format.D16Unorm, 0x81A5, 1, 2,
            MemoryMarshal.AsBytes(depth.AsSpan()))!;
        Assert.Equal(new float[] { 0, 1 }, captured.Floats);
        Assert.Null(captured.Bytes);
        Assert.Null(TextureDump.ToParityReadback(Format.D16Unorm, 0x81A5, 1, 2, new byte[3]));
        Assert.Null(TextureDump.ToParityReadback(Format.R8G8B8A8Unorm, 0x8058, 0, 2, []));
    }
}
