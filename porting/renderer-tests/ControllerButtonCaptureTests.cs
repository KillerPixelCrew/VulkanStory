using Optimum.Render.Vulkan.Platform;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public class ControllerButtonCaptureTests
{
    [Fact]
    public void BindingWaitsForTheClickThatOpenedCaptureToBeReleased()
    {
        var capture = new ControllerButtonCapture();
        capture.Begin("jump", 1u << 0);
        Assert.Null(capture.Update(1u << 0));
        Assert.True(capture.WaitingForRelease);
        Assert.Null(capture.Update(0));
        Assert.False(capture.WaitingForRelease);
        Assert.Null(capture.Update(0));
        Assert.Equal(("jump", 1), capture.Update(1u << 1));
        Assert.Null(capture.Action);
    }

    [Fact]
    public void CaptureCanBeCancelledWithoutChangingBindings()
    {
        var capture = new ControllerButtonCapture();
        capture.Begin("inventory", 0);
        capture.Cancel();
        Assert.Null(capture.Update(1u << 3));
        Assert.False(ControllerButtonCapture.CancelChordPressed(1u << 4));
        Assert.True(ControllerButtonCapture.CancelChordPressed((1u << 4) | (1u << 6)));
    }

    [Fact]
    public void BindingTableUpdatesOnlyTheSelectedAction()
    {
        var profile = new ControllerProfile();
        ControllerButtonBindings.Find("inventory").Set(profile, 12);
        Assert.Equal(12, profile.InventoryButton);
        Assert.Equal(0, profile.AcceptButton);
        Assert.Equal("D-pad down", ControllerButtonBindings.Name(12));
        Assert.Equal("Cross", ControllerButtonBindings.Name(0, 5));
        Assert.Equal("B", ControllerButtonBindings.Name(0, 2));
    }

    [Fact]
    public void RemappingToAnOccupiedButtonSwapsTheExistingAction()
    {
        var profile = new ControllerProfile();
        ControllerButtonBindings.AssignUnique(profile, "accept", profile.BackButton);
        Assert.Equal(1, profile.AcceptButton);
        Assert.Equal(0, profile.BackButton);
    }

    [Fact]
    public void InputHostCanBeginAndCancelInGameCapture()
    {
        using var input = new SdlGamepadInput(new VulkanClientPlatform(null!));
        input.BeginBinding("inventory");
        Assert.Equal("inventory", input.PendingBinding);
        input.CancelBinding();
        Assert.Null(input.PendingBinding);
    }
}
