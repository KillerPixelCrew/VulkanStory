using Optimum.Render.Vulkan.Core;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public class StreamlineFeatureSelectionTests
{
    [Theory]
    [InlineData(-1, true, null, true)]
    [InlineData(-1, false, null, false)]
    [InlineData(0, true, null, false)]
    [InlineData(1, true, "1", true)]
    [InlineData(-1, true, "0", false)]
    public void DlssGPluginSelectionDoesNotBlockOtherVendors(int preferredDevice,
        bool nvidiaAdapterPresent, string? overrideSetting, bool expected)
    {
        Assert.Equal(expected, StreamlineRuntime.ShouldLoadDlssG(preferredDevice,
            nvidiaAdapterPresent, overrideSetting));
        Assert.Equal(expected, StreamlineRuntime.ShouldLoadReflex(preferredDevice,
            nvidiaAdapterPresent, overrideSetting));
    }
}
