using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

// Retained GL token values carried as data to the Vulkan texture/sampler boundary.
/// <summary>Named legacy GL texture constants used only as adapter format/sampler data, never as native Vulkan handles.</summary>
/// <remarks>The parameter names are the device's own (<see cref="GlEnums" />), so both sides of the boundary read one definition.</remarks>
internal static class GameGlTextureTokens
{
    internal const int Bgra = 32993;
    internal const int Rgba8 = 32856;
    internal const int TextureMinFilter = GlEnums.TextureMinFilter;
    internal const int TextureMagFilter = GlEnums.TextureMagFilter;
    internal const int TextureWrapS = GlEnums.TextureWrapS;
    internal const int TextureWrapT = GlEnums.TextureWrapT;
}
