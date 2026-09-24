// Source: Optimum.Tests/OptimumDiagnosticsCountersTests.cs
namespace Optimum.Tests
{
using Vintagestory.API.Config;
using Xunit;

// [Collection("TessellationDiagnostics")]: shares OptimumDiagnostics' static
// tessellation counters with OptimumBoundedHandoffTests - both must be serialized
// relative to each other, or ResetTessellation() calls interleave with assertions
// under xUnit's default cross-class parallelism.
[Collection("TessellationDiagnostics")]
public class OptimumDiagnosticsCountersTests
{
    [Fact]
    public void HitSkipCounterTracksBothIndependently()
    {
        var counter = new OptimumDiagnostics.HitSkipCounter();
        counter.Hit();
        counter.Hit();
        counter.Skip();

        var (hits, skips) = counter.Snapshot();
        Assert.Equal(2, hits);
        Assert.Equal(1, skips);
    }

    [Fact]
    public void HitSkipCounterResetClearsBothCounts()
    {
        var counter = new OptimumDiagnostics.HitSkipCounter();
        counter.Hit();
        counter.Skip();

        counter.Reset();

        var (hits, skips) = counter.Snapshot();
        Assert.Equal(0, hits);
        Assert.Equal(0, skips);
    }

    [Fact]
    public void EveryShippedOptimizationHasACounter()
    {
        string[] expected =
        {
            "EntityShadowCull",
            "EntityRenderCull",
            "DynamicLightRadius",
            "BackgroundFpsLimiter",
            "PreciseFramePacing",
            "HudEntityNameTags",
            "ShadowFarVegetation",
            "RepulseAgents",
            "WeatherWindThrottle",
            "AnimBlockLodNear",
            "AnimBlockLodMid",
            "AnimBlockLodFar",
            "AnimBlockLodDeferred",
            "ParticleDistanceGate",
            "EntityLightBatch",
            "EntityShaderStateCache",
            "EntityTesselationBudget",
            "EntityOutfitShapeCache",
        };

        foreach (var name in expected)
        {
            Assert.True(OptimumDiagnostics.Counters.ContainsKey(name), $"missing counter: {name}");
        }
    }

    [Fact]
    public void GetCountersSummaryIncludesEveryCounterName()
    {
        string summary = OptimumDiagnostics.GetCountersSummary();
        foreach (var name in OptimumDiagnostics.Counters.Keys)
        {
            Assert.Contains(name, summary);
        }
    }

    [Fact]
    public void ResetAllCountersClearsChiselLodToo()
    {
        OptimumDiagnostics.RecordChiselLod(fullTriangles: 10, proxyTriangles: 0, fallback: false, elapsedTicks: 5);
        OptimumDiagnostics.ResetAllCounters();

        string summary = OptimumDiagnostics.GetChiselLodSummary();
        Assert.Contains("blocks=0", summary);
    }

    // Step 6 of the worker-pool wiring plan: OptimumTesselationWorkerRegistry.Register
    // publishes the registered thread id set, so `.optimum status` can show `ids=<the
    // tesselateterrain thread id>` as direct, in-game evidence that ClientMain::Start's
    // RegisterTesselationThread call actually ran.
    [Fact]
    public void TessellationSummaryReportsRegisteredWorkerIdsAndResets()
    {
        OptimumDiagnostics.ResetTessellation();

        var registry = new OptimumTesselationWorkerRegistry();
        registry.Register(4242);
        registry.Register(4242); // repeated registration must not duplicate the id
        registry.Register(9001);

        string summary = OptimumDiagnostics.GetTessellationSummary();
        Assert.Contains("workers=2", summary);
        Assert.Contains("ids=4242,9001", summary);
        Assert.Contains("ready-to-upload meanMs=", summary);
        Assert.DoesNotContain("ready->uploaded", summary);

        OptimumDiagnostics.ResetTessellation();
        Assert.Contains("workers=0 [ids=]", OptimumDiagnostics.GetTessellationSummary());
    }
}
}

// Source: Optimum.Tests/OptimumStatusTests.cs
namespace Optimum.Tests
{
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

public class OptimumStatusTests
{
    [Fact]
    public void DescribeTogglesCoversEveryPersistedField()
    {
        var names = OptimumConfig.DescribeToggles().Select(t => t.Name).ToHashSet();
        foreach (var prop in typeof(OptimumConfigData).GetProperties())
        {
            Assert.Contains(prop.Name, names);
        }
    }

    [Fact]
    public void DescribeTogglesReportsCurrentValues()
    {
        bool original = OptimumConfig.EntityShadowCull;
        try
        {
            OptimumConfig.EntityShadowCull = false;
            var entry = OptimumConfig.DescribeToggles().Single(t => t.Name == nameof(OptimumConfig.EntityShadowCull));
            Assert.Equal("False", entry.Value);
        }
        finally
        {
            OptimumConfig.EntityShadowCull = original;
        }
    }

    [Fact]
    public void WorldgenStatusReportsSuspendedPolicy()
    {
        string source = File.ReadAllText(PatchReader.FindRepositoryFile(
            "sources/VSEssentials/Systems/OptimumStatus.cs"));

        Assert.Contains("Worldgen work stealing: SUSPENDED (serial policy pending R1)", source);
        Assert.DoesNotContain("Worldgen work stealing: ON", source);
    }

    [Fact]
    public void StatusReportsThreadPoolSetMaxThreadsMeasurement()
    {
        string source = File.ReadAllText(PatchReader.FindRepositoryFile(
            "sources/VSEssentials/Systems/OptimumStatus.cs"));

        Assert.Contains("TyronThreadPool.SetMaxThreadsResult", source);
        Assert.Contains("TyronThreadPool.SetMaxThreadsWorkerBefore", source);
        Assert.Contains("TyronThreadPool.SetMaxThreadsWorkerAfter", source);
        Assert.Contains("TyronThreadPool.SetMaxThreadsIoBefore", source);
        Assert.Contains("TyronThreadPool.SetMaxThreadsIoAfter", source);
        Assert.Contains("threadpool: setMaxThreads=", source);
        Assert.Contains("api.Logger.Notification(\"[Optimum] threadpool:", source);
    }

    [Fact]
    public void StatusLogsGameLaunchTaskSummaryAtLevelFinalize()
    {
        string source = File.ReadAllText(PatchReader.FindRepositoryFile(
            "sources/VSEssentials/Systems/OptimumStatus.cs"));

        Assert.Contains("api.Event.LevelFinalize += LogGameLaunchTaskSummary;", source);
        Assert.Contains("OptimumDiagnostics.GetGameLaunchTaskSummary()", source);
        Assert.Contains("[Optimum] \" + OptimumDiagnostics.GetGameLaunchTaskSummary()", source);
    }

    [Fact]
    public void ThreadPoolCapUsesProcessorDerivedValues()
    {
        string source = File.ReadAllText(PatchReader.FindRepositoryFile(
            "VintagestoryApi/Common/TyronThreadPool.cs"));

        Assert.Contains("int workerMax = Math.Max(10, Environment.ProcessorCount * 2);", source);
        Assert.Contains("int ioMax = Math.Max(1, Environment.ProcessorCount);", source);
        Assert.Contains("ThreadPool.SetMaxThreads(workerMax, ioMax)", source);
        Assert.DoesNotContain("ThreadPool.SetMaxThreads(10, 1)", source);
    }

    [Fact]
    public void RuntimeThreadPoolCapMatchesProcessorDerivedValues()
    {
        ThreadPool.GetMaxThreads(out int workerBefore, out int ioBefore);
        _ = TyronThreadPool.Inst;

        try
        {
            Assert.Equal(Math.Max(10, Environment.ProcessorCount * 2), TyronThreadPool.SetMaxThreadsWorkerAfter);
            Assert.Equal(Math.Max(1, Environment.ProcessorCount), TyronThreadPool.SetMaxThreadsIoAfter);
            Assert.True(TyronThreadPool.SetMaxThreadsWorkerAfter >= Environment.ProcessorCount);
        }
        finally
        {
            ThreadPool.SetMaxThreads(workerBefore, ioBefore);
        }
    }

    [Fact]
    public void GameLaunchTaskDiagnosticsReportTimingAndQueueDepth()
    {
        OptimumDiagnostics.ResetGameLaunchTasks();
        try
        {
            OptimumDiagnostics.RecordGameLaunchTaskFrame();
            OptimumDiagnostics.RecordGameLaunchTask(Stopwatch.Frequency / 1000, 3);
            string summary = OptimumDiagnostics.GetGameLaunchTaskSummary();

            Assert.Contains("frames=1", summary);
            Assert.Contains("tasks=1", summary);
            Assert.Contains("averageMs=1", summary);
            Assert.Contains("maxMs=1", summary);
            Assert.Contains("peakQueueDepth=3", summary);
        }
        finally
        {
            OptimumDiagnostics.ResetGameLaunchTasks();
        }
    }

    [Fact]
    public void GameLaunchTaskExecutionRecordsTheExistingSingleTaskBranch()
    {
        string clientSource = File.ReadAllText(PatchReader.FindRepositoryFile(
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs"));
        string diagnosticsSource = File.ReadAllText(PatchReader.FindRepositoryFile(
            "sources/VintagestoryApi/Config/OptimumDiagnostics.cs"));

        Assert.Contains("if (GameLaunchTasks.Count > 0)", clientSource);
        Assert.Contains("ClientTask launchTask = GameLaunchTasks.Dequeue();", clientSource);
        Assert.Contains("Stopwatch.GetTimestamp()", clientSource);
        Assert.Contains("OptimumDiagnostics.RecordGameLaunchTask", clientSource);
        Assert.Contains("finally", clientSource);
        Assert.Contains("GetGameLaunchTaskSummary()", diagnosticsSource);
        Assert.Contains("ResetGameLaunchTasks();", diagnosticsSource);

        string patcherSource = PatcherSource.Read();
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"ExecuteMainThreadTasks\", 1", patcherSource);
    }
}
}

// Source: Optimum.Tests/chunk-render-diagnostics-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

public class ChunkRenderDiagnosticsCoverageTests
{
    [Fact]
    public void MeshPoolRecordsEveryMultiDrawSubmission()
    {
        string source = Read("VintagestoryApi/Client/MeshPool/MeshDataPool.cs");

        Assert.Contains("RecordChunkDrawCall(indicesGroupsCount)", source);
        Assert.Contains("render.RenderMesh(modelRef, indicesStartsByte, indicesSizes, indicesGroupsCount)", source);
    }

    [Fact]
    public void PoolManagerRecordsCullingAndVisiblePoolCounts()
    {
        string source = Read("VintagestoryApi/Client/MeshPool/MeshDataPoolManager.cs");

        Assert.Contains("int poolsRendered = 0;", source);
        Assert.Contains("Stopwatch.GetTimestamp()", source);
        Assert.Contains("RecordChunkFrustumCullTicks", source);
        Assert.Contains("int poolsCulled = 0;", source);
        Assert.Contains("poolsCulled++", source);
        Assert.Contains("RecordChunkRenderPass(poolsRendered, poolsCulled)", source);
        Assert.Equal(2, Count(source, "poolsRendered++"));
    }

    [Fact]
    public void MasterPoolAnchorsRenderCountersToClientFrames()
    {
        string source = Read("VintagestoryApi/Client/MeshPool/MeshDataPoolMasterManager.cs");

        Assert.Contains("RecordChunkRenderFrame()", source);
    }

    [Fact]
    public void RenderSummaryUsesFrameAnchoredCounters()
    {
        string source = Read("sources/VintagestoryApi/Config/OptimumDiagnostics.cs");

        Assert.Contains("RecordChunkRenderFrame", source);
        Assert.Contains("ResetChunkRenderFrame", source);
        Assert.Contains("ChunkRenderWindowSize = 120", source);
        Assert.Contains("windowFrames=", source);
        Assert.Contains("windowPoolsCulled/frame=", source);
        Assert.Contains("drawCalls/frame=", source);
        Assert.Contains("poolsRendered/frame=", source);
        Assert.Contains("visibleGroups/frame=", source);
        Assert.Contains("frustumCullMs/frame=", source);
    }

    [Fact]
    public void RenderDiagnosticsKeepsABoundedWindow()
    {
        OptimumDiagnostics.ResetChunkRender();
        OptimumDiagnostics.RecordChunkRenderFrame();

        for (int i = 0; i < 125; i++)
        {
            OptimumDiagnostics.RecordChunkDrawCall(1);
            OptimumDiagnostics.RecordChunkRenderPass(1, 1);
            OptimumDiagnostics.RecordChunkFrustumCullTicks(1);
            OptimumDiagnostics.RecordChunkRenderFrame();
        }

        string summary = OptimumDiagnostics.GetChunkRenderSummary();
        Assert.Contains("windowFrames=120", summary);
        double expectedCulled = 1.0;
        Assert.Contains($"windowPoolsCulled/frame={expectedCulled:0.0}", summary);
        OptimumDiagnostics.ResetChunkRender();
    }

    private static int Count(string source, string value)
    {
        return (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
    }

    private static string Read(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
}
