using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Borrowed provider ABI descriptors for renderer-owned image views.</summary>
internal static class ProviderImages
{
    internal static StreamlineTaggedImage Streamline(VulkanTexture texture) => new()
    {
        Image = texture.Image.Handle, View = texture.View.Handle, Layout = (uint)texture.Layout,
        Format = (uint)texture.Format, Width = texture.Width, Height = texture.Height, Usage = (uint)texture.Usage,
    };

    internal static Fsr3Image Fsr3(VulkanTexture texture) => new()
    {
        Image = texture.Image.Handle, Width = texture.Width, Height = texture.Height,
        Format = (uint)texture.Format,
    };
    internal static NgxResourceVk Ngx(VulkanTexture texture, bool readWrite) => NgxResourceVk.ImageView(
        texture.View, texture.Image,
        new ImageSubresourceRange(texture.Aspect, 0, texture.MipLevels, 0, texture.Layers),
        texture.Format, texture.Width, texture.Height, readWrite);
    internal static XessImage Xess(VulkanTexture texture) => new()
    {
        View = texture.View.Handle, Image = texture.Image.Handle,
        Range = new ImageSubresourceRange(texture.Aspect, 0, 1, 0, 1),
        Format = texture.Format, Width = texture.Width, Height = texture.Height,
    };
}
