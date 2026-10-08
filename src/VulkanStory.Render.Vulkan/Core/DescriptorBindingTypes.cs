using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>The set-2 type decision shared by descriptor writes and pipeline layout creation.</summary>
internal static class DescriptorBindingTypes
{
    /// <summary>Returns the shared storage-set descriptor type for a binding under the renderer convention.</summary>
    public static DescriptorType StorageSetDescriptorType(uint binding) =>
        binding == SetConvention.ProgramRecordBinding
            ? DescriptorType.UniformBufferDynamic
            : DescriptorType.StorageBuffer;
}
