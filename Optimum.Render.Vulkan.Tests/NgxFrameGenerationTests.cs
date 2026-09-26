using Optimum.Render.Vulkan.Core;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed class NgxFrameGenerationTests
{
    private static NgxFrameGenerationCamera Camera(float near = 0.1f, float far = 1000f,
        float[]? viewToClip = null) => new(
        viewToClip ?? Identity(), Identity(), Identity(), Identity(), near, far,
        1f, 16f / 9f, 0f, 0f, 0f, 0f, 0f,
        0f, 1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f);

    private static float[] Identity() => new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    [Fact]
    public void CameraRequiresFourFiniteMatricesAndOrderedDepthPlanes()
    {
        Assert.True(Camera().IsValid);
        Assert.False(Camera(near: 10f, far: 1f).IsValid);
        Assert.False(Camera(viewToClip: new float[15]).IsValid);
        float[] nonFinite = Identity();
        nonFinite[3] = float.NaN;
        Assert.False(Camera(viewToClip: nonFinite).IsValid);
    }

    [Fact]
    public void CannotCreateFeatureWithoutARecordingCommandBuffer()
    {
        var result = NgxFrameGenerationFeature.Create(default, 1920, 1080,
            Silk.NET.Vulkan.Format.R8G8B8A8Unorm, out NgxFrameGenerationFeature? feature);
        Assert.Null(feature);
        Assert.Equal(NgxResult.FailInvalidParameter, result);
    }
}
