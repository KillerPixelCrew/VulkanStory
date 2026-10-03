namespace VulkanStory.Render.Vulkan.Core;

internal partial struct Fsr3Image
{
    /// <summary>Adapt one owned Vulkan texture to the unchanged FidelityFX ABI.</summary>
    public static Fsr3Image From(VulkanTexture texture) => new()
    {
        Image = texture.Image.Handle,
        Width = texture.Width,
        Height = texture.Height,
        Format = (uint)texture.Format,
    };
}
