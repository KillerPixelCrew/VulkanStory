using Vintagestory.API.Client;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal static class GameTextureDefinitions
{
    // Preserve unknown token values too: the retained backend decides fallback.
    internal static TextureInternalFormat Internal(EnumTextureInternalFormat value) => (TextureInternalFormat)(int)value;
    internal static TexturePixelFormat Pixels(EnumTexturePixelFormat value) => (TexturePixelFormat)(int)value;
    internal static FramebufferAttachment Attachment(EnumFramebufferAttachment value) => (FramebufferAttachment)(int)value;
}
