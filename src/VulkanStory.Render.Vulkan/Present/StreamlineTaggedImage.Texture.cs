namespace VulkanStory.Render.Vulkan.Core;

internal partial struct StreamlineTaggedImage
{
    /// <summary>Adapt one owned texture to the unchanged Streamline bridge ABI.</summary>
    public static StreamlineTaggedImage From(VulkanTexture texture) => new()
    {
        Image = texture.Image.Handle,
        View = texture.View.Handle,
        Layout = (uint)texture.Layout,
        Format = (uint)texture.Format,
        Width = texture.Width,
        Height = texture.Height,
        Usage = (uint)texture.Usage,
    };
}
