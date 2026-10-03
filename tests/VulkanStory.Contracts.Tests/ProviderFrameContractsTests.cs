using VulkanStory.Contracts;
using Xunit;

namespace VulkanStory.Contracts.Tests;

public sealed class ProviderFrameContractsTests
{
    [Fact]
    public void LodBiasPreservesTheLegacyRenderScaleFormulaAndExplicitOffset()
    {
        var defaultPlan = new UpscalerPlan(960, 540, 1920, 1080, "quality");
        Assert.True(defaultPlan.IsValid);
        Assert.Equal(0.5f, defaultPlan.RenderScale);
        Assert.Equal(-2f, defaultPlan.LodBias, 5);

        var customOffset = defaultPlan with { LodBiasOffset = 0.25f };
        Assert.Equal(-1.25f, customOffset.LodBias, 5);
        Assert.Equal(-2f, UpscalerPlan.RecommendedLodBias(960, 1920, float.NaN), 5);
        Assert.Equal(0f, UpscalerPlan.RecommendedLodBias(1920, 1920));
    }

    [Fact]
    public void ProviderBiasOverridesTheGeneralRecommendation()
    {
        var plan = new UpscalerPlan(960, 540, 1920, 1080, "quality", -0.7f, 0.25f);
        Assert.Equal(-0.7f, plan.LodBias);
        Assert.False((plan with { RenderWidth = 0 }).IsValid);
    }

    [Fact]
    public void FrameGenerationRequiresACompleteInverseView()
    {
        var frame = new TemporalProviderFrame(0.25f, -0.5f, true, 16.7f,
            0.1f, 1000f, 1.2f, 1f, 2f, 3f, ReadOnlyMemory<float>.Empty);
        Assert.False(frame.HasInverseView);
        Assert.True((frame with { InverseView = new float[16] }).HasInverseView);
    }
}
