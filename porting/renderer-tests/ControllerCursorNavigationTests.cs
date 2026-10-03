using System.Collections.Generic;
using System.Reflection;
using OpenTK.Mathematics;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public class ControllerCursorNavigationTests
{
    [Fact]
    public void DirectionalMovePrefersAWidgetInTheRequestedRow()
    {
        var targets = new List<Vector2>
        {
            new(50, 50), // current widget
            new(100, 50),
            new(70, 95), // closer, but well off the requested axis
            new(0, 50)
        };
        Assert.True(ControllerCursorNavigation.TryNext(new Vector2(50, 50), Vector2.UnitX, targets, out Vector2 next));
        Assert.Equal(new Vector2(100, 50), next);
    }

    [Fact]
    public void DirectionalMoveDoesNotWrapToAWidgetBehindTheCursor()
    {
        var targets = new List<Vector2> { new(50, 50), new(0, 50) };
        Assert.False(ControllerCursorNavigation.TryNext(new Vector2(50, 50), Vector2.UnitX, targets, out _));
    }

    [Fact]
    public void PinnedGameExposesTheComposerFieldsUsedForSnapping()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo? elements = typeof(GuiComposer).GetField("interactiveElements", flags);
        Assert.NotNull(elements);
        Assert.True(typeof(Dictionary<string, GuiElement>).IsAssignableFrom(elements.FieldType));
        Assert.NotNull(typeof(ScreenManager).GetField("CurrentScreen", flags));
        Assert.NotNull(typeof(GuiScreenRunningGame).GetField("runningGame", flags));
    }
}
