using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

[assembly: InternalsVisibleTo("Optimum.Tests")]

namespace Vintagestory.API.Config;

/// <summary>
/// Runtime config for Optimum optimizations. Persists to ModConfig/optimum.json.
/// VintagestoryLib syncs these from ClientSettings at startup; forks read the static fields.
/// </summary>
public static class OptimumConfig
{
    /// <summary>
    /// Supplies the version to every managed assembly. Packaging scripts read
    /// the root VERSION file. Keep both values equal for each release.
    /// </summary>
    public const string Version = "0.3.17";

    public static bool RepulsionGateEnabled = true;
    public static int RepulsionDistance = 64;
    public static double RepulsionDistanceSq = 64.0 * 64.0;

    public static bool AnimBlockLodEnabled = true;

    /// <summary>
    /// Hard cap on animator updates per render frame, independent of the
    /// near/mid/far distance tiers above. Blocks over budget defer through
    /// the same skip-time accumulator the mid tier uses and catch up once
    /// their turn comes. 0 disables the cap.
    /// </summary>
    public static int AnimBlockLodFrameBudget = 256;

    public static bool WeatherWindThrottleEnabled = true;
    public static bool ParticleDistanceGateEnabled = true;
    public static bool ChiselLodEnabled = true;

    /// <summary>
    /// Playtested and measured in a running client (2026-07-09 and
    /// 2026-07-10 sessions, docs/benchmarking.md): the fragment-shader
    /// wrap (bug B3) works, OFF compiles bit-identical vanilla shaders,
    /// and ON at 8x8 trades ~3% mean FPS for a large stutter reduction
    /// (+84% 1% low FPS, p99 44->30 ms), ~10pp less CPU and ~30 MB less
    /// VRAM on a fragment-bound scene. Still default false: that is one
    /// trial per config on one route/GPU, and the 1%-low direction
    /// inverted between the two days' scenes, so the flip to true waits
    /// for a repeat run confirming the pattern (V2.2 in docs/todo.md).
    /// </summary>
    public static bool GreedyMeshEnabled = false;

    /// <summary>
    /// Caps the greedy merge span. Merged quads tile the texture via a
    /// UV-space fract() wrap in chunkopaque.fsh. Max 8 (3 bits in
    /// renderFlags). At 1x1 the emitter skips the whole pass (a 1x1
    /// "merge" would replace one vanilla quad with an identical one -
    /// all cost, no benefit), so 1 = merging off. Default 8 since the
    /// 2026-07-10 re-benchmark: 8x8 matched 4x4 on mean FPS and beat it
    /// on 1% lows (docs/benchmarking.md), and a default of 1 made
    /// enabling GreedyMeshEnabled silently do nothing. Inert while the
    /// master switch is false.
    /// </summary>
    public static int GreedyMeshMaxMergeWidth = 8;
    public static int GreedyMeshMaxMergeHeight = 8;

    /// <summary>
    /// Light quantization for greedy merge eligibility, 0-4. 0 = exact:
    /// faces merge only when all 4 corner light values are identical
    /// (pixel-identical to vanilla, but merges little with smooth
    /// lighting on). t > 0 quantizes each light channel to steps of 2^t
    /// (out of 255) before the equality test and emits the quantized
    /// value, letting faces that differ by under 2^t light levels merge.
    /// 1-2 is visually imperceptible in most scenes; the cost is a
    /// floor-quantization darkening of at most 2^t - 1 levels.
    /// </summary>
    public static int GreedyMeshLightTolerance = 0;

    /// <summary>
    /// Distance band for aggressive merging, in blocks, horizontal. 0 =
    /// uniform (GreedyMeshLightTolerance applies everywhere). > 0: chunks
    /// beyond this distance from the player merge with tolerance 4
    /// (16-level light) regardless of the base tolerance - a stretched
    /// light gradient 100+ blocks away is invisible, and far chunks are
    /// where vertex counts accumulate. Chunks pick up the new mode on
    /// their next natural retesselation when crossing the band, same as
    /// chisel LOD.
    /// </summary>
    public static int GreedyMeshFarDistance = 0;
    public static double GreedyMeshFarDistanceSq = 0;

    /// <summary>
    /// When true (default), merged quads sample the atlas with
    /// textureGrad() and derivatives taken from the unwrapped UV, which
    /// keeps mip selection seamless across the fract() wrap. When false,
    /// merged quads use a plain texture() lookup: visible mip seams can
    /// appear at tile boundaries on distant merged quads, but the
    /// explicit-gradient sampler cost (reduced-rate on some GPUs) goes
    /// away. Exists to isolate where the measured ON cost comes from
    /// (2026-07-10: ON at 8x8 costs ~3% mean FPS on a fragment-bound
    /// scene, docs/benchmarking.md) - flip to false, re-run the same
    /// route, and compare. Only affects shading when GreedyMeshEnabled
    /// is true and merges happen; requires a restart like the rest.
    /// </summary>
    public static bool GreedyMeshTextureGrad = true;

    /// <summary>
    /// Set by the ShaderRegistry patch each time chunk shaders compile:
    /// true when they were stamped with #define GREEDYMESH 1. The emitter
    /// refuses to emit merged (tiled) quads unless this is true, so a
    /// config/shader mismatch (e.g. config edited mid-session before a
    /// shader reload) degrades to 1x1 merges instead of feeding sentinel
    /// bits to a shader that won't decode them. Not persisted.
    /// </summary>
    public static volatile bool GreedyMeshShadersCompiledOn;

    public static int ChiselLodDistance = 48;
    public static double ChiselLodDistanceSq = 48.0 * 48.0;

    /// <summary>
    /// Hysteresis factor for entity/chisel render distance. Entities that were
    /// visible last frame stay visible until they exceed innerRadius * 1.1.
    /// (1.1)^2 = 1.21, precomputed to avoid sqrt in the render loop.
    /// </summary>
    public static double HysteresisFactorSq = 1.21;

    /// <summary>
    /// R3: scale ChunkCuller's occlusion-culling engagement threshold by view
    /// distance instead of a fixed 100-chunk floor, so culling still pays for
    /// its own traversal cost at low view distances where fewer than 100
    /// chunks ever load.
    /// </summary>
    public static bool OcclusionCullingScaleEnabled = true;

    /// <summary>
    /// Issue #72: replace ChunkCuller's per-shell raycast visibility walk with a
    /// breadth-first flood fill over the same per-chunk face-connectivity graph
    /// (the technique Minecraft "cave culling" and Sodium use). The raycast marches
    /// up to 3 rays per shell position and, at high view distance, runs against
    /// thousands of chunks per recompute; the BFS is linear in loaded chunks and
    /// visits them front-to-back. Falls back to the vanilla raycast when false.
    ///
    /// Correctness: the face-connectivity flood is the CONSERVATIVE model - a chunk
    /// is culled only when EVERY path of mutually-open faces from the camera is
    /// blocked, in which case no straight sightline can reach it either, so it can
    /// never hide a chunk the player can see (Durand et al. SIGGRAPH 2000 PVS
    /// criterion). The vanilla raycast is the approximation: it over-marks (marks a
    /// chunk visible before testing it, plus a 2-chunk march overshoot) and samples
    /// sightlines with only 3 sub-rays per shell endpoint. The one flood failure mode
    /// that can hide a chunk (MC-70850) comes from testing only a single biased
    /// incoming "flow" face; this implementation avoids it by testing reachability
    /// through ALL incoming faces (bfsReachable), plus a first-ring exemption that
    /// mirrors the raycast's `num2 > 1` pass-through and the same camera-neighbourhood
    /// seeding vanilla does.
    ///
    /// Measured in-client (same fixed world, seed 72720): raycast ~12-13 ms/walk
    /// marking ~1000-1090 chunks visible; BFS ~1.9 ms/walk marking ~1026-1030 - about
    /// 6.5x faster with a comparable-or-slightly-tighter visible set (no deficit vs
    /// the raycast). Default true.
    /// </summary>
    public static bool BfsChunkVisibilityEnabled = true;

    /// <summary>
    /// Reuse the dynamic-light entity scan from the previous frame while the
    /// player is roughly stationary, instead of rescanning every frame.
    /// Refreshes on player movement past a small threshold or every 15
    /// frames, whichever comes first, so an entity crossing into or out of
    /// range is picked up within a quarter second at most while standing still.
    /// </summary>
    public static bool DynamicLightCacheEnabled = true;

    public static bool EntityLightBatchEnabled = true;
    public static bool EntityShaderStateCacheEnabled = true;

    /// <summary>
    /// Issue #75: Enables GPU indirect draw submission (glMultiDrawElementsIndirect)
    /// for chunk mesh pools when supported by the hardware and driver (OpenGL 4.3+ / ARB_multi_draw_indirect).
    /// Defaults to false during evaluation.
    /// </summary>
    public static bool IndirectDrawEnabled = false;

    /// <summary>
    /// Set at startup after OpenGL capability probe. False on macOS (OpenGL 4.1) and legacy drivers.
    /// </summary>
    public static bool IndirectDrawSupported = false;

    /// <summary>
    /// Issue #85: Tracks whether performance ecosystem mods are loaded in the client.
    /// Set dynamically by OptimumCompatibilityGuard during ModLoader startup or assembly scan.
    /// Not persisted to optimum.json.
    /// </summary>
    public static bool KometDetected;
    public static bool OptiTimeDetected;
    public static bool TungstenDetected;
    public static bool SynergyDetected;

    /// <summary>
    /// Issue #85: When true (default), Optimum detects Komet and safely yields conflicting
    /// host subsystems (e.g. MDI indirect rendering and SIMD frustum culling) to prevent
    /// graphics pipeline crashes or corrupted state from Komet's cancelling Harmony prefixes.
    /// Persisted to ModConfig/optimum.json.
    /// </summary>
    public static bool KometGuardEnabled = true;

    public static bool EffectiveIndirectDraw => IndirectDrawEnabled && IndirectDrawSupported && (!KometDetected || !KometGuardEnabled);

    /// <summary>
    /// Issue #75: Enables SIMD-vectorized frustum culling on CPU (AVX2 / ARM NEON)
    /// for bounding volumes in chunk mesh pools and model locations.
    /// </summary>
    public static bool SimdCullingEnabled = true;

    /// <summary>
    /// True when the CPU supports 128-bit or 256-bit vector operations.
    /// </summary>
    public static bool SimdCullingSupported => Vector128.IsHardwareAccelerated;

    public static bool EffectiveSimdCulling => SimdCullingEnabled && SimdCullingSupported && (!KometDetected || !KometGuardEnabled);

    /// <summary>
    /// Caps how many entities may re-tesselate their shape (EntityShapeRenderer.TesselateShape)
    /// in a single frame. A re-tesselation's synchronous cost - shape parse, clone,
    /// step-parenting, texture atlas insertion, animator rebuild - runs 50-230ms per entity for
    /// geared/dressed humanoids (measured via .optimum stutterwatch), and a burst of entities
    /// needing it at once (e.g. a trader caravan loading in) front-loads all of that into one
    /// frame. Entities over budget simply retry next frame - ShapeFresh stays false, no state
    /// to unwind. An entity's very first tesselation (no mesh yet) is never gated, so newly
    /// spawned/loaded entities don't sit invisible waiting for a budget slot. 0 disables the cap.
    /// </summary>
    public static int EntityTesselationFrameBudget = 2;

    /// <summary>
    /// Caches the fully assembled outfit shape (post gear step-parenting) and resolved texture
    /// set for EntityDressedHumanoid.OnTesselation, keyed by outfit signature - see
    /// OptimumOutfitShapeCache in VSSurvivalMod for the full mechanism and why it's safe (every
    /// consumer gets an independent Shape.Clone(), never a shared mutable instance). Deliberately
    /// does NOT share the animator/InitForAnimations output the way vanilla's own AnimationCache
    /// does for undressed entities - that needs a cache key vanilla's AnimationCache doesn't have,
    /// and is a separate, higher-risk follow-up. Default OFF until played with real gameplay:
    /// this is new code on the entity-appearance path, and only compile/unit-test verified so far.
    /// </summary>
    public static bool EntityOutfitShapeCacheEnabled = false;

    /// <summary>
    /// The higher-risk follow-up scoped out of EntityOutfitShapeCacheEnabled: shares the
    /// animator build (ClientAnimator/ServerAnimator RootElements+RootPoses+Animations) across
    /// entities with the same outfit signature, mirroring vanilla's own AnimationCache - see
    /// OptimumOutfitAnimatorCache in VSSurvivalMod. Vanilla's own cache keys only on
    /// entity.Code + base shape, which can't tell outfits apart, so EntityDressedHumanoid always
    /// takes the uncached AnimManager.LoadAnimator path (Shape.InitForAnimations +
    /// ClientAnimator.CreateForEntity's full element-tree walk) - this is the single largest
    /// remaining cost in the stutter this file's other outfit settings target (measured
    /// 7-8ms/call even with the shape+texture cache and prewarm enabled). Requires duplicating
    /// Entity.OnTesselation(ref Shape, string, ref bool)'s overlay/behavior/willDisableElements
    /// handling in EntityDressedHumanoid to intercept only the final animator build - safe today
    /// (trader/villager entity types have no shape overlays and no client behaviors that mutate
    /// the shape) but higher-risk than the other outfit settings if that ever changes. Default
    /// OFF pending real gameplay testing: only compile/unit-test verified so far.
    /// </summary>
    public static bool EntityOutfitAnimatorCacheEnabled = false;

    /// <summary>
    /// Prewarms the entity texture atlas with every outfit variant's textures, for every
    /// entity type with an outfit config, once during the loading screen (capi.Event.LevelFinalize)
    /// - see OptimumOutfitTexturePrewarmerModSystem in VSSurvivalMod for the full mechanism.
    /// Uses the same GetOrInsertTexture call EntityDressedHumanoid already makes at runtime;
    /// this only changes WHEN that cost is paid (loading screen, invisible) vs mid-gameplay
    /// (a 111-231ms single-call outlier the first time any NPC wears a given outfit piece,
    /// measured via .optimum stutterwatch). Default OFF pending real gameplay testing: this
    /// extends loading time by an amount proportional to outfit variant count, and is only
    /// compile/unit-test verified so far.
    /// </summary>
    public static bool EntityOutfitTexturePrewarmEnabled = false;

    [ThreadStatic]
    public static bool RouteChiselLodMeshes;

    /// <summary>
    /// Issue #73: pool the per-clone CustomMeshDataPart buffers that chunk mesh
    /// finalization (MeshData.CloneUsingRecycler) allocates. Profiling attributed
    /// ~45 KB/chunk (99% of finalize allocation) to these clones. Default on; the
    /// pool is bypassed (fresh clone) whenever the caller is not the single
    /// tessellation worker, so it is safe with the current 1-worker setup and
    /// degrades to vanilla behavior if that ever changes. Set false to disable.
    /// </summary>
    public static bool MeshPartPoolEnabled = true;

    /// <summary>
    /// Issue #73: true when the CustomMeshDataPart pool may be used. Requires the
    /// feature flag AND at most one tessellation worker, so the pool's
    /// single-thread-owned free lists are never touched by concurrent pulls. If a
    /// second worker ever registers this returns false and clones fall back to
    /// fresh allocation (vanilla behavior), which is always correct.
    /// </summary>
    public static bool MeshPartPoolActive => MeshPartPoolEnabled && OptimumDiagnostics.TessWorkerCount <= 1;

    /// <summary>
    /// Issue #74: reuse a per-render-thread scratch ItemRenderInfo in the GUI item
    /// render path (InventoryItemRenderer.RenderItemstackToGui) instead of allocating
    /// a fresh one per visible slot per frame. Profiling measured ~104 B/slot/frame
    /// (up to 135 slots => ~14 KB/frame with an inventory open). The public
    /// IRenderAPI.GetItemStackRenderInfo API still returns a fresh instance, so mods
    /// that call it and retain the result are unaffected; only the internal per-slot
    /// path reuses, and the reused instance never escapes the synchronous
    /// RenderItemstackToGui call (the ItemRenderDelegate contract is per-call transient).
    /// All collectible render hooks still run every frame with identical inputs, so
    /// output is behaviour-identical. Set false to fall back to the vanilla allocation.
    /// </summary>
    public static bool ItemRenderInfoReuseEnabled = true;

    // Settings that live in VintagestoryLib (read per-frame from ClientSettings).
    // Mirrored here for persistence only.
    public static bool EntityShadowCull = true;
    public static int ShadowCullDistance = 80;
    public static bool DynamicLightScale = true;
    public static bool BackgroundFpsLimit = true;
    public static bool PreciseFramePacing = true;
    public static bool ShadowFarVegetation = true;

    /// <summary>
    /// T3: Reduce position-packet send frequency for distant entities (IsTracked==1,
    /// beyond ~50 blocks). Default OFF: this changes observable network behavior.
    /// FarTrackedTickStride=2 sends at 15Hz instead of 30Hz. Fast-moving entities
    /// and EntityItems bypass the throttle.
    /// </summary>
    public static bool DistanceSendFrequencyEnabled = false;
    public static int FarTrackedTickStride = 2;

    /// <summary>
    /// T4: Split the random tick pass into N sub-passes (default 8) at
    /// BlockTickInterval/N intervals. Each pass processes only 1/N of chunks
    /// (modulo filtering). Reduces tick-time sawtooth from ~48ms max to ~6ms
    /// per pass. Chunks still receive the same aggregate tick rate per cycle.
    /// Default ON: pure scheduling change, no gameplay logic change.
    /// </summary>
    public static bool RandomTickSliceEnabled = true;

    /// <summary>
    /// Lets diagnostic runs share worldgen passes across worker threads.
    /// Optimum suspends automatic worker scheduling until worldgen R1 isolates
    /// mutable generator state. The environment override controls experiments.
    /// </summary>
    public static bool WorldgenWorkStealingEnabled = false;

    /// <summary>
    /// Runtime cap for client tessellation workers. The startup scan lowers it
    /// to one when a foreign texture source can race on singleton state.
    /// </summary>
    public static volatile int TesselationWorkerCap = int.MaxValue;

    /// <summary>
    /// Maximum number of completed meshes waiting for render-thread upload.
    /// </summary>
    public static int TesselationUploadQueueCapacity = 128;

    /// <summary>
    /// Allows parallel worldgen with assemblies outside the audited set.
    /// This override can produce unsafe worldgen state and stays off by default.
    /// </summary>
    public static volatile bool WorldgenConcurrencyForce;

    /// <summary>
    /// Worldgen worker policy. Controls how many additional worldgen threads
    /// run alongside the main chunk thread.
    /// Values: "auto" (detect hardware), "serial" (0 workers), "1", "2", "3".
    /// Default "auto": serial on systems with 8 or fewer logical processors,
    /// 1 worker on systems with more than 8.
    /// Environment variables OPTIMUM_WORLDGEN_MT and OPTIMUM_WORLDGEN_WORKERS
    /// override this setting for benchmark automation.
    /// </summary>
    public static string WorldgenWorkerPolicy = "auto";

    /// <summary>
    /// Replace the vanilla ChunkMapLayer upload pipeline with a page-cached
    /// renderer. Pages (8x8 chunks, 256x256 pixels) persist to disk as
    /// zstd-compressed RGBA and upload to a GL_TEXTURE_2D_ARRAY for
    /// single-draw-call rendering. Explored chunks never re-generate on map
    /// open: the cache serves them in sub-100ms. Default true.
    /// </summary>
    public static bool MapPageCacheEnabled = true;

    public static bool EffectiveMapPageCache => MapPageCacheEnabled &&
        !IsShaderFeatureDisabled("MapPageCache");

    /// <summary>
    /// Number of layers in the GL_TEXTURE_2D_ARRAY used for map page
    /// rendering. Each layer holds one 256x256 page. 128 covers most
    /// viewport sizes at normal zoom; raise for extreme view distances.
    /// </summary>
    public static int MapPageCacheMaxLayers = 128;

    /// <summary>
    /// Use BC7 compressed textures (GL_ARB_texture_compression_bptc) for
    /// map pages when the GPU supports it. Cuts VRAM 4x and upload bandwidth
    /// proportionally. Falls back to RGBA8 when the extension is absent.
    /// </summary>
    public static bool MapPageCacheBc7 = true;

    /// <summary>
    /// Runtime flag: true when the GPU reports GL_ARB_texture_compression_bptc.
    /// Set at startup, not persisted.
    /// </summary>
    public static volatile bool MapPageCacheBc7Supported;

    /// <summary>
    /// Generate approximate biome-colored map tiles for chunks the player
    /// has not explored. Uses the world seed plus climate, ocean, and forest
    /// region maps to produce a low-fidelity terrain overview. The pregen
    /// pixels carry a desaturation tint so the player can distinguish them
    /// from real explored terrain at a glance. Default false (opt-in).
    /// </summary>
    public static bool MapPageCachePregen = false;

    /// <summary>
    /// Enables the parallel read-only SQLite connection pool for chunk column
    /// loading (OptimumChunkReadPool). In singleplayer, DB I/O dominates chunk
    /// thread time; the pool fans the per-Y-level SELECT queries out across
    /// several read-only connections in WAL mode. Default true.
    /// </summary>
    public static bool ChunkReadPoolEnabled = true;

    /// <summary>
    /// Number of read-only SQLite connections in the chunk read pool.
    /// Clamped to [1, 8] by OptimumChunkReadPool itself. Default 4.
    /// </summary>
    public static int ChunkReadPoolWorkers = 4;

    /// <summary>
    /// Adaptive chunk generation radius: reduces the effective view radius
    /// under gen-queue pressure (player exploring fast) so fewer columns
    /// queue at once. Recovers to full radius when the queue drains.
    /// Default ON in singleplayer. Multiplayer servers already have dedicated
    /// gen threads and a capped MaxChunkRadius, so this mostly helps SP.
    /// </summary>
    public static bool AdaptiveRadiusEnabled = true;

    /// <summary>
    /// Floor radius in chunks: the controller never drops below this value.
    /// 4 chunks = 128 blocks = still enough to avoid visible pop-in at
    /// normal walk speed.
    /// </summary>
    public static int AdaptiveRadiusFloor = 4;

    /// <summary>
    /// When the EWMA-smoothed queue depth exceeds this count, the controller
    /// shrinks the radius by 1 per tick. Default 60 = ~2x the typical
    /// steady-state queue when walking at normal speed.
    /// </summary>
    public static int AdaptiveRadiusHighThreshold = 60;

    /// <summary>
    /// When the smoothed queue depth falls below this count, the controller
    /// recovers the radius by 1 per tick. Default 20 = the queue has drained
    /// enough to safely grow the radius back.
    /// </summary>
    public static int AdaptiveRadiusLowThreshold = 20;

    /// <summary>
    /// Runtime-only: the current effective max chunk radius after adaptive
    /// scaling. Written by OptimumAdaptiveRadiusController.Tick(), read by
    /// ServerSystemSendChunks to cap how far out it requests chunks. When
    /// AdaptiveRadiusEnabled is false, stays at int.MaxValue (no cap).
    /// Not persisted.
    /// </summary>
    public static volatile int AdaptiveRadiusEffective = int.MaxValue;

    /// <summary>
    /// When true, ExecuteMainThreadTasks drains multiple launch tasks per frame
    /// within LaunchTaskBudgetMs. When false, vanilla one-per-frame behavior.
    /// Default false (opt-in experiment).
    /// </summary>
    public static volatile bool LaunchTaskBudgetEnabled;

    /// <summary>
    /// Per-frame time budget in milliseconds for draining launch tasks.
    /// Clamped [1, 500]. Only effective when LaunchTaskBudgetEnabled is true.
    /// </summary>
    public static volatile int LaunchTaskBudgetMs = 100;

    /// <summary>
    /// Parallelize ServerChunk.FromBytes deserialization after the read pool
    /// fetches raw bytes. Safe because each FromBytes call creates a new
    /// ServerChunk instance with no shared mutable state.
    /// </summary>
    public static volatile bool ChunkDeserializeParallel = true;

    /// <summary>
    /// Minimum chunkMapSizeY required before parallel deserialization kicks in.
    /// Below this threshold the overhead of Parallel.For exceeds serial.
    /// Clamped [2, 64].
    /// </summary>
    public static volatile int ChunkDeserializeParallelMinY = 4;

    /// <summary>
    /// Move shader asset I/O, ToText decode, and LoadShaderProgram to a
    /// background thread during loadRegisteredShaderPrograms. GL calls
    /// (Compile, SetCustomSampler) stay on the GL thread.
    /// </summary>
    public static volatile bool ShaderPreprocessParallel = true;

    /// <summary>
    /// FSR render scale: 1.0 = native (off), 0.85 = quality, 0.77 = balanced, 0.67 = performance.
    /// Multiplies ssaaLevel in SetupDefaultFrameBuffers. Disables FXAA when < 1.0.
    /// </summary>
    public static float RenderScale = 1.0f;

    /// <summary>
    /// Temporal anti-aliasing. Off by default: with Taa false the render chain
    /// must stay byte-identical to the pre-TAA one, so nothing here may change a
    /// matrix, a target or a shader define unless it is on.
    /// </summary>
    public static bool Taa = false;

    /// <summary>
    /// Post-resolve sharpening strength, 0 (none) to 1. TAA trades sharpness for
    /// stability; the sharpen pass buys some of it back. Kept separate from the
    /// FSR1 RCAS strength so the two are never applied at full force together.
    /// </summary>
    public static float TaaSharpness = 0.2f;

    /// <summary>
    /// LOD bias applied to sampled textures while TAA is on. Jitter gives the
    /// resolve sub-pixel samples, so mip selection can afford to be sharper than
    /// the unjittered frame would allow. Negative sharpens.
    /// </summary>
    public static float TaaMipBias = -0.5f;

    /// <summary>
    /// Debug visualisation of the temporal pipeline: 0 off, higher values select
    /// motion, reactive, validity and rejection views.
    /// </summary>
    public static int TaaDebugView = 0;

    /// <summary>
    /// Applies the jitter window without running a resolve. A developer switch for
    /// isolating "is the shear correct" from "is the resolve correct": with it on,
    /// a static scene must visibly shimmer by exactly one pixel. Not in the GUI.
    /// </summary>
    public static bool TaaJitterDev = false;

    /// <summary>
    /// Which ambient occlusion runs: "auto", "vanilla" or "gtao"
    /// (docs/research/ambient-occlusion.md, section E).
    ///
    /// "auto" (the default) is the GTAO visibility-bitmask pass on the Vulkan backend
    /// whenever TAA is active, and vanilla SSAO otherwise. "gtao" asks for it on Vulkan
    /// without TAA too (a measurement configuration: two denoise passes, a still noise
    /// index). "vanilla" keeps vanilla SSAO. The OpenGL backend ignores the setting and
    /// always runs vanilla SSAO. Vanilla's own SSAO quality setting still switches AO off
    /// entirely at 0 on both backends: the G-buffer both passes read exists only above 0.
    ///
    /// A string, like <see cref="Renderer" />: an unrecognised value degrades to "auto"
    /// rather than failing the whole file.
    /// </summary>
    public static string AmbientOcclusion = "auto";

    /// <summary>
    /// The GTAO quality preset: "low" (1x2), "medium" (2x2), "high" (3x3) or "ultra" (9x3,
    /// two denoise passes). Medium is the render-resolution handheld candidate; the handheld
    /// default is decided by the section D measurements.
    /// </summary>
    public static string AmbientOcclusionPreset = "medium";

    /// <summary>
    /// The master switch for ambient occlusion, in the Optimum options tab: false skips both
    /// the vanilla SSAO pass and the GTAO pass for the frame and changes nothing else in the
    /// post chain.
    ///
    /// Orthogonal to <see cref="AmbientOcclusion" />, which decides which AO runs while this is
    /// on. The shader defines (SSAOLEVEL, OPTIMUMAO) are stamped from the mode and the vanilla
    /// SSAO quality, never from this, so flipping it needs no shader reload and no frame buffer
    /// rebuild and the selected AO comes back exactly as it was - which is what makes it an A/B
    /// comparison rather than a settings change.
    /// </summary>
    public static bool AmbientOcclusionEnabled = true;

    /// <summary>
    /// The ambient occlusion debug view (Optimum options tab): the final composition writes the
    /// AO term alone as greyscale instead of the graded scene - white fully lit, dark fully
    /// occluded. It shows whichever AO ran this frame, the platform's own visibility texture or
    /// the vanilla blurred SSAO target, and does nothing while AO is off, because neither target
    /// was written then. A view, not a render setting: one uniform per frame, no reload.
    /// </summary>
    public static bool AmbientOcclusionDebugView = false;

    /// <summary>
    /// Whether GTAO is selected for a backend: never on OpenGL; on Vulkan with "gtao", or
    /// with "auto" while TAA is active.
    /// </summary>
    public static bool GtaoSelected(bool vulkanBackend, bool taaActive)
    {
        if (!vulkanBackend) return false;
        if (string.Equals(AmbientOcclusion, "gtao", StringComparison.OrdinalIgnoreCase)) return true;
        return taaActive && string.Equals(AmbientOcclusion, "auto", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GTAO for the backend actually running and the TAA state actually in effect.</summary>
    public static bool EffectiveGtao => GtaoSelected(OptimumRender.IsVulkan, EffectiveTaa);

    /// <summary>
    /// Stamped by ShaderRegistry when it builds the shader prefixes: true when the shaders were
    /// compiled with <c>#define OPTIMUMAO 1</c> (the class channel writes and the GTAO compose
    /// branch). The platform runs GTAO only while this holds, so the pass and the shaders
    /// that feed and compose it can never disagree between two shader reloads.
    /// </summary>
    public static bool AmbientOcclusionShadersUseGtao { get; set; }

    /// <summary>
    /// Which renderer the client runs: "opengl", "vulkan", or "auto".
    ///
    /// OpenGL is the default and stays so until the Vulkan backend reaches
    /// parity. "auto" means Vulkan where the vendor and driver are known good and
    /// OpenGL everywhere else, so it can be switched on per-vendor as the matrix
    /// goes green without asking anyone to change a setting.
    ///
    /// A string rather than an enum because it is persisted in optimum.json,
    /// where an unrecognised value should degrade to OpenGL rather than fail to
    /// parse the whole file.
    /// </summary>
    public static string Renderer = "opengl";

    /// <summary>True when the configuration asks for Vulkan at all.</summary>
    public static bool WantsVulkanRenderer =>
        string.Equals(Renderer, "vulkan", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Renderer, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when Vulkan was reached through "auto" rather than asked for by name.
    ///
    /// The two are not the same decision. "vulkan" is a choice the player made and
    /// is honoured wherever the backend runs at all; "auto" is a default they
    /// never chose, so it only selects the backend on driver families it has been
    /// exercised against and stays on OpenGL everywhere else.
    /// </summary>
    public static bool RendererSelectedAutomatically =>
        string.Equals(Renderer, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// R4: cap the god-rays post-process at 100 texture samples when enabled.
    /// The disabled path sends the vanilla 180-sample limit. This option can
    /// change the post-process image, so it stays off by default.
    /// </summary>
    public const int VanillaGodRaysSampleLimit = 180;
    public const int OptimumGodRaysSampleLimit = 100;
    public static bool GodRaysSampleCapEnabled = false;
    public static int GodRaysSampleLimit => EffectiveGodRaysSampleCap
        ? OptimumGodRaysSampleLimit
        : VanillaGodRaysSampleLimit;

    // Compatibility state comes from the launcher's metadata scan. It stays
    // outside OptimumConfigData so a mod cannot make a runtime fallback
    // persistent by changing its shader files.
    private static readonly HashSet<string> _shaderCompatibilityDisabledFeatures = new(StringComparer.OrdinalIgnoreCase);
    // "all" until a schema 2 report says otherwise: no report read yet is no report.
    private static readonly HashSet<string> _shaderCompatibilityRewriterPrograms = new(StringComparer.OrdinalIgnoreCase) { AllShaderPrograms };
    private static bool _shaderCompatibilityScanFailed;
    private static bool _greedyMeshVertexShaderReady;
    private static bool _greedyMeshFragmentShaderReady;
    private static string? _shaderCompatibilityFingerprint;
    private static string? _dataPath;

    public static bool EffectiveGreedyMesh => GreedyMeshEnabled &&
        !IsShaderFeatureDisabled("GreedyMesh") &&
        _greedyMeshVertexShaderReady && _greedyMeshFragmentShaderReady;

    public static float EffectiveRenderScale => IsShaderFeatureDisabled("RenderScale") ? 1.0f : RenderScale;

    public static bool EffectiveGodRaysSampleCap => GodRaysSampleCapEnabled &&
        !IsShaderFeatureDisabled("GodRaysSampleCap");

    /// <summary>
    /// The terrain texture LOD bias every atlas sampler runs with: the render
    /// scale's own bias (log2 of the scale, so a half-resolution frame samples
    /// one mip sharper) plus <see cref="TaaMipBias" /> while TAA is on.
    ///
    /// Both terms live here because two call sites apply them and must agree:
    /// ChunkRenderer sets the parameter on each atlas texture, and ShaderRegistry
    /// sets it on the chunkopaque/chunktopsoil sampler objects, which override
    /// the texture parameter for the units they are bound to.
    ///
    /// 0 with TAA off and render scale 1.0 - the value that means "do not touch
    /// the parameter at all", which is what keeps TAA off byte-identical.
    /// </summary>
    public static float EffectiveTerrainLodBias
    {
        get
        {
            float bias = 0f;
            float scale = EffectiveRenderScale;
            if (scale < 1.0f)
            {
                bias += MathF.Log2(Math.Clamp(scale, 0.5f, 1.0f));
            }
            if (EffectiveTaa)
            {
                bias += Math.Clamp(TaaMipBias, -2.0f, 1.0f);
            }
            return bias;
        }
    }

    // Like the Vulkan renderer selection, TAA is a renderer-level feature: a
    // missing launcher scan must not disable it (IsShaderFeatureDisabled reports
    // everything disabled without a scan), only an explicit scan verdict does.
    public static bool EffectiveTaa => Taa &&
        !TaaRuntimeDisabled &&
        !IsFeatureExplicitlyDisabled("Taa");

    /// <summary>
    /// Set by the platform when TAA's frame buffers or resolve shader could not
    /// be created; TAA stays off for the rest of the session and the shaders
    /// that compile against <see cref="EffectiveTaa" /> (final.fsh's FXAA branch)
    /// are rebuilt so the FXAA fallback really runs.
    /// </summary>
    public static bool TaaRuntimeDisabled { get; private set; }

    public static bool DisableTaaAtRuntime()
    {
        if (TaaRuntimeDisabled) return false;
        TaaRuntimeDisabled = true;
        return true;
    }

    public static bool EffectiveEntityLightBatch => EntityLightBatchEnabled &&
        !IsShaderFeatureDisabled("EntityLightBatch");

    public static bool EffectiveEntityShaderStateCache => EntityShaderStateCacheEnabled &&
        !IsShaderFeatureDisabled("EntityShaderStateCache");

    public static bool EffectiveOit => !IsShaderFeatureDisabled("Oit");

    public static bool EffectiveShaderPreprocessParallel => ShaderPreprocessParallel &&
        !IsShaderFeatureDisabled("ShaderPreprocessParallel");

    public static bool IsShaderFeatureDisabled(string feature) =>
        _shaderCompatibilityScanFailed || _shaderCompatibilityDisabledFeatures.Contains(feature);

    /// <summary>
    /// Whether the launcher's mod compatibility scan actually produced a report.
    ///
    /// <see cref="IsShaderFeatureDisabled" /> deliberately treats a missing scan
    /// as "everything disabled", which is the right fail-safe but makes the two
    /// cases indistinguishable to a caller that wants to explain itself. The
    /// renderer selection reports "the scan has not run" separately from "a mod
    /// requires OpenGL", because the remedies are completely different.
    /// </summary>
    public static bool ShaderCompatibilityScanAvailable => !_shaderCompatibilityScanFailed;

    /// <summary>
    /// Whether the scan named this feature, ignoring the "scan failed means all
    /// disabled" fallback that <see cref="IsShaderFeatureDisabled" /> applies.
    ///
    /// Only correct for decisions that are not shader features - the renderer
    /// backend is the one such decision - because for a real shader feature the
    /// conservative fallback is the whole point.
    /// </summary>
    public static bool IsFeatureExplicitlyDisabled(string feature) =>
        _shaderCompatibilityDisabledFeatures.Contains(feature);

    /// <summary>
    /// The entry of the scan's <c>rewriterPrograms</c> (report schema 2) that stands for every program:
    /// a shaderincludes override, or a scan that could not finish.
    /// </summary>
    public const string AllShaderPrograms = "all";

    /// <summary>
    /// Whether the launcher's scan found a mod replacing <paramref name="passName" />'s GLSL, so the Vulkan
    /// renderer must build it through the rewriter from that source instead of linking the native SPIR-V
    /// (docs/vulkan-native-shaders.md section 8). True when <c>rewriterPrograms</c> names the program
    /// (case-insensitive, the scanner lowercases base names) or holds <see cref="AllShaderPrograms" />.
    /// A missing, unreadable, failed or pre-schema-2 report counts as <see cref="AllShaderPrograms" />,
    /// the scanner's own conservative rule. Asking for <see cref="AllShaderPrograms" /> itself answers
    /// whether every program is rewriter-only.
    /// </summary>
    public static bool IsShaderProgramOverriddenByMods(string passName) =>
        IsShaderProgramOverriddenBy(_shaderCompatibilityRewriterPrograms, passName);

    public static void SetGreedyMeshShaderAbi(bool vertexShaderReady, bool fragmentShaderReady)
    {
        _greedyMeshVertexShaderReady = vertexShaderReady;
        _greedyMeshFragmentShaderReady = fragmentShaderReady;
        GreedyMeshShadersCompiledOn = vertexShaderReady && fragmentShaderReady;
    }

    public static void ResetShaderCompatibilityAfterReload()
    {
        SetGreedyMeshShaderAbi(false, false);
    }

    public static void ResetShaderCompatibilityForTests()
    {
        _shaderCompatibilityDisabledFeatures.Clear();
        _shaderCompatibilityScanFailed = false;
        _shaderCompatibilityFingerprint = null;
        ResetShaderCompatibilityAfterReload();
    }

    private static void LoadShaderCompatibilityReport()
    {
        _shaderCompatibilityDisabledFeatures.Clear();
        _shaderCompatibilityScanFailed = false;
        _shaderCompatibilityFingerprint = null;
        SetRewriterProgramsToAll();
        ResetShaderCompatibilityAfterReload();

        if (_dataPath == null) return;

        string reportPath = Path.Combine(_dataPath, ".optimum", "shader-compatibility.json");
        try
        {
            if (!File.Exists(reportPath)) return;

            string json = File.ReadAllText(reportPath);
            var report = JsonSerializer.Deserialize<ShaderCompatibilityState>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (report == null)
            {
                _shaderCompatibilityScanFailed = true;
                return;
            }

            if (report.DisabledFeatures != null)
            {
                foreach (string? feature in report.DisabledFeatures)
                {
                    if (!string.IsNullOrWhiteSpace(feature)) _shaderCompatibilityDisabledFeatures.Add(feature);
                }
            }

            _shaderCompatibilityRewriterPrograms.Clear();
            foreach (string program in RewriterProgramsOf(report)) _shaderCompatibilityRewriterPrograms.Add(program);

            _shaderCompatibilityScanFailed = report.ScanFailed;
            _shaderCompatibilityFingerprint = report.Fingerprint;
        }
        catch (Exception)
        {
            _shaderCompatibilityDisabledFeatures.Clear();
            _shaderCompatibilityScanFailed = true;
            _shaderCompatibilityFingerprint = null;
            SetRewriterProgramsToAll();
        }
    }

    /// <summary>
    /// The <c>rewriterPrograms</c> of a shader compatibility report's JSON, as <see cref="IsShaderProgramOverriddenByMods" />
    /// reads them: the named programs, or the single <see cref="AllShaderPrograms" /> for a null, unreadable, pre-schema-2
    /// or failed report and for one without the field. Pure; the loaded state is not touched.
    /// </summary>
    public static IReadOnlyCollection<string> ParseShaderRewriterPrograms(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson)) return new[] { AllShaderPrograms };
        try
        {
            var report = JsonSerializer.Deserialize<ShaderCompatibilityState>(reportJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return RewriterProgramsOf(report);
        }
        catch (Exception)
        {
            return new[] { AllShaderPrograms };
        }
    }

    /// <summary>Whether <paramref name="rewriterPrograms" /> (see <see cref="ParseShaderRewriterPrograms" />) send <paramref name="passName" /> to the rewriter.</summary>
    public static bool IsShaderProgramOverriddenBy(IEnumerable<string> rewriterPrograms, string passName)
    {
        foreach (string program in rewriterPrograms)
        {
            if (string.Equals(program, AllShaderPrograms, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(passName) && string.Equals(program, passName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    // Schema 2 carries the programs a mod's GLSL replaced. A v1 report, a report without the list, or a
    // failed scan cannot say which programs are safe, so all of them stay rewriter-only.
    private static List<string> RewriterProgramsOf(ShaderCompatibilityState? report)
    {
        var programs = new List<string>();
        if (report == null || report.SchemaVersion < 2 || report.RewriterPrograms == null || report.ScanFailed)
        {
            programs.Add(AllShaderPrograms);
            return programs;
        }
        foreach (string? program in report.RewriterPrograms)
        {
            if (!string.IsNullOrWhiteSpace(program)) programs.Add(program.Trim());
        }
        return programs;
    }

    private static void SetRewriterProgramsToAll()
    {
        _shaderCompatibilityRewriterPrograms.Clear();
        _shaderCompatibilityRewriterPrograms.Add(AllShaderPrograms);
    }

    public static void ReloadShaderCompatibilityReport()
    {
        LoadShaderCompatibilityReport();
    }

    private static string? _configPath;

    public static void SetRepulsionDistance(int blocks)
    {
        RepulsionDistance = blocks;
        RepulsionDistanceSq = (double)blocks * blocks;
    }

    public static void SetChiselLodDistance(int blocks)
    {
        ChiselLodDistance = blocks;
        ChiselLodDistanceSq = (double)blocks * blocks;
    }

    /// <summary>
    /// Resolves the worker count from the persisted policy ("auto", "serial", "1"-"3").
    /// Serial on systems with 8 or fewer logical processors (APU/iGPU saturation).
    /// 1 worker on 8+ core systems with headroom for parallel terrain generation.
    /// </summary>
    public static int GetWorldgenWorkerCount(int logicalProcessors, bool reducedServerThreads)
    {
        if (reducedServerThreads) return 0;

        string policy = WorldgenWorkerPolicy ?? "auto";
        return policy switch
        {
            "serial" or "0" => 0,
            "1" => 1,
            "2" => Math.Min(2, Math.Max(0, logicalProcessors / 4 - 1)),
            "3" => Math.Min(3, Math.Max(0, logicalProcessors / 4 - 1)),
            _ => logicalProcessors > 8 ? 1 : 0, // "auto"
        };
    }

    /// <summary>
    /// Worker ceiling for adaptive scaling. Returns 0 when serial, otherwise
    /// the configured or auto-detected ceiling.
    /// </summary>
    public static int GetWorldgenWorkerCeiling(int logicalProcessors, bool reducedServerThreads)
    {
        if (reducedServerThreads) return 0;

        string policy = WorldgenWorkerPolicy ?? "auto";
        return policy switch
        {
            "serial" or "0" => 0,
            "1" => 1,
            "2" => 2,
            "3" => 3,
            _ => logicalProcessors > 8 ? 2 : 0, // "auto" ceiling
        };
    }

    /// <summary>
    /// Resolves the exact worker count for a diagnostic environment override.
    /// Priority: env var > config policy > auto default.
    /// Reduced-thread mode forces serial regardless.
    /// </summary>
    public static int ResolveWorldgenWorkerCount(
        int logicalProcessors,
        bool reducedServerThreads,
        string? mtOverride,
        string? workerCountOverride)
    {
        if (reducedServerThreads) return 0;

        // Env var explicit override (highest priority, for benchmarks)
        if (mtOverride == "1" && workerCountOverride != null)
        {
            return workerCountOverride switch
            {
                "1" => 1,
                "2" => 2,
                "3" => 3,
                _ => 0,
            };
        }

        // Env var explicitly disables
        if (mtOverride == "0") return 0;

        // Fall through to config-based policy
        return GetWorldgenWorkerCount(logicalProcessors, reducedServerThreads);
    }

    /// <summary>
    /// One entry per field OptimumConfigData persists, keyed by the persisted
    /// name rather than the backing static field's own identifier (they differ
    /// for a few toggles, e.g. RepulsionGateEnabled persists as RepulsionGate).
    /// Drives .optimum status and the coverage test that keeps this in sync
    /// with OptimumConfigData whenever a field gets added or removed.
    /// </summary>
    public static (string Name, string Value)[] DescribeToggles() => new (string, string)[]
    {
        (nameof(OptimumConfigData.EntityShadowCull), EntityShadowCull.ToString()),
        (nameof(OptimumConfigData.ShadowCullDistance), ShadowCullDistance.ToString()),
        (nameof(OptimumConfigData.DynamicLightScale), DynamicLightScale.ToString()),
        (nameof(OptimumConfigData.BackgroundFpsLimit), BackgroundFpsLimit.ToString()),
        (nameof(OptimumConfigData.PreciseFramePacing), PreciseFramePacing.ToString()),
        (nameof(OptimumConfigData.RepulsionGate), RepulsionGateEnabled.ToString()),
        (nameof(OptimumConfigData.RepulsionDistance), RepulsionDistance.ToString()),
        (nameof(OptimumConfigData.AnimBlockLod), AnimBlockLodEnabled.ToString()),
        (nameof(OptimumConfigData.AnimBlockLodFrameBudget), AnimBlockLodFrameBudget.ToString()),
        (nameof(OptimumConfigData.ShadowFarVegetation), ShadowFarVegetation.ToString()),
        (nameof(OptimumConfigData.WeatherWindThrottle), WeatherWindThrottleEnabled.ToString()),
        (nameof(OptimumConfigData.ParticleDistanceGate), ParticleDistanceGateEnabled.ToString()),
        (nameof(OptimumConfigData.ChiselLod), ChiselLodEnabled.ToString()),
        (nameof(OptimumConfigData.ChiselLodDistance), ChiselLodDistance.ToString()),
        (nameof(OptimumConfigData.OcclusionCullingScale), OcclusionCullingScaleEnabled.ToString()),
        (nameof(OptimumConfigData.BfsChunkVisibility), BfsChunkVisibilityEnabled.ToString()),
        (nameof(OptimumConfigData.MeshPartPool), MeshPartPoolEnabled.ToString()),
        (nameof(OptimumConfigData.ItemRenderInfoReuse), ItemRenderInfoReuseEnabled.ToString()),
        (nameof(OptimumConfigData.DynamicLightCache), DynamicLightCacheEnabled.ToString()),
        (nameof(OptimumConfigData.EntityLightBatch), EntityLightBatchEnabled.ToString()),
        (nameof(OptimumConfigData.EntityShaderStateCache), EntityShaderStateCacheEnabled.ToString()),
        (nameof(OptimumConfigData.EntityTesselationFrameBudget), EntityTesselationFrameBudget.ToString()),
        (nameof(OptimumConfigData.EntityOutfitShapeCache), EntityOutfitShapeCacheEnabled.ToString()),
        (nameof(OptimumConfigData.EntityOutfitAnimatorCache), EntityOutfitAnimatorCacheEnabled.ToString()),
        (nameof(OptimumConfigData.EntityOutfitTexturePrewarm), EntityOutfitTexturePrewarmEnabled.ToString()),
        (nameof(OptimumConfigData.GreedyMeshEnabled), GreedyMeshEnabled.ToString()),
        (nameof(OptimumConfigData.GreedyMeshMaxMergeWidth), GreedyMeshMaxMergeWidth.ToString()),
        (nameof(OptimumConfigData.GreedyMeshMaxMergeHeight), GreedyMeshMaxMergeHeight.ToString()),
        (nameof(OptimumConfigData.GreedyMeshLightTolerance), GreedyMeshLightTolerance.ToString()),
        (nameof(OptimumConfigData.GreedyMeshFarDistance), GreedyMeshFarDistance.ToString()),
        (nameof(OptimumConfigData.GreedyMeshTextureGrad), GreedyMeshTextureGrad.ToString()),
        (nameof(OptimumConfigData.RenderScale), RenderScale.ToString("F2")),
        (nameof(OptimumConfigData.Renderer), Renderer),
        (nameof(OptimumConfigData.GodRaysSampleCap), GodRaysSampleCapEnabled.ToString()),
        (nameof(OptimumConfigData.Taa), Taa.ToString()),
        (nameof(OptimumConfigData.TaaSharpness), TaaSharpness.ToString("F2")),
        (nameof(OptimumConfigData.TaaMipBias), TaaMipBias.ToString("F2")),
        (nameof(OptimumConfigData.TaaDebugView), TaaDebugView.ToString()),
        (nameof(OptimumConfigData.TaaJitterDev), TaaJitterDev.ToString()),
        (nameof(OptimumConfigData.AmbientOcclusion), AmbientOcclusion),
        (nameof(OptimumConfigData.AmbientOcclusionPreset), AmbientOcclusionPreset),
        (nameof(OptimumConfigData.AmbientOcclusionEnabled), AmbientOcclusionEnabled.ToString()),
        (nameof(OptimumConfigData.AmbientOcclusionDebugView), AmbientOcclusionDebugView.ToString()),
        (nameof(OptimumConfigData.MapPageCache), MapPageCacheEnabled.ToString()),
        (nameof(OptimumConfigData.MapPageCacheMaxLayers), MapPageCacheMaxLayers.ToString()),
        (nameof(OptimumConfigData.MapPageCacheBc7), MapPageCacheBc7.ToString()),
        (nameof(OptimumConfigData.RandomTickSlice), RandomTickSliceEnabled.ToString()),
        (nameof(OptimumConfigData.WorldgenWorkStealing), WorldgenWorkStealingEnabled.ToString()),
        (nameof(OptimumConfigData.ChunkReadPoolEnabled), ChunkReadPoolEnabled.ToString()),
        (nameof(OptimumConfigData.ChunkReadPoolWorkers), ChunkReadPoolWorkers.ToString()),
        (nameof(OptimumConfigData.AdaptiveRadius), AdaptiveRadiusEnabled.ToString()),
        (nameof(OptimumConfigData.AdaptiveRadiusFloor), AdaptiveRadiusFloor.ToString()),
        (nameof(OptimumConfigData.AdaptiveRadiusHighThreshold), AdaptiveRadiusHighThreshold.ToString()),
        (nameof(OptimumConfigData.AdaptiveRadiusLowThreshold), AdaptiveRadiusLowThreshold.ToString()),
        (nameof(OptimumConfigData.LaunchTaskBudgetEnabled), LaunchTaskBudgetEnabled.ToString()),
        (nameof(OptimumConfigData.LaunchTaskBudgetMs), LaunchTaskBudgetMs.ToString()),
        (nameof(OptimumConfigData.WorldgenWorkerPolicy), WorldgenWorkerPolicy),
        (nameof(OptimumConfigData.ChunkDeserializeParallel), ChunkDeserializeParallel.ToString()),
        (nameof(OptimumConfigData.ChunkDeserializeParallelMinY), ChunkDeserializeParallelMinY.ToString()),
        (nameof(OptimumConfigData.ShaderPreprocessParallel), ShaderPreprocessParallel.ToString()),
        (nameof(OptimumConfigData.IndirectDraw), IndirectDrawEnabled.ToString()),
        (nameof(OptimumConfigData.SimdCulling), SimdCullingEnabled.ToString()),
        (nameof(OptimumConfigData.KometGuard), KometGuardEnabled.ToString()),
    };

    /// <summary>
    /// Set the data path root (e.g. GamePaths.DataPath). Call once at startup.
    /// </summary>
    public static void SetDataPath(string dataPath)
    {
        if (dataPath == null)
        {
            _dataPath = null;
            _configPath = null;
            ResetShaderCompatibilityForTests();
            return;
        }
        string dir = Path.Combine(dataPath, "ModConfig");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "optimum.json");
        _dataPath = dataPath;
        LoadShaderCompatibilityReport();
    }

    /// <summary>
    /// Load config from optimum.json. Missing keys keep their compiled
    /// defaults. After a successful load the file is written back, so
    /// clamped values are normalized on disk and fields added since the
    /// file was written appear in it automatically; a missing file is
    /// created with the defaults on first run. A file that fails to
    /// parse is left untouched (never clobber what the user typed).
    /// </summary>
    public static void Load()
    {
        if (_configPath == null) return;

        if (!File.Exists(_configPath))
        {
            Save();
            return;
        }

        try
        {
            string json = File.ReadAllText(_configPath);
            var data = JsonSerializer.Deserialize<OptimumConfigData>(json);
            if (data == null) return;

            EntityShadowCull = data.EntityShadowCull;
            ShadowCullDistance = data.ShadowCullDistance;
            DynamicLightScale = data.DynamicLightScale;
            BackgroundFpsLimit = data.BackgroundFpsLimit;
            PreciseFramePacing = data.PreciseFramePacing;
            RepulsionGateEnabled = data.RepulsionGate;
            RepulsionDistance = data.RepulsionDistance;
            RepulsionDistanceSq = (double)data.RepulsionDistance * data.RepulsionDistance;
            AnimBlockLodEnabled = data.AnimBlockLod;
            AnimBlockLodFrameBudget = data.AnimBlockLodFrameBudget;
            ShadowFarVegetation = data.ShadowFarVegetation;
            WeatherWindThrottleEnabled = data.WeatherWindThrottle;
            ParticleDistanceGateEnabled = data.ParticleDistanceGate;
            ChiselLodEnabled = data.ChiselLod;
            ChiselLodDistance = data.ChiselLodDistance;
            ChiselLodDistanceSq = (double)data.ChiselLodDistance * data.ChiselLodDistance;
            OcclusionCullingScaleEnabled = data.OcclusionCullingScale;
            BfsChunkVisibilityEnabled = data.BfsChunkVisibility;
            MeshPartPoolEnabled = data.MeshPartPool;
            ItemRenderInfoReuseEnabled = data.ItemRenderInfoReuse;
            DynamicLightCacheEnabled = data.DynamicLightCache;
            EntityLightBatchEnabled = data.EntityLightBatch;
            EntityShaderStateCacheEnabled = data.EntityShaderStateCache;
            EntityTesselationFrameBudget = Math.Max(0, data.EntityTesselationFrameBudget);
            EntityOutfitShapeCacheEnabled = data.EntityOutfitShapeCache;
            EntityOutfitAnimatorCacheEnabled = data.EntityOutfitAnimatorCache;
            EntityOutfitTexturePrewarmEnabled = data.EntityOutfitTexturePrewarm;
            GreedyMeshEnabled = data.GreedyMeshEnabled;
            // Clamped to the tile-count encoding's ceiling (3 bits, max 8)
            // so a hand-edited optimum.json can't request a merge wider
            // than the shader can tile.
            GreedyMeshMaxMergeWidth = Math.Clamp(data.GreedyMeshMaxMergeWidth, 1, 8);
            GreedyMeshMaxMergeHeight = Math.Clamp(data.GreedyMeshMaxMergeHeight, 1, 8);
            GreedyMeshLightTolerance = Math.Clamp(data.GreedyMeshLightTolerance, 0, 4);
            GreedyMeshFarDistance = Math.Max(0, data.GreedyMeshFarDistance);
            GreedyMeshFarDistanceSq = (double)GreedyMeshFarDistance * GreedyMeshFarDistance;
            GreedyMeshTextureGrad = data.GreedyMeshTextureGrad;
            RenderScale = Math.Clamp(data.RenderScale, 0.5f, 1.0f);
            // An unrecognised value means OpenGL rather than a parse failure, so
            // a hand-edited config cannot leave the client unable to start.
            string requestedRenderer = data.Renderer?.Trim() ?? "";
            Renderer =
                string.Equals(requestedRenderer, "vulkan", StringComparison.OrdinalIgnoreCase) ? "vulkan" :
                string.Equals(requestedRenderer, "auto", StringComparison.OrdinalIgnoreCase) ? "auto" :
                "opengl";
            GodRaysSampleCapEnabled = data.GodRaysSampleCap;
            Taa = data.Taa;
            TaaSharpness = Math.Clamp(data.TaaSharpness, 0f, 1f);
            TaaMipBias = Math.Clamp(data.TaaMipBias, -2f, 1f);
            TaaDebugView = Math.Max(0, data.TaaDebugView);
            TaaJitterDev = data.TaaJitterDev;
            // Unrecognised values degrade to the defaults rather than failing the file.
            string requestedAo = data.AmbientOcclusion?.Trim().ToLowerInvariant() ?? "";
            AmbientOcclusion = requestedAo is "vanilla" or "gtao" ? requestedAo : "auto";
            string requestedAoPreset = data.AmbientOcclusionPreset?.Trim().ToLowerInvariant() ?? "";
            AmbientOcclusionPreset = requestedAoPreset is "low" or "high" or "ultra" ? requestedAoPreset : "medium";
            AmbientOcclusionEnabled = data.AmbientOcclusionEnabled;
            AmbientOcclusionDebugView = data.AmbientOcclusionDebugView;
            MapPageCacheEnabled = data.MapPageCache;
            MapPageCacheMaxLayers = Math.Clamp(data.MapPageCacheMaxLayers, 16, 512);
            MapPageCacheBc7 = data.MapPageCacheBc7;
            RandomTickSliceEnabled = data.RandomTickSlice;
            WorldgenWorkStealingEnabled = data.WorldgenWorkStealing;
            ChunkReadPoolEnabled = data.ChunkReadPoolEnabled;
            ChunkReadPoolWorkers = Math.Clamp(data.ChunkReadPoolWorkers, 1, 8);
            ChunkDeserializeParallel = data.ChunkDeserializeParallel;
            ChunkDeserializeParallelMinY = Math.Clamp(data.ChunkDeserializeParallelMinY, 2, 64);
            ShaderPreprocessParallel = data.ShaderPreprocessParallel;
            AdaptiveRadiusEnabled = data.AdaptiveRadius;
            AdaptiveRadiusFloor = Math.Clamp(data.AdaptiveRadiusFloor, 1, 48);
            AdaptiveRadiusHighThreshold = Math.Max(1, data.AdaptiveRadiusHighThreshold);
            AdaptiveRadiusLowThreshold = Math.Max(1, data.AdaptiveRadiusLowThreshold);
            LaunchTaskBudgetEnabled = data.LaunchTaskBudgetEnabled;
            LaunchTaskBudgetMs = Math.Clamp(data.LaunchTaskBudgetMs, 1, 500);
            WorldgenWorkerPolicy = data.WorldgenWorkerPolicy ?? "auto";
            IndirectDrawEnabled = data.IndirectDraw;
            SimdCullingEnabled = data.SimdCulling;
            KometGuardEnabled = data.KometGuard;
        }
        catch (Exception)
        {
            // Corrupt file: ignore, use defaults, and do NOT write back.
            return;
        }

        // Successful parse: re-persist so the on-disk file always carries
        // the full field set at the (clamped) values actually in effect.
        Save();
    }

    /// <summary>
    /// Persist current state to optimum.json.
    /// </summary>
    public static void Save()
    {
        if (_configPath == null) return;

        var data = new OptimumConfigData
        {
            EntityShadowCull = EntityShadowCull,
            ShadowCullDistance = ShadowCullDistance,
            DynamicLightScale = DynamicLightScale,
            BackgroundFpsLimit = BackgroundFpsLimit,
            PreciseFramePacing = PreciseFramePacing,
            RepulsionGate = RepulsionGateEnabled,
            RepulsionDistance = RepulsionDistance,
            AnimBlockLod = AnimBlockLodEnabled,
            AnimBlockLodFrameBudget = AnimBlockLodFrameBudget,
            ShadowFarVegetation = ShadowFarVegetation,
            WeatherWindThrottle = WeatherWindThrottleEnabled,
            ParticleDistanceGate = ParticleDistanceGateEnabled,
            ChiselLod = ChiselLodEnabled,
            ChiselLodDistance = ChiselLodDistance,
            OcclusionCullingScale = OcclusionCullingScaleEnabled,
            BfsChunkVisibility = BfsChunkVisibilityEnabled,
            MeshPartPool = MeshPartPoolEnabled,
            ItemRenderInfoReuse = ItemRenderInfoReuseEnabled,
            DynamicLightCache = DynamicLightCacheEnabled,
            EntityLightBatch = EntityLightBatchEnabled,
            EntityShaderStateCache = EntityShaderStateCacheEnabled,
            EntityTesselationFrameBudget = EntityTesselationFrameBudget,
            EntityOutfitShapeCache = EntityOutfitShapeCacheEnabled,
            EntityOutfitAnimatorCache = EntityOutfitAnimatorCacheEnabled,
            EntityOutfitTexturePrewarm = EntityOutfitTexturePrewarmEnabled,
            GreedyMeshEnabled = GreedyMeshEnabled,
            GreedyMeshMaxMergeWidth = GreedyMeshMaxMergeWidth,
            GreedyMeshMaxMergeHeight = GreedyMeshMaxMergeHeight,
            GreedyMeshLightTolerance = GreedyMeshLightTolerance,
            GreedyMeshFarDistance = GreedyMeshFarDistance,
            GreedyMeshTextureGrad = GreedyMeshTextureGrad,
            RenderScale = RenderScale,
            Renderer = Renderer,
            GodRaysSampleCap = GodRaysSampleCapEnabled,
            Taa = Taa,
            TaaSharpness = TaaSharpness,
            TaaMipBias = TaaMipBias,
            TaaDebugView = TaaDebugView,
            TaaJitterDev = TaaJitterDev,
            AmbientOcclusion = AmbientOcclusion,
            AmbientOcclusionPreset = AmbientOcclusionPreset,
            AmbientOcclusionEnabled = AmbientOcclusionEnabled,
            AmbientOcclusionDebugView = AmbientOcclusionDebugView,
            MapPageCache = MapPageCacheEnabled,
            MapPageCacheMaxLayers = MapPageCacheMaxLayers,
            MapPageCacheBc7 = MapPageCacheBc7,
            RandomTickSlice = RandomTickSliceEnabled,
            WorldgenWorkStealing = WorldgenWorkStealingEnabled,
            ChunkReadPoolEnabled = ChunkReadPoolEnabled,
            ChunkReadPoolWorkers = ChunkReadPoolWorkers,
            ChunkDeserializeParallel = ChunkDeserializeParallel,
            ChunkDeserializeParallelMinY = ChunkDeserializeParallelMinY,
            ShaderPreprocessParallel = ShaderPreprocessParallel,
            AdaptiveRadius = AdaptiveRadiusEnabled,
            AdaptiveRadiusFloor = AdaptiveRadiusFloor,
            AdaptiveRadiusHighThreshold = AdaptiveRadiusHighThreshold,
            AdaptiveRadiusLowThreshold = AdaptiveRadiusLowThreshold,
            LaunchTaskBudgetEnabled = LaunchTaskBudgetEnabled,
            LaunchTaskBudgetMs = LaunchTaskBudgetMs,
            WorldgenWorkerPolicy = WorldgenWorkerPolicy,
            IndirectDraw = IndirectDrawEnabled,
            SimdCulling = SimdCullingEnabled,
            KometGuard = KometGuardEnabled,
        };

        try
        {
            string json = JsonSerializer.Serialize(data, _jsonOpts);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception)
        {
            // Disk full or permissions: silently skip.
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private sealed class ShaderCompatibilityState
    {
        public int SchemaVersion { get; set; }
        public List<string>? RewriterPrograms { get; set; }
        public bool ScanFailed { get; set; }
        public string? Fingerprint { get; set; }
        public List<string>? DisabledFeatures { get; set; }
    }
}

internal sealed class OptimumConfigData
{
    public bool EntityShadowCull { get; set; } = true;
    public int ShadowCullDistance { get; set; } = 80;
    public bool DynamicLightScale { get; set; } = true;
    public bool BackgroundFpsLimit { get; set; } = true;
    public bool PreciseFramePacing { get; set; } = true;
    public bool RepulsionGate { get; set; } = true;
    public int RepulsionDistance { get; set; } = 64;
    public bool AnimBlockLod { get; set; } = true;
    public int AnimBlockLodFrameBudget { get; set; } = 256;
    public bool ShadowFarVegetation { get; set; } = true;
    public bool WeatherWindThrottle { get; set; } = true;
    public bool ParticleDistanceGate { get; set; } = true;
    public bool ChiselLod { get; set; } = true;
    public int ChiselLodDistance { get; set; } = 48;
    public bool OcclusionCullingScale { get; set; } = true;
    public bool BfsChunkVisibility { get; set; } = true;
    public bool MeshPartPool { get; set; } = true;
    public bool ItemRenderInfoReuse { get; set; } = true;
    public bool DynamicLightCache { get; set; } = true;
    public bool EntityLightBatch { get; set; } = true;
    public bool EntityShaderStateCache { get; set; } = true;
    public int EntityTesselationFrameBudget { get; set; } = 2;
    public bool EntityOutfitShapeCache { get; set; } = false;
    public bool EntityOutfitAnimatorCache { get; set; } = false;
    public bool EntityOutfitTexturePrewarm { get; set; } = false;
    public bool GreedyMeshEnabled { get; set; } = false;
    public int GreedyMeshMaxMergeWidth { get; set; } = 8;
    public int GreedyMeshMaxMergeHeight { get; set; } = 8;
    public int GreedyMeshLightTolerance { get; set; } = 0;
    public int GreedyMeshFarDistance { get; set; } = 0;
    public bool GreedyMeshTextureGrad { get; set; } = true;
    public float RenderScale { get; set; } = 1.0f;
    public string Renderer { get; set; } = "opengl";
    public bool GodRaysSampleCap { get; set; } = false;
    public bool Taa { get; set; } = false;
    public float TaaSharpness { get; set; } = 0.2f;
    public float TaaMipBias { get; set; } = -0.5f;
    public int TaaDebugView { get; set; } = 0;
    public bool TaaJitterDev { get; set; } = false;
    public string AmbientOcclusion { get; set; } = "auto";
    public string AmbientOcclusionPreset { get; set; } = "medium";
    public bool AmbientOcclusionEnabled { get; set; } = true;
    public bool AmbientOcclusionDebugView { get; set; } = false;
    public bool MapPageCache { get; set; } = true;
    public int MapPageCacheMaxLayers { get; set; } = 128;
    public bool MapPageCacheBc7 { get; set; } = true;
    public bool RandomTickSlice { get; set; } = true;
    public bool WorldgenWorkStealing { get; set; } = false;
    public bool ChunkReadPoolEnabled { get; set; } = true;
    public int ChunkReadPoolWorkers { get; set; } = 4;
    public bool ChunkDeserializeParallel { get; set; } = true;
    public int ChunkDeserializeParallelMinY { get; set; } = 4;
    public bool ShaderPreprocessParallel { get; set; } = true;
    public bool AdaptiveRadius { get; set; } = true;
    public int AdaptiveRadiusFloor { get; set; } = 4;
    public int AdaptiveRadiusHighThreshold { get; set; } = 60;
    public int AdaptiveRadiusLowThreshold { get; set; } = 20;
    public bool LaunchTaskBudgetEnabled { get; set; } = false;
    public int LaunchTaskBudgetMs { get; set; } = 100;
    public string WorldgenWorkerPolicy { get; set; } = "auto";
    public bool IndirectDraw { get; set; } = false;
    public bool SimdCulling { get; set; } = true;
    public bool KometGuard { get; set; } = true;
}
