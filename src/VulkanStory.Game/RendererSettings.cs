using System.Text.Json;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

/// <summary>Immutable requested renderer and input options; normalization clamps supported values without claiming device availability.</summary>
internal sealed record RendererSettings
{
    /// <summary>Whether early startup should create the Vulkan/SDL session; disabling takes effect at the next launch.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Requested ordinary render fraction, normalized to 0.25 through 1; an active SR plan supplies its own scale.</summary>
    public float RenderScale { get; init; } = 1f;
    /// <summary>Requests native TAA when SR does not replace it and session readiness permits it.</summary>
    public bool Taa { get; init; }
    /// <summary>Native TAA sharpening strength, normalized to zero through one.</summary>
    public float TaaSharpness { get; init; } = .2f;
    /// <summary>Additional native TAA terrain mip bias, normalized to -2 through zero.</summary>
    public float TaaMipBias { get; init; } = -.5f;
    /// <summary>Nonnegative native TAA diagnostic view index.</summary>
    public int TaaDebugView { get; init; }
    /// <summary>Development option requesting the temporal jitter window independently of the normal TAA/SR selection.</summary>
    public bool TaaJitterDev { get; init; }
    /// <summary>Requested SR provider identifier; effective availability is tracked separately by RendererSettingsState.</summary>
    public string Upscaler { get; init; } = "off";
    /// <summary>Requested SR quality identifier; allowed names are normalized against the requested provider.</summary>
    public string UpscalerQuality { get; init; } = "quality";
    /// <summary>Requested blend between provider-recommended and ordinary terrain mip bias, normalized to zero through one.</summary>
    public float UpscalerLodBiasOffset { get; init; } = 1f;
    /// <summary>Requested FG provider identifier; per-frame eligibility and effective activation are tracked separately.</summary>
    public string FrameGeneration { get; init; } = "off";
    /// <summary>Requested total output multiplier, normalized to 2 through 6; actual provider limits may be lower.</summary>
    public int FrameGenerationMultiplier { get; init; } = 2;
    /// <summary>Displays sampled real/generated presentation-counter rates in the game UI.</summary>
    public bool ShowFpsCounter { get; init; }
    /// <summary>Requested latency policy identifier: off, on or boost; the active provider owns pacing.</summary>
    public string LowLatencyMode { get; init; } = "on";
    /// <summary>Requested AO policy: auto, vanilla or gtao.</summary>
    public string AmbientOcclusion { get; init; } = "auto";
    /// <summary>Requested GTAO quality preset: low, medium, high or ultra.</summary>
    public string AmbientOcclusionPreset { get; init; } = "medium";
    /// <summary>Requests an AO diagnostic output instead of ordinary final shading where supported.</summary>
    public bool AmbientOcclusionDebugView { get; init; }
    /// <summary>Enables the retained reduced god-rays sample policy.</summary>
    public bool GodRaysSampleCap { get; init; }
    /// <summary>Requests the retained reduced shadow allocation/shader policy.</summary>
    public bool HandheldShadowTier { get; init; }
    /// <summary>Enables compatible precompiled native shader paths; asset overrides may decline individual programs.</summary>
    public bool NativeShaders { get; init; } = true;
    /// <summary>Requests Streamline initialization for supported NVIDIA latency/frame-generation features.</summary>
    public bool Streamline { get; init; } = true;
    /// <summary>Enables controller polling and session game/UI bindings.</summary>
    public bool ControllerEnabled { get; init; } = true;
    /// <summary>Enables session touch-to-mouse handling.</summary>
    public bool TouchEnabled { get; init; } = true;

    /// <summary>Returns a supported, finite settings snapshot with provider-specific choice normalization.</summary>
    /// <returns>A normalized copy; device/provider availability is evaluated separately.</returns>
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
            TaaMipBias = float.IsFinite(TaaMipBias) ? Math.Clamp(TaaMipBias, -2f, 0f) : -.5f,
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

/// <summary>Publishes normalized requested options and retains session provider refusals plus the active SR scale/LOD plan.</summary>
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
            if (EffectiveTaa) bias += Settings.TaaMipBias;
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

/// <summary>Loads and saves VulkanStory-owned settings under the selected game data path.</summary>
internal sealed class RendererSettingsStore(string dataPath)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal string Pathname => Path.Combine(dataPath, "ModConfig", "vulkanstory.json");
    /// <summary>Reads and normalizes VulkanStory settings from the selected data path.</summary>
    /// <returns>Persisted normalized settings, or defaults when the file does not exist.</returns>
    /// <remarks>Malformed JSON and file-access failures propagate to the control/bootstrap caller.</remarks>
    internal RendererSettings Load() => File.Exists(Pathname)
        ? (JsonSerializer.Deserialize<RendererSettings>(File.ReadAllText(Pathname), Json) ?? new RendererSettings()).Normalize()
        : new RendererSettings();
    /// <summary>Writes normalized JSON to a sibling temporary file and replaces the settings pathname.</summary>
    /// <param name="settings">Requested settings to persist.</param>
    /// <remarks>Creates the ModConfig directory. Serialization/file errors propagate; applying the settings belongs to the runtime control queue.</remarks>
    internal void Save(RendererSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Pathname)!);
        string temporary = Pathname + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), Json));
        File.Move(temporary, Pathname, overwrite: true);
    }
}
