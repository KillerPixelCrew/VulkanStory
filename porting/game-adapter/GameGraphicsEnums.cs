using Silk.NET.Vulkan;
using Vintagestory.API.Client;

namespace VulkanStory.Game;

/// <summary>
/// Game-enum translations extracted from the original renderer's GlEnums.
/// Staged for the game adapter; this file does not join the backend project.
/// </summary>
internal static class GameGraphicsEnums
{
    public static PrimitiveTopology TopologyFrom(EnumDrawMode mode) => mode switch
    {
        EnumDrawMode.Triangles => PrimitiveTopology.TriangleList,
        EnumDrawMode.Lines => PrimitiveTopology.LineList,
        EnumDrawMode.LineStrip => PrimitiveTopology.LineStrip,
        _ => PrimitiveTopology.TriangleList,
    };

    public static Format TextureFormatFrom(EnumTextureInternalFormat format) => format switch
    {
        EnumTextureInternalFormat.Rgba8 => Format.R8G8B8A8Unorm,
        EnumTextureInternalFormat.Rgba16f => Format.R16G16B16A16Sfloat,
        EnumTextureInternalFormat.R16f => Format.R16Sfloat,
        EnumTextureInternalFormat.DepthComponent32 => Format.D32Sfloat,
        _ => Format.R8G8B8A8Unorm,
    };
}
