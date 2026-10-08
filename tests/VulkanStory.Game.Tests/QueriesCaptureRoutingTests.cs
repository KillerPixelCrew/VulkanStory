using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.ClientNative;
using VulkanStory.Render.Vulkan;
using Xunit;

namespace VulkanStory.Game.Tests;

/// <summary>Checks official query/capture patch ownership, supported token guards, and removal.</summary>
/// <remarks>No GPU query result or screenshot file is produced.</remarks>
public sealed class QueriesCaptureRoutingTests
{
    [Fact]
    public void ActualCallSitesInstallAndCaptureOwnershipAndQueryGuardsAvoidGl()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        using var device = new VulkanDevice();
        bool enabled = false;
        var group = QueriesCaptureConsumerPatches.CreateSubset(() => enabled);
        group.Validate();
        var adapter = GameGraphicsAdapter.Attach(platform, device, () => enabled, () => true, () => 4,
            () => false, new GameShaderCallbacks(() => false, (_, _) => { }, _ => { }));
        var shot = new Screenshot();
        var moon = (SystemRenderSunMoon)RuntimeHelpers.GetUninitializedObject(typeof(SystemRenderSunMoon));
        MethodBase[] originals =
        [
            typeof(SystemRenderSunMoon).GetConstructor([typeof(ClientMain)])!,
            typeof(SystemRenderSunMoon).GetMethod("OnRenderFrame3DPost", BindingFlags.Instance | BindingFlags.NonPublic)!,
            typeof(SystemRenderSunMoon).GetMethod("Dispose", [typeof(ClientMain)])!,
            typeof(Screenshot).GetMethod("GrabScreenshot", [typeof(Size2i), typeof(bool), typeof(bool), typeof(bool)])!,
            typeof(ClientPlatformWindows).GetMethod("SaveScreenshot", [typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(string)])!,
            typeof(ClientPlatformWindows).GetMethod("GrabScreenshot", [typeof(bool), typeof(bool)])!,
            typeof(ClientPlatformWindows).GetMethod("GrabScreenshot", [typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool)])!,
        ];
        try
        {
            group.Install();
            foreach (var original in originals)
                Assert.Contains("vulkanstory.routing.graphics-queries-capture", Harmony.GetPatchInfo(original)!.Owners);
            enabled = true;
            int query = 0;
            Assert.Throws<InvalidOperationException>(() => QueriesCaptureConsumerPatches.Generate(1, ref query, moon));
            Assert.Throws<InvalidOperationException>(() => platform.SaveScreenshot()); // Missing service rejects before file/Skia work.
            typeof(ClientPlatformWindows).GetField("screenshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(platform, shot);
            Assert.Same(shot, adapter.CaptureService());
            Assert.Same(adapter, GameGraphicsAdapter.CaptureOwner(shot));
            Assert.Throws<InvalidOperationException>(() => GameGraphicsAdapter.CaptureOwner(new Screenshot()));
            QueriesCaptureConsumerPatches.ReadPixels(0, 0, 0, 0, (PixelFormat)32993, (PixelType)5121, IntPtr.Zero, shot);
            Assert.Throws<NotSupportedException>(() => QueriesCaptureConsumerPatches.ReadPixels(0, 0, 0, 0,
                (PixelFormat)6408, (PixelType)5121, IntPtr.Zero, shot));
            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            game.Platform = platform;
            typeof(ClientSystem).GetField("game", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(moon, game);
            Assert.Throws<NotSupportedException>(() => QueriesCaptureConsumerPatches.Generate(2, ref query, moon));
            int result = 42;
            Assert.Throws<InvalidOperationException>(() => QueriesCaptureConsumerPatches.Result(99, (GetQueryObjectParam)34919, ref result, moon));
            Assert.Equal(42, result);
            Assert.Throws<InvalidOperationException>(() => QueriesCaptureConsumerPatches.Begin((QueryTarget)35092, 99, moon));
            Assert.Throws<NotSupportedException>(() => QueriesCaptureConsumerPatches.Begin((QueryTarget)0, 99, moon));
            Assert.Throws<InvalidOperationException>(() => QueriesCaptureConsumerPatches.Delete(99, moon));
        }
        finally { enabled = false; group.Remove(); adapter.Dispose(); }
        foreach (var original in originals)
            Assert.DoesNotContain("vulkanstory.routing.graphics-queries-capture", Harmony.GetPatchInfo(original)?.Owners ?? []);
    }
}
