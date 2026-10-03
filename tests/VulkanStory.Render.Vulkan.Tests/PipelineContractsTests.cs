using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Focused expectations carried from VertexAttributeDefaultTests and PipelineCacheTests.
public sealed class PipelineContractsTests
{
    [Theory]
    [InlineData("float", Format.R32Sfloat, 0u)]
    [InlineData("vec3", Format.R32G32B32Sfloat, 0u)]
    [InlineData("int", Format.R32Sint, 16u)]
    [InlineData("uvec3", Format.R32G32B32Uint, 16u)]
    public void MissingVertexAttributesUseTheMatchingDefaultFormat(
        string type, Format expectedFormat, uint expectedOffset)
    {
        Assert.True(GlslType.TryParse(type, out GlslType parsed));
        VertexLayoutDescription merged = VertexLayoutDescription.Empty.WithDefaultsFor(
            new[] { new VertexInputSlot("x", 7, parsed) });

        VertexAttribute attribute = Assert.Single(merged.Attributes);
        Assert.Equal(7u, attribute.Location);
        Assert.Equal(VertexLayoutDescription.DefaultAttributeBinding, attribute.Binding);
        Assert.Equal(expectedFormat, attribute.Format);
        Assert.Equal(expectedOffset, attribute.Offset);
    }

    [Fact]
    public void FullscreenShaderWithoutInputsNeedsNoDefaultVertexBuffer()
    {
        Assert.Same(VertexLayoutDescription.Empty,
            VertexLayoutDescription.Empty.WithDefaultsFor(Array.Empty<VertexInputSlot>()));
    }

    [Fact]
    public void DriverCacheGrowthIsMeasuredFromTheLastSavedSize()
    {
        const long mib = 1024 * 1024;
        long second = Stopwatch.Frequency;
        var trigger = new PipelineCacheGrowthTrigger(8 * mib, TimeSpan.FromSeconds(10), baselineBytes: 2 * mib);

        Assert.False(trigger.SampleDue(100 * second));
        Assert.False(trigger.SampleDue(105 * second));
        Assert.True(trigger.SampleDue(110 * second));
        Assert.False(trigger.SampleDue(119 * second));
        Assert.True(trigger.SampleDue(121 * second));

        Assert.False(trigger.GrewEnough(9 * mib));
        Assert.True(trigger.GrewEnough(10 * mib));
        trigger.NoteSaved(10 * mib);
        Assert.False(trigger.GrewEnough(17 * mib));
        Assert.True(trigger.GrewEnough(18 * mib));
    }
}
