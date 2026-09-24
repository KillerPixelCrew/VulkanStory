// Source: Optimum.Tests/ChiselLodCoverageTests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

public class ChiselLodCoverageTests
{
    [Theory]
    [InlineData("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityMicroBlock.cs.patch")]
    public void ChiselLodRegistersFullMeshForMediumRange(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.DoesNotContain("AddMeshData(Mesh, cmapdata, 0)", source);
        Assert.DoesNotContain("AddMeshData(Mesh, 0)", source);
        Assert.Contains("AddMeshData(Mesh, 2)", source);
        Assert.Contains("AddMeshData(lodMesh, 3)", source);
    }

    [Theory]
    [InlineData("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityMicroBlock.cs.patch")]
    public void ChiselLodBuildsProxyFromMajorityMaterial(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.DoesNotContain("Block primaryBlock = Api.World.GetBlock(BlockIds[0])", source);
        Assert.DoesNotContain("tesselator.TesselateBlock(primaryBlock", source);
        Assert.Contains("GetMajorityMaterialId()", source);
        Assert.Contains("TesselatorManager.GetDefaultBlockMesh(primaryBlock)?.Clone()", source);
    }

    [Theory]
    [InlineData("sources/VintagestoryApi/Config/OptimumConfig.cs")]
    [InlineData("VintagestoryApi/Config/OptimumConfig.cs")]
    public void ChiselLodExposesRoutingAndDiagnostics(string relativePath)
    {
        string configPath = FindRepositoryFile(relativePath);
        string source = File.ReadAllText(configPath);
        string diagnosticsPath = Path.Combine(Path.GetDirectoryName(configPath)!, "OptimumDiagnostics.cs");
        string diagnostics = File.Exists(diagnosticsPath) ? File.ReadAllText(diagnosticsPath) : source;

        Assert.Contains("RouteChiselLodMeshes", source);
        Assert.Contains("OptimumDiagnostics", source);
        Assert.Contains("RecordChiselLod", diagnostics);
        Assert.Contains("GetChiselLodSummary", diagnostics);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch")]
    public void ChiselLodRoutesIntoSeparateChunkPools(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("currentOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("centerOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("edgeOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("OptimumConfig.RouteChiselLodMeshes", source);
        Assert.Contains("centerOptimumChiselModeldataByRenderPassByLodLevel, out TesselatedChunkPart[] centerOptimumChiselParts, true", source);
        Assert.Contains("edgeOptimumChiselModeldataByRenderPassByLodLevel, out TesselatedChunkPart[] edgeOptimumChiselParts, true", source);
    }

    [Fact]
    public void ChiselLodChunkTesselatorKeepsReloadLockVanillaCompatible()
    {
        // The patch must not retype ReloadLock to System.Threading.Lock (which would
        // break cecil transplant, same class of bug as chunksLock in 0.2.1).
        // If it doesn't appear in the patch at all, the field stays vanilla (object).
        string patch = File.ReadAllText(FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch"));

        Assert.DoesNotContain("+\tpublic readonly Lock ReloadLock", patch);
        Assert.DoesNotContain("+\tpublic Lock ReloadLock", patch);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch")]
    public void ChiselLodPoolsAreAllocatedInTransplantedAtlasUpdate(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("centerOptimumChiselModeldataByRenderPassByLodLevel == null", source);
        Assert.Contains("edgeOptimumChiselModeldataByRenderPassByLodLevel == null", source);
        Assert.Contains("centerOptimumChiselModeldataByRenderPassByLodLevel[i] = new MeshData[values.Length][];", source);
        Assert.Contains("edgeOptimumChiselModeldataByRenderPassByLodLevel[i] = new MeshData[values.Length][];", source);
    }

    [Fact]
    public void ChiselLodClosedSourceRouteIsRegisteredAsCecilTargets()
    {
        string programSource = PatcherSource.Read();
        string cecilList = File.ReadAllText(FindRepositoryFile("patches/cecil-owned.list"));

        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"UpdateForAtlasses\", 1", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"NowProcessChunk\", 5", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"BuildBlockPolygons\", 3", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"BuildBlockPolygons_EdgeOnly\", 3", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"BuildDecorPolygons\", 5", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"GetMeshPoolForPass\", 3", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.TesselatedChunkPart\", \"AddModelAndStoreLocation\", 8", programSource);
        Assert.Contains("\"populateTesselatedChunkPart\"", programSource);
        Assert.Contains("\"MergeTesselatedChunkParts\"", programSource);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/TesselatedChunkPart.cs.patch", cecilList);
    }

    [Theory]
    [InlineData("VintagestoryApi/Client/MeshPool/MeshDataPool.cs")]
    [InlineData("patches/VintagestoryApi/Client/MeshPool/MeshDataPool.cs.patch")]
    public void ChiselLodPoolLocationsCarryCustomDistanceFlag(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("OptimumUseChiselLodDistance", source);
        // 1.22.7: method is InFrustumAndRange (renamed from SphereInFrustumAndRange),
        // and inside the class the fields are accessed without a 'location.' prefix.
        Assert.Contains("InFrustumAndRange(FrustumCullSphere, FrustumVisible, LodLevel, OptimumUseChiselLodDistance)", source);
    }

    [Theory]
    [InlineData("VintagestoryApi/Client/Render/FrustumCulling.cs")]
    [InlineData("patches/VintagestoryApi/Client/Render/FrustumCulling.cs.patch")]
    public void ChiselLodFrustumUsesConfiguredDistance(string relativePath)
    {
        string path = FindRepositoryFile(relativePath);
        Assert.True(File.Exists(path), $"{relativePath} must exist.");

        string source = File.ReadAllText(path);

        Assert.Contains("optimumUseChiselLodDistance", source);
        Assert.Contains("OptimumConfig.ChiselLodDistanceSq", source);
        Assert.Contains("distance <= chiselDistanceSq && distance < ViewDistanceSq", source);
        Assert.Contains("case 2:", source);
        Assert.Contains("case 3:", source);
    }

    // Regression: shadow passes had zero chisel-LOD awareness, so the LOD3 full-cube proxy
    // always entered the depth map (CullInstantShadowPassFar's `LodLevel >= 1` accepts 3, and
    // CullInstantShadowPassNear had no LOD check at all). Result: a carved block cast a
    // full-block shadow at any distance, including right next to the camera.
    [Theory]
    [InlineData("VintagestoryApi/Client/MeshPool/MeshDataPool.cs")]
    [InlineData("patches/VintagestoryApi/Client/MeshPool/MeshDataPool.cs.patch")]
    public void ChiselLodShadowPassesRespectChiselDistance(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains(
            "case EnumFrustumCullMode.CullInstantShadowPassNear:",
            source);
        Assert.Contains(
            "culler.InFrustumShadowPass(FrustumCullSphere) && culler.OptimumChiselLodVisible(FrustumCullSphere, LodLevel, OptimumUseChiselLodDistance)",
            source);
        Assert.Contains(
            "culler.InFrustumShadowPass(FrustumCullSphere) && LodLevel >= 1 && culler.OptimumChiselLodVisible(FrustumCullSphere, LodLevel, OptimumUseChiselLodDistance)",
            source);
        // The vanilla, chisel-unaware shadow expressions must be gone.
        Assert.DoesNotContain(
            "culler.InFrustumShadowPass(FrustumCullSphere);",
            source);
        Assert.DoesNotContain(
            "culler.InFrustumShadowPass(FrustumCullSphere) && LodLevel >= 1;",
            source);
    }

    [Theory]
    [InlineData("VintagestoryApi/Client/Render/FrustumCulling.cs")]
    [InlineData("patches/VintagestoryApi/Client/Render/FrustumCulling.cs.patch")]
    public void ChiselLodFrustumExposesSharedShadowHelper(string relativePath)
    {
        string source = File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains(
            "public bool OptimumChiselLodVisible(Sphere sphere, int lodLevel, bool optimumUseChiselLodDistance)",
            source);
        Assert.Contains("!optimumUseChiselLodDistance || !OptimumConfig.ChiselLodEnabled", source);
        Assert.Contains("return distance <= chiselDistanceSq;", source);
        Assert.Contains("return distance > chiselDistanceSq;", source);
    }

    // Runtime counterpart of the shadow fix: vanilla's shadow cases never call
    // InFrustumAndRange, so a second Cecil hook folds each InFrustumShadowPass result
    // through OptimumApiBridge.InFrustumShadowPass.
    [Fact]
    public void ChiselLodShadowHookIsWiredIntoApiPatcher()
    {
        string patcher = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));

        Assert.Contains("PatchChiselLodShadowHook", patcher);
        Assert.Contains("method.Name == \"InFrustumShadowPass\" && method.Parameters.Count == 3", patcher);
        Assert.Contains("method.Name == \"InFrustumShadowPass\" &&", patcher);
        // Both shadow cases (near + far) must be hooked, hence the exact-count guard.
        Assert.Contains("Expected 2 chisel LOD shadow hooks", patcher);
        // The hook pushes culler (arg 2) and the location (this) after the vanilla call.
        Assert.Contains("Instruction.Create(OpCodes.Ldarg_2)", patcher);
        Assert.Contains("optimumShadowCheck", patcher);
    }

    // Regression: the bridge ignored lodLevel entirely. Because TesselatedChunkPart flags
    // ALL LOD levels of a chisel chunk part, LOD2 and LOD3 got the same answer - both visible
    // up close (z-fighting), both invisible past the chisel distance (block disappears).
    [Fact]
    public void ChiselLodBridgeBranchesOnLodLevel()
    {
        string source = File.ReadAllText(FindRepositoryFile("optimum-api-contracts/optimum-api-bridge.cs"));

        // The buggy shape: one distance test returned for every chisel-flagged location.
        Assert.DoesNotContain(
            "return playerPos.HorDistanceSqTo(sphere.x, sphere.z) < OptimumConfig.ChiselLodDistanceSq;",
            source);

        Assert.Contains("lodLevel == 2 || lodLevel == 3", source);
        Assert.Contains("lodLevel == 2", source);
        Assert.Contains("innerThreshold", source);
        Assert.Contains("outerThreshold", source);
        Assert.Contains("nowVisible ? outerThreshold : innerThreshold", source);
        // The base frustum / view-distance bound must still be enforced.
        Assert.Contains("culler.InFrustumAndRange(sphere, nowVisible, 1)", source);
        Assert.Contains("return culler.InFrustumAndRange(sphere, nowVisible, lodLevel);", source);

        // Shadow-pass bridge entry point used by the new Cecil hook.
        Assert.Contains(
            "public static bool InFrustumShadowPass(bool baseResult, FrustumCulling culler, ModelDataPoolLocation location)",
            source);
        Assert.Contains("location.FrustumCullSphere", source);
        Assert.Contains("location.LodLevel", source);
    }

    [Theory]
    [InlineData("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityMicroBlock.cs.patch")]
    public void ChiselLodMicroblockRecordsDiagnostics(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("RouteChiselLodMeshes = true", source);
        Assert.Contains("RouteChiselLodMeshes = false", source);
        Assert.Contains("OptimumDiagnostics.RecordChiselLod", source);
    }

    [Fact]
    public void ChiselLodCommandsRegisteredClientSide()
    {
        string source = File.ReadAllText(FindRepositoryFile("sources/VSEssentials/Systems/OptimumStatus.cs"));

        Assert.Contains("lodstats", source);
        Assert.Contains("lodreset", source);
        Assert.Contains("GetChiselLodSummary", source);
        Assert.Contains("ResetChiselLod", source);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
}

// Source: Optimum.Tests/ChunkMeshingQuickWinsCoverageTests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

public class ChunkMeshingQuickWinsCoverageTests
{
    [Fact]
    public void SortableQueueSortAlreadyReusesTheScratchBuffer()
    {
        // C2: already shipped before this batch started, part of the
        // open-source VintagestoryApi fork (no Cecil constraint). Assert
        // it stays that way.
        string source = File.ReadAllText(FindRepositoryFile("VintagestoryApi/Datastructures/SortableQueue.cs"));

        // expandArray() legitimately allocates T[maxSize] to grow the ring
        // buffer; the thing that should be gone is Sort()'s own copy of
        // that pattern, replaced by the sortBuffer reuse check below.
        int sortMethodStart = source.IndexOf("public void Sort()", System.StringComparison.Ordinal);
        int sortMethodEnd = source.IndexOf("public void RunForEach", System.StringComparison.Ordinal);
        Assert.True(sortMethodStart > 0 && sortMethodEnd > sortMethodStart, "Could not locate the Sort() method body.");
        string sortMethodBody = source[sortMethodStart..sortMethodEnd];

        // The old code unconditionally allocated `T[] newArray = new T[maxSize]`
        // every call; the fix reuses `sortBuffer`, only (re)allocating it
        // when missing or too small.
        Assert.DoesNotContain("newArray", sortMethodBody);
        Assert.Contains("if (sortBuffer == null || sortBuffer.Length < maxSize)", sortMethodBody);
    }

    [Fact]
    public void ChunkTesselatorManagerNoLongerUsesAnonymousDelegateInSource()
    {
        // C1: OnBeforeFrame used `tessChunksQueue.RunForEach(delegate(...) {...})`,
        // a closure the method can't carry through Cecil transplant.
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch");
        Assert.DoesNotContain("RunForEach(delegate", source);
    }

    [Fact]
    public void ChunkTesselatorManagerKeepsLockFieldsAsObjectNotLock()
    {
        // Deliberate: this file's (unshipped) System.Threading.Lock
        // migration compiles `lock` differently (Lock.EnterScope vs
        // Monitor.Enter/Exit). Cecil's field-reference remapping matches
        // by name only, not type, so transplanting OnBeforeFrame while
        // this field is Lock-typed but the vanilla target's field is still
        // object-typed would carry mismatched IL through undetected by
        // either safety check. Assert the fields stay object-typed here
        // until that migration is actually Cecil-registered.
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch");

        Assert.Contains("private readonly object tessChunksQueueLock = new object();", source);
        Assert.Contains("private readonly object tessChunksQueuePriorityLock = new object();", source);
        Assert.DoesNotContain("readonly Lock tessChunksQueueLock", source);
        Assert.DoesNotContain("readonly Lock tessChunksQueuePriorityLock", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch")]
    public void OnBeforeFrameSkipsSortWhenPlayerHasNotMoved(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("SortMoveThresholdSq = 0.25", source);
        Assert.Contains("SortYawThreshold = 0.05f", source);
        Assert.Contains("tessChunksQueue.RunForEach(_optimumRecalcPriority ??= OptimumRecalcPriority);", source);
        Assert.Contains("chunk.RecalcPriority(game.player);", source);
        Assert.Contains("OptimumDiagnostics.ChunkUploadSort.Hit()", source);
        Assert.Contains("OptimumDiagnostics.ChunkUploadSort.Skip()", source);
    }

    [Fact]
    public void OnBeforeFrameIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = PatcherSource.Read();
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselatorManager\", \"OnBeforeFrame\", 1", programSource);
    }

    [Fact]
    public void AddTesselatedChunkNoLongerAllocatesAFreshVec3iInSource()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch");
        Assert.DoesNotContain("new Vec3i(tesschunk.positionX", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch")]
    public void AddTesselatedChunkReusesTheScratchVec3i(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));
        Assert.Contains("(chunkOriginScratch ??= new Vec3i()).Set(tesschunk.positionX", source);
    }

    [Fact]
    public void AddTesselatedChunkIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = PatcherSource.Read();
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"AddTesselatedChunk\", 2", programSource);
    }

    [Fact]
    public void TesselatedChunkNoLongerAllocatesFreshPoolLocationListsInSource()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/TesselatedChunk.cs.patch");

        Assert.DoesNotContain("new List<ModelDataPoolLocation>(centerParts.Length);", source);
        Assert.DoesNotContain("new List<ModelDataPoolLocation>(edgeParts.Length);", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/TesselatedChunk.cs.patch")]
    public void TesselatedChunkReusesChunkRendererScratchLists(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("chunkRenderer.centerPoolLocationsScratch ??= new List<ModelDataPoolLocation>(centerParts.Length)", source);
        Assert.Contains("chunkRenderer.edgePoolLocationsScratch ??= new List<ModelDataPoolLocation>(edgeParts.Length)", source);
    }

    [Fact]
    public void AddCenterAndEdgeToPoolsAreRegisteredAsCecilTransplantTargets()
    {
        string programSource = PatcherSource.Read();

        Assert.Contains("\"Vintagestory.Client.NoObf.TesselatedChunk\", \"AddCenterToPools\", 5", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.TesselatedChunk\", \"AddEdgeToPools\", 5", programSource);
    }

    // Regression guard for the worker-pool wiring plan's Step 1: slotOwned must be
    // cleared at each enqueue branch, not only once at the end of the try block.
    // Otherwise a throw between an enqueue and the trailing assignment makes the
    // catch double-release the upload handoff slot, which OptimumBoundedHandoff.Release()
    // throws on (InvalidOperationException on an empty handoff), on the render thread.
    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch")]
    public void TesselateChunkClearsSlotOwnedAtEachEnqueueBranch(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        int occurrences = 0;
        int index = 0;
        while ((index = source.IndexOf("slotOwned = false;", index, StringComparison.Ordinal)) >= 0)
        {
            occurrences++;
            index += "slotOwned = false;".Length;
        }

        Assert.Equal(2, occurrences);
        Assert.DoesNotContain("slotOwned = !queued;", source);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
}
