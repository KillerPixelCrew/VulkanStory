using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan;
using Xunit;

namespace VulkanStory.Game.Tests;

public sealed class FramebufferConsumerRoutingTests
{
    [Fact]
    public void OriginalNullBindingUpdatesOriginalFieldAndRetainsViewport()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        var previous = new FrameBufferRef { FboId = 77, Width = 32, Height = 24 };
        typeof(ClientPlatformWindows).GetField("curFb", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(platform, previous);
        using var device = new VulkanDevice(); // No native context; only null binding is exercised.
        bool enabled = false;
        var group = FramebufferConsumerPatches.CreateSubset(() => enabled);
        group.Validate();
        var adapter = GameGraphicsAdapter.Attach(platform, device, () => enabled, () => true, () => 4,
            () => false, new GameShaderCallbacks(() => false, (_, _) => { }, _ => { }));
        var setter = typeof(ClientPlatformWindows).GetProperty("CurrentFrameBuffer")!.SetMethod!;
        try
        {
            group.Install();
            Assert.Same(previous, platform.CurrentFrameBuffer); // Dormant original getter.
            Assert.Contains("vulkanstory.routing.graphics-framebuffers", Harmony.GetPatchInfo(setter)!.Owners);
            enabled = true;
            adapter.Stated.Viewport = new Silk.NET.Vulkan.Rect2D(new Silk.NET.Vulkan.Offset2D(2, 3),
                new Silk.NET.Vulkan.Extent2D(64, 48));
            platform.CurrentFrameBuffer = null!;
            Assert.Null(platform.CurrentFrameBuffer);
            Assert.Equal(-1, adapter.CurrentTargetId);
            Assert.Equal(2, adapter.Stated.Viewport.Offset.X);
            Assert.Equal(64u, adapter.Stated.Viewport.Extent.Width);
            var error = Assert.Throws<InvalidOperationException>(() => platform.CurrentFrameBuffer = previous);
            Assert.Contains("no renderer owner", error.Message);
            Assert.Null(platform.CurrentFrameBuffer);
        }
        finally { enabled = false; group.Remove(); adapter.Dispose(); }
        Assert.DoesNotContain("vulkanstory.routing.graphics-framebuffers", Harmony.GetPatchInfo(setter)?.Owners ?? []);
    }

    [Fact]
    public void ClearPrefixesAndCheckedSelectionSitesUseOwnedCpuStateAndRemoveTogether()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        var originalClear = new float[] { 0f, 0f, 0f, 1f };
        typeof(ClientPlatformWindows).GetField("clearColor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(platform, originalClear);
        using var device = new VulkanDevice(); // CPU routing only; no native frame or target.
        bool enabled = false;
        var group = FramebufferConsumerPatches.CreateSubset(() => enabled);
        group.Validate(); // All eight prefix signatures and eight original call-site bodies.
        var adapter = GameGraphicsAdapter.Attach(platform, device, () => enabled, () => true, () => 4,
            () => false, new GameShaderCallbacks(() => false, (_, _) => { }, _ => { }));
        MethodInfo[] patched =
        [
            typeof(ClientPlatformWindows).GetMethod("GlClearColorRgbaf", [typeof(float), typeof(float), typeof(float), typeof(float)])!,
            typeof(ClientPlatformWindows).GetMethod("ClearFrameBuffer", [typeof(FrameBufferRef), typeof(bool)])!,
            typeof(ClientPlatformWindows).GetMethod("ClearFrameBuffer", [typeof(FrameBufferRef), typeof(float[]), typeof(bool), typeof(bool)])!,
            typeof(ClientPlatformWindows).GetMethod("ClearFrameBuffer", [typeof(EnumFrameBuffer)])!,
            typeof(ClientPlatformWindows).GetMethod("CreateFramebuffer", [typeof(FramebufferAttrs)])!,
            typeof(ClientPlatformWindows).GetMethod("SetupDefaultFrameBuffers", Type.EmptyTypes)!,
            typeof(ClientPlatformWindows).GetMethod("LoadFrameBuffer", [typeof(EnumFrameBuffer)])!,
            typeof(ClientPlatformWindows).GetMethod("UnloadFrameBuffer", [typeof(EnumFrameBuffer)])!,
            typeof(ClientPlatformWindows).GetMethod("MergeTransparentRenderPass", Type.EmptyTypes)!,
            typeof(ClientPlatformWindows).GetMethod("RenderFinalComposition", Type.EmptyTypes)!,
            typeof(ClientPlatformWindows).GetMethod("RenderPostprocessingEffects", [typeof(float[])])!,
        ];
        try
        {
            group.Install();
            foreach (MethodInfo method in patched)
                Assert.Contains("vulkanstory.routing.graphics-framebuffers", Harmony.GetPatchInfo(method)!.Owners);
            enabled = true;
            platform.CurrentFrameBuffer = null!;
            platform.GlClearColorRgbaf(.1f, .2f, .3f, .4f);
            // Original private-array clears are independent of GL clear-color state.
            Assert.Equal(new float[] { 0f, 0f, 0f, 1f }, GameFramebufferBindings.ClearColor(platform));
            FramebufferDrawBufferRoutes.SelectSingle((DrawBufferMode)0, platform);
            Assert.Equal(0u, adapter.Stated.DrawBuffers(-1));
            FramebufferDrawBufferRoutes.SelectSingle((DrawBufferMode)1029, platform);
            Assert.Equal(1u, adapter.Stated.DrawBuffers(-1));
            FramebufferDrawBufferRoutes.SelectMultiple(1, [(DrawBuffersEnum)0], platform);
            Assert.Equal(0u, adapter.Stated.DrawBuffers(-1));
            FramebufferDrawBufferRoutes.SelectMultiple(1, [(DrawBuffersEnum)1029], platform);
            Assert.Equal(1u, adapter.Stated.DrawBuffers(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => FramebufferDrawBufferRoutes.SelectMultiple(2, [(DrawBuffersEnum)1029], platform));
            Assert.Throws<NotSupportedException>(() => FramebufferDrawBufferRoutes.SelectSingle((DrawBufferMode)36064, platform));
            Assert.Throws<NotSupportedException>(() => FramebufferDrawBufferRoutes.SelectMultiple(1, [(DrawBuffersEnum)36064], platform));
            Assert.Equal(1u, adapter.Stated.DrawBuffers(-1)); // Invalid selections never change it.
            // Original patched pass prefixes and the actual post-clear wrapper
            // can dispatch without entering GL. GPU colors are not asserted.
            platform.ClearFrameBuffer(EnumFrameBuffer.Primary);
            platform.ClearFrameBuffer(EnumFrameBuffer.Transparent);
            FramebufferDrawBufferRoutes.ClearColorBuffer((ClearBuffer)6144, 0, [1f, 1f, 1f, 1f], platform);
            Assert.Throws<NotSupportedException>(() => FramebufferDrawBufferRoutes.ClearColorBuffer((ClearBuffer)6145, 0, [1f], platform));
            var foreign = new FrameBufferRef { FboId = 77, Width = 32, Height = 24, ColorTextureIds = [88] };
            Assert.Throws<InvalidOperationException>(() => platform.ClearFrameBuffer(foreign, true));
            Assert.Throws<InvalidOperationException>(() => platform.ClearFrameBuffer(foreign, [1f, 0f, 0f, 1f], true, true));
            Assert.Throws<InvalidOperationException>(() => platform.ClearFrameBuffer(EnumFrameBuffer.Default));
            Assert.Null(platform.CurrentFrameBuffer);
            Assert.Equal(-1, adapter.CurrentTargetId);
        }
        finally { enabled = false; group.Remove(); adapter.Dispose(); }
        foreach (MethodInfo method in patched)
            Assert.DoesNotContain("vulkanstory.routing.graphics-framebuffers", Harmony.GetPatchInfo(method)?.Owners ?? []);
    }
}
