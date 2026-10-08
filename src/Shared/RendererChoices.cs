namespace VulkanStory.Settings;

/// <summary>One source of supported renderer-setting values and their GUI labels.</summary>
internal static class RendererChoices
{
    private static readonly (string Value, string Label)[] Upscalers = [("off", "Off"), ("dlss", "DLSS"), ("xess", "XeSS"), ("fsr3", "FSR 3.1"), ("fsr4", "FSR 4")];
    private static readonly (string Value, string Label)[] Quality = [("dlaa", "Native AA"), ("quality", "Quality"), ("balanced", "Balanced"), ("performance", "Performance"), ("ultraperformance", "Ultra Performance")];
    private static readonly (string Value, string Label)[] XessQuality = [("dlaa", "Native AA"), ("ultraqualityplus", "Ultra Quality+"), ("ultraquality", "Ultra Quality"), ("quality", "Quality"), ("balanced", "Balanced"), ("performance", "Performance"), ("ultraperformance", "Ultra Performance")];
    private static readonly (string Value, string Label)[] Generation = [("off", "Off"), ("dlss", "DLSS-G"), ("xess", "XeSS-FG"), ("fsr3", "FSR 3 FG")];
    private static readonly (string Value, string Label)[] Latency = [("off", "Off"), ("on", "On"), ("boost", "Boost")];
    private static readonly (string Value, string Label)[] Occlusion = [("auto", "Auto"), ("vanilla", "SSAO"), ("gtao", "GTAO")];
    private static readonly (string Value, string Label)[] OcclusionQuality = [("low", "Low"), ("medium", "Medium"), ("high", "High"), ("ultra", "Ultra")];

    /// <summary>Returns supported values and labels; XeSS adds its SDK-specific quality choices.</summary>
    /// <param name="key">Renderer setting name shared by commands and the settings panel.</param>
    /// <param name="xess">Whether quality values should include XeSS-specific modes.</param>
    /// <returns>Shared supported values and user-facing labels; callers must not mutate the underlying array.</returns>
    /// <exception cref="ArgumentException">The setting name has no defined choices.</exception>
    internal static IReadOnlyList<(string Value, string Label)> Get(string key, bool xess = false) => key switch
    {
        "Upscaler" => Upscalers,
        "UpscalerQuality" => xess ? XessQuality : Quality,
        "FrameGeneration" => Generation,
        "LowLatencyMode" => Latency,
        "AmbientOcclusion" => Occlusion,
        "AmbientOcclusionPreset" => OcclusionQuality,
        _ => throw new ArgumentException("Unknown renderer choice: " + key),
    };
}
