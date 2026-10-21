using System.Reflection;

namespace VulkanStory.Game.Performance;

/// <summary>The 1.22.7 performance ports. Binding happens in each feature's validation, so a missing member refuses only that feature.</summary>
internal static class PerformanceFeatures
{
    internal static PerformanceFeature[] Create1227(Assembly essentials, Assembly survival) =>
    [
        ChunkVisibilityWalk.Create(),
        TerrainOcclusionCull.Create(),
        GpuChunkMeshing.Create(),
        EntityOcclusionCull.Create(),
        GpuEntityDraws.Create(essentials, survival),
        GpuJointPoses.Create(essentials, survival),
        ShadowFarFoliage.Create(),
        EntityShadowCull.Create(),
        ParticleDistanceGate.Create(),
        ParticleBudgets.Create(),
        GpuParticleVisuals.Create(),
        GpuParticleSimulation.Create(),
        EarlyItemMerge.Create(),
        DroppedItemCull.Create(essentials),
        UploadQueueSortSkip.Create(),
        MeshPartPool.Create(),
        ParallelChunkReads.Create(),
        WorldgenPipeline.Create(),
        GpuTerrainNoise.Create(),
        AnimationLinqFree.Create(),
        FallingBlockMeshFix.Create(essentials),
        OutfitShapeCache.Create(survival),
        GuiItemRenderInfoReuse.Create(),
        WeatherWindSampleCache.Create(essentials),
        AnimatedBlockLod.Create(),
        DynamicLightSelection.Create(),
        EntityRetessellationBudget.Create(essentials),
        PathfindingNodePool.Create(),
        EntityLightBatch.Create(),
        SimdFrustumTests.Create(),
        OutfitAnimatorCache.Create(survival),
        ParallelTerrainGeneration.Create(),
        PhysicsCollections.Create(),
        ChiselMeshLod.Create(survival),
        WorldMapPageCache.Create(),
        PerformanceTrace.Create(),
        TranslationLookupCache.Create(),
        AudioDecodeBuffers.Create(),
        ShapeParseCache.Create(survival),
        CloudCoordinateReuse.Create(essentials),
    ];
}
