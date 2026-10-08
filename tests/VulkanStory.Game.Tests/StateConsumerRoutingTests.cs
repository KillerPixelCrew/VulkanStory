using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan;
using Xunit;

namespace VulkanStory.Game.Tests;

/// <summary>Checks original graphics state calls populating renderer-owned state through active patches.</summary>
/// <remarks>Uses a device without a Vulkan context; no native draw is recorded.</remarks>
public sealed class StateConsumerRoutingTests
{
    [Fact]
    public void OriginalStateCallsPopulateOwnedStateWithoutCallingGl()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        using var device = new VulkanDevice(); // CPU state only; no Vulkan context.
        bool enabled = false;
        var group = StateConsumerPatches.CreateSubset(() => enabled);
        group.Validate();
        var adapter = GameGraphicsAdapter.Attach(platform, device, () => enabled, () => true, () => 4,
            () => false, new GameShaderCallbacks(() => false, (_, _) => { }, _ => { }));
        try
        {
            group.Install(); enabled = true;
            platform.GlViewport(1, 2, 128, 96);
            Assert.Equal(1, adapter.Stated.Viewport.Offset.X);
            Assert.Equal(128u, adapter.Stated.Viewport.Extent.Width);
            platform.GlScissor(-2, -3, 10, 11);
            Assert.Equal(0, adapter.Stated.Scissor.Offset.X);
            Assert.Equal(0, adapter.Stated.Scissor.Offset.Y);
            Assert.Equal(8u, adapter.Stated.Scissor.Extent.Width);
            Assert.Equal(8u, adapter.Stated.Scissor.Extent.Height);
            platform.GlScissorFlag(true);
            Assert.True(platform.GlScissorFlagEnabled);
            platform.GlEnableDepthTest(); platform.GlDepthMask(false);
            platform.GlDepthFunc((EnumDepthFunction)515);
            Assert.True(adapter.Stated.DepthTest); Assert.False(adapter.Stated.DepthWrite);
            Assert.Equal(CompareOp.LessOrEqual, adapter.Stated.DepthCompare);
            platform.GlEnableCullFace(); platform.GlCullFaceFront();
            Assert.True(adapter.Stated.CullEnabled); Assert.False(adapter.Stated.CullBack);
            platform.GLLineWidth(2f); platform.GLWireframes(true);
            Assert.Equal(2f, adapter.Stated.LineWidth); Assert.True(adapter.Stated.Wireframe);
            platform.GlColorMask(false, true, false, true);
            Assert.Equal(ColorComponentFlags.GBit | ColorComponentFlags.ABit, adapter.Stated.ColorMask);
            platform.GlToggleBlend(true, EnumBlendMode.Multiply);
            Assert.True(adapter.Stated.BlendEnabled);
            platform.GlToggleBlend(false, EnumBlendMode.Standard);
            Assert.False(adapter.Stated.BlendEnabled);
            platform.GlDisableDepthTest(); platform.GlDisableCullFace(); platform.GlScissorFlag(false);
            Assert.False(adapter.Stated.DepthTest); Assert.False(adapter.Stated.CullEnabled);
            Assert.False(platform.GlScissorFlagEnabled);
        }
        finally { enabled = false; group.Remove(); adapter.Dispose(); }
    }
}
