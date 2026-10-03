using Vintagestory.API.Client;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

/// <summary>
/// Staged game-enum conversion for the retained backend blend factor table.
/// </summary>
internal static class GameAttachmentBlend
{
    public static RenderBlendMode From(EnumBlendMode mode) => mode switch
    {
        EnumBlendMode.Brighten => RenderBlendMode.Brighten,
        EnumBlendMode.Multiply => RenderBlendMode.Multiply,
        EnumBlendMode.PremultipliedAlpha => RenderBlendMode.PremultipliedAlpha,
        EnumBlendMode.Glow => RenderBlendMode.Glow,
        EnumBlendMode.Overlay => RenderBlendMode.Overlay,
        _ => RenderBlendMode.Standard,
    };
}
