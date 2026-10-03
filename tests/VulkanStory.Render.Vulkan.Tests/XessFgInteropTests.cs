using System.Runtime.InteropServices;
using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Retained native x64 layout from XessFgBridgeTests and the C++ frame ABI.
public sealed class XessFgInteropTests
{
    [Fact]
    public void PresentationFrameKeepsNativeMatrixAndFenceOffsets()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(216, Marshal.SizeOf<XessPresentationFrame>());
        Assert.Equal(0, Offset(nameof(XessPresentationFrame.FrameId)));
        Assert.Equal(4, Offset(nameof(XessPresentationFrame.Reset)));
        Assert.Equal(8, Offset(nameof(XessPresentationFrame.Vsync)));
        Assert.Equal(12, Offset(nameof(XessPresentationFrame.Reserved)));
        Assert.Equal(16, Offset(nameof(XessPresentationFrame.Color)));
        Assert.Equal(24, Offset(nameof(XessPresentationFrame.Depth)));
        Assert.Equal(32, Offset(nameof(XessPresentationFrame.Motion)));
        Assert.Equal(40, Offset(nameof(XessPresentationFrame.Hudless)));
        Assert.Equal(48, Offset(nameof(XessPresentationFrame.Ui)));
        Assert.Equal(56, Offset(nameof(XessPresentationFrame.ViewMatrix)));
        Assert.Equal(120, Offset(nameof(XessPresentationFrame.ProjectionMatrix)));
        Assert.Equal(184, Offset(nameof(XessPresentationFrame.JitterX)));
        Assert.Equal(188, Offset(nameof(XessPresentationFrame.JitterY)));
        Assert.Equal(192, Offset(nameof(XessPresentationFrame.MotionScaleX)));
        Assert.Equal(196, Offset(nameof(XessPresentationFrame.MotionScaleY)));
        Assert.Equal(200, Offset(nameof(XessPresentationFrame.ReadyFenceValue)));
        Assert.Equal(208, Offset(nameof(XessPresentationFrame.DoneFenceValue)));
    }

    private static int Offset(string field) =>
        Marshal.OffsetOf<XessPresentationFrame>(field).ToInt32();
}
