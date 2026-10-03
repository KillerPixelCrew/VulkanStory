using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

public sealed class ContextBoundaryTests
{
    [Fact]
    public void PresentCountsReachTheGameAdapterWithoutGameConfigTypes()
    {
        int real = 0;
        uint sdk = 0;
        VulkanStats.ConfigurePresentationObserver(() => real++, count => sdk += count);
        try
        {
            VulkanStats.NotePresent(generated: false);
            VulkanStats.NotePresent(generated: true);
            VulkanStats.NoteSdkActualPresents(3);
            Assert.Equal(1, real);
            Assert.Equal(3u, sdk);
        }
        finally
        {
            VulkanStats.ConfigurePresentationObserver(null, null);
        }
    }
}
