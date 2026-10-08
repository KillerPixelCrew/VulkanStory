namespace VulkanStory.Game;

// Retained GL token values carried as data to the Vulkan texture/sampler boundary.
/// <summary>Named legacy GL texture constants used only as adapter format/sampler data, never as native Vulkan handles.</summary>
internal static class GameGlTextureTokens
{
    internal const int Bgra = 32993;
    internal const int Rgba8 = 32856;
    internal const int TextureMinFilter = 10241;
    internal const int TextureMagFilter = 10240;
    internal const int TextureWrapS = 10242;
    internal const int TextureWrapT = 10243;
}
