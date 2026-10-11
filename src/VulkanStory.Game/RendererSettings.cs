using System.Text.Json;
using VulkanStory.Contracts;
using VulkanStory.Settings;

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
    /// <summary>Skips foliage (BlendNoCull terrain) in the far shadow cascade.</summary>
    public bool ShadowFarFoliage { get; init; } = true;
    /// <summary>Skips entity shadows beyond <see cref="EntityShadowDistance" /> blocks.</summary>
    public bool EntityShadowCull { get; init; } = true;
    /// <summary>Horizontal entity shadow distance in blocks.</summary>
    public int EntityShadowDistance { get; init; } = 80;
    /// <summary>Skips re-sorting the chunk upload queue while nothing that orders it changed.</summary>
    public bool UploadQueueSortSkip { get; init; } = true;
    /// <summary>Replaces the per-entity LINQ collision-box animation check with an allocation-free loop.</summary>
    public bool AnimationLinqFree { get; init; } = true;
    /// <summary>Fixes the falling-block mesh buffer reuse and duplicate upload.</summary>
    public bool FallingBlockMeshFix { get; init; } = true;
    /// <summary>Recycles the custom attribute parts of tessellated chunk meshes.</summary>
    public bool MeshPartPool { get; init; } = true;
    /// <summary>Skips particle spawns whose emitter lies beyond the view distance.</summary>
    public bool ParticleDistanceGate { get; init; } = true;
    /// <summary>Applies live density, burst and resident limits to configurable client particles.</summary>
    public bool ParticleBudgets { get; init; } = true;
    /// <summary>Percentage of the requested particle quantity, including fractional-count sampling.</summary>
    public int ParticleQuantityPercent { get; init; } = 50;
    /// <summary>Maximum particles per spawn call; zero disables this limit.</summary>
    public int ParticleSpawnLimit { get; init; } = 256;
    /// <summary>Maximum living configurable particles in each client pool; zero disables this limit.</summary>
    public int ParticlePoolLimit { get; init; } = 8192;
    /// <summary>Merges settled matching dropped stacks sooner in the integrated server.</summary>
    public bool EarlyItemMerge { get; init; } = true;
    /// <summary>Skips drawing dropped items beyond <see cref="DroppedItemDistance" /> blocks.</summary>
    public bool DroppedItemCull { get; init; } = true;
    /// <summary>Horizontal dropped-item draw distance in blocks.</summary>
    public int DroppedItemDistance { get; init; } = 64;
    /// <summary>Caches assembled villager/trader outfit shapes and texture sets, and pre-inserts outfit textures gradually after world load.</summary>
    public bool OutfitShapeCache { get; init; } = true;
    /// <summary>Walks the occlusion culler's own rays from a precomputed shared tree instead of recomputing each ray.</summary>
    public bool ChunkVisibilityWalk { get; init; } = true;
    /// <summary>Leaves terrain chunk parts hidden behind opaque terrain (GPU depth pyramid of a recent frame) out of the main camera draws.</summary>
    public bool TerrainOcclusionCull { get; init; } = true;
    /// <summary>Skips render preparation and draws of entities hidden behind opaque terrain (same occlusion depth).</summary>
    public bool EntityOcclusionCull { get; init; } = true;
    /// <summary>Reads a chunk column's raw chunk data in parallel in singleplayer; applies at the next world load.</summary>
    public bool ParallelChunkReads { get; init; } = true;
    /// <summary>Live singleplayer worldgen worker policy: off, auto, 1, 2 or 3.</summary>
    public string WorldgenWorkers { get; init; } = "auto";

    /// <summary>Reuses render-info scratch objects only inside synchronous GUI item rendering.</summary>
    public bool GuiItemRenderInfoReuse { get; init; } = true;
    /// <summary>Caches wind samples for at most 50 ms while smoothing continues every frame.</summary>
    public bool WeatherWindSampleCache { get; init; } = true;
    /// <summary>Uses a face-connectivity flood instead of the ray tree for chunk visibility.</summary>
    public bool BfsChunkVisibility { get; init; } = true;
    /// <summary>Staggers distant animated-block evaluation while retaining accumulated animation time.</summary>
    public bool AnimatedBlockLod { get; init; } = true;
    /// <summary>Bounds dynamic-light membership scans to 100 ms with movement and population invalidation.</summary>
    public bool DynamicLightScanCache { get; init; } = true;
    /// <summary>Selects the nearest dynamic lights without allocating a LINQ sort.</summary>
    public bool NearestLightSelection { get; init; } = true;
    /// <summary>Spreads existing entity shape rebuilds across frames with an eight-frame maximum deferral.</summary>
    public bool EntityRetessellationBudget { get; init; } = true;
    /// <summary>Reuses temporary pathfinding nodes while keeping returned paths independent of later searches.</summary>
    public bool PathfindingNodePool { get; init; } = true;
    /// <summary>Batches visible entity light reads by chunk under the game's original chunk lock.</summary>
    public bool EntityLightBatch { get; init; } = true;
    /// <summary>Evaluates frustum planes in SIMD vectors with the game's original precision and LOD thresholds.</summary>
    public bool SimdFrustumTests { get; init; } = true;
    /// <summary>Caches outfit pose layouts with separate mutable pose graphs for each entity.</summary>
    public bool OutfitAnimatorCache { get; init; } = true;
    /// <summary>Allows idle worldgen workers to generate Terrain columns with independent generator workspaces.</summary>
    public bool ParallelTerrainGeneration { get; init; } = true;
    /// <summary>Reuses physics client lists only when queued readers have released them.</summary>
    public bool PhysicsCollectionReuse { get; init; } = true;
    /// <summary>Reserves activation-list capacity in one batch; all entities still activate in the original tick.</summary>
    public bool PhysicsActivationBatch { get; init; } = true;
    /// <summary>Uses a majority-material proxy for distant chisel geometry while retaining full geometry for live toggles.</summary>
    public bool ChiselMeshLod { get; init; } = true;
    /// <summary>Distance in blocks at which chisel chunk parts switch to the proxy.</summary>
    public int ChiselLodDistance { get; init; } = 48;
    /// <summary>Caches world-map pages on disk and renders resident pages with a Vulkan array texture.</summary>
    public bool WorldMapPageCache { get; init; } = true;
    /// <summary>Reuses CPU occlusion depth and hierarchy storage without changing visibility tests.</summary>
    public bool OcclusionStorageReuse { get; init; } = true;
    /// <summary>Caches raw translation resolution with language reload invalidation; formatting remains live.</summary>
    public bool TranslationLookupCache { get; init; } = true;
    /// <summary>Uses pooled PCM segments in the original Vorbis decoder.</summary>
    public bool AudioDecodeBufferReuse { get; init; } = true;
    /// <summary>Caches private static-shape templates for fruiting-bush tessellation, with independent caller data.</summary>
    public bool ShapeParseCache { get; init; } = true;
    /// <summary>Prepares outfit shape metadata on owned background inputs; atlas insertion stays on the owner thread.</summary>
    public bool OutfitMetadataPreparation { get; init; } = true;
    /// <summary>Uses loaded-chunk indices and precomputed neighbor edges for flood-fill visibility.</summary>
    public bool IndexedChunkVisibility { get; init; } = true;
    /// <summary>Reuses private tile-position scratch vectors in the stock cloud updater.</summary>
    public bool CloudCoordinateReuse { get; init; } = true;

    /// <summary>Returns this snapshot with every performance switch copied from <paramref name="source" />; all of them apply live.</summary>
    internal RendererSettings WithPerformanceFrom(RendererSettings source) => this with
    {
        ShadowFarFoliage = source.ShadowFarFoliage,
        EntityShadowCull = source.EntityShadowCull,
        EntityShadowDistance = source.EntityShadowDistance,
        UploadQueueSortSkip = source.UploadQueueSortSkip,
        AnimationLinqFree = source.AnimationLinqFree,
        FallingBlockMeshFix = source.FallingBlockMeshFix,
        MeshPartPool = source.MeshPartPool,
        ParticleDistanceGate = source.ParticleDistanceGate,
        ParticleBudgets = source.ParticleBudgets,
        ParticleQuantityPercent = source.ParticleQuantityPercent,
        ParticleSpawnLimit = source.ParticleSpawnLimit,
        ParticlePoolLimit = source.ParticlePoolLimit,
        EarlyItemMerge = source.EarlyItemMerge,
        DroppedItemCull = source.DroppedItemCull,
        DroppedItemDistance = source.DroppedItemDistance,
        OutfitShapeCache = source.OutfitShapeCache,
        ChunkVisibilityWalk = source.ChunkVisibilityWalk,
        TerrainOcclusionCull = source.TerrainOcclusionCull,
        EntityOcclusionCull = source.EntityOcclusionCull,
        ParallelChunkReads = source.ParallelChunkReads,
        WorldgenWorkers = source.WorldgenWorkers,
        GuiItemRenderInfoReuse = source.GuiItemRenderInfoReuse,
        WeatherWindSampleCache = source.WeatherWindSampleCache,
        BfsChunkVisibility = source.BfsChunkVisibility,
        AnimatedBlockLod = source.AnimatedBlockLod,
        DynamicLightScanCache = source.DynamicLightScanCache,
        NearestLightSelection = source.NearestLightSelection,
        EntityRetessellationBudget = source.EntityRetessellationBudget,
        PathfindingNodePool = source.PathfindingNodePool,
        EntityLightBatch = source.EntityLightBatch,
        SimdFrustumTests = source.SimdFrustumTests,
        OutfitAnimatorCache = source.OutfitAnimatorCache,
        ParallelTerrainGeneration = source.ParallelTerrainGeneration,
        PhysicsCollectionReuse = source.PhysicsCollectionReuse,
        PhysicsActivationBatch = source.PhysicsActivationBatch,
        ChiselMeshLod = source.ChiselMeshLod,
        ChiselLodDistance = source.ChiselLodDistance,
        WorldMapPageCache = source.WorldMapPageCache,
        OcclusionStorageReuse = source.OcclusionStorageReuse,
        TranslationLookupCache = source.TranslationLookupCache,
        AudioDecodeBufferReuse = source.AudioDecodeBufferReuse,
        ShapeParseCache = source.ShapeParseCache,
        OutfitMetadataPreparation = source.OutfitMetadataPreparation,
        IndexedChunkVisibility = source.IndexedChunkVisibility,
        CloudCoordinateReuse = source.CloudCoordinateReuse,
    };

    /// <summary>Returns a supported, finite settings snapshot with provider-specific choice normalization.</summary>
    /// <returns>A normalized copy; device/provider availability is evaluated separately.</returns>
    internal RendererSettings Normalize()
    {
        // RendererChoices is the single source of supported values and ranges.
        static string Choice(string? value, string key, string fallback, bool xess = false)
        {
            string? trimmed = value?.Trim();
            foreach (var (choice, _) in RendererChoices.Get(key, xess))
                if (string.Equals(choice, trimmed, StringComparison.OrdinalIgnoreCase)) return choice;
            return fallback;
        }
        string provider = Choice(Upscaler, nameof(Upscaler), "off");
        string quality = Choice(UpscalerQuality, nameof(UpscalerQuality), "quality", xess: provider == "xess");
        return this with
        {
            RenderScale = RendererChoices.Range(nameof(RenderScale)).Clamp(RenderScale),
            TaaSharpness = RendererChoices.Range(nameof(TaaSharpness)).Clamp(TaaSharpness),
            TaaMipBias = RendererChoices.Range(nameof(TaaMipBias)).Clamp(TaaMipBias),
            Upscaler = provider, UpscalerQuality = quality,
            UpscalerLodBiasOffset = RendererChoices.Range(nameof(UpscalerLodBiasOffset)).Clamp(UpscalerLodBiasOffset),
            FrameGeneration = Choice(FrameGeneration, nameof(FrameGeneration), "off"),
            FrameGenerationMultiplier = RendererChoices.Range(nameof(FrameGenerationMultiplier)).Clamp(FrameGenerationMultiplier),
            LowLatencyMode = Choice(LowLatencyMode, nameof(LowLatencyMode), "on"),
            AmbientOcclusion = Choice(AmbientOcclusion, nameof(AmbientOcclusion), "auto"),
            AmbientOcclusionPreset = Choice(AmbientOcclusionPreset, nameof(AmbientOcclusionPreset), "medium"),
            TaaDebugView = Math.Max(0, TaaDebugView),
            EntityShadowDistance = RendererChoices.Range(nameof(EntityShadowDistance)).Clamp(EntityShadowDistance),
            DroppedItemDistance = RendererChoices.Range(nameof(DroppedItemDistance)).Clamp(DroppedItemDistance),
            ParticleQuantityPercent = RendererChoices.Range(nameof(ParticleQuantityPercent)).Clamp(ParticleQuantityPercent),
            ParticleSpawnLimit = RendererChoices.Range(nameof(ParticleSpawnLimit)).Clamp(ParticleSpawnLimit),
            ParticlePoolLimit = RendererChoices.Range(nameof(ParticlePoolLimit)).Clamp(ParticlePoolLimit),
            ChiselLodDistance = RendererChoices.Range(nameof(ChiselLodDistance)).Clamp(ChiselLodDistance),
            WorldgenWorkers = Choice(WorldgenWorkers, nameof(WorldgenWorkers), "auto"),
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
    /// <summary>Latency selection using the requested FG provider, before provider availability is known.</summary>
    internal RendererLatencySelection LatencySelection => EffectiveLatencySelection(null);
    /// <summary>Latency selection whose FG input excludes a requested provider that has failed for this session.</summary>
    /// <param name="unavailable">Returns a refusal for a failed FG provider, or null while it remains usable; null treats the request as effective.</param>
    /// <returns>Requested latency mode paired with the requested FG provider, or Off when that provider is unavailable.</returns>
    /// <remarks>A failed provider no longer forces low latency On; the user's requested latency mode applies.</remarks>
    internal RendererLatencySelection EffectiveLatencySelection(Func<string, string?>? unavailable)
    {
        RendererSettings current = Settings;
        string generation = current.FrameGeneration != "off" && unavailable?.Invoke(current.FrameGeneration) != null
            ? "off" : current.FrameGeneration;
        return new(current.LowLatencyMode, generation);
    }
}

/// <summary>Loads and saves VulkanStory-owned settings under the selected game data path.</summary>
internal sealed class RendererSettingsStore(string dataPath)
{
    /// <summary>Settings JSON options shared by the file store and the runtime control bridge.</summary>
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
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
    internal void Save(RendererSettings settings) =>
        AtomicFile.WriteAllText(Pathname, JsonSerializer.Serialize(settings.Normalize(), Json));
}
