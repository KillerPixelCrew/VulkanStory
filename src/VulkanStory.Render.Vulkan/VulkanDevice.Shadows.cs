using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    /// <summary>D16 shadow maps need attachment, comparison sampling and linear filtering.</summary>
    public bool SupportsCompactShadowDepth()
    {
        _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice,
            Format.D16Unorm, out FormatProperties properties);
        const FormatFeatureFlags required = FormatFeatureFlags.DepthStencilAttachmentBit |
            FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.SampledImageFilterLinearBit |
            FormatFeatureFlags.TransferSrcBit | FormatFeatureFlags.TransferDstBit;
        return (properties.OptimalTilingFeatures & required) == required;
    }
}
