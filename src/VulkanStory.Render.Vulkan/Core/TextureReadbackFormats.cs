using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

// Retained raw-texel sizing shared by capture and readback.
internal static class TextureReadbackFormats
{
    public static int BytesPerTexel(Format format) => format switch
    {
        Format.R16G16B16A16Sfloat => 8,
        Format.R16G16B16A16Unorm => 8,
        Format.R32G32B32A32Sfloat => 16,
        Format.R32Sfloat => 4,
        Format.R16Sfloat => 2,
        Format.D32Sfloat => 4,
        Format.D16Unorm => 2,
        Format.R8Unorm or Format.R8Uint or Format.R8Srgb => 1,
        _ => 4,
    };
}
