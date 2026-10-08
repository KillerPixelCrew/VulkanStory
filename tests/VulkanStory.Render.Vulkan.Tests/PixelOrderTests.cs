using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Retained pixel-order regression from the baseline capture tests.
/// <summary>Checks in-place RGBA/BGRA channel swapping, reversibility, and no-op input guards.</summary>
/// <remarks>Operates on pinned CPU texels without GPU readback.</remarks>
public sealed class PixelOrderTests
{
    [Fact]
    public unsafe void TheClientSeamTurnsTheDevicesRgbaIntoTheGlPathsBgra()
    {
        byte[] texels = [255, 17, 0, 255, 0, 34, 255, 200];
        fixed (byte* data = texels)
        {
            PixelOrder.SwapRedAndBlue((IntPtr)data, 2);
        }

        // Red in, B G R A out - and back again, because the conversion is its own
        // inverse, which is what lets one writer serve both backends.
        Assert.Equal([0, 17, 255, 255, 255, 34, 0, 200], texels);
        fixed (byte* data = texels)
        {
            PixelOrder.SwapRedAndBlue((IntPtr)data, 2);
        }
        Assert.Equal([255, 17, 0, 255, 0, 34, 255, 200], texels);

        // Nothing to convert is not a crash.
        PixelOrder.SwapRedAndBlue(IntPtr.Zero, 4);
        fixed (byte* data = texels)
        {
            PixelOrder.SwapRedAndBlue((IntPtr)data, 0);
            PixelOrder.SwapRedAndBlue((IntPtr)data, -1);
        }
        Assert.Equal([255, 17, 0, 255, 0, 34, 255, 200], texels);
    }
}
