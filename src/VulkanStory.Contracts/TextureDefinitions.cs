namespace VulkanStory.Contracts;

// Retained GL-shaped resource tokens at the game boundary.
public enum TextureInternalFormat
{
    DepthComponent32 = 33191, Rgba16f = 34842, Rgba8 = 32856, R16f = 33325,
}
public enum TexturePixelFormat { DepthComponent = 6402, Rgba = 6408, Red = 6403 }
public enum FramebufferAttachment
{
    ColorAttachment0 = 36064, ColorAttachment1 = 36065, ColorAttachment2 = 36066,
    ColorAttachment3 = 36067, ColorAttachment4 = 36068, DepthAttachment = 36096,
}
