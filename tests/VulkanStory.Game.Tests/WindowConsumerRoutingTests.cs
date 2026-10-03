using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Xunit;

namespace VulkanStory.Game.Tests;

public sealed class WindowConsumerRoutingTests
{
    [Fact]
    public void ActualHarmonyGroupPreservesDormantCallsAndRejectsMissingActiveAdapter()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        var originalSize = new Size2i(123, 456);
        typeof(ClientPlatformWindows).GetField("screensize", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(platform, originalSize);
        bool enabled = false;
        StartupPatchGroup group = WindowConsumerPatches.CreateCoreGroup(() => enabled);
        group.Validate();
        try
        {
            group.Install();
            Assert.Same(originalSize, platform.ScreenSize);
            enabled = true;
            var error = Assert.Throws<InvalidOperationException>(() => platform.ScreenSize);
            Assert.Contains("no SDL adapter", error.Message);
        }
        finally
        {
            enabled = false;
            group.Remove();
        }
        Assert.Same(originalSize, platform.ScreenSize);
    }
}
