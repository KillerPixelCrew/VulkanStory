using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed class SdlKeyMapTests
{
    [Theory]
    [InlineData(4, GlKeys.A)]
    [InlineData(29, GlKeys.Z)]
    [InlineData(30, GlKeys.Number1)]
    [InlineData(39, GlKeys.Number0)]
    [InlineData(40, GlKeys.Enter)]
    [InlineData(45, GlKeys.Minus)]
    [InlineData(46, GlKeys.Plus)]
    [InlineData(58, GlKeys.F1)]
    [InlineData(69, GlKeys.F12)]
    [InlineData(89, GlKeys.Keypad1)]
    [InlineData(97, GlKeys.Keypad9)]
    [InlineData(98, GlKeys.Keypad0)]
    [InlineData(104, GlKeys.F13)]
    [InlineData(115, GlKeys.F24)]
    [InlineData(224, GlKeys.ControlLeft)]
    [InlineData(229, GlKeys.ShiftRight)]
    [InlineData(230, GlKeys.AltRight)]
    [InlineData(0, GlKeys.Unknown)]
    [InlineData(512, GlKeys.Unknown)]
    public void PhysicalScancodeMapsToExistingGameHotkey(int scancode, GlKeys expected) =>
        Assert.Equal(expected, SdlKeyMap.ToGlKey(scancode));

    [Fact]
    public void EveryMappedKeyFitsTheGamesKeyboardState()
    {
        for (int scancode = 0; scancode < 512; scancode++)
            Assert.InRange((int)SdlKeyMap.ToGlKey(scancode), 0, 130);
    }
}
