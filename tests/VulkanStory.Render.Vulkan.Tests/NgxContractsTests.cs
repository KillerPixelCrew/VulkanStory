using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Selected CPU-only contracts from the source renderer's NGX evaluate,
// lifetime, and frame generation suites.
/// <summary>Checks NGX ABI layout, temporal evaluation mapping, controlled lifetime callbacks, and camera/parameter guards.</summary>
/// <remarks>The create guard uses invalid handles; these cases do not establish real DLSS execution.</remarks>
public sealed unsafe class NgxContractsTests
{
    [Fact]
    public void VulkanResourceLayoutMatchesTheNativeNgxAbi()
    {
        Assert.Equal(48, sizeof(NgxImageViewInfoVk));
        Assert.Equal(56, sizeof(NgxResourceVk));
        var resource = default(NgxResourceVk);
        byte* origin = (byte*)&resource;
        Assert.Equal(16, (int)((byte*)&resource.ImageViewInfo.AspectMask - origin));
        Assert.Equal(36, (int)((byte*)&resource.ImageViewInfo.Format - origin));
        Assert.Equal(40, (int)((byte*)&resource.ImageViewInfo.Width - origin));
        Assert.Equal(44, (int)((byte*)&resource.ImageViewInfo.Height - origin));
        Assert.Equal(48, (int)((byte*)&resource.Type - origin));
        Assert.Equal(52, (int)(&resource.ReadWrite - origin));
    }

    [Fact]
    public void DlssEvaluationUsesTheActualRenderPixelJitterAndReset()
    {
        var temporal = new TemporalProviderFrame(0.25f, -0.5f, true, 16.7f,
            0.1f, 1000f, 1.2f, 0f, 0f, 0f, ReadOnlyMemory<float>.Empty);
        NgxDlssEvaluation inputs = NgxDlssEvaluation.FromTemporalFrame(temporal);
        Assert.Equal(0.25f, inputs.JitterOffsetX);
        Assert.Equal(-0.5f, inputs.JitterOffsetY);
        Assert.Equal(1f, inputs.MotionVectorScaleX);
        Assert.Equal(1f, inputs.MotionVectorScaleY);
        Assert.True(inputs.Reset);
    }

    [Fact]
    public void ShutdownReleasesDrainsAndEndsTheNgxLifetimeOnce()
    {
        var order = new List<string>();
        var owner = new NgxLifetimeOwner(device =>
        {
            order.Add("shutdown:" + device.ToInt64());
            return NgxResult.Success;
        });
        Assert.Equal(NgxLifetimeOutcome.Done,
            owner.Initialize(new IntPtr(0x1234), () =>
            {
                order.Add("init");
                return NgxResult.Success;
            }, out _));
        owner.FeatureCreated();
        Assert.Equal(NgxLifetimeOutcome.Done, owner.ShutDown(
            () => { order.Add("release"); owner.FeatureRetired(); },
            () => { order.Add("drain"); return 1; }));
        Assert.Equal(new[] { "init", "release", "drain", "shutdown:4660" }, order);
        Assert.Equal(1, owner.ShutdownCalls);
        Assert.Equal(NgxLifetimeOutcome.AlreadyShutDown, owner.ShutDown(null, null));
    }

    [Fact]
    public void FrameGenerationRejectsInvalidCameraOrNoRecordingBuffer()
    {
        static float[] Identity() =>
            [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        NgxFrameGenerationCamera Camera(float near, float far, float[] projection) => new(
            projection, Identity(), Identity(), Identity(), near, far,
            1f, 16f / 9f, 0f, 0f, 0f, 0f, 0f,
            0f, 1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f);

        Assert.True(Camera(0.1f, 1000f, Identity()).IsValid);
        Assert.False(Camera(10f, 1f, Identity()).IsValid);
        Assert.False(Camera(0.1f, 1000f, new float[15]).IsValid);

    }
}
