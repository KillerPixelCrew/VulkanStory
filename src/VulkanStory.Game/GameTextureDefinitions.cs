using Vintagestory.API.Client;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

/// <summary>Converts the original game texture format enums to the retained renderer numeric format contract.</summary>
internal static class GameTextureDefinitions
{
    // Preserve unknown token values too: the retained backend decides fallback.
    internal static TextureInternalFormat Internal(EnumTextureInternalFormat value) => (TextureInternalFormat)(int)value;
    internal static TexturePixelFormat Pixels(EnumTexturePixelFormat value) => (TexturePixelFormat)(int)value;
    internal static FramebufferAttachment Attachment(EnumFramebufferAttachment value) => (FramebufferAttachment)(int)value;
}
