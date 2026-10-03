using System.Text.Json;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal sealed record RendererSettings
{
    public bool Enabled { get; init; } = true;
    public float RenderScale { get; init; } = 1f;
    public bool Taa { get; init; }
    public float TaaSharpness { get; init; } = .2f;
    public float TaaMipBias { get; init; } = -.5f;
    public int TaaDebugView { get; init; }
    public bool TaaJitterDev { get; init; }
    public string Upscaler { get; init; } = "off";
    public string UpscalerQuality { get; init; } = "quality";
    public float UpscalerLodBiasOffset { get; init; } = 1f;
    public string FrameGeneration { get; init; } = "off";
    public int FrameGenerationMultiplier { get; init; } = 2;
    public bool ShowFpsCounter { get; init; }
    public string LowLatencyMode { get; init; } = "on";
    public string AmbientOcclusion { get; init; } = "auto";
    public string AmbientOcclusionPreset { get; init; } = "medium";
    public bool AmbientOcclusionDebugView { get; init; }
    public bool GodRaysSampleCap { get; init; }
    public bool HandheldShadowTier { get; init; }
    public bool NativeShaders { get; init; } = true;
    public bool Streamline { get; init; } = true;
    public bool ControllerEnabled { get; init; } = true;
    public bool TouchEnabled { get; init; } = true;

    internal RendererSettings Normalize()
    {
        static string Choice(string? value, string fallback, params string[] choices) =>
            choices.FirstOrDefault(choice => string.Equals(choice, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;
        string provider = Choice(Upscaler, "off", "off", "dlss", "xess", "fsr3", "fsr4");
        string quality = provider == "xess"
            ? Choice(UpscalerQuality, "quality", "dlaa", "ultraquality", "ultraqualityplus", "quality", "balanced", "performance", "ultraperformance")
            : Choice(UpscalerQuality, "quality", "dlaa", "quality", "balanced", "performance", "ultraperformance");
        return this with
        {
            RenderScale = float.IsFinite(RenderScale) ? Math.Clamp(RenderScale, .25f, 1f) : 1f,
            TaaSharpness = float.IsFinite(TaaSharpness) ? Math.Clamp(TaaSharpness, 0f, 1f) : .2f,
            TaaMipBias = float.IsFinite(TaaMipBias) ? Math.Clamp(TaaMipBias, -4f, 0f) : -.5f,
            Upscaler = provider, UpscalerQuality = quality,
            UpscalerLodBiasOffset = float.IsFinite(UpscalerLodBiasOffset) ? Math.Clamp(UpscalerLodBiasOffset, 0f, 1f) : 1f,
            FrameGeneration = Choice(FrameGeneration, "off", "off", "dlss", "fsr3", "xess"),
            FrameGenerationMultiplier = Math.Clamp(FrameGenerationMultiplier, 2, 6),
            LowLatencyMode = Choice(LowLatencyMode, "on", "off", "on", "boost"),
            AmbientOcclusion = Choice(AmbientOcclusion, "auto", "auto", "vanilla", "gtao"),
            AmbientOcclusionPreset = Choice(AmbientOcclusionPreset, "medium", "low", "medium", "high", "ultra"),
            TaaDebugView = Math.Max(0, TaaDebugView),
        };
    }
}

internal sealed class RendererSettingsState : IUpscalerRuntimeState
{
    private RendererSettings settings;
    private readonly HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private float activeRenderScale, activeLodBias;
    internal bool TaaDisabled { get; private set; }
    internal RendererSettingsState(RendererSettings settings) => this.settings = settings.Normalize();
    internal RendererSettings Settings => Volatile.Read(ref settings);
    internal void Apply(RendererSettings value) => Volatile.Write(ref settings, value.Normalize());
    internal string EffectiveUpscaler { get { lock (gate) return disabled.Contains(Settings.Upscaler) ? "off" : Settings.Upscaler; } }
    internal bool UpscalerReplacesTaa => EffectiveUpscaler != "off";
    internal bool EffectiveTaa => Settings.Taa && !TaaDisabled && !UpscalerReplacesTaa;
    internal bool EffectiveTemporalPipeline => EffectiveTaa || UpscalerReplacesTaa || Settings.FrameGeneration != "off";
    public bool DlssRequested => EffectiveUpscaler == "dlss";
    public string Quality => Settings.UpscalerQuality;
    public float LodBiasOffset => Settings.UpscalerLodBiasOffset;
    internal float ActiveRenderScale => Volatile.Read(ref activeRenderScale);
    internal float ActiveLodBias => Volatile.Read(ref activeLodBias);
    internal float EffectiveTerrainLodBias
    {
        get
        {
            float scale = Settings.RenderScale;
            float bias = scale < 1f ? MathF.Log2(Math.Clamp(scale, .5f, 1f)) : 0f;
            if (EffectiveTaa) bias += Math.Clamp(Settings.TaaMipBias, -2f, 1f);
            if (UpscalerReplacesTaa && ActiveRenderScale > 0f) bias = Math.Clamp(ActiveLodBias, -3f, 1f);
            return bias;
        }
    }
    public void SetActivePlan(float scale, float bias) { Volatile.Write(ref activeRenderScale, scale); Volatile.Write(ref activeLodBias, bias); }
    public void ClearActivePlan() => SetActivePlan(0, 0);
    public bool DisableAtRuntime()
    {
        lock (gate)
        {
            if (Settings.Upscaler == "off" || !disabled.Add(Settings.Upscaler)) return false;
            ClearActivePlan(); return true;
        }
    }
    internal bool IsDisabled(string provider) { lock (gate) return disabled.Contains(provider); }
    internal void DisableTaa() => TaaDisabled = true;
    internal RendererLatencySelection LatencySelection => new(Settings.LowLatencyMode, Settings.FrameGeneration);
}

internal sealed class RendererSettingsStore(string dataPath)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal string Pathname => Path.Combine(dataPath, "ModConfig", "vulkanstory.json");
    internal RendererSettings Load() => File.Exists(Pathname)
        ? (JsonSerializer.Deserialize<RendererSettings>(File.ReadAllText(Pathname), Json) ?? new RendererSettings()).Normalize()
        : new RendererSettings();
    internal void Save(RendererSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Pathname)!);
        string temporary = Pathname + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), Json));
        File.Move(temporary, Pathname, overwrite: true);
    }
}
