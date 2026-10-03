using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

/// <summary>
/// GPU timestamp section labels become <c>stats.gpu</c> keys, which the profiling
/// scripts parse with <c>[a-z_0-9]+</c>.
/// </summary>
public class GpuTimestampsTests
{
    [Theory]
    [InlineData("stage_Opaque", "stage_opaque")]
    [InlineData("post_TaaResolve", "post_taaresolve")]
    [InlineData("upscale dlss-sr", "upscale_dlss_sr")]
    [InlineData("", "unnamed")]
    public void LabelsBecomeStatsKeys(string label, string expected) =>
        Assert.Equal(expected, GpuTimestamps.Token(label));

    [Fact]
    public void StatsGatingFollowsTheStatsLog() =>
        Assert.False(GpuTimestamps.EnabledByEnvironment(null));
}
