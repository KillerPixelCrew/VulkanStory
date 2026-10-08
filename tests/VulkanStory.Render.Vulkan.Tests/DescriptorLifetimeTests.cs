using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Retained CPU cases from ResourceLifetimeTests.
/// <summary>Checks descriptor types, resource-age classification, and resource-lifetime-aware cache keys.</summary>
/// <remarks>CPU cache contracts only; no Vulkan descriptor lifetime is exercised.</remarks>
public sealed class DescriptorLifetimeTests
{
    [Fact]
    public void ProgramRecordUsesTheDynamicUniformDescriptorType()
    {
        Assert.Equal(DescriptorType.UniformBufferDynamic,
            DescriptorBindingTypes.StorageSetDescriptorType((uint)SetConvention.ProgramRecordBinding));
        Assert.Equal(DescriptorType.StorageBuffer,
            DescriptorBindingTypes.StorageSetDescriptorType((uint)SetConvention.ProgramRecordBinding + 1));
    }

    [Fact]
    public void RecentBindingsExpireWithoutAffectingPermanentResources()
    {
        var age = new ResourceAge(2);
        age.NoteFrame(100);
        age.NoteFrame(200);
        age.NoteFrame(300);
        Assert.False(age.IsShortLived(0));
        Assert.False(age.IsShortLived(200));
        Assert.True(age.IsShortLived(201));

        var permanent = new BufferBindingValue(0, new Silk.NET.Vulkan.Buffer(1), 0, 64);
        var recent = new SamplerBindingValue(0, new ImageView(1), new Sampler(2), Resource: 201);
        var contents = new DescriptorSetContents(1, 0, [recent], [permanent]);
        Assert.True(age.NamesShortLived(contents));
        age.NoteFrame(400);
        Assert.False(age.NamesShortLived(contents));
    }

    [Fact]
    public void RecycledHandlesDoNotAliasDifferentResourceLifetimes()
    {
        static DescriptorSetContents Entry(ulong lifetime) => new(1, 1,
            [new SamplerBindingValue(0, new ImageView(123), new Sampler(456), Resource: lifetime)],
            [new BufferBindingValue(1, new Silk.NET.Vulkan.Buffer(789), 0, 256, Resource: lifetime)]);
        var cache = new Dictionary<DescriptorSetContents, string>
        {
            [Entry(10)] = "original",
            [Entry(11)] = "replacement"
        };
        Assert.Equal("original", cache[Entry(10)]);
        Assert.Equal("replacement", cache[Entry(11)]);
    }
}
