namespace VulkanStory.Contracts;

// Retained GL-shaped resource tokens at the game boundary.
/// <summary>GL-shaped storage-format tokens retained at the game resource boundary.</summary>
public enum TextureInternalFormat
{
    DepthComponent32 = 33191, Rgba16f = 34842, Rgba8 = 32856, R16f = 33325,
}
/// <summary>Source channel-layout tokens used when translating game texture uploads.</summary>
public enum TexturePixelFormat { DepthComponent = 6402, Rgba = 6408, Red = 6403 }
/// <summary>Retained attachment tokens mapped by the game bridge to renderer-owned framebuffer slots.</summary>
public enum FramebufferAttachment
{
    ColorAttachment0 = 36064, ColorAttachment1 = 36065, ColorAttachment2 = 36066,
    ColorAttachment3 = 36067, ColorAttachment4 = 36068, DepthAttachment = 36096,
}
