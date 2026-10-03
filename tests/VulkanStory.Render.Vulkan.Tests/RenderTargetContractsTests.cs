using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Focused expectations carried from FramePlanningTests and UiSeparationTests.
public sealed class RenderTargetContractsTests
{
    [Fact]
    public void AlternatingHistoryPassesReuseTheirTwoPlansUntilTheTargetChanges()
    {
        static PassSignature Pass(int target, int read) => new()
        {
            NameId = target, Width = 32, Height = 24, FormatsId = 1,
            Attachments = [new AttachmentUse(target, ResourceUsage.ColorWrite, true)],
            Reads = [read],
        };

        var graph = new FrameGraph { Enabled = true };
        PassSignature a = Pass(10, 11);
        PassSignature b = Pass(11, 10);
        graph.OpenPass(a, true);
        graph.EndFrame();
        FramePlan? planA = graph.Plan;
        graph.OpenPass(b, true);
        graph.EndFrame();
        FramePlan? planB = graph.Plan;

        for (int i = 0; i < 6; i++)
        {
            int index = graph.OpenPass(i % 2 == 0 ? a : b, true);
            Assert.Equal(AttachmentLoadOp.DontCare, graph.PlannedLoad(index, 0));
            graph.EndFrame();
            Assert.Same(i % 2 == 0 ? planA : planB, graph.Plan);
        }

        PassSignature resized = a.Clone();
        resized.Height++;
        int changed = graph.OpenPass(resized, true);
        Assert.Equal(AttachmentLoadOp.Load, graph.PlannedLoad(changed, 0));
        graph.EndFrame();
        Assert.NotSame(planA, graph.Plan);
    }

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
