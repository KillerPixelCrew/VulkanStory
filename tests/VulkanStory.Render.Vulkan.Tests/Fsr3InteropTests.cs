using System.Runtime.InteropServices;
using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Retained ABI expectations from Fsr3Tests and FrameGenerationBoundaryTests.
public sealed class Fsr3InteropTests
{
    [Fact]
    public void FidelityFxBridgeStructsKeepTheirOriginalLayout()
    {
        Assert.Equal(24, Marshal.SizeOf<Fsr3Image>());
        Assert.Equal(136, Marshal.SizeOf<Fsr3Frame>());
        Assert.Equal(104, Marshal.OffsetOf<Fsr3Frame>(nameof(Fsr3Frame.JitterX)).ToInt32());
        Assert.Equal(248, Marshal.SizeOf<Fsr3FgFrame>());
    }
}
