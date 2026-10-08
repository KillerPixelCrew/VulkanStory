using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

// Retained raw-texel sizing shared by capture and readback.
/// <summary>Raw texel sizing shared by framebuffer capture and texture readback.</summary>
internal static class TextureReadbackFormats
{
    /// <summary>Returns retained raw-readback texel sizes for supported formats.</summary>
    /// <param name="format">Image format being copied to host memory.</param>
    /// <returns>Raw bytes per texel; unlisted formats use the retained four-byte fallback.</returns>
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
