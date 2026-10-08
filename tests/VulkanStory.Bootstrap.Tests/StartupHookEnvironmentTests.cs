using VulkanStory.Bootstrap;
using Xunit;

namespace VulkanStory.Bootstrap.Tests;

/// <summary>Checks removal of only this assembly's absolute startup-hook entries while retaining other hook order.</summary>
/// <remarks>Exercises string filtering without changing the test process environment.</remarks>
public sealed class StartupHookEnvironmentTests
{
    [Fact]
    public void RemovesOnlyOurAbsoluteEntryAndPreservesOtherHooksAndOrder()
    {
        string own = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VulkanStory.Bootstrap.dll"));
        string other = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "another", "VulkanStory.Bootstrap.dll"));
        string input = string.Join(Path.PathSeparator, "OtherHook", own, other, "LastHook");
        Assert.Equal(string.Join(Path.PathSeparator, "OtherHook", other, "LastHook"), StartupHookEnvironment.WithoutOwnHook(input, own));
    }

    [Fact]
    public void RemovingAllOurDuplicatesLeavesNoInheritedActivation()
    {
        string own = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VulkanStory.Bootstrap.dll"));
        Assert.Equal("", StartupHookEnvironment.WithoutOwnHook(string.Join(Path.PathSeparator, own, own), own));
        Assert.Null(StartupHookEnvironment.WithoutOwnHook(null, own));
    }
}
