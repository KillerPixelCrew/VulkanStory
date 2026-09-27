using Optimum.Render.Vulkan.Core;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public class AmdAntiLagFrameKeyTests
{
    [Fact]
    public void InputAndPresentShareOneFrameKeyExactlyOnce()
    {
        var key = new AmdAntiLagFrameKey();
        key.BeginFrame(41);

        Assert.False(key.InputStart(40));
        Assert.False(key.PresentStart(41));
        Assert.True(key.InputStart(41));
        Assert.False(key.InputStart(41));
        Assert.False(key.PresentStart(40));
        Assert.True(key.PresentStart(41));
        Assert.False(key.PresentStart(41));
        Assert.False(key.InputStart(41));
    }

    [Fact]
    public void SkippedPresentCannotLeakIntoTheNextFrame()
    {
        var key = new AmdAntiLagFrameKey();
        key.BeginFrame(41);
        Assert.True(key.InputStart(41));
        key.Cancel();
        Assert.False(key.PresentStart(41));

        key.BeginFrame(42);
        Assert.False(key.PresentStart(42));
        Assert.True(key.InputStart(42));
        Assert.True(key.PresentStart(42));
    }
}
