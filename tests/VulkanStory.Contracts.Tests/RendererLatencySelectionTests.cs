using VulkanStory.Contracts;
using Xunit;

namespace VulkanStory.Contracts.Tests;

public sealed class RendererLatencySelectionTests
{
    [Theory]
    [InlineData("off", "off", 0)]
    [InlineData("on", "off", 1)]
    [InlineData("boost", "off", 2)]
    [InlineData("off", "dlss", 1)]
    [InlineData("off", "fsr3", 1)]
    [InlineData("off", "xess", 1)]
    [InlineData("boost", "dlss", 2)]
    public void SelectedModeRetainsFrameGenerationDependency(string selected, string activeFg, int expected)
    {
        var selection = new RendererLatencySelection(selected, activeFg);
        Assert.Equal(selected, selection.RequestedMode);
        Assert.Equal(activeFg, selection.EffectiveFrameGeneration);
        Assert.Equal(expected, selection.EffectiveMode);
    }
}
