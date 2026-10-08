using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan;
using Xunit;

namespace VulkanStory.Game.Tests;

/// <summary>Checks official framebuffer lifecycle routes, host dimensions, placeholders, and ownership/disposal guards.</summary>
/// <remarks>Uses owned zero-ID placeholders and CPU state rather than native framebuffer allocation.</remarks>
public sealed class FramebufferLifecycleRoutingTests
{
    [Fact]
    public void OriginalSetupAndLoadsUseConfiguredPixelsAndOwnedPlaceholderLifetime()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        using var device = new VulkanDevice(); // No context, native allocation or frame.
        bool enabled = false;
        var group = FramebufferConsumerPatches.CreateSubset(() => enabled);
        group.Validate();
        var adapter = GameGraphicsAdapter.Attach(platform, device, () => enabled, () => true, () => 4,
            () => false, new GameShaderCallbacks(() => false, (_, _) => { }, _ => { }));
        MethodInfo[] added =
        [
            typeof(ClientPlatformWindows).GetMethod("SetupDefaultFrameBuffers", Type.EmptyTypes)!,
            typeof(ClientPlatformWindows).GetMethod("DisposeFrameBuffers", [typeof(List<FrameBufferRef>)])!,
            typeof(ClientPlatformWindows).GetMethod("LoadFrameBuffer", [typeof(FrameBufferRef), typeof(int)])!,
            typeof(ClientPlatformWindows).GetMethod("UnloadFrameBuffer", [typeof(FrameBufferRef)])!,
            typeof(ClientPlatformWindows).GetMethod("LoadFrameBuffer", [typeof(EnumFrameBuffer)])!,
            typeof(ClientPlatformWindows).GetMethod("UnloadFrameBuffer", [typeof(EnumFrameBuffer)])!,
        ];
        int resetFg = 0, releaseAo = 0, built = 0, temporalReset = 0, planned = 0;
        (int Width, int Height) display = (0, 0);
        var host = new GameFramebufferHost(() => display,
            () => new GameFramebufferSettings(2, 1, .5f, .5f, true, true, false),
            (_, _) => { planned++; return null; },
            reason => Assert.Fail(reason), reason => Assert.Fail(reason),
            _ => { }, reason => Assert.Fail(reason),
            () => resetFg++, () => releaseAo++, (_, _) => built++, () => temporalReset++);
        try
        {
            group.Install();
            foreach (MethodInfo method in added)
                Assert.Contains("vulkanstory.routing.graphics-framebuffers", Harmony.GetPatchInfo(method)!.Owners);
            enabled = true;
            var missing = Assert.Throws<InvalidOperationException>(() => platform.SetupDefaultFrameBuffers());
            Assert.Contains("host is not configured", missing.Message);
            Assert.Throws<InvalidOperationException>(() => platform.LoadFrameBuffer(EnumFrameBuffer.Default));
            adapter.ConfigureFramebufferHost(host);
            adapter.FrameState = new GameGraphicsFrameState(false, false, 4);
            adapter.TaaHistoryValid = true;
            List<FrameBufferRef> buffers = platform.SetupDefaultFrameBuffers();
            Assert.Equal(26, buffers.Count);
            Assert.All(buffers, target => Assert.Null(target));
            Assert.Equal(1, planned);
            Assert.False(adapter.TaaHistoryValid);
            Assert.Equal(-1, adapter.FrameState.MotionAttachment);
            Assert.Equal(.5f, GameFramebufferBindings.SsaaLevel(platform));
            Assert.True((bool)typeof(ClientPlatformWindows).GetField("SetupSSAO", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(platform)!);
            Assert.Equal(1, (int)typeof(ClientPlatformWindows).GetField("ShadowMapQuality", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(platform)!);
            Assert.Equal(0, built);
            Assert.Equal(0, temporalReset);
            display = (640, 360);
            platform.ToggleOffscreenBuffer(false);
            platform.LoadFrameBuffer(EnumFrameBuffer.Default);
            Assert.Null(platform.CurrentFrameBuffer);
            Assert.Equal(-1, adapter.CurrentTargetId);
            Assert.Equal(640u, adapter.Stated.Viewport.Extent.Width);
            Assert.Equal(360u, adapter.Stated.Viewport.Extent.Height);
            adapter.Stated.DepthWrite = false;
            platform.UnloadFrameBuffer(EnumFrameBuffer.Transparent);
            Assert.True(adapter.Stated.DepthWrite);
            Assert.Equal(320u, adapter.Stated.Viewport.Extent.Width);
            Assert.Equal(180u, adapter.Stated.Viewport.Extent.Height);
            platform.UnloadFrameBuffer(new FrameBufferRef()); // Original ignores the reference.
            Assert.Null(platform.CurrentFrameBuffer);
            Assert.Equal(320u, adapter.Stated.Viewport.Extent.Width);

            // Real owned zero-ID placeholders, not fake positive GPU handles.
            FrameBufferRef far = adapter.CreatePlaceholderTarget(1024, 1024);
            FrameBufferRef near = adapter.CreatePlaceholderTarget(2048, 2048);
            buffers[11] = far; buffers[12] = near;
            typeof(ClientPlatformWindows).GetField("frameBuffers", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(platform, buffers);
            Assert.Throws<InvalidOperationException>(() => platform.LoadFrameBuffer(far, 2));
            platform.LoadFrameBuffer(EnumFrameBuffer.ShadowmapFar);
            Assert.Same(far, platform.CurrentFrameBuffer);
            Assert.Equal(-1, adapter.CurrentTargetId);
            Assert.True(adapter.Stated.DepthWrite && adapter.Stated.DepthTest && adapter.Stated.CullEnabled);
            var foreign = new FrameBufferRef { FboId = 77 };
            Assert.Throws<InvalidOperationException>(() => platform.DisposeFrameBuffers([far, foreign]));
            Assert.False(far.Disposed);
            Assert.Equal(0, resetFg);
            platform.DisposeFrameBuffers(buffers);
            Assert.True(far.Disposed && near.Disposed);
            Assert.Null(platform.CurrentFrameBuffer);
            Assert.Equal(1, resetFg);
            Assert.Equal(1, releaseAo);
            platform.DisposeFrameBuffers(buffers);
            platform.DisposeFrameBuffer(far);
            Assert.Equal(1, resetFg);
            Assert.Equal(1, releaseAo);
            Assert.Throws<ObjectDisposedException>(() => platform.LoadFrameBuffer(EnumFrameBuffer.ShadowmapFar));
        }
        finally { enabled = false; group.Remove(); adapter.Dispose(); }
        foreach (MethodInfo method in added)
            Assert.DoesNotContain("vulkanstory.routing.graphics-framebuffers", Harmony.GetPatchInfo(method)?.Owners ?? []);
    }
}
