using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

// Retained raw-texel sizing shared by capture and readback.
/// <summary>Raw texel sizing shared by framebuffer capture and texture readback.</summary>
internal static class TextureReadbackFormats
{
    /// <summary>Returns exact raw-readback texel sizes for supported uncompressed formats.</summary>
    /// <param name="format">Image format being copied to host memory.</param>
    /// <returns>Raw bytes per texel.</returns>
    /// <exception cref="NotSupportedException">The format has no supported uncompressed readback representation.</exception>
    public static int BytesPerTexel(Format format) => format switch
    {
        Format.R8Unorm or Format.R8SNorm or Format.R8Uint or Format.R8Sint or Format.R8Srgb => 1,
        Format.R8G8Unorm or Format.R8G8SNorm or Format.R8G8Uint or Format.R8G8Sint or Format.R8G8Srgb or
        Format.R16Unorm or Format.R16SNorm or Format.R16Uint or Format.R16Sint or Format.R16Sfloat or
        Format.D16Unorm => 2,
        Format.R8G8B8Unorm or Format.R8G8B8SNorm or Format.R8G8B8Uint or Format.R8G8B8Sint or Format.R8G8B8Srgb or
        Format.B8G8R8Unorm or Format.B8G8R8SNorm or Format.B8G8R8Uint or Format.B8G8R8Sint or Format.B8G8R8Srgb => 3,
        Format.R8G8B8A8Unorm or Format.R8G8B8A8SNorm or Format.R8G8B8A8Uint or Format.R8G8B8A8Sint or Format.R8G8B8A8Srgb or
        Format.B8G8R8A8Unorm or Format.B8G8R8A8SNorm or Format.B8G8R8A8Uint or Format.B8G8R8A8Sint or Format.B8G8R8A8Srgb or
        Format.R16G16Unorm or Format.R16G16SNorm or Format.R16G16Uint or Format.R16G16Sint or Format.R16G16Sfloat or
        Format.R32Uint or Format.R32Sint or Format.R32Sfloat or Format.D32Sfloat or
        Format.B10G11R11UfloatPack32 or Format.E5B9G9R9UfloatPack32 => 4,
        Format.R16G16B16Unorm or Format.R16G16B16SNorm or Format.R16G16B16Uint or Format.R16G16B16Sint or
        Format.R16G16B16Sfloat => 6,
        Format.R16G16B16A16Unorm or Format.R16G16B16A16SNorm or Format.R16G16B16A16Uint or Format.R16G16B16A16Sint or
        Format.R16G16B16A16Sfloat or Format.R32G32Uint or Format.R32G32Sint or Format.R32G32Sfloat => 8,
        Format.R32G32B32Uint or Format.R32G32B32Sint or Format.R32G32B32Sfloat => 12,
        Format.R32G32B32A32Uint or Format.R32G32B32A32Sint or Format.R32G32B32A32Sfloat => 16,
        _ => throw new NotSupportedException("Unsupported raw texture readback format: " + format),
    };

    /// <summary>Whether the retained parity decoder can interpret this raw format.</summary>
    internal static bool HasParityDecoder(Format format) => format is
        Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb or Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb or
        Format.R8Unorm or Format.R16G16B16A16Sfloat or Format.R16G16B16A16Unorm or Format.R32G32B32A32Sfloat or
        Format.R16Sfloat or Format.R32Sfloat or Format.D32Sfloat or Format.D16Unorm;
}
