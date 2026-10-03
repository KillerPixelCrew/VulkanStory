using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan;
using Xunit;

namespace VulkanStory.Game.Tests;

public sealed class UniformBufferIntegrationTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Payload { public long First, Second; }

    [Fact]
    public void OriginalUboMethodsWriteOwnedShadowRangesAndPreserveDisposal()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        // Uniform shadows are CPU state; this device never creates a Vulkan context or window.
        using var device = new VulkanDevice();
        bool enabled = false;
        var group = ShaderConsumerPatches.CreateSubset(() => enabled);
        group.Validate();
        var adapter = GameGraphicsAdapter.Attach(platform, device, () => enabled,
            () => true, () => 4, () => false,
            new GameShaderCallbacks(() => false, (_, _) => { }, _ => { }));
        try
        {
            group.Install();
            enabled = true;
            var buffer = Assert.IsType<UBO>(platform.CreateUBO(123, 0, "IntegrationBlock", 16));
            Assert.Equal(16, buffer.Size);
            Assert.False(buffer.Disposed);
            Assert.True(buffer.Handle > 0);
            Assert.Equal(new byte[16], adapter.UniformBufferShadowForTests(buffer));

            var payload = new Payload { First = 0x0102030405060708, Second = 0x1112131415161718 };
            buffer.Update(payload);
            byte[] expected = [.. BitConverter.GetBytes(payload.First), .. BitConverter.GetBytes(payload.Second)];
            Assert.Equal(expected, adapter.UniformBufferShadowForTests(buffer));
            Assert.Null(UniformBufferUploads.BoundForTests);
            Assert.Equal(0, UniformBufferUploads.ObservedHandlesForTests);

            var changed = new Payload { First = 0x2122232425262728, Second = 0 };
            // Original generic range upload copies from the beginning of its pinned struct.
            buffer.Update(changed, 8, 8);
            BitConverter.GetBytes(changed.First).CopyTo(expected, 8);
            Assert.Equal(expected, adapter.UniformBufferShadowForTests(buffer));
            Assert.Null(UniformBufferUploads.BoundForTests);
            Assert.Equal(0, UniformBufferUploads.ObservedHandlesForTests);

            byte[] patch = [9, 8, 7, 6];
            buffer.Update((object)patch, 0, patch.Length);
            patch.CopyTo(expected, 0);
            Assert.Equal(expected, adapter.UniformBufferShadowForTests(buffer));

            Assert.Equal(0, UniformBufferUploads.ObservedHandlesForTests);

            buffer.Dispose();
            Assert.True(buffer.Disposed);
            Assert.Null(adapter.UniformBufferShadowForTests(buffer));
            buffer.Dispose();
            Assert.True(buffer.Disposed);
            Assert.Null(adapter.UniformBufferShadowForTests(buffer));
            Assert.Null(UniformBufferUploads.BoundForTests);
        }
        finally
        {
            enabled = false;
            group.Remove();
            adapter.Dispose();
        }
    }
}
