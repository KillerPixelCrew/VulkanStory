using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

internal partial struct NgxResourceVk
{
    /// <summary>
    /// The backend texture adapter: level 0 and layer 0 of its whole-image view.
    /// It joins the NGX ABI type when the texture manager is compiled.
    /// </summary>
    public static NgxResourceVk Texture(VulkanTexture texture, bool readWrite) => ImageView(
        texture.View, texture.Image,
        new ImageSubresourceRange(texture.Aspect, 0, texture.MipLevels, 0, texture.Layers),
        texture.Format, texture.Width, texture.Height, readWrite);
}
