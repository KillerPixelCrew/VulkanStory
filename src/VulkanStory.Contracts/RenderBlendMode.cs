namespace VulkanStory.Contracts;

/// <summary>The blend meanings carried from the game adapter into Vulkan rendering.</summary>
public enum RenderBlendMode
{
    Standard,
    Brighten,
    Multiply,
    PremultipliedAlpha,
    Glow,
    Overlay,
}
