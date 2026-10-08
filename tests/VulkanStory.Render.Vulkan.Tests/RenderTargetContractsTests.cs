using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Focused expectations carried from FramePlanningTests and UiSeparationTests.
/// <summary>Checks CPU history-plan reuse and premultiplied UI blend-factor policy.</summary>
/// <remarks>Does not create a target or validate visible UI composition.</remarks>
public sealed class RenderTargetContractsTests
{
    [Fact]
    public void OnlyStandardUiBlendChangesTheSourceAlphaFactor()
    {
        AttachmentBlend standard = AttachmentBlend.For(true, RenderBlendMode.Standard).ForUiImage();
        Assert.Equal(BlendFactor.SrcAlpha, standard.SrcColor);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, standard.DstColor);
        Assert.Equal(BlendFactor.One, standard.SrcAlpha);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, standard.DstAlpha);

        foreach (RenderBlendMode mode in new[]
                 {
                     RenderBlendMode.PremultipliedAlpha, RenderBlendMode.Brighten,
                     RenderBlendMode.Multiply, RenderBlendMode.Glow, RenderBlendMode.Overlay,
                 })
        {
            AttachmentBlend plain = AttachmentBlend.For(true, mode);
            Assert.Equal(plain, plain.ForUiImage());
        }
    }
}
