using System.Runtime.InteropServices;
using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Current managed/native ABI, including the SR reactive-mask resource.
/// <summary>Checks managed FidelityFX SR/FG bridge struct sizes and the current jitter offset.</summary>
/// <remarks>ABI layout checks do not execute FSR or verify output quality.</remarks>
public sealed class Fsr3InteropTests
{
    [Fact]
    public void FidelityFxBridgeStructsMatchNativeLayout()
    {
        Assert.Equal(24, Marshal.SizeOf<Fsr3Image>());
        Assert.Equal(160, Marshal.SizeOf<Fsr3Frame>());
        Assert.Equal(128, Marshal.OffsetOf<Fsr3Frame>(nameof(Fsr3Frame.JitterX)).ToInt32());
        Assert.Equal(248, Marshal.SizeOf<Fsr3FgFrame>());
    }
}
