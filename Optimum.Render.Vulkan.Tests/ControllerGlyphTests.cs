using System.Collections.Generic;
using Optimum;
using Optimum.Render.Vulkan.Platform;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed class ControllerGlyphTests
{
    [Fact]
    public void HintsFollowRemappedFaceButtonsAndTriggerAxes()
    {
        var profile = new ControllerProfile { AcceptButton = 2, SneakButton = 9, PrimaryTriggerAxis = 5,
            SecondaryTriggerAxis = 0, SecondaryTriggerNegative = true };
        Dictionary<string, string> glyphs = ControllerGlyphs.Build(profile, button =>
            button switch { 0 => "Cross", 1 => "Circle", 2 => "Square", 3 => "Triangle", _ => "Button" });
        Assert.Equal("□", glyphs["jump"]);
        Assert.Equal("R2", glyphs["primarymouse"]);
        Assert.Equal("LX−", glyphs["secondarymouse"]);
        Assert.Equal("L1", glyphs["sneak"]);
    }

    [Fact]
    public void PromptSnapshotDoesNotChangeWithCallerAndClearsOnDisconnect()
    {
        var glyphs = new Dictionary<string, string> { ["jump"] = "A" };
        OptimumControllerHints.SetControllerActive(false);
        OptimumControllerHints.Publish(glyphs);
        try
        {
            Assert.Null(OptimumControllerHints.GlyphFor("jump"));
            Assert.True(OptimumControllerHints.SetControllerActive(true));
            Assert.False(OptimumControllerHints.SetControllerActive(true));
            glyphs["jump"] = "B";
            Assert.Equal("A", OptimumControllerHints.GlyphFor("jump"));
            Assert.Equal("[A] Jump", OptimumControllerHints.LabelFor("jump", "Jump"));
            Assert.Null(OptimumControllerHints.GlyphFor("unknown"));
            Assert.True(OptimumControllerHints.SetControllerActive(false));
            Assert.Null(OptimumControllerHints.GlyphFor("jump"));
            Assert.Equal("Jump", OptimumControllerHints.LabelFor("jump", "Jump"));
        }
        finally { OptimumControllerHints.Publish(null); }
        Assert.Null(OptimumControllerHints.GlyphFor("jump"));
    }

    [Fact]
    public void PromptListenersRunOnlyWhenTheSnapshotOrInputModeChanges()
    {
        OptimumControllerHints.Publish(null);
        int changes = 0;
        System.Action listener = () => changes++;
        OptimumControllerHints.Subscribe(listener);
        OptimumControllerHints.Publish(new Dictionary<string, string> { ["jump"] = "A" });
        Assert.Equal(1, changes);
        Assert.True(OptimumControllerHints.SetControllerActive(true));
        Assert.Equal(2, changes);
        Assert.False(OptimumControllerHints.SetControllerActive(true));
        Assert.Equal(2, changes);
        OptimumControllerHints.Publish(null);
        Assert.Equal(3, changes);
        System.GC.KeepAlive(listener);
    }
}
