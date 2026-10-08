using System.Runtime.InteropServices;
using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Matches the retained native/fsr4/bridge.cpp VulkanStoryFsr4Frame ABI.
/// <summary>Checks x64 shared FSR4 dispatch size and field/fence offsets against the bridge ABI.</summary>
/// <remarks>Does not execute Vulkan/DX12 interoperability or an AMD provider.</remarks>
public sealed class Fsr4InteropTests
{
    [Fact]
    public void SharedDispatchKeepsTheNativeX64Layout()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(96, Marshal.SizeOf<Fsr4Frame>());
        Assert.Equal(0, Offset(nameof(Fsr4Frame.Color)));
        Assert.Equal(8, Offset(nameof(Fsr4Frame.Depth)));
        Assert.Equal(16, Offset(nameof(Fsr4Frame.Motion)));
        Assert.Equal(24, Offset(nameof(Fsr4Frame.Output)));
        Assert.Equal(32, Offset(nameof(Fsr4Frame.RenderWidth)));
        Assert.Equal(36, Offset(nameof(Fsr4Frame.RenderHeight)));
        Assert.Equal(40, Offset(nameof(Fsr4Frame.DisplayWidth)));
        Assert.Equal(44, Offset(nameof(Fsr4Frame.DisplayHeight)));
        Assert.Equal(48, Offset(nameof(Fsr4Frame.JitterX)));
        Assert.Equal(52, Offset(nameof(Fsr4Frame.JitterY)));
        Assert.Equal(56, Offset(nameof(Fsr4Frame.DeltaMs)));
        Assert.Equal(60, Offset(nameof(Fsr4Frame.NearPlane)));
        Assert.Equal(64, Offset(nameof(Fsr4Frame.FarPlane)));
        Assert.Equal(68, Offset(nameof(Fsr4Frame.Fov)));
        Assert.Equal(72, Offset(nameof(Fsr4Frame.Reset)));
        Assert.Equal(80, Offset(nameof(Fsr4Frame.ReadyValue)));
        Assert.Equal(88, Offset(nameof(Fsr4Frame.DoneValue)));
    }

    private static int Offset(string field) => Marshal.OffsetOf<Fsr4Frame>(field).ToInt32();
}
