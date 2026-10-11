using HarmonyLib;
using Vintagestory.API.Common;

namespace VulkanStory.Game.Performance;

/// <summary>One optional game-side performance port: validated and installed on its own Harmony owner, gated per call by its setting.</summary>
/// <param name="Name">Stable feature identifier used for its Harmony owner and status line.</param>
/// <param name="Enabled">Reads the feature's switch from the current settings snapshot.</param>
/// <param name="Validate">Checks signatures and IL anchors without mutating anything.</param>
/// <param name="Install">Applies the feature's patches with the supplied owner-specific Harmony instance.</param>
/// <param name="Uninstall">Optional extra teardown (for example disarming a hook in a startup group) after its patches are removed.</param>
/// <param name="WorldReady">Optional work once a client world finished loading (owner thread).</param>
/// <param name="WorldLeft">Optional cleanup when a client world is left (owner thread).</param>
internal sealed record PerformanceFeature(string Name, System.Func<RendererSettings, bool> Enabled, Action Validate, Action<Harmony> Install,
    Action? Uninstall = null, Action<Vintagestory.API.Client.ICoreClientAPI>? WorldReady = null, Action? WorldLeft = null);

/// <summary>Optional performance patches installed after the renderer commits; a failing feature disables only itself.</summary>
/// <remarks>Unlike the startup transaction, nothing here is mandatory. Patches stay installed and fall back to the original path when their switch is off.</remarks>
internal static class PerformancePatchSet
{
    private const string OwnerPrefix = "vulkanstory.performance.";
    private static readonly object Gate = new();
    private static readonly List<(PerformanceFeature Feature, string? Refusal)> Features = new();
    private static RendererSettingsState? settings;

    /// <summary>Current requested settings, or null while the set is not installed (callers then take the original path).</summary>
    internal static RendererSettings? Current => Volatile.Read(ref settings)?.Settings;

    /// <summary>Whether a Harmony owner belongs to VulkanStory, so self-owned patches are not mistaken for third-party ones.</summary>
    internal static bool IsOwnOwner(string owner) => owner.StartsWith("vulkanstory.", StringComparison.Ordinal);

    /// <summary>Validates and installs each feature independently; failures are logged and reported in the status line.</summary>
    /// <param name="features">Features supplied by the version profile.</param>
    /// <param name="state">Session settings read on every patched call.</param>
    /// <param name="logger">Game logger receiving refusals.</param>
    internal static void Install(IEnumerable<PerformanceFeature> features, RendererSettingsState state, ILogger logger)
    {
        lock (Gate)
        {
            if (settings != null) throw new InvalidOperationException("Performance patches are already installed.");
            Volatile.Write(ref settings, state);
            foreach (var feature in features)
            {
                string? refusal = null;
                var harmony = new Harmony(OwnerPrefix + feature.Name);
                try
                {
                    feature.Validate();
                    feature.Install(harmony);
                }
                catch (Exception error)
                {
                    refusal = error.Message;
                    try { harmony.UnpatchAll(harmony.Id); feature.Uninstall?.Invoke(); } catch { /* Report the original refusal. */ }
                    logger.Warning("VulkanStory: performance feature {0} disabled: {1}", feature.Name, error.Message);
                }
                Features.Add((feature, refusal));
            }
        }
    }

    /// <summary>Removes every installed feature's patches and stops gating on settings.</summary>
    internal static void Remove()
    {
        lock (Gate)
        {
            var failures = new List<Exception>();
            foreach (var (feature, refusal) in Features)
            {
                if (refusal != null) continue;
                try { new Harmony(OwnerPrefix + feature.Name).UnpatchAll(OwnerPrefix + feature.Name); feature.Uninstall?.Invoke(); }
                catch (Exception error) { failures.Add(error); }
            }
            Features.Clear();
            Volatile.Write(ref settings, null);
            PerformanceCounters.Reset();
            if (failures.Count != 0) throw new AggregateException("Performance patch removal failed.", failures);
        }
    }

    /// <summary>Runs installed features' world-ready hooks; a failing hook is logged and never stops the world.</summary>
    internal static void NotifyWorldReady(Vintagestory.API.Client.ICoreClientAPI api)
    {
        foreach (var feature in Installed())
            try { feature.WorldReady?.Invoke(api); }
            catch (Exception error) { api.Logger.Warning("VulkanStory: performance feature {0} world setup failed: {1}", feature.Name, error.Message); }
    }

    /// <summary>Runs installed features' world-left hooks.</summary>
    internal static void NotifyWorldLeft()
    {
        foreach (var feature in Installed())
            try { feature.WorldLeft?.Invoke(); }
            catch { /* Cleanup is best effort; the next world rebuilds its state. */ }
    }

    private static PerformanceFeature[] Installed()
    {
        lock (Gate) return Features.Where(entry => entry.Refusal == null).Select(entry => entry.Feature).ToArray();
    }

    /// <summary>Whether a feature validated and installed; the settings panel greys out refused features through the status text.</summary>
    internal static bool IsInstalled(string name)
    {
        lock (Gate) return Features.Any(entry => entry.Feature.Name == name && entry.Refusal == null);
    }

    /// <summary>One status line per feature: active, off by setting, or refused with its reason.</summary>
    internal static string Status
    {
        get
        {
            lock (Gate)
            {
                if (Features.Count == 0) return "Performance patches: not installed";
                RendererSettings? current = Current;
                return "Performance patches:\n" + string.Join("\n", Features.Select(entry =>
                    "  " + entry.Feature.Name + ": " + (entry.Refusal != null ? "unavailable (" + entry.Refusal + ")" :
                        current != null && entry.Feature.Enabled(current) ? "active" : "off")));
            }
        }
    }
}

/// <summary>Process-wide counters the runtime diagnostics sample once per second.</summary>
internal static class PerformanceCounters
{
    internal static long ShadowFoliagePoolsSkipped, EntityShadowsSkipped, QueueSortsSkipped, QueueSortsRun, FallingMeshUploadsSaved, FallingMeshesBuilt,
        MeshPartsReused, MeshPartsAllocated, ParticleSpawnsGated, DroppedItemsCulled, OutfitShapeHits, OutfitShapeMisses, OutfitTexturesPrewarmed,
        ShapeParseHits, ShapeParseMisses,
        VisibilityBfsMismatches,
        CloudCoordinatesReused,
        ItemMergeSearches, ItemStacksMerged, ItemUnitsMerged, ItemEntitiesMerged, ParticleSpawnsBudgeted,
        OcclusionTests, OcclusionCulled, OcclusionPublished, EntityOcclusionTests, EntityOcclusionCulled, VisibilityWalks, VisibilityChunksVisited, VisibleChunks, VisibilityMismatches, VisibilityRayChunks, CullMicroseconds, CullsTimed, WorldgenColumns, WorldgenLeaseConflicts,
        WorldgenWorkerFaults, ParallelChunkReads;
    private static readonly System.Reflection.FieldInfo[] Fields = typeof(PerformanceCounters)
        .GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
        .Where(field => field.FieldType == typeof(long)).ToArray();
    internal static void Reset()
    {
        foreach (var field in Fields) field.SetValue(null, 0L);
    }
    /// <summary>Current totals keyed by counter name (camel case).</summary>
    internal static Dictionary<string, long> Snapshot() => Fields.ToDictionary(
        field => char.ToLowerInvariant(field.Name[0]) + field.Name[1..], field => (long)field.GetValue(null)!);
}
