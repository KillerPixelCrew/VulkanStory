using System.Collections.Generic;
using VulkanStory.Game.Input;
using VulkanStory.Platform.Sdl;
using Vintagestory.API.Client;
using Xunit;

namespace VulkanStory.Game.Tests;

public sealed class SdlTouchMouseTests
{
    private static SdlInputEvent Finger(uint type, ulong time, ulong finger, float x, float y) =>
        new(type, time, 42, X: x, Y: y, TouchId: 7, FingerId: finger);

    private static SdlTouchMouse Create(List<string> actions) => new(
        (_, _, _, _) => { },
        (button, down, _, _) => actions.Add(button + (down ? "+" : "-")));

    [Fact]
    public void TapClicksOnceOnReleaseWithoutEarlyPress()
    {
        var actions = new List<string>();
        var touch = Create(actions);
        touch.Handle(Finger(SdlEventPump.FingerDown, 1, 1, 0.25f, 0.5f), (800, 600), (1600, 1200), 1);
        Assert.Empty(actions);
        touch.Handle(Finger(SdlEventPump.FingerUp, 100_000_000, 1, 0.25f, 0.5f),
            (800, 600), (1600, 1200), 100_000_000);
        Assert.Equal(new[] { "Left+", "Left-" }, actions);
    }

    [Fact]
    public void NormalizedFingerMotionUsesDrawablePixels()
    {
        var positions = new List<(float X, float Y, float Dx, float Dy)>();
        var touch = new SdlTouchMouse((x, y, dx, dy) => positions.Add((x, y, dx, dy)),
            (_, _, _, _) => { });
        touch.Handle(Finger(SdlEventPump.FingerDown, 1, 1, 0.25f, 0.5f), (800, 600), (1600, 1200), 1);
        touch.Handle(Finger(SdlEventPump.FingerMotion, 2, 1, 0.3f, 0.55f), (800, 600), (1600, 1200), 2);
        Assert.Equal((400f, 600f, 0f, 0f), positions[0]);
        Assert.InRange(positions[1].X, 479.99f, 480.01f);
        Assert.InRange(positions[1].Y, 659.99f, 660.01f);
        Assert.InRange(positions[1].Dx, 79.99f, 80.01f);
        Assert.InRange(positions[1].Dy, 59.99f, 60.01f);
    }

    [Fact]
    public void DragStartsPastLogicalSlopAndReleasesOnCancellation()
    {
        var actions = new List<string>();
        var touch = Create(actions);
        touch.Handle(Finger(SdlEventPump.FingerDown, 1, 1, 0.1f, 0.1f), (800, 600), (1600, 1200), 1);
        touch.Handle(Finger(SdlEventPump.FingerMotion, 2, 1, 0.105f, 0.1f), (800, 600), (1600, 1200), 2);
        Assert.Empty(actions); // Eight drawable pixels are below the 24-pixel slop.
        touch.Handle(Finger(SdlEventPump.FingerMotion, 3, 1, 0.12f, 0.1f), (800, 600), (1600, 1200), 3);
        Assert.Equal(new[] { "Left+" }, actions);
        touch.Handle(Finger(SdlEventPump.FingerCanceled, 4, 1, 0.12f, 0.1f),
            (800, 600), (1600, 1200), 4);
        Assert.Equal(new[] { "Left+", "Left-" }, actions);
    }

    [Fact]
    public void StationaryLongPressRightClicksWithoutLeftClick()
    {
        var actions = new List<string>();
        var touch = Create(actions);
        touch.Handle(Finger(SdlEventPump.FingerDown, 1, 1, 0.5f, 0.5f), (800, 600), (1600, 1200), 1);
        touch.Tick(400_000_000);
        Assert.Empty(actions);
        touch.Tick(600_000_000);
        touch.Handle(Finger(SdlEventPump.FingerUp, 700_000_000, 1, 0.5f, 0.5f),
            (800, 600), (1600, 1200), 700_000_000);
        Assert.Equal(new[] { "Right+", "Right-" }, actions);
    }

    [Fact]
    public void SecondFingerCancelsDragAndSuppressesClicksUntilAllLift()
    {
        var actions = new List<string>();
        var touch = Create(actions);
        touch.Handle(Finger(SdlEventPump.FingerDown, 1, 1, 0.1f, 0.1f), (800, 600), (1600, 1200), 1);
        touch.Handle(Finger(SdlEventPump.FingerMotion, 2, 1, 0.2f, 0.1f), (800, 600), (1600, 1200), 2);
        touch.Handle(Finger(SdlEventPump.FingerDown, 3, 2, 0.7f, 0.5f), (800, 600), (1600, 1200), 3);
        touch.Handle(Finger(SdlEventPump.FingerUp, 4, 1, 0.2f, 0.1f), (800, 600), (1600, 1200), 4);
        touch.Handle(Finger(SdlEventPump.FingerUp, 5, 2, 0.7f, 0.5f), (800, 600), (1600, 1200), 5);
        Assert.Equal(new[] { "Left+", "Left-" }, actions);
    }
}
