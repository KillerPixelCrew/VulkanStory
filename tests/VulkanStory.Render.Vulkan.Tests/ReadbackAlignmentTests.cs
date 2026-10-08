using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

/// <summary>Checks copy-offset alignment for mixed texel sizes and four-byte transfer constraints.</summary>
/// <remarks>Arithmetic coverage only; no native readback transfer is submitted.</remarks>
public sealed class ReadbackAlignmentTests
{
    [Theory]
    [InlineData(1UL, 8UL)]
    [InlineData(2UL, 8UL)]
    [InlineData(4UL, 8UL)]
    [InlineData(8UL, 8UL)]
    [InlineData(16UL, 16UL)]
    public void MixedTexelReadbacksKeepLegalCopyOffsets(ulong texelBytes, ulong expectedAlignment)
    {
        ulong alignment = ReadbackManager.OffsetAlignmentFor(texelBytes);
        Assert.Equal(expectedAlignment, alignment);
        // An odd number of preceding RGBA8 texels used to place RGBA32F at
        // offset 8, violating its 16-byte texel alignment requirement.
        ulong precedingBytes = 12;
        ulong offset = (precedingBytes + alignment - 1) / alignment * alignment;
        Assert.Equal(0UL, offset % texelBytes);
        Assert.Equal(0UL, offset % 4);
    }
}
