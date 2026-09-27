using Optimum.Render.Vulkan.Platform;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public class ControllerToggleStateTests
{
    [Fact]
    public void SneakToggleStaysHeldUntilTheNextPress()
    {
        var state = new ControllerToggleState();
        Assert.True(state.Update(true, toggleEnabled: true, active: true));
        Assert.True(state.Update(false, toggleEnabled: true, active: true));
        Assert.False(state.Update(true, toggleEnabled: true, active: true));
    }

    [Fact]
    public void MenuAndFocusTransitionsClearTheLatchWithoutReactingToAHeldButton()
    {
        var state = new ControllerToggleState();
        Assert.True(state.Update(true, toggleEnabled: true, active: true));
        Assert.False(state.Update(true, toggleEnabled: true, active: false));
        Assert.False(state.Update(true, toggleEnabled: true, active: true));
        Assert.False(state.Update(false, toggleEnabled: true, active: true));
        Assert.True(state.Update(true, toggleEnabled: true, active: true));
    }

    [Fact]
    public void HoldModeAndDisconnectResetReleaseSneak()
    {
        var state = new ControllerToggleState();
        Assert.True(state.Update(true, toggleEnabled: false, active: true));
        Assert.False(state.Update(false, toggleEnabled: false, active: true));
        Assert.True(state.Update(true, toggleEnabled: true, active: true));
        state.Reset(pressed: true);
        Assert.False(state.Update(true, toggleEnabled: true, active: true));
    }
}
