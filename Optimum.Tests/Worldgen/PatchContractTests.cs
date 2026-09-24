// Source: Optimum.Tests/worldgen-exact-worker-patch-contract-tests.cs
namespace Optimum.Tests
{
using System;
using Xunit;

public class WorldgenExactWorkerPatchContractTests
{
    private const string ChunkThreadPatch = "patches/VintagestoryLib/Vintagestory.Server/ChunkServerThread.cs.patch";
    private const string SupplyChunksPatch = "patches/VintagestoryLib/Vintagestory.Server/ServerSystemSupplyChunks.cs.patch";

    [Fact]
    public void ChunkThreadUsesStrictOverrideResolutionAndRecordsExactMode()
    {
        string source = PatchReader.ReadPatch(ChunkThreadPatch);

        Assert.Contains("ResolveWorldgenWorkerCount", source);
        Assert.Contains("optimumExactWorldgenWorkerMode", source);
        Assert.Contains("worldgen worker override rejected", source);
        Assert.Contains("bool requestedParallelWorldgen = worldgenMtOverride == \"1\" && workerCountOverride != null", source);
        Assert.Contains("if (requestedParallelWorldgen && !optimumExactWorldgenWorkerMode)", source);
        Assert.DoesNotContain("Math.Clamp(forcedWorkers", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactModeFreezesBothAdaptiveMutationPaths()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);

        Assert.Contains("chunkthread.optimumExactWorldgenWorkerMode ? chunkthread.additionalWorldGenThreadsCount", source);
        Assert.Contains("!chunkthread.optimumExactWorldgenWorkerMode && Volatile.Read(ref optimumPostSpawnRaised)", source);
        Assert.Contains("!chunkthread.optimumExactWorldgenWorkerMode && optimumAdaptiveController.ShouldEvaluate()", source);
    }

    [Fact]
    public void WorkerLoopProtectsItsDivisionFromZero()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);

        Assert.Contains("Math.Max(1, chunkthread.additionalWorldGenThreadsCount)", source);
    }
}
}

// Source: Optimum.Tests/worldgen-pass-timing-tests.cs
namespace Optimum.Tests
{
using System.Diagnostics;
using Vintagestory.API.Config;
using Xunit;

public class WorldgenPassTimingTests
{
    [Fact]
    public void RecordWorldgenPassTiming_AccumulatesTicksAndColumns()
    {
        OptimumDiagnostics.ResetWorldgenPassTiming();

        OptimumDiagnostics.RecordWorldgenPassTiming(1, 1000);
        OptimumDiagnostics.RecordWorldgenPassTiming(1, 2000);
        OptimumDiagnostics.RecordWorldgenPassTiming(2, 500);

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("Terrain=", summary);
        Assert.Contains("2cols", summary);
        Assert.Contains("TerrainFeatures=", summary);
        Assert.Contains("1cols", summary);
    }

    [Fact]
    public void RecordWorldgenPassTiming_IgnoresOutOfRangePass()
    {
        OptimumDiagnostics.ResetWorldgenPassTiming();

        OptimumDiagnostics.RecordWorldgenPassTiming(99, 5000);
        OptimumDiagnostics.RecordWorldgenPassTiming(-1, 5000);

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("totalColumns=0", summary);
    }

    [Fact]
    public void RecordWorldgenPassTiming_AllFivePasses()
    {
        OptimumDiagnostics.ResetWorldgenPassTiming();

        OptimumDiagnostics.RecordWorldgenPassTiming(1, 100);
        OptimumDiagnostics.RecordWorldgenPassTiming(2, 200);
        OptimumDiagnostics.RecordWorldgenPassTiming(3, 300);
        OptimumDiagnostics.RecordWorldgenPassTiming(4, 400);
        OptimumDiagnostics.RecordWorldgenPassTiming(5, 500);

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("Terrain=", summary);
        Assert.Contains("TerrainFeatures=", summary);
        Assert.Contains("Vegetation=", summary);
        Assert.Contains("SunLightFlood=", summary);
        Assert.Contains("PreDone=", summary);
        Assert.Contains("totalColumns=5", summary);
    }

    [Fact]
    public void ResetWorldgenPassTiming_ClearsAll()
    {
        OptimumDiagnostics.RecordWorldgenPassTiming(1, 9999);
        OptimumDiagnostics.RecordWorldgenPassTiming(3, 9999);

        OptimumDiagnostics.ResetWorldgenPassTiming();

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("totalColumns=0", summary);
        Assert.Contains("Terrain=0ms/col(0cols", summary);
    }

    [Fact]
    public void GetWorldgenPassTimingSummary_ReportsMeanMsPerColumn()
    {
        OptimumDiagnostics.ResetWorldgenPassTiming();

        long ticksPer10Ms = Stopwatch.Frequency / 100;
        OptimumDiagnostics.RecordWorldgenPassTiming(1, ticksPer10Ms);
        OptimumDiagnostics.RecordWorldgenPassTiming(1, ticksPer10Ms);

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("2cols", summary);
        Assert.Contains("Terrain=", summary);
        Assert.DoesNotContain("Terrain=0ms/col", summary);
    }

    [Fact]
    public void ResetAllCounters_IncludesWorldgenPass()
    {
        OptimumDiagnostics.RecordWorldgenPassTiming(2, 5000);

        OptimumDiagnostics.ResetAllCounters();

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("totalColumns=0", summary);
    }
}
}

// Source: Optimum.Tests/worldgen-r1-workspace-patch-contract-tests.cs
namespace Optimum.Tests
{
using System;
using System.Linq.Expressions;
using System.Reflection;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;
using Xunit;

public sealed class WorldgenR1WorkspacePatchContractTests
{
    [Fact]
    public void TerrainGeneratorsOwnScratchStatePerWorker()
    {
        string terra = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/1.GenTerra/GenTerra.cs.patch");
        string rock = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/2.GenRockStrata/GenRockStrata.cs.patch");
        string caves = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/3.GenCaves/GenCaves.cs.patch");
        string layers = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/4.GenBlockLayers/GenBlockLayers.cs.patch");

        Assert.Contains("ThreadLocal<GenerationWorkspace> workspaces", terra);
        Assert.Contains("landformMapLock", terra);
        Assert.Contains("ThreadLocal<RockWorkspace> workspaces", rock);
        Assert.Contains("provinceMapLock", rock);
        Assert.Contains("float[] rockGroupMaxThickness = new float[4];", rock);
        Assert.Contains("IMapChunk mapChunk;", rock);
        Assert.Contains("ThreadLocal<LCGRandom> caveRandThreadLocal", caves);
        Assert.Contains("ThreadLocal<BlockLayerWorkspace> workspaces", layers);
        Assert.Contains("LCGRandom rnd;", layers);
    }

    [Fact]
    public void ReflectiveBlockLayerAdaptersCanResolveTheLegacyRandomField()
    {
        FieldInfo? field = typeof(GenBlockLayers).GetField(
            "rnd",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal(typeof(LCGRandom), field!.FieldType);

        ParameterExpression instance = Expression.Parameter(typeof(GenBlockLayers), "instance");
        Expression getterBody = Expression.Field(instance, field);
        Func<GenBlockLayers, LCGRandom> getter = Expression.Lambda<Func<GenBlockLayers, LCGRandom>>(
            getterBody,
            instance).Compile();

        Assert.NotNull(getter);
    }

    [Fact]
    public void ReflectiveRockStrataAdaptersCanResolveLegacyFields()
    {
        Type type = typeof(GenRockStrataNew);
        string[] requiredFields = [
            "rockGroupMaxThickness",
            "rockGroupCurrentThickness",
            "mapChunk",
            "heightMap",
            "rdx",
            "rdz",
            "map",
            "lerpMapInv",
            "chunkInRegionX",
            "chunkInRegionZ",
            "provinces"
        ];

        foreach (string fieldName in requiredFields)
        {
            FieldInfo? field = type.GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.NotNull(field);

            ParameterExpression instance = Expression.Parameter(type, "instance");
            Expression getterBody = Expression.Field(instance, field);
            LambdaExpression getter = Expression.Lambda(getterBody, instance);
            Delegate compiled = getter.Compile();
            Assert.NotNull(compiled);
        }
    }

    [Fact]
    public void R1FixesTheKnownSharedCollectionGenerators()
    {
        string ponds = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/7.GenPonds/GenPonds.cs.patch");
        string postProcess = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/1.GenTerra/GenTerraPostProcess.cs.patch");
        string partial = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/GenPartial.cs.patch");
        string deposits = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/5.GenDeposits/GenDeposits.cs.patch");

        Assert.Contains("ThreadLocal<PondWorkspace> workspaces", ponds);
        Assert.Contains("ThreadLocal<IWorldGenBlockAccessor> blockAccessors", ponds);
        Assert.Contains("CurrentBlockAccessor", ponds);
        Assert.DoesNotContain("readonly QueueOfInt searchPositionsDeltas", ponds);
        Assert.Contains("ThreadLocal<TerraPostProcessWorkspace> workspaces", postProcess);
        Assert.Contains("ThreadLocal<IWorldGenBlockAccessor> blockAccessors", postProcess);
        Assert.Contains("CurrentBlockAccessor", postProcess);
        Assert.DoesNotContain("HashSet<int> chunkVisitedNodes", postProcess);
        Assert.Contains("ThreadLocal<LCGRandom> chunkRandThreadLocal", partial);
        Assert.Contains("chunkRandThreadLocal?.Value ?? this.chunkRand", deposits);
        Assert.Contains("ThreadLocal<IBlockAccessor> blockAccessors", deposits);
        Assert.DoesNotContain("ThreadLocal<LCGRandom> depositRandThreadLocal", deposits);
        Assert.DoesNotContain("ThreadLocal<Dictionary<BlockPos, DepositVariant>> subDepositsToPlaceThreadLocal", deposits);
    }

    [Fact]
    public void SchedulerOwnsFootprintsAroundEveryConcurrentPopulate()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerSystemSupplyChunks.cs.patch");

        Assert.Contains("chunkthread.optimumWorldgenFootprints", source);
        Assert.Contains("TryAcquireWorldgenFootprint", source);
        Assert.Contains("out OptimumWorldgenFootprintLease footprintLease", source);
        Assert.Contains("footprintLease?.Dispose()", source);
        Assert.Contains("requestedChunkColumn.FlagToRequeue();", source);
    }

    [Fact]
    public void UnloadReservesTheSameFootprintUntilPersistenceCompletes()
    {
        string unload = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerSystemUnloadChunks.cs.patch");
        string thread = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ChunkServerThread.cs.patch");
        string patcher = PatcherSource.Read();

        int acquire = unload.IndexOf("TryAcquireOptimumWorldgenFootprint", System.StringComparison.Ordinal);
        int readLock = unload.IndexOf("item2.generatingLock.AcquireReadLock()", acquire, System.StringComparison.Ordinal);
        int setChunks = unload.IndexOf("gameDatabase.SetChunks", readLock, System.StringComparison.Ordinal);
        int setMapChunks = unload.IndexOf("gameDatabase.SetMapChunks", setChunks, System.StringComparison.Ordinal);
        int dispose = unload.IndexOf("leases[i]?.Dispose()", setMapChunks, System.StringComparison.Ordinal);

        Assert.True(acquire >= 0);
        Assert.True(readLock > acquire);
        Assert.True(setChunks > readLock);
        Assert.True(setMapChunks > setChunks);
        Assert.True(dispose > setMapChunks);
        Assert.Contains("optimumWorldgenFootprints", thread);
        Assert.Contains("TryAcquireOptimumWorldgenFootprint", thread);
        Assert.Contains("\"TryAcquireOptimumWorldgenFootprint\"", patcher);
        Assert.Contains("\"optimumUnloadGenLeases\"", patcher);
    }

    [Fact]
    public void SafetyGateDetectsPatchesOnMethodsTheHandlerCallsNotJustTheHandler()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerSystemSupplyChunks.cs.patch");

        // A mod can patch a method the registered handler calls (GenTerra.generate)
        // or a compiler-generated closure the handler invokes (GenTerra.<>c__DisplayClass34_0)
        // without patching the handler method itself. The safety gate must scan the
        // declaring type and its nested types, not just Harmony.GetPatchInfo(handler.Method).
        Assert.Contains("IsWorldgenHandlerHarmonyPatched", source);
        Assert.Contains("handlerMethod.DeclaringType", source);
        Assert.Contains("declaringType.GetMethods", source);
        Assert.Contains("declaringType.GetNestedTypes", source);
    }
}
}
