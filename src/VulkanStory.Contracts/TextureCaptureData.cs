namespace VulkanStory.Contracts;

/// <summary>Level-zero decoded texels in retained row order, with no implicit vertical flip.</summary>
public sealed class TextureCaptureData
{
    /// <summary>Retained GL-format token describing the decoded source attachment.</summary>
    public int GlInternalFormat { get; set; }
    /// <summary>Width of the captured level-zero image in texels.</summary>
    public int Width { get; set; }
    /// <summary>Height of the captured level-zero image in texels.</summary>
    public int Height { get; set; }
    /// <summary>Four RGBA bytes per texel; null for float and depth formats.</summary>
    public byte[]? Bytes { get; set; }
    /// <summary>Four floats per colour texel or one per depth texel.</summary>
    public float[]? Floats { get; set; }
}
