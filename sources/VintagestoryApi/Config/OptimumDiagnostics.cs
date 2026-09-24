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

namespace Vintagestory.API.Config;

public static class OptimumDiagnostics
{
    // Stratum-ported optimization counters (server-side ports)
    private static long _serverTickCount;
    private static long _collisionFastPathHits;
    private static long _collisionFastPathSkips;
    private static long _pathNodePoolRents;
    private static long _pathNodePoolOverflows;
    private static long _collectEntitiesStridedSkips;
    private static long _mechPowerTickCount;

    public static long ServerTickCount => Interlocked.Read(ref _serverTickCount);
    public static long CollisionFastPathHits => Interlocked.Read(ref _collisionFastPathHits);
    public static long CollisionFastPathSkips => Interlocked.Read(ref _collisionFastPathSkips);
    public static long PathNodePoolRents => Interlocked.Read(ref _pathNodePoolRents);
    public static long PathNodePoolOverflows => Interlocked.Read(ref _pathNodePoolOverflows);
    public static long CollectEntitiesStridedSkips => Interlocked.Read(ref _collectEntitiesStridedSkips);
    public static long MechPowerTickCount => Interlocked.Read(ref _mechPowerTickCount);

    public static void RecordServerTick() => Interlocked.Increment(ref _serverTickCount);
    public static void RecordCollisionFastPathHit() => Interlocked.Increment(ref _collisionFastPathHits);
    public static void RecordCollisionFastPathSkip() => Interlocked.Increment(ref _collisionFastPathSkips);
    public static void RecordPathNodePoolRent() => Interlocked.Increment(ref _pathNodePoolRents);
    public static void RecordPathNodePoolOverflow() => Interlocked.Increment(ref _pathNodePoolOverflows);
    public static void RecordCollectEntitiesStridedSkip() => Interlocked.Increment(ref _collectEntitiesStridedSkips);
    public static void RecordMechPowerTick() => Interlocked.Increment(ref _mechPowerTickCount);

    public static void ResetStratumCounters()
    {
        Interlocked.Exchange(ref _serverTickCount, 0);
        Interlocked.Exchange(ref _collisionFastPathHits, 0);
        Interlocked.Exchange(ref _collisionFastPathSkips, 0);
        Interlocked.Exchange(ref _pathNodePoolRents, 0);
        Interlocked.Exchange(ref _pathNodePoolOverflows, 0);
        Interlocked.Exchange(ref _collectEntitiesStridedSkips, 0);
        Interlocked.Exchange(ref _mechPowerTickCount, 0);
    }

    public static string GetStratumSummary()
    {
        return $"[Optimum Stratum ports] ticks={ServerTickCount} collFP={CollisionFastPathHits}/{CollisionFastPathSkips} " +
               $"pathPool={PathNodePoolRents}/{PathNodePoolOverflows} collectSkips={CollectEntitiesStridedSkips} " +
               $"mechTicks={MechPowerTickCount}";
    }

    // Optimum-native optimization counters (client-side)
    private static long _chiselLodBlocks;
    private static long _chiselLodFullMeshContributions;
    private static long _chiselLodProxyMeshContributions;
    private static long _chiselLodFallbackMeshContributions;
    private static long _chiselLodFullTriangles;
    private static long _chiselLodProxyTriangles;
    private static long _chiselLodTesselationTicks;

    private static long _animBlockRuns;
    private static long _animBlockTicks;

    private static long _greedyMeshChunks;
    private static long _greedyMeshQuads;
    private static long _greedyMeshBlocksConsumed;

    private static long _entityLightBatchFrames;
    private static long _entityLightSamples;
    private static long _entityLightPreparedSamples;
    private static long _entityLightChunkGroups;
    private static long _entityLightFailedChunkGroups;
    private static long _entityLightCoordinateMismatches;
    private static long _entityLightChunkInvalidations;
    private static long _entityLightLockBatches;
    private static long _entityLightMaxBatchSize;
    private static long _entityLightTimedFrames;
    private static long _entityLightBatchTicks;

    private static long _entityShaderSegments;
    private static long _entityShaderUses;
    private static long _entityShaderUniformUploadsAvoided;
    private static long _entityShaderUboLookupsAvoided;

    public static void RecordEntityLightBatch(int samples, int preparedSamples, int chunkGroups, int failedChunkGroups, int lockBatches = 0, int maxBatchSize = 0, long elapsedTicks = 0)
    {
        Interlocked.Increment(ref _entityLightBatchFrames);
        Interlocked.Add(ref _entityLightSamples, samples);
        Interlocked.Add(ref _entityLightPreparedSamples, preparedSamples);
        Interlocked.Add(ref _entityLightChunkGroups, chunkGroups);
        Interlocked.Add(ref _entityLightFailedChunkGroups, failedChunkGroups);
        Interlocked.Add(ref _entityLightLockBatches, lockBatches);
        Interlocked.Add(ref _entityLightBatchTicks, elapsedTicks);
        if (elapsedTicks > 0)
        {
            Interlocked.Increment(ref _entityLightTimedFrames);
        }
        long observed = Volatile.Read(ref _entityLightMaxBatchSize);
        while (maxBatchSize > observed)
        {
            long previous = Interlocked.CompareExchange(ref _entityLightMaxBatchSize, maxBatchSize, observed);
            if (previous == observed)
            {
                break;
            }
            observed = previous;
        }
    }

    public static void RecordEntityLightCoordinateMismatch()
    {
        Interlocked.Increment(ref _entityLightCoordinateMismatches);
    }

    public static void RecordEntityLightChunkInvalidation()
    {
        Interlocked.Increment(ref _entityLightChunkInvalidations);
    }

    public static void RecordEntityShaderSegment(int useCount)
    {
        Interlocked.Increment(ref _entityShaderSegments);
        Interlocked.Add(ref _entityShaderUses, useCount);
        int sharedCallsAvoided = Math.Max(0, useCount - 1);
        Interlocked.Add(ref _entityShaderUniformUploadsAvoided, sharedCallsAvoided * 2L);
        Interlocked.Add(ref _entityShaderUboLookupsAvoided, sharedCallsAvoided);
    }

    public static void ResetEntityRenderP0()
    {
        Interlocked.Exchange(ref _entityLightBatchFrames, 0);
        Interlocked.Exchange(ref _entityLightSamples, 0);
        Interlocked.Exchange(ref _entityLightPreparedSamples, 0);
        Interlocked.Exchange(ref _entityLightChunkGroups, 0);
        Interlocked.Exchange(ref _entityLightFailedChunkGroups, 0);
        Interlocked.Exchange(ref _entityLightCoordinateMismatches, 0);
        Interlocked.Exchange(ref _entityLightChunkInvalidations, 0);
        Interlocked.Exchange(ref _entityLightLockBatches, 0);
        Interlocked.Exchange(ref _entityLightMaxBatchSize, 0);
        Interlocked.Exchange(ref _entityLightTimedFrames, 0);
        Interlocked.Exchange(ref _entityLightBatchTicks, 0);
        Interlocked.Exchange(ref _entityShaderSegments, 0);
        Interlocked.Exchange(ref _entityShaderUses, 0);
        Interlocked.Exchange(ref _entityShaderUniformUploadsAvoided, 0);
        Interlocked.Exchange(ref _entityShaderUboLookupsAvoided, 0);
    }

    /// <summary>
    /// One call per BuildBlockPolygons invocation from OptimumGreedyMeshEmitter
    /// (bug B7). quads/blocksConsumed are 0 when the chunk had no eligible
    /// interior faces this pass.
    /// </summary>
    public static void RecordGreedyMeshChunk(int quads, int blocksConsumed)
    {
        Interlocked.Increment(ref _greedyMeshChunks);
        Interlocked.Add(ref _greedyMeshQuads, quads);
        Interlocked.Add(ref _greedyMeshBlocksConsumed, blocksConsumed);
    }

    public static void ResetGreedyMesh()
    {
        Interlocked.Exchange(ref _greedyMeshChunks, 0);
        Interlocked.Exchange(ref _greedyMeshQuads, 0);
        Interlocked.Exchange(ref _greedyMeshBlocksConsumed, 0);
    }

    public static string GetGreedyMeshSummary()
    {
        long chunks = Interlocked.Read(ref _greedyMeshChunks);
        long quads = Interlocked.Read(ref _greedyMeshQuads);
        long blocksConsumed = Interlocked.Read(ref _greedyMeshBlocksConsumed);
        double blocksPerQuad = quads == 0 ? 0 : (double)blocksConsumed / quads;

        // The memory the merge actually removed from the chunk pools:
        // each vanilla quad a merge absorbed would have been one FaceData
        // struct (64 bytes, std430: 3x vec3 padded + uv + uvSize + ivec4
        // flags + colormapData) plus 6 ints of indices (24 bytes) on the
        // SSBO path. The GL 3.3 vertex path is in the same ballpark
        // (4 verts x ~32 bytes + indices), and the emitter only merges on
        // the SSBO path anyway, so one number is honest enough here.
        long quadsSaved = blocksConsumed - quads;
        double poolMBSaved = quadsSaved * 88.0 / (1024.0 * 1024.0);

        return $"Optimum greedy mesh: enabled={OptimumConfig.GreedyMeshEnabled}, maxMergeWidth={OptimumConfig.GreedyMeshMaxMergeWidth}, maxMergeHeight={OptimumConfig.GreedyMeshMaxMergeHeight}, lightTolerance={OptimumConfig.GreedyMeshLightTolerance}, farDistance={OptimumConfig.GreedyMeshFarDistance}, chunks={chunks}, quads={quads}, blocksConsumed={blocksConsumed}, blocksPerQuad={blocksPerQuad:0.00}, quadsSaved={quadsSaved}, estPoolMBSaved={poolMBSaved:0.00}";
    }

    // --- Issue #73 mesh-construction profiler (diagnostic only) ---
    // Per NowProcessChunk call: wall-clock ticks and thread-allocated bytes,
    // split into the tessellation phase (BuildBlockPolygons: the per-block
    // loop) and the finalize phase (populateTesselatedChunkPart +
    // MergeTesselatedChunkParts: the part-object + clone allocations). Runs on
    // the tesselation thread. Enabled by OPTIMUM_MESH_PROFILE=1 via
    // OptimumConfig.MeshProfileEnabled.
    private static long _meshChunks;
    private static long _meshTessTicks;
    private static long _meshFinalizeTicks;
    private static long _meshTotalTicks;
    private static long _meshTessAllocBytes;
    private static long _meshFinalizeAllocBytes;
    private static long _meshPartsCreated;
    private static long _meshCloneAllocBytes;
    private static long _meshCloneSmallVerts;
    private static long _meshCloneLargeVerts;
    private static long _meshSinceLog;

    public static void RecordMeshClone(long bytes, int smallVerts, int largeVerts)
    {
        Interlocked.Add(ref _meshCloneAllocBytes, bytes);
        Interlocked.Add(ref _meshCloneSmallVerts, smallVerts);
        Interlocked.Add(ref _meshCloneLargeVerts, largeVerts);
    }
    public static volatile bool MeshProfileEnabled;
    public const int MeshProfileLogEvery = 200;

    public static void RecordMeshTess(long tessTicks, long tessAllocBytes)
    {
        Interlocked.Add(ref _meshTessTicks, tessTicks);
        Interlocked.Add(ref _meshTessAllocBytes, tessAllocBytes);
    }

    /// <summary>Returns true when a mesh-profile summary should be logged now.</summary>
    public static bool RecordMeshFinalize(long finalizeTicks, long finalizeAllocBytes, int partsCreated, long totalTicks)
    {
        Interlocked.Increment(ref _meshChunks);
        Interlocked.Add(ref _meshFinalizeTicks, finalizeTicks);
        Interlocked.Add(ref _meshFinalizeAllocBytes, finalizeAllocBytes);
        Interlocked.Add(ref _meshPartsCreated, partsCreated);
        Interlocked.Add(ref _meshTotalTicks, totalTicks);
        if (!MeshProfileEnabled) return false;
        return Interlocked.Increment(ref _meshSinceLog) % MeshProfileLogEvery == 0;
    }

    public static void ResetMeshProfile()
    {
        Interlocked.Exchange(ref _meshChunks, 0);
        Interlocked.Exchange(ref _meshTessTicks, 0);
        Interlocked.Exchange(ref _meshFinalizeTicks, 0);
        Interlocked.Exchange(ref _meshTotalTicks, 0);
        Interlocked.Exchange(ref _meshTessAllocBytes, 0);
        Interlocked.Exchange(ref _meshFinalizeAllocBytes, 0);
        Interlocked.Exchange(ref _meshCloneAllocBytes, 0);
        Interlocked.Exchange(ref _meshCloneSmallVerts, 0);
        Interlocked.Exchange(ref _meshCloneLargeVerts, 0);
        Interlocked.Exchange(ref _meshPartsCreated, 0);
    }

    public static string GetMeshProfileSummary()
    {
        long chunks = Interlocked.Read(ref _meshChunks);
        if (chunks == 0) return "Optimum mesh profile: no chunks tesselated";
        double f = System.Diagnostics.Stopwatch.Frequency / 1000.0;
        long tessTicks = Interlocked.Read(ref _meshTessTicks);
        long finTicks = Interlocked.Read(ref _meshFinalizeTicks);
        long totalTicks = tessTicks + finTicks;
        long tessAlloc = Interlocked.Read(ref _meshTessAllocBytes);
        long finAlloc = Interlocked.Read(ref _meshFinalizeAllocBytes);
        long cloneAlloc = Interlocked.Read(ref _meshCloneAllocBytes);
        long smallV = Interlocked.Read(ref _meshCloneSmallVerts);
        long largeV = Interlocked.Read(ref _meshCloneLargeVerts);
        long parts = Interlocked.Read(ref _meshPartsCreated);
        double totalAllocMB = (tessAlloc + finAlloc) / (1024.0 * 1024.0);
        return $"Optimum mesh profile: chunks={chunks}"
            + $", total meanMs={totalTicks / f / chunks:0.000}"
            + $", tess meanMs={tessTicks / f / chunks:0.000}"
            + $", finalize meanMs={finTicks / f / chunks:0.000}"
            + $", allocPerChunk={(tessAlloc + finAlloc) / (double)chunks:0} B"
            + $" (tess={tessAlloc / (double)chunks:0} B, finalize={finAlloc / (double)chunks:0} B, ofWhichClone={cloneAlloc / (double)chunks:0} B)"
            + $", cloneVertsPerChunk small={smallV / (double)chunks:0}/large={largeV / (double)chunks:0}"
            + $", partsPerChunk={parts / (double)chunks:0.0}"
            + $", totalAllocMB={totalAllocMB:0.0}";
    }

    // --- Issue #74 item-render profiler (diagnostic only) ---
    // Counts GetItemStackRenderInfo calls (one per visible item slot per frame) and
    // the thread-allocated bytes they cost, logged every ItemRenderProfileLogEvery
    // frames. Runs on the render thread. Enabled by OPTIMUM_ITEM_PROFILE=1. This
    // measures whether per-slot item render info is a real per-frame CPU/GC hotspot
    // (e.g. inventory or chest open) before optimizing it.
    public static volatile bool ItemRenderProfileEnabled;
    public const int ItemRenderProfileLogEvery = 120;
    private static long _itemRenderCalls;
    private static long _itemRenderAllocBytes;
    private static long _itemRenderFrames;
    private static long _itemRenderSinceLog;
    private static long _itemRenderCallsThisFrame;
    private static long _itemRenderMaxCallsPerFrame;

    /// <summary>Record one GetItemStackRenderInfo call and the bytes it allocated.</summary>
    public static void RecordItemRender(long allocBytes)
    {
        _itemRenderCalls++;
        _itemRenderCallsThisFrame++;
        _itemRenderAllocBytes += allocBytes;
    }

    /// <summary>Call once per rendered frame; returns true when a summary is due.</summary>
    public static bool ItemRenderEndFrame()
    {
        _itemRenderFrames++;
        if (_itemRenderCallsThisFrame > _itemRenderMaxCallsPerFrame)
            _itemRenderMaxCallsPerFrame = _itemRenderCallsThisFrame;
        _itemRenderCallsThisFrame = 0;
        if (!ItemRenderProfileEnabled) return false;
        _itemRenderSinceLog++;
        return _itemRenderSinceLog % ItemRenderProfileLogEvery == 0;
    }

    public static string GetItemRenderProfileSummary()
    {
        long frames = _itemRenderFrames;
        if (frames == 0) return "Optimum item-render profile: no frames";
        return $"Optimum item-render profile: frames={frames}"
            + $", callsPerFrame={_itemRenderCalls / (double)frames:0.0}"
            + $", maxCallsPerFrame={_itemRenderMaxCallsPerFrame}"
            + $", allocPerFrame={_itemRenderAllocBytes / (double)frames:0} B"
            + $", allocPerCall={(_itemRenderCalls == 0 ? 0 : _itemRenderAllocBytes / (double)_itemRenderCalls):0} B"
            + $", totalCalls={_itemRenderCalls}";
    }

    public static void RecordChiselLod(int fullTriangles, int proxyTriangles, bool fallback, long elapsedTicks)
    {
        Interlocked.Increment(ref _chiselLodBlocks);
        Interlocked.Increment(ref _chiselLodFullMeshContributions);
        Interlocked.Add(ref _chiselLodFullTriangles, fullTriangles);
        Interlocked.Add(ref _chiselLodTesselationTicks, elapsedTicks);

        if (fallback)
        {
            Interlocked.Increment(ref _chiselLodFallbackMeshContributions);
        }
        else
        {
            Interlocked.Increment(ref _chiselLodProxyMeshContributions);
            Interlocked.Add(ref _chiselLodProxyTriangles, proxyTriangles);
        }
    }

    public static void ResetChiselLod()
    {
        Interlocked.Exchange(ref _chiselLodBlocks, 0);
        Interlocked.Exchange(ref _chiselLodFullMeshContributions, 0);
        Interlocked.Exchange(ref _chiselLodProxyMeshContributions, 0);
        Interlocked.Exchange(ref _chiselLodFallbackMeshContributions, 0);
        Interlocked.Exchange(ref _chiselLodFullTriangles, 0);
        Interlocked.Exchange(ref _chiselLodProxyTriangles, 0);
        Interlocked.Exchange(ref _chiselLodTesselationTicks, 0);
    }

    public static string GetChiselLodSummary()
    {
        long blocks = Interlocked.Read(ref _chiselLodBlocks);
        long fullMeshes = Interlocked.Read(ref _chiselLodFullMeshContributions);
        long proxyMeshes = Interlocked.Read(ref _chiselLodProxyMeshContributions);
        long fallbackMeshes = Interlocked.Read(ref _chiselLodFallbackMeshContributions);
        long fullTriangles = Interlocked.Read(ref _chiselLodFullTriangles);
        long proxyTriangles = Interlocked.Read(ref _chiselLodProxyTriangles);
        long ticks = Interlocked.Read(ref _chiselLodTesselationTicks);

        double proxyRate = blocks == 0 ? 0 : (double)proxyMeshes * 100.0 / blocks;
        double elapsedMs = ticks * 1000.0 / Stopwatch.Frequency;

        return $"Optimum chisel LOD: blocks={blocks}, fullMeshes={fullMeshes}, proxyMeshes={proxyMeshes}, proxyRate={proxyRate:0.0}%, fallbackMeshes={fallbackMeshes}, fullTriangles={fullTriangles}, proxyTriangles={proxyTriangles}, microblockTesselationMs={elapsedMs:0.###}";
    }

    // Chisel LOD shadow-pass cull diagnostics (OptimumApiBridge.InFrustumShadowPass, added
    // 92a4c72). Reset once per frame by the stutter watch below, so the summary always
    // reflects only the current frame's cost rather than a lifetime total.
    private static long _chiselShadowCullCalls;
    private static long _chiselShadowCullTicks;

    public static void RecordChiselShadowCull(long elapsedTicks)
    {
        Interlocked.Increment(ref _chiselShadowCullCalls);
        Interlocked.Add(ref _chiselShadowCullTicks, elapsedTicks);
    }

    public static void ResetChiselShadowCull()
    {
        Interlocked.Exchange(ref _chiselShadowCullCalls, 0);
        Interlocked.Exchange(ref _chiselShadowCullTicks, 0);
    }

    public static string GetChiselShadowCullSummary()
    {
        long calls = Interlocked.Read(ref _chiselShadowCullCalls);
        long ticks = Interlocked.Read(ref _chiselShadowCullTicks);
        double elapsedMs = ticks * 1000.0 / Stopwatch.Frequency;
        return $"Optimum chisel shadow cull: calls={calls}, elapsedMs={elapsedMs:0.###}";
    }

    /// <summary>
    /// Opt-in per-frame stutter diagnostic. When enabled, ClientPlatformWindows logs
    /// <see cref="BuildStutterReport"/> to the client log for every frame at or above
    /// <see cref="StutterWatchThresholdMs"/>, attributing the frame to Optimum's own
    /// per-frame subsystems so a slow frame can be traced without an external profiler.
    /// Session-only: not persisted, toggled via the ".optimum stutterwatch" command.
    /// </summary>
    public static volatile bool StutterWatchEnabled;
    public static volatile int StutterWatchThresholdMs = 25;

    /// <summary>
    /// Attributes ONLY the Optimum-owned per-frame subsystems below (each counter is reset
    /// every frame by <see cref="ResetPerFrameStutterCounters"/>, so every number here is
    /// this frame's cost, not a lifetime total). It cannot see vanilla's own baseline render
    /// cost, GC pauses, disk I/O, or GPU/driver stalls - a stutter with none of these
    /// subsystems standing out did NOT come from Optimum's own code, and needs vanilla's
    /// own frame profiler (".debug logticks &lt;ms&gt;", logs the full vanilla+Optimum stage
    /// breakdown to the client log) or an external trace to localize.
    /// </summary>
    public static string BuildStutterReport(double frameMs)
    {
        var sb = new StringBuilder();
        sb.Append($"[Optimum stutter-watch] frame took {frameMs:0.##} ms (threshold {StutterWatchThresholdMs} ms) - Optimum-owned subsystems only, see \".debug logticks\" for the full vanilla+Optimum breakdown:");
        sb.Append("\n  ").Append(GetChiselShadowCullSummary());
        sb.Append("\n  ").Append(GetChiselLodSummary());
        sb.Append("\n  ").Append(GetAnimBlockSummary());
        sb.Append("\n  ").Append(GetGreedyMeshSummary());
        sb.Append("\n  ").Append(GetChunkRenderSummary());
        sb.Append("\n  ").Append(GetChunkUploadSummary());
        sb.Append("\n  ").Append(GetEntityAnimationSummary());
        {
            var (hits, skips) = EntityTesselationBudget.Snapshot();
            sb.Append($"\n  Optimum entity tesselation budget: tesselated={hits}, deferred={skips}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resets every counter fed into <see cref="BuildStutterReport"/>. Called once per frame
    /// by ClientPlatformWindows when stutter-watch is enabled, so each counter always
    /// reflects only the frame that just ended rather than an accumulating lifetime total.
    /// </summary>
    public static void ResetPerFrameStutterCounters()
    {
        ResetChiselShadowCull();
        ResetChiselLod();
        ResetAnimBlock();
        ResetGreedyMesh();
        ResetChunkRenderFrame();
        ResetChunkUpload();
        ResetEntityAnimation();
        EntityTesselationBudget.Reset();
    }

    private const int EntityAnimationBandCount = 5;
    private const int EntityAnimationUnknownBand = 4;
    private static readonly string[] EntityAnimationBandNames = { "player", "near", "mid", "far", "unknown" };
    private static readonly long[] _entityAnimationManagerCalls = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationManagerSkips = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationHeadUpdates = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationPoseUpdates = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationPoseSkips = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationMatrixBuilds = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationMatrixSkips = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationLoopingSoundPasses = new long[EntityAnimationBandCount];
    private static long _entityAnimationMatrixTicks;

    [ThreadStatic]
    private static bool _entityAnimationContextActive;

    [ThreadStatic]
    private static int _entityAnimationBand;

    /// <summary>
    /// Sets the distance band for the entity whose animation manager runs next.
    /// Stutter-watch owns this context, so normal frames perform no distance work.
    /// </summary>
    public static void BeginEntityAnimationContext(bool isPlayer, double distanceSq)
    {
        if (!StutterWatchEnabled) return;

        _entityAnimationBand = isPlayer ? 0 : distanceSq <= 24.0 * 24.0 ? 1 : distanceSq <= 48.0 * 48.0 ? 2 : 3;
        _entityAnimationContextActive = true;
    }

    public static void EndEntityAnimationContext()
    {
        _entityAnimationContextActive = false;
    }

    public static void RecordEntityAnimationManagerCall() => AddEntityAnimationCount(_entityAnimationManagerCalls);
    public static void RecordEntityAnimationManagerSkip() => AddEntityAnimationCount(_entityAnimationManagerSkips);
    public static void RecordEntityAnimationHeadUpdate() => AddEntityAnimationCount(_entityAnimationHeadUpdates);
    public static void RecordEntityAnimationPoseUpdate() => AddEntityAnimationCount(_entityAnimationPoseUpdates);
    public static void RecordEntityAnimationPoseSkip() => AddEntityAnimationCount(_entityAnimationPoseSkips);
    public static void RecordEntityAnimationMatrixBuild() => AddEntityAnimationCount(_entityAnimationMatrixBuilds);

    public static void RecordEntityAnimationMatrixTicks(long elapsedTicks)
    {
        Interlocked.Add(ref _entityAnimationMatrixTicks, elapsedTicks);
    }

    public static void RecordEntityAnimationMatrixSkip() => AddEntityAnimationCount(_entityAnimationMatrixSkips);
    public static void RecordEntityAnimationLoopingSoundPass() => AddEntityAnimationCount(_entityAnimationLoopingSoundPasses);

    private static void AddEntityAnimationCount(long[] counts)
    {
        if (!StutterWatchEnabled) return;
        int band = _entityAnimationContextActive ? _entityAnimationBand : EntityAnimationUnknownBand;
        Interlocked.Increment(ref counts[band]);
    }

    public static void ResetEntityAnimation()
    {
        Array.Clear(_entityAnimationManagerCalls);
        Array.Clear(_entityAnimationManagerSkips);
        Array.Clear(_entityAnimationHeadUpdates);
        Array.Clear(_entityAnimationPoseUpdates);
        Array.Clear(_entityAnimationPoseSkips);
        Array.Clear(_entityAnimationMatrixBuilds);
        Array.Clear(_entityAnimationMatrixSkips);
        Array.Clear(_entityAnimationLoopingSoundPasses);
        Interlocked.Exchange(ref _entityAnimationMatrixTicks, 0);
    }

    public static string GetEntityAnimationSummary()
    {
        long managerCalls = SumEntityAnimationCounts(_entityAnimationManagerCalls);
        long managerSkips = SumEntityAnimationCounts(_entityAnimationManagerSkips);
        long headUpdates = SumEntityAnimationCounts(_entityAnimationHeadUpdates);
        long poseUpdates = SumEntityAnimationCounts(_entityAnimationPoseUpdates);
        long poseSkips = SumEntityAnimationCounts(_entityAnimationPoseSkips);
        long matrixBuilds = SumEntityAnimationCounts(_entityAnimationMatrixBuilds);
        long matrixSkips = SumEntityAnimationCounts(_entityAnimationMatrixSkips);
        long loopingSoundPasses = SumEntityAnimationCounts(_entityAnimationLoopingSoundPasses);
        double matrixMs = Interlocked.Read(ref _entityAnimationMatrixTicks) * 1000.0 / Stopwatch.Frequency;

        var sb = new StringBuilder();
        sb.Append($"Optimum entity animation: managerCalls={managerCalls}, managerSkips={managerSkips}, headUpdates={headUpdates}, poseUpdates={poseUpdates}, poseSkips={poseSkips}, matrixBuilds={matrixBuilds}, matrixSkips={matrixSkips}, loopingSoundPasses={loopingSoundPasses}, matrixMs={matrixMs:0.###}, bands=");
        for (int i = 0; i < EntityAnimationBandCount; i++)
        {
            if (i > 0) sb.Append(';');
            sb.Append(EntityAnimationBandNames[i]).Append(':')
                .Append(Interlocked.Read(ref _entityAnimationManagerCalls[i])).Append('/')
                .Append(Interlocked.Read(ref _entityAnimationPoseUpdates[i])).Append('/')
                .Append(Interlocked.Read(ref _entityAnimationPoseSkips[i])).Append('/')
                .Append(Interlocked.Read(ref _entityAnimationMatrixBuilds[i])).Append('/')
                .Append(Interlocked.Read(ref _entityAnimationMatrixSkips[i]));
        }

        return sb.ToString();
    }

    private static long SumEntityAnimationCounts(long[] counts)
    {
        long total = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            total += Interlocked.Read(ref counts[i]);
        }

        return total;
    }

    // Entity re-tesselation frame budget (EntityTesselationFrameBudget). Reset once per frame
    // from SystemRenderEntities.OnBeforeRender; consumed by EntityShapeRenderer.BeforeRender
    // before calling TesselateShape() on an entity that already has a mesh (first tesselation
    // is never gated). Interlocked since entity rendering only runs on the render thread today,
    // but the check-and-decrement must still be atomic against itself for correctness.
    private static int _entityTesselationBudgetRemaining;

    public static void ResetEntityTesselationBudget()
    {
        Volatile.Write(ref _entityTesselationBudgetRemaining, OptimumConfig.EntityTesselationFrameBudget);
    }

    /// <summary>
    /// Returns true if a re-tesselation is allowed this frame (and consumes one unit of
    /// budget), false if the frame's budget is already spent. A budget of 0 disables the cap
    /// entirely (always returns true).
    /// </summary>
    public static bool TryConsumeEntityTesselationBudget()
    {
        if (OptimumConfig.EntityTesselationFrameBudget <= 0) return true;

        while (true)
        {
            int current = Volatile.Read(ref _entityTesselationBudgetRemaining);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _entityTesselationBudgetRemaining, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Accumulates the animator.OnFrame cost for animated blocks that actually
    /// ran this frame (near tier, or mid tier on a due frame). Two timestamp
    /// reads per call is noise next to the OnFrame work itself.
    /// </summary>
    public static void RecordAnimBlockTicks(long elapsedTicks)
    {
        Interlocked.Increment(ref _animBlockRuns);
        Interlocked.Add(ref _animBlockTicks, elapsedTicks);
    }

    public static void ResetAnimBlock()
    {
        Interlocked.Exchange(ref _animBlockRuns, 0);
        Interlocked.Exchange(ref _animBlockTicks, 0);
    }

    public static string GetAnimBlockSummary()
    {
        long runs = Interlocked.Read(ref _animBlockRuns);
        long ticks = Interlocked.Read(ref _animBlockTicks);
        double elapsedMs = ticks * 1000.0 / Stopwatch.Frequency;

        return $"Optimum anim block LOD: runs={runs}, animatorMs={elapsedMs:0.###}";
    }

    /// <summary>
    /// Lock-free hit/skip pair for one optimization. Hit means the full
    /// (vanilla-equivalent) path ran; skip means the optimization's fast
    /// path fired instead. A single Interlocked.Increment per call, no
    /// allocation, safe to call from a per-frame or per-entity hot path.
    /// </summary>
    public sealed class HitSkipCounter
    {
        private long _hits;
        private long _skips;

        public void Hit() => Interlocked.Increment(ref _hits);
        public void Skip() => Interlocked.Increment(ref _skips);

        public void Reset()
        {
            Interlocked.Exchange(ref _hits, 0);
            Interlocked.Exchange(ref _skips, 0);
        }

        public (long Hits, long Skips) Snapshot() => (Interlocked.Read(ref _hits), Interlocked.Read(ref _skips));
    }

    public static readonly HitSkipCounter EntityShadowCull = new();
    public static readonly HitSkipCounter EntityRenderCull = new();
    public static readonly HitSkipCounter DynamicLightRadius = new();
    public static readonly HitSkipCounter BackgroundFpsLimiter = new();
    public static readonly HitSkipCounter PreciseFramePacing = new();
    public static readonly HitSkipCounter HudEntityNameTags = new();
    public static readonly HitSkipCounter ShadowFarVegetation = new();
    public static readonly HitSkipCounter RepulseAgents = new();
    public static readonly HitSkipCounter WeatherWindThrottle = new();
    public static readonly HitSkipCounter AnimBlockLodNear = new();
    public static readonly HitSkipCounter AnimBlockLodMid = new();
    public static readonly HitSkipCounter AnimBlockLodFar = new();
    public static readonly HitSkipCounter AnimBlockLodDeferred = new();
    public static readonly HitSkipCounter ParticleDistanceGate = new();
    public static readonly HitSkipCounter OcclusionCullingScale = new();
    public static readonly HitSkipCounter BfsChunkVisibility = new();
    public static readonly HitSkipCounter DynamicLightCache = new();
    public static readonly HitSkipCounter ChunkUploadSort = new();
    public static readonly HitSkipCounter EntityLightBatch = new();
    public static readonly HitSkipCounter EntityShaderStateCache = new();
    public static readonly HitSkipCounter EntityTesselationBudget = new();
    public static readonly HitSkipCounter EntityOutfitShapeCache = new();
    public static readonly HitSkipCounter EntityOutfitAnimatorCache = new();
    // Issue #73: hit = a CustomMeshDataPart clone served from the pool, skip = a
    // fresh allocation (cold pool, non-poolable part, or pool inactive).
    public static readonly HitSkipCounter MeshPartPool = new();

    /// <summary>
    /// Every hit/skip counter above, keyed by name, for .optimum status and
    /// the coverage test that keeps this list honest. Declared after the
    /// individual fields so their static initializers have already run.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, HitSkipCounter> Counters = new Dictionary<string, HitSkipCounter>
    {
        [nameof(EntityShadowCull)] = EntityShadowCull,
        [nameof(EntityRenderCull)] = EntityRenderCull,
        [nameof(DynamicLightRadius)] = DynamicLightRadius,
        [nameof(BackgroundFpsLimiter)] = BackgroundFpsLimiter,
        [nameof(PreciseFramePacing)] = PreciseFramePacing,
        [nameof(HudEntityNameTags)] = HudEntityNameTags,
        [nameof(ShadowFarVegetation)] = ShadowFarVegetation,
        [nameof(RepulseAgents)] = RepulseAgents,
        [nameof(WeatherWindThrottle)] = WeatherWindThrottle,
        [nameof(AnimBlockLodNear)] = AnimBlockLodNear,
        [nameof(AnimBlockLodMid)] = AnimBlockLodMid,
        [nameof(AnimBlockLodFar)] = AnimBlockLodFar,
        [nameof(AnimBlockLodDeferred)] = AnimBlockLodDeferred,
        [nameof(ParticleDistanceGate)] = ParticleDistanceGate,
        [nameof(OcclusionCullingScale)] = OcclusionCullingScale,
        [nameof(BfsChunkVisibility)] = BfsChunkVisibility,
        [nameof(DynamicLightCache)] = DynamicLightCache,
        [nameof(ChunkUploadSort)] = ChunkUploadSort,
        [nameof(EntityLightBatch)] = EntityLightBatch,
        [nameof(EntityShaderStateCache)] = EntityShaderStateCache,
        [nameof(EntityTesselationBudget)] = EntityTesselationBudget,
        [nameof(EntityOutfitShapeCache)] = EntityOutfitShapeCache,
        [nameof(EntityOutfitAnimatorCache)] = EntityOutfitAnimatorCache,
        [nameof(MeshPartPool)] = MeshPartPool,
    };

    public static void ResetAllCounters()
    {
        foreach (var counter in Counters.Values)
        {
            counter.Reset();
        }
        ResetChiselLod();
        ResetAnimBlock();
        ResetGreedyMesh();
        ResetEntityRenderP0();
        ResetTessellation();
        ResetEntityAnimation();
        ResetGameLaunchTasks();
        ResetWorldgenPassTiming();
    }

    // Game launch task diagnostics. The client intentionally runs at most one
    // launch task per frame. These counters measure the current policy before
    // any pacing change considers queue depth or task duration.
    private static long _gameLaunchTaskFrames;
    private static long _gameLaunchTaskCount;
    private static long _gameLaunchTaskTicks;
    private static long _gameLaunchTaskMaxTicks;
    private static long _gameLaunchTaskPeakDepth;

    public static void RecordGameLaunchTask(long elapsedTicks, int queueDepth)
    {
        Interlocked.Increment(ref _gameLaunchTaskCount);
        Interlocked.Add(ref _gameLaunchTaskTicks, elapsedTicks);
        UpdatePeak(ref _gameLaunchTaskMaxTicks, elapsedTicks);
        UpdatePeak(ref _gameLaunchTaskPeakDepth, queueDepth);
    }

    /// <summary>
    /// Called once per frame that processes at least one launch task.
    /// Separated from RecordGameLaunchTask so multi-task frames count
    /// as one frame but multiple tasks.
    /// </summary>
    public static void RecordGameLaunchTaskFrame()
    {
        Interlocked.Increment(ref _gameLaunchTaskFrames);
    }

    public static void ResetGameLaunchTasks()
    {
        Interlocked.Exchange(ref _gameLaunchTaskFrames, 0);
        Interlocked.Exchange(ref _gameLaunchTaskCount, 0);
        Interlocked.Exchange(ref _gameLaunchTaskTicks, 0);
        Interlocked.Exchange(ref _gameLaunchTaskMaxTicks, 0);
        Interlocked.Exchange(ref _gameLaunchTaskPeakDepth, 0);
    }

    public static string GetGameLaunchTaskSummary()
    {
        long frames = Interlocked.Read(ref _gameLaunchTaskFrames);
        long count = Interlocked.Read(ref _gameLaunchTaskCount);
        long totalTicks = Interlocked.Read(ref _gameLaunchTaskTicks);
        long maxTicks = Interlocked.Read(ref _gameLaunchTaskMaxTicks);
        double totalMs = totalTicks * 1000.0 / Stopwatch.Frequency;
        double averageMs = count == 0 ? 0 : totalMs / count;
        double maxMs = maxTicks * 1000.0 / Stopwatch.Frequency;
        double tasksPerFrame = frames == 0 ? 0 : (double)count / frames;

        return $"Optimum game launch tasks: frames={frames}, tasks={count}, tasks/frame={tasksPerFrame:0.00}, averageMs={averageMs:0.###}, maxMs={maxMs:0.###}, totalMs={totalMs:0.###}, peakQueueDepth={Interlocked.Read(ref _gameLaunchTaskPeakDepth)}";
    }

    private static void UpdatePeak(ref long target, long value)
    {
        long current = Interlocked.Read(ref target);
        while (value > current)
        {
            long previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }
            current = previous;
        }
    }

    // Chunk render diagnostics (Phase 1 for rank 2 command batching evaluation)
    private static long _chunkRenderFrames;
    private static long _chunkDrawCalls;
    private static long _chunkPoolsRendered;
    private static long _chunkPoolsCulled;
    private static long _chunkVisibleGroups;
    private static long _chunkFrustumCullTicks;

    // Issue #75: GPU indirect vs conventional draw submission timing & counters
    private static long _chunkIndirectDrawCalls;
    private static long _chunkConventionalDrawCalls;
    private static long _chunkIndirectSubmissionTicks;
    private static long _chunkConventionalSubmissionTicks;
    private static long _chunkIndirectGroups;
    private static long _chunkConventionalGroups;

    public static long ChunkIndirectDrawCalls => Interlocked.Read(ref _chunkIndirectDrawCalls);
    public static long ChunkConventionalDrawCalls => Interlocked.Read(ref _chunkConventionalDrawCalls);
    public static long ChunkIndirectSubmissionTicks => Interlocked.Read(ref _chunkIndirectSubmissionTicks);
    public static long ChunkConventionalSubmissionTicks => Interlocked.Read(ref _chunkConventionalSubmissionTicks);
    public static long ChunkIndirectGroups => Interlocked.Read(ref _chunkIndirectGroups);
    public static long ChunkConventionalGroups => Interlocked.Read(ref _chunkConventionalGroups);

    public static void RecordChunkDrawSubmission(int groupCount, bool isIndirect, long elapsedTicks)
    {
        if (isIndirect)
        {
            Interlocked.Increment(ref _chunkIndirectDrawCalls);
            Interlocked.Add(ref _chunkIndirectGroups, groupCount);
            Interlocked.Add(ref _chunkIndirectSubmissionTicks, elapsedTicks);
        }
        else
        {
            Interlocked.Increment(ref _chunkConventionalDrawCalls);
            Interlocked.Add(ref _chunkConventionalGroups, groupCount);
            Interlocked.Add(ref _chunkConventionalSubmissionTicks, elapsedTicks);
        }
    }

    public static void ResetChunkDrawSubmissionCounters()
    {
        Interlocked.Exchange(ref _chunkIndirectDrawCalls, 0);
        Interlocked.Exchange(ref _chunkConventionalDrawCalls, 0);
        Interlocked.Exchange(ref _chunkIndirectSubmissionTicks, 0);
        Interlocked.Exchange(ref _chunkConventionalSubmissionTicks, 0);
        Interlocked.Exchange(ref _chunkIndirectGroups, 0);
        Interlocked.Exchange(ref _chunkConventionalGroups, 0);
    }

    // Issue #75 Tier 2: SIMD frustum culling counters
    private static long _simdFrustumTests;
    private static long _simdFrustumCulled;

    public static long SimdFrustumTests => Interlocked.Read(ref _simdFrustumTests);
    public static long SimdFrustumCulled => Interlocked.Read(ref _simdFrustumCulled);

    public static void RecordSimdFrustumTest(bool culled)
    {
        Interlocked.Increment(ref _simdFrustumTests);
        if (culled)
        {
            Interlocked.Increment(ref _simdFrustumCulled);
        }
    }

    public static void ResetSimdFrustumCounters()
    {
        Interlocked.Exchange(ref _simdFrustumTests, 0);
        Interlocked.Exchange(ref _simdFrustumCulled, 0);
    }

    private const int ChunkRenderWindowSize = 120;
    private static readonly long[] _chunkWindowDrawCalls = new long[ChunkRenderWindowSize];
    private static readonly long[] _chunkWindowPoolsRendered = new long[ChunkRenderWindowSize];
    private static readonly long[] _chunkWindowPoolsCulled = new long[ChunkRenderWindowSize];
    private static readonly long[] _chunkWindowVisibleGroups = new long[ChunkRenderWindowSize];
    private static readonly long[] _chunkWindowFrustumCullTicks = new long[ChunkRenderWindowSize];
    private static long _chunkWindowSampleCount;
    private static long _chunkWindowDrawCallsTotal;
    private static long _chunkWindowPoolsRenderedTotal;
    private static long _chunkWindowPoolsCulledTotal;
    private static long _chunkWindowVisibleGroupsTotal;
    private static long _chunkWindowFrustumCullTicksTotal;
    private static long _chunkWindowLastDrawCalls;
    private static long _chunkWindowLastPoolsRendered;
    private static long _chunkWindowLastPoolsCulled;
    private static long _chunkWindowLastVisibleGroups;
    private static long _chunkWindowLastFrustumCullTicks;
    private static long _chunkWindowNextSample;
    private static int _chunkWindowBaselineReady;

    /// <summary>
    /// Called once per MeshDataPool.RenderMesh invocation (one MultiDrawElements call).
    /// </summary>
    public static void RecordChunkDrawCall(int groupCount)
    {
        Interlocked.Increment(ref _chunkDrawCalls);
        Interlocked.Add(ref _chunkVisibleGroups, groupCount);
    }

    /// <summary>
    /// Called once per MeshDataPoolManager.Render.
    /// poolsRendered counts pools with visible groups in this render pass.
    /// poolsCulled counts normal-dimension pools with no visible groups after frustum culling.
    /// </summary>
    public static void RecordChunkRenderPass(int poolsRendered)
    {
        RecordChunkRenderPass(poolsRendered, 0);
    }

    public static void RecordChunkRenderPass(int poolsRendered, int poolsCulled)
    {
        Interlocked.Add(ref _chunkPoolsRendered, poolsRendered);
        Interlocked.Add(ref _chunkPoolsCulled, poolsCulled);
    }

    /// <summary>
    /// Called once per client frame from MeshDataPoolMasterManager.OnFrame.
    /// </summary>
    public static void RecordChunkRenderFrame()
    {
        Interlocked.Increment(ref _chunkRenderFrames);
        if (Interlocked.Exchange(ref _chunkWindowBaselineReady, 1) == 0)
        {
            SetChunkWindowBaseline();
            return;
        }

        RecordChunkWindowSample();
    }

    private static void SetChunkWindowBaseline()
    {
        Interlocked.Exchange(ref _chunkWindowLastDrawCalls, Interlocked.Read(ref _chunkDrawCalls));
        Interlocked.Exchange(ref _chunkWindowLastPoolsRendered, Interlocked.Read(ref _chunkPoolsRendered));
        Interlocked.Exchange(ref _chunkWindowLastPoolsCulled, Interlocked.Read(ref _chunkPoolsCulled));
        Interlocked.Exchange(ref _chunkWindowLastVisibleGroups, Interlocked.Read(ref _chunkVisibleGroups));
        Interlocked.Exchange(ref _chunkWindowLastFrustumCullTicks, Interlocked.Read(ref _chunkFrustumCullTicks));
    }

    private static void RecordChunkWindowSample()
    {
        long draws = Interlocked.Read(ref _chunkDrawCalls);
        long pools = Interlocked.Read(ref _chunkPoolsRendered);
        long culled = Interlocked.Read(ref _chunkPoolsCulled);
        long groups = Interlocked.Read(ref _chunkVisibleGroups);
        long cullTicks = Interlocked.Read(ref _chunkFrustumCullTicks);
        long sampleNumber = Interlocked.Increment(ref _chunkWindowNextSample);
        long drawDelta = draws - Interlocked.Exchange(ref _chunkWindowLastDrawCalls, draws);
        long poolDelta = pools - Interlocked.Exchange(ref _chunkWindowLastPoolsRendered, pools);
        long culledDelta = culled - Interlocked.Exchange(ref _chunkWindowLastPoolsCulled, culled);
        long groupDelta = groups - Interlocked.Exchange(ref _chunkWindowLastVisibleGroups, groups);
        long cullTickDelta = cullTicks - Interlocked.Exchange(ref _chunkWindowLastFrustumCullTicks, cullTicks);
        int slot = (int)((sampleNumber - 1) % ChunkRenderWindowSize);

        Interlocked.Add(ref _chunkWindowDrawCallsTotal, drawDelta - _chunkWindowDrawCalls[slot]);
        Interlocked.Add(ref _chunkWindowPoolsRenderedTotal, poolDelta - _chunkWindowPoolsRendered[slot]);
        Interlocked.Add(ref _chunkWindowPoolsCulledTotal, culledDelta - _chunkWindowPoolsCulled[slot]);
        Interlocked.Add(ref _chunkWindowVisibleGroupsTotal, groupDelta - _chunkWindowVisibleGroups[slot]);
        Interlocked.Add(ref _chunkWindowFrustumCullTicksTotal, cullTickDelta - _chunkWindowFrustumCullTicks[slot]);
        _chunkWindowDrawCalls[slot] = drawDelta;
        _chunkWindowPoolsRendered[slot] = poolDelta;
        _chunkWindowPoolsCulled[slot] = culledDelta;
        _chunkWindowVisibleGroups[slot] = groupDelta;
        _chunkWindowFrustumCullTicks[slot] = cullTickDelta;
        Interlocked.Exchange(ref _chunkWindowSampleCount, Math.Min(sampleNumber, ChunkRenderWindowSize));
    }

    /// <summary>
    /// Accumulates frustum cull time across all pools in one frame.
    /// </summary>
    public static void RecordChunkFrustumCullTicks(long ticks)
    {
        Interlocked.Add(ref _chunkFrustumCullTicks, ticks);
    }

    public static void ResetChunkRender()
    {
        Interlocked.Exchange(ref _chunkRenderFrames, 0);
        Interlocked.Exchange(ref _chunkDrawCalls, 0);
        Interlocked.Exchange(ref _chunkPoolsRendered, 0);
        Interlocked.Exchange(ref _chunkPoolsCulled, 0);
        Interlocked.Exchange(ref _chunkVisibleGroups, 0);
        Interlocked.Exchange(ref _chunkFrustumCullTicks, 0);
        Interlocked.Exchange(ref _chunkWindowSampleCount, 0);
        Interlocked.Exchange(ref _chunkWindowDrawCallsTotal, 0);
        Interlocked.Exchange(ref _chunkWindowPoolsRenderedTotal, 0);
        Interlocked.Exchange(ref _chunkWindowPoolsCulledTotal, 0);
        Interlocked.Exchange(ref _chunkWindowVisibleGroupsTotal, 0);
        Interlocked.Exchange(ref _chunkWindowFrustumCullTicksTotal, 0);
        Interlocked.Exchange(ref _chunkWindowLastDrawCalls, 0);
        Interlocked.Exchange(ref _chunkWindowLastPoolsRendered, 0);
        Interlocked.Exchange(ref _chunkWindowLastPoolsCulled, 0);
        Interlocked.Exchange(ref _chunkWindowLastVisibleGroups, 0);
        Interlocked.Exchange(ref _chunkWindowLastFrustumCullTicks, 0);
        Interlocked.Exchange(ref _chunkWindowNextSample, 0);
        Interlocked.Exchange(ref _chunkWindowBaselineReady, 0);
        Array.Clear(_chunkWindowDrawCalls);
        Array.Clear(_chunkWindowPoolsRendered);
        Array.Clear(_chunkWindowPoolsCulled);
        Array.Clear(_chunkWindowVisibleGroups);
        Array.Clear(_chunkWindowFrustumCullTicks);
        ResetChunkDrawSubmissionCounters();
        ResetSimdFrustumCounters();
    }

    public static void ResetChunkRenderFrame()
    {
        if (Volatile.Read(ref _chunkWindowBaselineReady) != 0)
        {
            RecordChunkWindowSample();
        }

        Interlocked.Exchange(ref _chunkRenderFrames, 0);
        Interlocked.Exchange(ref _chunkDrawCalls, 0);
        Interlocked.Exchange(ref _chunkPoolsRendered, 0);
        Interlocked.Exchange(ref _chunkPoolsCulled, 0);
        Interlocked.Exchange(ref _chunkVisibleGroups, 0);
        Interlocked.Exchange(ref _chunkFrustumCullTicks, 0);
        Interlocked.Exchange(ref _chunkWindowLastDrawCalls, 0);
        Interlocked.Exchange(ref _chunkWindowLastPoolsRendered, 0);
        Interlocked.Exchange(ref _chunkWindowLastPoolsCulled, 0);
        Interlocked.Exchange(ref _chunkWindowLastVisibleGroups, 0);
        Interlocked.Exchange(ref _chunkWindowLastFrustumCullTicks, 0);
        Interlocked.Exchange(ref _chunkWindowBaselineReady, 0);
    }

    // Chunk upload diagnostics (Phase 1 for rank 3 persistent mapped upload)
    private static long _chunkUploadFrames;
    private static long _chunkUploadBytes;
    private static long _chunkUploadCalls;
    private static long _chunkUploadTicks;

    /// <summary>
    /// Called per updateVAO invocation on the persistent path.
    /// </summary>
    public static void RecordChunkUpload(int bytes, long ticks)
    {
        Interlocked.Increment(ref _chunkUploadCalls);
        Interlocked.Add(ref _chunkUploadBytes, bytes);
        Interlocked.Add(ref _chunkUploadTicks, ticks);
    }

    /// <summary>
    /// Called once per frame from the upload limiter to mark frame boundaries.
    /// </summary>
    public static void RecordChunkUploadFrame()
    {
        Interlocked.Increment(ref _chunkUploadFrames);
    }

    public static void ResetChunkUpload()
    {
        Interlocked.Exchange(ref _chunkUploadFrames, 0);
        Interlocked.Exchange(ref _chunkUploadBytes, 0);
        Interlocked.Exchange(ref _chunkUploadCalls, 0);
        Interlocked.Exchange(ref _chunkUploadTicks, 0);
    }

    public static string GetChunkUploadSummary()
    {
        long frames = Interlocked.Read(ref _chunkRenderFrames); // reuse render frame counter as proxy
        long bytes = Interlocked.Read(ref _chunkUploadBytes);
        long calls = Interlocked.Read(ref _chunkUploadCalls);
        long ticks = Interlocked.Read(ref _chunkUploadTicks);
        double ms = ticks * 1000.0 / Stopwatch.Frequency;

        double bytesPerFrame = frames == 0 ? 0 : (double)bytes / frames;
        double callsPerFrame = frames == 0 ? 0 : (double)calls / frames;
        double msPerFrame = frames == 0 ? 0 : ms / frames;
        double mbTotal = bytes / (1024.0 * 1024.0);

        return $"Optimum chunk upload: frames={frames}, calls/frame={callsPerFrame:0.0}, KB/frame={bytesPerFrame / 1024:0.0}, uploadMs/frame={msPerFrame:0.###}, totalMB={mbTotal:0.0}, totalMs={ms:0.###}";
    }

    public static string GetChunkRenderSummary()
    {
        long frames = Interlocked.Read(ref _chunkRenderFrames);
        long draws = Interlocked.Read(ref _chunkDrawCalls);
        long pools = Interlocked.Read(ref _chunkPoolsRendered);
        long culled = Interlocked.Read(ref _chunkPoolsCulled);
        long groups = Interlocked.Read(ref _chunkVisibleGroups);
        long cullTicks = Interlocked.Read(ref _chunkFrustumCullTicks);
        long windowFrames = Interlocked.Read(ref _chunkWindowSampleCount);
        long windowDraws = Interlocked.Read(ref _chunkWindowDrawCallsTotal);
        long windowPools = Interlocked.Read(ref _chunkWindowPoolsRenderedTotal);
        long windowCulled = Interlocked.Read(ref _chunkWindowPoolsCulledTotal);
        long windowGroups = Interlocked.Read(ref _chunkWindowVisibleGroupsTotal);
        long windowCullTicks = Interlocked.Read(ref _chunkWindowFrustumCullTicksTotal);
        double cullMs = cullTicks * 1000.0 / Stopwatch.Frequency;
        double windowCullMs = windowCullTicks * 1000.0 / Stopwatch.Frequency;

        double drawsPerFrame = frames == 0 ? 0 : (double)draws / frames;
        double poolsPerFrame = frames == 0 ? 0 : (double)pools / frames;
        double culledPerFrame = frames == 0 ? 0 : (double)culled / frames;
        double groupsPerFrame = frames == 0 ? 0 : (double)groups / frames;
        double cullMsPerFrame = frames == 0 ? 0 : cullMs / frames;
        double windowDrawsPerFrame = windowFrames == 0 ? 0 : (double)windowDraws / windowFrames;
        double windowPoolsPerFrame = windowFrames == 0 ? 0 : (double)windowPools / windowFrames;
        double windowCulledPerFrame = windowFrames == 0 ? 0 : (double)windowCulled / windowFrames;
        double windowGroupsPerFrame = windowFrames == 0 ? 0 : (double)windowGroups / windowFrames;
        double windowCullMsPerFrame = windowFrames == 0 ? 0 : windowCullMs / windowFrames;

        long indDraws = Interlocked.Read(ref _chunkIndirectDrawCalls);
        long convDraws = Interlocked.Read(ref _chunkConventionalDrawCalls);
        long indTicks = Interlocked.Read(ref _chunkIndirectSubmissionTicks);
        long convTicks = Interlocked.Read(ref _chunkConventionalSubmissionTicks);
        double indMs = indTicks * 1000.0 / Stopwatch.Frequency;
        double convMs = convTicks * 1000.0 / Stopwatch.Frequency;
        double totalSubMs = indMs + convMs;
        double subMsPerFrame = frames == 0 ? 0 : totalSubMs / frames;
        double indDrawsPerFrame = frames == 0 ? 0 : (double)indDraws / frames;

        long simdTests = Interlocked.Read(ref _simdFrustumTests);
        long simdCulled = Interlocked.Read(ref _simdFrustumCulled);
        double simdTestsPerFrame = frames == 0 ? 0 : (double)simdTests / frames;
        double simdCulledPerFrame = frames == 0 ? 0 : (double)simdCulled / frames;

        string kometNote = OptimumConfig.KometDetected ? ", kometOverride=true" : "";
        return $"Optimum chunk render: frames={frames}, drawCalls/frame={drawsPerFrame:0.0}, poolsRendered/frame={poolsPerFrame:0.0}, poolsCulled/frame={culledPerFrame:0.0}, visibleGroups/frame={groupsPerFrame:0.0}, frustumCullMs/frame={cullMsPerFrame:0.###}, totalCullMs={cullMs:0.###}, submissionMs/frame={subMsPerFrame:0.###}, indirectDraws/frame={indDrawsPerFrame:0.0}, simdTests/frame={simdTestsPerFrame:0.0}, simdCulled/frame={simdCulledPerFrame:0.0}, windowFrames={windowFrames}, windowDrawCalls/frame={windowDrawsPerFrame:0.0}, windowPoolsRendered/frame={windowPoolsPerFrame:0.0}, windowPoolsCulled/frame={windowCulledPerFrame:0.0}, windowVisibleGroups/frame={windowGroupsPerFrame:0.0}, windowFrustumCullMs/frame={windowCullMsPerFrame:0.###}{kometNote}";
    }

    public static string GetCountersSummary()
    {
        var sb = new StringBuilder("Optimum counters (hit=ran full path, skip=fast-pathed):");
        foreach (var (name, counter) in Counters)
        {
            var (hits, skips) = counter.Snapshot();
            long total = hits + skips;
            double skipRate = total == 0 ? 0 : skips * 100.0 / total;
            sb.Append($"\n  {name}: hits={hits}, skips={skips}, skipRate={skipRate:0.0}%");
        }
        double entityLightBatchMs = Interlocked.Read(ref _entityLightBatchTicks) * 1000.0 / Stopwatch.Frequency;
        sb.Append($"\n  EntityLightBatchTotals: frames={Interlocked.Read(ref _entityLightBatchFrames)}, samples={Interlocked.Read(ref _entityLightSamples)}, prepared={Interlocked.Read(ref _entityLightPreparedSamples)}, chunkGroups={Interlocked.Read(ref _entityLightChunkGroups)}, failedChunkGroups={Interlocked.Read(ref _entityLightFailedChunkGroups)}, coordinateMismatches={Interlocked.Read(ref _entityLightCoordinateMismatches)}, chunkInvalidations={Interlocked.Read(ref _entityLightChunkInvalidations)}, lockBatches={Interlocked.Read(ref _entityLightLockBatches)}, maxBatchSize={Interlocked.Read(ref _entityLightMaxBatchSize)}, timedFrames={Interlocked.Read(ref _entityLightTimedFrames)}, sampledBatchMs={entityLightBatchMs:0.###}");
        sb.Append($"\n  EntityShaderStateCacheTotals: segments={Interlocked.Read(ref _entityShaderSegments)}, uses={Interlocked.Read(ref _entityShaderUses)}, uniformUploadsAvoided={Interlocked.Read(ref _entityShaderUniformUploadsAvoided)}, uboLookupsAvoided={Interlocked.Read(ref _entityShaderUboLookupsAvoided)}");
        sb.Append($"\n  {GetGameLaunchTaskSummary()}");
        return sb.ToString();
    }

    // Tessellation pipeline instrumentation (Phase 2, Steps 9-10)
    private static long _tessChunksProcessed;
    private static long _tessTotalTicks;
    private static long _tessPeakQueueDepth;
    private static long _tessCurrentQueueDepth;
    private static long _tessReadyToUploadTicks; // wall-clock from "chunk data ready" to "mesh uploaded"
    private static long _tessReadyToUploadCount;
    private static long _tessRetryRequeueTotal;
    private static long _tessRetryRequeueWorst; // worst per-chunk requeue count
    private static long _tessBackpressureCount;
    private static long _tessHandoffCapacity;
    private static long _tessHandoffPeak;
    private static readonly object _tessWorkerGate = new();
    private static readonly List<int> _tessWorkerIds = new();

    /// <summary>Called after each chunk tessellation completes (success or retry).</summary>
    public static void RecordTessellation(long elapsedTicks, int queueDepth)
    {
        Interlocked.Increment(ref _tessChunksProcessed);
        Interlocked.Add(ref _tessTotalTicks, elapsedTicks);

        // Update peak (lock-free CAS loop)
        long current = Interlocked.Read(ref _tessPeakQueueDepth);
        while (queueDepth > current)
        {
            long prev = Interlocked.CompareExchange(ref _tessPeakQueueDepth, queueDepth, current);
            if (prev == current) break;
            current = prev;
        }
        Volatile.Write(ref _tessCurrentQueueDepth, queueDepth);
    }

    /// <summary>Called when a tessellated chunk is uploaded to the render thread.</summary>
    public static void RecordTessUpload(long readyToUploadTicks)
    {
        Interlocked.Increment(ref _tessReadyToUploadCount);
        Interlocked.Add(ref _tessReadyToUploadTicks, readyToUploadTicks);
    }

    /// <summary>Called on each RetryTesselationException requeue.</summary>
    public static void RecordTessRetry(int perChunkRetryCount)
    {
        Interlocked.Increment(ref _tessRetryRequeueTotal);

        // Update worst per-chunk (lock-free CAS loop)
        long current = Interlocked.Read(ref _tessRetryRequeueWorst);
        while (perChunkRetryCount > current)
        {
            long prev = Interlocked.CompareExchange(ref _tessRetryRequeueWorst, perChunkRetryCount, current);
            if (prev == current) break;
            current = prev;
        }
    }

    /// <summary>Called when a completed mesh returns to the dirty queue because the handoff is full.</summary>
    public static void RecordTessBackpressure()
    {
        Interlocked.Increment(ref _tessBackpressureCount);
    }

    /// <summary>Called once from OptimumBoundedHandoff's constructor with its actual capacity.</summary>
    public static void RecordTessHandoffCapacity(int capacity)
    {
        Interlocked.Exchange(ref _tessHandoffCapacity, capacity);
    }

    /// <summary>Called on each successful OptimumBoundedHandoff.TryReserve with the new reserved count.</summary>
    public static void RecordTessHandoffReserved(int reserved)
    {
        long current = Interlocked.Read(ref _tessHandoffPeak);
        while (reserved > current)
        {
            long prev = Interlocked.CompareExchange(ref _tessHandoffPeak, reserved, current);
            if (prev == current) break;
            current = prev;
        }
    }

    /// <summary>Called from OptimumTesselationWorkerRegistry.Register on first registration of a thread id.</summary>
    public static void RecordTessWorkerRegistered(int threadId)
    {
        lock (_tessWorkerGate)
        {
            if (!_tessWorkerIds.Contains(threadId))
            {
                _tessWorkerIds.Add(threadId);
            }
        }
    }

    /// <summary>Number of distinct registered tessellation worker threads.</summary>
    public static int TessWorkerCount
    {
        get { lock (_tessWorkerGate) { return _tessWorkerIds.Count; } }
    }

    /// <summary>Check if a chunk has exceeded the retry threshold (50) and should log a warning.</summary>
    public static bool ShouldWarnTessRetry(int perChunkRetryCount) => perChunkRetryCount == 50;

    public static void ResetTessellation()
    {
        Interlocked.Exchange(ref _tessChunksProcessed, 0);
        Interlocked.Exchange(ref _tessTotalTicks, 0);
        Interlocked.Exchange(ref _tessPeakQueueDepth, 0);
        Interlocked.Exchange(ref _tessCurrentQueueDepth, 0);
        Interlocked.Exchange(ref _tessReadyToUploadTicks, 0);
        Interlocked.Exchange(ref _tessReadyToUploadCount, 0);
        Interlocked.Exchange(ref _tessRetryRequeueTotal, 0);
        Interlocked.Exchange(ref _tessRetryRequeueWorst, 0);
        Interlocked.Exchange(ref _tessBackpressureCount, 0);
        Interlocked.Exchange(ref _tessHandoffPeak, 0);
        lock (_tessWorkerGate)
        {
            _tessWorkerIds.Clear();
        }
    }

    public static string GetTessellationSummary()
    {
        long chunks = Interlocked.Read(ref _tessChunksProcessed);
        long ticks = Interlocked.Read(ref _tessTotalTicks);
        long peak = Interlocked.Read(ref _tessPeakQueueDepth);
        long currentQ = Volatile.Read(ref _tessCurrentQueueDepth);
        long uploadCount = Interlocked.Read(ref _tessReadyToUploadCount);
        long uploadTicks = Interlocked.Read(ref _tessReadyToUploadTicks);
        long retries = Interlocked.Read(ref _tessRetryRequeueTotal);
        long worstRetry = Interlocked.Read(ref _tessRetryRequeueWorst);
        long backpressure = Interlocked.Read(ref _tessBackpressureCount);
        long handoffCapacity = Interlocked.Read(ref _tessHandoffCapacity);
        long handoffPeak = Interlocked.Read(ref _tessHandoffPeak);

        double totalMs = ticks * 1000.0 / Stopwatch.Frequency;
        double meanMs = chunks == 0 ? 0 : totalMs / chunks;
        double uploadMs = uploadCount == 0 ? 0 : uploadTicks * 1000.0 / Stopwatch.Frequency / uploadCount;

        string workerIds;
        int workerCount;
        lock (_tessWorkerGate)
        {
            workerCount = _tessWorkerIds.Count;
            workerIds = string.Join(",", _tessWorkerIds);
        }

        return $"Optimum tessellation: chunks={chunks}, meanMs/chunk={meanMs:0.###}, queuePeak={peak}, queueNow={currentQ}, ready-to-upload meanMs={uploadMs:0.###}, retries={retries}, worstPerChunk={worstRetry}, backpressure={backpressure}, workers={workerCount} [ids={workerIds}], handoffPeak={handoffPeak}/{handoffCapacity}";
    }

    // Worldgen per-pass timing diagnostics (Step 33)
    private const int WorldgenPassCount = 6; // None(0), Terrain(1), TerrainFeatures(2), Vegetation(3), NeighbourSunLightFlood(4), PreDone(5)
    private static readonly long[] _worldgenPassTicks = new long[WorldgenPassCount];
    private static readonly long[] _worldgenPassColumns = new long[WorldgenPassCount];
    internal static long _worldgenTotalColumns;

    public static void RecordWorldgenPassTiming(int pass, long elapsedTicks)
    {
        if ((uint)pass >= WorldgenPassCount) return;
        Interlocked.Add(ref _worldgenPassTicks[pass], elapsedTicks);
        Interlocked.Increment(ref _worldgenPassColumns[pass]);
        Interlocked.Increment(ref _worldgenTotalColumns);
    }

    public static void ResetWorldgenPassTiming()
    {
        Array.Clear(_worldgenPassTicks);
        Array.Clear(_worldgenPassColumns);
        Interlocked.Exchange(ref _worldgenTotalColumns, 0);
    }

    private static readonly string[] WorldgenPassNames = { "None", "Terrain", "TerrainFeatures", "Vegetation", "SunLightFlood", "PreDone" };

    public static string GetWorldgenPassTimingSummary()
    {
        long totalColumns = Interlocked.Read(ref _worldgenTotalColumns);
        var sb = new StringBuilder();
        sb.Append($"Optimum worldgen pass timing: totalColumns={totalColumns}");
        for (int i = 1; i < WorldgenPassCount; i++)
        {
            long ticks = Interlocked.Read(ref _worldgenPassTicks[i]);
            long cols = Interlocked.Read(ref _worldgenPassColumns[i]);
            double totalMs = ticks * 1000.0 / Stopwatch.Frequency;
            double meanMs = cols == 0 ? 0 : totalMs / cols;
            sb.Append($", {WorldgenPassNames[i]}={meanMs:0.###}ms/col({cols}cols,{totalMs:0.#}ms)");
        }
        return sb.ToString();
    }

    // Chunk visibility culler walk timing (issue #72): compares the raycast vs
    // BFS visibility walk in the running client. Records elapsed ticks per full
    // CullInvisibleChunks recompute (the walk only re-runs when the camera changes
    // chunk), tagged by which algorithm ran. Logs a summary every LogEvery walks so
    // a headless timed run captures the number without in-game chat input.
    private static long _cullWalkRaycastCount;
    private static long _cullWalkRaycastTicks;
    private static long _cullWalkRaycastVisible;
    private static long _cullWalkBfsCount;
    private static long _cullWalkBfsTicks;
    private static long _cullWalkBfsVisible;
    private static long _cullWalkSinceLog;
    public static volatile bool CullWalkLogEnabled;
    public const int CullWalkLogEvery = 20;

    /// <summary>
    /// Records one full visibility-walk recompute. bfs=true when runBfsVisibility
    /// ran, false for the vanilla raycast. visibleMarked is the number of chunks the
    /// walk left visible this pass (only counted when CullWalkLogEnabled, for the
    /// raycast-vs-BFS superset A/B). Returns true when a summary should be logged now.
    /// </summary>
    public static bool RecordChunkCullerWalk(bool bfs, long elapsedTicks, int visibleMarked)
    {
        if (bfs)
        {
            Interlocked.Increment(ref _cullWalkBfsCount);
            Interlocked.Add(ref _cullWalkBfsTicks, elapsedTicks);
            Interlocked.Add(ref _cullWalkBfsVisible, visibleMarked);
        }
        else
        {
            Interlocked.Increment(ref _cullWalkRaycastCount);
            Interlocked.Add(ref _cullWalkRaycastTicks, elapsedTicks);
            Interlocked.Add(ref _cullWalkRaycastVisible, visibleMarked);
        }
        if (!CullWalkLogEnabled) return false;
        return Interlocked.Increment(ref _cullWalkSinceLog) % CullWalkLogEvery == 0;
    }

    public static string GetChunkCullerWalkSummary()
    {
        long rc = Interlocked.Read(ref _cullWalkRaycastCount);
        long rt = Interlocked.Read(ref _cullWalkRaycastTicks);
        long rv = Interlocked.Read(ref _cullWalkRaycastVisible);
        long bc = Interlocked.Read(ref _cullWalkBfsCount);
        long bt = Interlocked.Read(ref _cullWalkBfsTicks);
        long bv = Interlocked.Read(ref _cullWalkBfsVisible);
        double rMs = rc == 0 ? 0 : rt * 1000.0 / Stopwatch.Frequency / rc;
        double bMs = bc == 0 ? 0 : bt * 1000.0 / Stopwatch.Frequency / bc;
        double rVis = rc == 0 ? 0 : (double)rv / rc;
        double bVis = bc == 0 ? 0 : (double)bv / bc;
        return $"Optimum chunk culler walk: raycast walks={rc}, meanMs={rMs:0.####}, meanVisible={rVis:0.0}; bfs walks={bc}, meanMs={bMs:0.####}, meanVisible={bVis:0.0}";
    }

    public static void ResetChunkCullerWalk()
    {
        Interlocked.Exchange(ref _cullWalkRaycastCount, 0);
        Interlocked.Exchange(ref _cullWalkRaycastTicks, 0);
        Interlocked.Exchange(ref _cullWalkRaycastVisible, 0);
        Interlocked.Exchange(ref _cullWalkBfsCount, 0);
        Interlocked.Exchange(ref _cullWalkBfsTicks, 0);
        Interlocked.Exchange(ref _cullWalkBfsVisible, 0);
        Interlocked.Exchange(ref _cullWalkSinceLog, 0);
    }

    // In-client frame-time sampler for the issue #72 FPS A/B. Gated by
    // CullWalkLogEnabled so it costs nothing on a normal frame. Collects raw
    // frame-time samples (ms) in a fixed ring; every FpsLogEvery frames it logs
    // mean/P50/P95/P99 and the derived FPS, then keeps sampling. Runs entirely on
    // the render thread (window_RenderFrame), so no locking is needed.
    private const int FpsRingSize = 4096;
    private static readonly double[] _fpsRing = new double[FpsRingSize];
    private static int _fpsCount;
    private static long _fpsTotalFrames;
    public const int FpsLogEvery = 600; // ~ every 600 frames

    /// <summary>
    /// Records one client frame's time in milliseconds. Returns true when an FPS
    /// summary should be logged now (caller logs GetFrameTimeSummary()).
    /// </summary>
    public static bool RecordFrameTime(double frameMs)
    {
        if (!CullWalkLogEnabled) return false;
        int idx = _fpsCount % FpsRingSize;
        _fpsRing[idx] = frameMs;
        _fpsCount++;
        _fpsTotalFrames++;
        return _fpsCount % FpsLogEvery == 0;
    }

    public static string GetFrameTimeSummary()
    {
        int n = System.Math.Min(_fpsCount, FpsRingSize);
        if (n == 0) return "Optimum frame time: no samples";
        var copy = new double[n];
        System.Array.Copy(_fpsRing, copy, n);
        System.Array.Sort(copy);
        double sum = 0;
        for (int i = 0; i < n; i++) sum += copy[i];
        double mean = sum / n;
        double p50 = copy[(int)(n * 0.50)];
        double p95 = copy[System.Math.Min(n - 1, (int)(n * 0.95))];
        double p99 = copy[System.Math.Min(n - 1, (int)(n * 0.99))];
        double meanFps = mean > 0 ? 1000.0 / mean : 0;
        double p95Fps = p95 > 0 ? 1000.0 / p95 : 0;
        return $"Optimum frame time: frames={_fpsTotalFrames}, samples={n}, meanMs={mean:0.###} ({meanFps:0.0} fps), p50Ms={p50:0.###}, p95Ms={p95:0.###} ({p95Fps:0.0} fps), p99Ms={p99:0.###}";
    }

    public static void ResetFrameTime()
    {
        _fpsCount = 0;
        _fpsTotalFrames = 0;
        System.Array.Clear(_fpsRing);
    }

    // Chunk deserialization parallelism diagnostics
    private static long _chunkDeserializeParallelColumns;
    private static long _chunkDeserializeParallelChunks;

    public static void RecordChunkDeserializeParallel(int chunksInColumn)
    {
        Interlocked.Increment(ref _chunkDeserializeParallelColumns);
        Interlocked.Add(ref _chunkDeserializeParallelChunks, chunksInColumn);
    }

    public static void ResetChunkDeserializeParallel()
    {
        Interlocked.Exchange(ref _chunkDeserializeParallelColumns, 0);
        Interlocked.Exchange(ref _chunkDeserializeParallelChunks, 0);
    }

    public static string GetChunkDeserializeParallelSummary()
    {
        long columns = Interlocked.Read(ref _chunkDeserializeParallelColumns);
        long chunks = Interlocked.Read(ref _chunkDeserializeParallelChunks);
        return $"Optimum chunk deserialize parallel: columns={columns}, chunks={chunks}";
    }

    /// <summary>
    /// Issue #85: Returns diagnostic summary of detected conflicting guest mods (e.g. Komet).
    /// </summary>
    public static string GetConflictingModsSummary()
    {
        return OptimumConfig.KometDetected
            ? $"Optimum conflicting mods: {OptimumCompatibilityGuard.GetKometStatusLine()}"
            : "Optimum conflicting mods: none detected";
    }

    /// <summary>
    /// Issue #85: Returns diagnostic summary of detected performance ecosystem mods.
    /// </summary>
    public static string GetPerformanceModsSummary()
    {
        return OptimumCompatibilityGuard.GetPerformanceModsSummary();
    }
}
