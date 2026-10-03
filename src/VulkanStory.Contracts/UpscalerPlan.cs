namespace VulkanStory.Contracts;

/// <summary>Render and display dimensions selected by one reconstruction provider.</summary>
public readonly record struct UpscalerPlan(
    int RenderWidth, int RenderHeight, int DisplayWidth, int DisplayHeight,
    string Quality, float? ProviderLodBias = null, float LodBiasOffset = 1f)
{
    public bool IsValid => RenderWidth > 0 && RenderHeight > 0 && DisplayWidth > 0 &&
        DisplayHeight > 0 && RenderWidth <= DisplayWidth && RenderHeight <= DisplayHeight;

    public float RenderScale => DisplayWidth > 0 ? (float)RenderWidth / DisplayWidth : 1f;

    public float LodBias => ProviderLodBias ?? RecommendedLodBias(RenderWidth, DisplayWidth, LodBiasOffset);

    // Preserves the original OptimumConfig formula. The game adapter supplies
    // the setting explicitly; providers never read a game or global config.
    public static float RecommendedLodBias(int renderWidth, int displayWidth, float offset = 1f)
    {
        if (displayWidth <= 0) return 0f;
        float scale = (float)renderWidth / displayWidth;
        if (!float.IsFinite(scale) || scale <= 0f || scale >= 1f) return 0f;
        float clampedOffset = float.IsFinite(offset) ? Math.Clamp(offset, 0f, 1f) : 1f;
        return MathF.Log2(Math.Clamp(scale, 0.01f, 1f)) - clampedOffset;
    }
}
