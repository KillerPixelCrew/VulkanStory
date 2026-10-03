using System.Reflection;
using VulkanStory.Game;
using Xunit;

namespace VulkanStory.Bootstrap.Tests;

public sealed class StartupTargetTests
{
    private sealed class Overloads
    {
        public void Start(int value) { GC.KeepAlive(value); }
        public void Start(string value) { GC.KeepAlive(value); }
    }

    [Fact]
    public void MatchesTheExactOverloadNotJustParameterCount()
    {
        var target = new StartupTarget(typeof(Overloads).FullName!, "Start", false, ["System.String"], "test");
        MethodBase method = StartupTargets.Resolve(typeof(Overloads), target);
        Assert.Equal(typeof(string), Assert.Single(method.GetParameters()).ParameterType);
    }

    [Fact]
    public void RejectsChangedSignatureAndStaticness()
    {
        var changed = new StartupTarget(typeof(Overloads).FullName!, "Start", false, ["System.Int64"], "test");
        Assert.Throws<MissingMethodException>(() => StartupTargets.Resolve(typeof(Overloads), changed));
        Assert.Throws<MissingMethodException>(() => StartupTargets.Resolve(typeof(Overloads), changed with { Parameters = ["System.Int32"], IsStatic = true }));
    }
}
