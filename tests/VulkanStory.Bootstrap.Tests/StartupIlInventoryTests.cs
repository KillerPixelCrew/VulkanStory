using System.Reflection;
using VulkanStory.Game;
using Xunit;

namespace VulkanStory.Bootstrap.Tests;

public sealed class StartupIlInventoryTests
{
    private sealed class Fixture
    {
        public int Value = -3;
    }

    private static int ReadAndCall(Fixture fixture) => Math.Abs(fixture.Value);

    [Fact]
    public void ScannerKeepsExactFieldAndCallOperandsInIlOrder()
    {
        MethodInfo method = typeof(StartupIlInventoryTests).GetMethod(
            nameof(ReadAndCall), BindingFlags.NonPublic | BindingFlags.Static)!;
        IReadOnlyList<StartupIlUse> uses = StartupIlInventory.Scan(method);

        Assert.Contains(uses, use => use.Opcode == "ldfld" &&
            use.Member.Contains("::Value:System.Int32", StringComparison.Ordinal));
        Assert.Contains(uses, use => use.Opcode == "call" &&
            use.Member == "System.Math::Abs(System.Int32)");
        Assert.True(uses.Zip(uses.Skip(1), (a, b) => a.Offset < b.Offset).All(inOrder => inOrder));
    }
}
