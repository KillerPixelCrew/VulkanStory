using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;
using Xunit;

namespace VulkanStory.Game.Tests;

/// <summary>Checks texture patch ownership failure before native GL access and matching removal.</summary>
/// <remarks>Does not allocate, upload, or sample textures.</remarks>
public sealed class TextureConsumerRoutingTests
{
    [Fact]
    public void ActualTexturePrefixesRejectMissingOwnerBeforeCallingGlAndAreRemoved()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        MethodInfo delete = typeof(ClientPlatformWindows).GetMethod("GLDeleteTexture", [typeof(int)])!;
        bool enabled = true;
        var group = TextureConsumerPatches.CreateSubset(() => enabled);
        group.Validate();
        try
        {
            group.Install();
            Assert.Contains("vulkanstory.routing.graphics-textures", Harmony.GetPatchInfo(delete)!.Owners);
            var error = Assert.Throws<TargetInvocationException>(() => delete.Invoke(platform, [123]));
            var cause = Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Contains("no renderer adapter", cause.Message);
        }
        finally
        {
            enabled = false;
            group.Remove();
        }
        Assert.DoesNotContain("vulkanstory.routing.graphics-textures",
            Harmony.GetPatchInfo(delete)?.Owners ?? []);
    }
}
