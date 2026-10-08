using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

/// <summary>Checks managed RGBA/float to BGRA8 conversion, row/alpha retention, and depth-only rejection.</summary>
/// <remarks>Uses in-memory capture data rather than reading a GPU framebuffer.</remarks>
public sealed class FramebufferCaptureTests
{
    [Fact]
    public void CapturePreservesRowsAndAlphaWhileWritingBgra()
    {
        var source = new TextureCaptureData { Width = 1, Height = 2, Bytes = [255, 0, 0, 64, 0, 0, 255, 128] };
        Assert.Equal(new byte[] { 0, 0, 255, 64, 255, 0, 0, 128 }, FramebufferCapture.Bgra8(source));
        Assert.Equal(new byte[] { 255, 0, 0, 64, 0, 0, 255, 128 }, source.Bytes);
    }
    [Fact]
    public void HdrCaptureClampsFullColorTexelsAndRejectsDepthOnlyData()
    {
        var source = new TextureCaptureData { Width = 1, Height = 1, Floats = [-.25f, .5f, 2f, 1f] };
        Assert.Equal(new byte[] { 255, 127, 0, 255 }, FramebufferCapture.Bgra8(source));
        Assert.Throws<NotSupportedException>(() => FramebufferCapture.Bgra8(
            new TextureCaptureData { Width = 1, Height = 1, Floats = [1f] }));
    }
}
