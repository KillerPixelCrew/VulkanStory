using VulkanStory.Game;
using Xunit;

namespace VulkanStory.Bootstrap.Tests;

public sealed class StartupIlProfileTests
{
    private static StartupMethodInventory Method(params StartupIlUse[] uses) =>
        new("startup", "Fixture.Start()", 32, uses);

    [Fact]
    public void ChangedOperandAtTheSameOffsetIsRejectedBeforePatching()
    {
        var anchor = new StartupIlUse(5, "call", 123, "Fixture::CreateWindow()");
        StartupMethodInventory expected = Method(anchor);
        StartupMethodInventory changed = Method(anchor with { Token = 456, Member = "Fixture::OtherWindow()" });
        Assert.Throws<InvalidOperationException>(() => StartupIlProfile.Verify([expected], [changed]));
        Assert.Throws<InvalidOperationException>(() => StartupIlProfile.Verify([expected], [Method(anchor with { Offset = 6 })]));
    }

    [Fact]
    public void AdditionalOrMissingOccurrencesAreRejected()
    {
        var anchor = new StartupIlUse(5, "call", 123, "Fixture::CreateWindow()");
        Assert.Throws<InvalidOperationException>(() => StartupIlProfile.Verify([Method(anchor)], [Method()]));
        Assert.Throws<InvalidOperationException>(() => StartupIlProfile.Verify([Method(anchor)],
            [Method(anchor, anchor with { Offset = 10 })]));
        StartupIlProfile.Verify([Method(anchor)], [Method(anchor)]);
    }
}
