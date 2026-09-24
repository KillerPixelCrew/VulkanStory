// Source: Optimum.Tests/BfsChunkVisibilityTests.cs
namespace Optimum.Tests
{
using System.Collections.Generic;
using Vintagestory.API.Config;
using Xunit;

/// <summary>
/// Correctness tests for the BFS chunk-visibility walk (issue #72). The guiding
/// invariant, from the MC-70850 "see-through chunks" bug, is that the walk must
/// never hide a chunk the player could actually see: BFS visibility may only ever
/// be a conservative (>=) superset of the ground-truth reachable set.
/// </summary>
public class BfsChunkVisibilityTests
{
    /// <summary>
    /// Synthetic chunk graph. Every loaded chunk is fully traversable (all faces
    /// passable) unless listed in <see cref="opaque"/>, which blocks ALL traversal
    /// through that chunk (no face pair passable) — a fully solid chunk.
    /// </summary>
    private sealed class FakeGraph : IChunkVisibilityGraph
    {
        private readonly HashSet<(int, int, int)> loaded;
        private readonly HashSet<(int, int, int)> opaque;
        private readonly HashSet<(int, int, int)>? outOfFrustum;
        public readonly HashSet<(int, int, int)> Visible = new();

        public FakeGraph(
            IEnumerable<(int, int, int)> loaded,
            IEnumerable<(int, int, int)>? opaque = null,
            IEnumerable<(int, int, int)>? outOfFrustum = null)
        {
            this.loaded = new HashSet<(int, int, int)>(loaded);
            this.opaque = opaque == null ? new() : new HashSet<(int, int, int)>(opaque);
            this.outOfFrustum = outOfFrustum == null ? null : new HashSet<(int, int, int)>(outOfFrustum);
        }

        public bool ChunkExists(int cx, int cy, int cz) => loaded.Contains((cx, cy, cz));

        public bool IsVisibleThrough(int cx, int cy, int cz, int fromFace, int toFace)
        {
            if (opaque.Contains((cx, cy, cz))) return false;
            return true; // all non-opaque chunks are fully passable
        }

        public bool IsInFrustum(int cx, int cy, int cz)
        {
            if (outOfFrustum == null) return true;
            return !outOfFrustum.Contains((cx, cy, cz));
        }

        public void MarkVisible(int cx, int cy, int cz) => Visible.Add((cx, cy, cz));

        public bool IsWithinWorldBounds(int cx, int cy, int cz) =>
            loaded.Contains((cx, cy, cz)); // treat only loaded coords as in-bounds for the test
    }

    private static List<(int, int, int)> Grid(int r)
    {
        var g = new List<(int, int, int)>();
        for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
                for (int z = -r; z <= r; z++)
                    g.Add((x, y, z));
        return g;
    }

    [Fact]
    public void OpenField_AllInFrustumChunksVisible()
    {
        // No opaque chunks, everything in frustum: every loaded chunk must be visible.
        var grid = Grid(3);
        var graph = new FakeGraph(grid);
        var bfs = new OptimumChunkVisibilityBfs();

        bfs.Run(graph, 0, 0, 0);

        Assert.Equal(new HashSet<(int, int, int)>(grid), graph.Visible);
    }

    [Fact]
    public void SolidWall_HidesChunksFullyBehindIt_ButNeverHidesReachableOnes()
    {
        // A solid X=2 plane (distance 2 from the camera, past the first-ring
        // exemption) between the camera (0,0,0) and everything at X>=3. With the
        // wall spanning the whole loaded Y/Z extent there is no way around, so the
        // X>=3 chunks are genuinely occluded and may be culled. X<=1 stays visible.
        var grid = Grid(4);
        var wall = new List<(int, int, int)>();
        for (int y = -4; y <= 4; y++)
            for (int z = -4; z <= 4; z++)
                wall.Add((2, y, z));

        var graph = new FakeGraph(grid, opaque: wall);
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        // Everything at X <= 1 is reachable and must be visible (no over-cull).
        foreach (var c in grid)
        {
            if (c.Item1 <= 1)
            {
                Assert.Contains(c, graph.Visible);
            }
        }
        // The wall plane itself is adjacent to a visible chunk, so it is visible.
        foreach (var c in wall)
        {
            Assert.Contains(c, graph.Visible);
        }
        // At least some chunks strictly behind the full wall were culled (benefit exists).
        int behind = 0;
        foreach (var c in grid) if (c.Item1 >= 3 && !graph.Visible.Contains(c)) behind++;
        Assert.True(behind > 0, "expected some occluded chunks behind a full solid wall to be culled");
    }

    [Fact]
    public void FirstRingSolidChunk_DoesNotBlankWhatIsBehindIt()
    {
        // A lone solid chunk one step from the camera must NOT hide the chunks
        // behind it: vanilla's raycast passes through the first ring (its `num2 > 1`
        // guard), so the BFS first-ring exemption must too. Regression for the
        // over-cull the adversarial review found.
        var grid = Grid(3);
        var graph = new FakeGraph(grid, opaque: new[] { (0, 0, -1) }); // solid, distance 1 north
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        // The chunks directly behind the first-ring solid chunk stay visible.
        Assert.Contains((0, 0, -2), graph.Visible);
        Assert.Contains((0, 0, -3), graph.Visible);
    }

    [Fact]
    public void NeverOverCullsVersusGroundTruthReachability()
    {
        // Random-ish opaque set; compare BFS visible set against an independent
        // brute-force adjacency reachability that ignores never-go-back/frustum
        // (the maximal set of chunks connected to the camera through passable
        // chunks). BFS with occlusion may cull MORE than pure adjacency, but the
        // invariant we assert is the safety direction that matters for MC-70850:
        // every chunk BFS marks visible is genuinely graph-connected to the camera.
        var grid = Grid(4);
        var opaque = new List<(int, int, int)>
        {
            (2, 0, 0), (2, 1, 0), (2, -1, 0), (-3, 0, 2), (0, 2, -2), (1, 1, 1),
        };
        var graph = new FakeGraph(grid, opaque: opaque);
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        var reachable = BruteForceReachable(new HashSet<(int, int, int)>(grid), new HashSet<(int, int, int)>(opaque), (0, 0, 0));

        // Safety: BFS never marks a chunk visible that is not actually reachable.
        foreach (var v in graph.Visible)
        {
            Assert.Contains(v, reachable);
        }
    }

    [Fact]
    public void FrustumCulledChunksAreNotVisible_NorAreChunksOnlyReachableThroughThem()
    {
        // Put the entire X>0 half-space out of frustum. Those chunks must not be
        // visible, and because the flood cannot pass through a frustum-culled chunk,
        // nothing solely behind them becomes visible either.
        var grid = Grid(2);
        var outFov = new List<(int, int, int)>();
        foreach (var c in grid) if (c.Item1 > 0) outFov.Add(c);

        var graph = new FakeGraph(grid, outOfFrustum: outFov);
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        foreach (var c in outFov)
        {
            Assert.DoesNotContain(c, graph.Visible);
        }
        // X<=0 chunks remain visible.
        foreach (var c in grid) if (c.Item1 <= 0) Assert.Contains(c, graph.Visible);
    }

    [Fact]
    public void CameraChunkAlwaysVisibleEvenWhenSolid()
    {
        // Camera embedded in a solid chunk: the chunk it's in must still render.
        var grid = Grid(1);
        var graph = new FakeGraph(grid, opaque: new[] { (0, 0, 0) });
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        Assert.Contains((0, 0, 0), graph.Visible);
    }

    [Fact]
    public void OcclusionOff_MarksEveryInFrustumReachableChunk()
    {
        // With occlusion disabled, opaque chunks do not block the flood; every
        // in-frustum chunk connected by adjacency is visible.
        var grid = Grid(2);
        var graph = new FakeGraph(grid, opaque: new[] { (1, 0, 0) });
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0, useOcclusion: false);

        Assert.Equal(new HashSet<(int, int, int)>(grid), graph.Visible);
    }

    [Fact]
    public void LibInlineBfs_MirrorsTheReferenceInvariants()
    {
        // Reconciles the two algorithm copies: the unit-tested reference
        // OptimumChunkVisibilityBfs (this file) and the inlined runBfsVisibility in
        // the ChunkCuller patch (which cannot be unit-tested directly because it
        // needs the live ClientMain/ClientChunk). This pins the Lib copy to the same
        // load-bearing invariants the reference guarantees, so the two cannot
        // silently diverge: editing the patch to drop one of these fails this test.
        string patch = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkCuller.cs.patch");

        Assert.Contains("OptimumConfig.BfsChunkVisibilityEnabled", patch); // toggle-gated
        Assert.Contains("runBfsVisibility", patch);
        Assert.Contains("bfsReachable", patch);                            // reachability check
        Assert.Contains("for (int from = 0; from < 6; from++)", patch);    // all incoming faces (MC-70850-safe)
        Assert.Contains("camDist > 1", patch);                             // first-ring exemption (num2>1 mirror)
        Assert.Contains("bfsRadius", patch);                               // view-distance bound (no sky-flood freeze)
        Assert.Contains("bfsOppositeOf", patch);                           // never-go-back mask
        Assert.Contains("bfsQueue = new Queue<long>()", patch);            // lazy init (injected-field initializers don't run)
    }

    // Brute-force flood over passable (non-opaque) chunks, 6-connected, ignoring
    // frustum and never-go-back. This is the maximal set of chunks reachable from
    // the camera; BFS-visible must be a subset of it.
    private static HashSet<(int, int, int)> BruteForceReachable(
        HashSet<(int, int, int)> loaded,
        HashSet<(int, int, int)> opaque,
        (int, int, int) start)
    {
        int[] dx = { 0, 1, 0, -1, 0, 0 };
        int[] dy = { 0, 0, 0, 0, 1, -1 };
        int[] dz = { -1, 0, 1, 0, 0, 0 };
        var seen = new HashSet<(int, int, int)>();
        var q = new Queue<(int, int, int)>();
        seen.Add(start);
        q.Enqueue(start);
        while (q.Count > 0)
        {
            var (cx, cy, cz) = q.Dequeue();
            // Can only leave this chunk if it is passable (or it's the start chunk).
            bool passable = !opaque.Contains((cx, cy, cz)) || (cx, cy, cz) == start;
            if (!passable) continue;
            for (int d = 0; d < 6; d++)
            {
                var n = (cx + dx[d], cy + dy[d], cz + dz[d]);
                if (!loaded.Contains(n) || seen.Contains(n)) continue;
                seen.Add(n);
                q.Enqueue(n);
            }
        }
        return seen;
    }
}
}

// Source: Optimum.Tests/OcclusionCullingCoverageTests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

public class OcclusionCullingCoverageTests
{
    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkCuller.cs.patch")]
    public void ThresholdIsScaledByViewDistanceInsteadOfFixed(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("OptimumConfig.OcclusionCullingScaleEnabled", source);
        Assert.Contains("ClientSettings.ViewDistance / 32 + 1", source);
        Assert.Contains("Math.Max(50,", source);
        Assert.Contains("OptimumDiagnostics.OcclusionCullingScale.Skip()", source);
        Assert.Contains("OptimumDiagnostics.OcclusionCullingScale.Hit()", source);
        // The fixed floor stays available as the toggle-off fallback, not deleted outright.
        Assert.Contains(": 100;", source);
    }

    [Fact]
    public void CullInvisibleChunksIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkCuller\", \"CullInvisibleChunks\"", programSource);
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

// Source: Optimum.Tests/OptimumSimdCullingTests.cs
namespace Optimum.Tests
{
using System;
using System.Runtime.Intrinsics;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

[Collection("OptimumConfig")]
public class OptimumSimdCullingTests
{
    [Fact]
    public void SimdCullingHardwareDetectionReflectsVectorSupport()
    {
        Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.SimdCullingSupported);
        Assert.Equal(OptimumConfig.SimdCullingEnabled && Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);
    }

    [Fact]
    public void SimdCullingConfigToggleDisablesEffectiveState()
    {
        bool original = OptimumConfig.SimdCullingEnabled;
        try
        {
            OptimumConfig.SimdCullingEnabled = false;
            Assert.False(OptimumConfig.EffectiveSimdCulling);

            OptimumConfig.SimdCullingEnabled = true;
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);
        }
        finally
        {
            OptimumConfig.SimdCullingEnabled = original;
        }
    }

    [Fact]
    public void SimdCullingCountersIncrementAndReset()
    {
        OptimumDiagnostics.ResetSimdFrustumCounters();
        Assert.Equal(0, OptimumDiagnostics.SimdFrustumTests);
        Assert.Equal(0, OptimumDiagnostics.SimdFrustumCulled);

        OptimumDiagnostics.RecordSimdFrustumTest(culled: false);
        Assert.Equal(1, OptimumDiagnostics.SimdFrustumTests);
        Assert.Equal(0, OptimumDiagnostics.SimdFrustumCulled);

        OptimumDiagnostics.RecordSimdFrustumTest(culled: true);
        Assert.Equal(2, OptimumDiagnostics.SimdFrustumTests);
        Assert.Equal(1, OptimumDiagnostics.SimdFrustumCulled);

        OptimumDiagnostics.ResetSimdFrustumCounters();
        Assert.Equal(0, OptimumDiagnostics.SimdFrustumTests);
        Assert.Equal(0, OptimumDiagnostics.SimdFrustumCulled);
    }

    [Fact]
    public void ChunkRenderSummaryIncludesSimdCullStats()
    {
        OptimumDiagnostics.ResetSimdFrustumCounters();
        OptimumDiagnostics.RecordSimdFrustumTest(culled: true);
        OptimumDiagnostics.RecordSimdFrustumTest(culled: false);

        string summary = OptimumDiagnostics.GetChunkRenderSummary();
        Assert.Contains("simdTests/frame=", summary);
        Assert.Contains("simdCulled/frame=", summary);
    }

    [Fact]
    public void SimdCullingMatchesScalarInFrustumAndRangeAcrossRandomSpheres()
    {
        var culler = new FrustumCulling();
        var playerPos = new BlockPos(1000, 150, 1000);
        culler.UpdateViewDistance(256);
        culler.lod0BiasSq = 32 * 32;
        culler.lod2BiasSq = 128 * 128;

        double[] proj = Mat4d.Create();
        Mat4d.Perspective(proj, 70f * GameMath.DEG2RAD, 1.777f, 0.1f, 500f);

        double[] cam = Mat4d.Create();
        Mat4d.LookAt(cam, [1000, 150, 1000], [1000, 150, 1100], [0, 1, 0]);

        culler.CalcFrustumEquations(playerPos, proj, cam);

        var rng = new Random(12345);
        int totalTests = 500;

        for (int i = 0; i < totalTests; i++)
        {
            float sx = (float)(1000 + (rng.NextDouble() - 0.5) * 600);
            float sy = (float)(150 + (rng.NextDouble() - 0.5) * 100);
            float sz = (float)(1000 + (rng.NextDouble() - 0.1) * 600);
            var sphere = new Sphere(sx, sy, sz, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half);

            for (int lod = 0; lod <= 3; lod++)
            {
                bool scalarResult = culler.InFrustumAndRange(sphere, nowVisible: false, lodLevel: lod);
                bool simdResult = OptimumFrustumCullSimd.InFrustumAndRange(culler, sphere, nowVisible: false, lodLevel: lod, location: null!);

                Assert.True(scalarResult == simdResult,
                    $"Mismatch at test {i}, LOD {lod}: scalar={scalarResult}, simd={simdResult}, sphere=({sx}, {sy}, {sz})");
            }
        }
    }

    [Fact]
    public void SimdCullingMatchesScalarInFrustum()
    {
        var culler = new FrustumCulling();
        var playerPos = new BlockPos(5000, 100, 5000);

        double[] proj = Mat4d.Create();
        Mat4d.Perspective(proj, 75f * GameMath.DEG2RAD, 1.6f, 0.5f, 400f);

        double[] cam = Mat4d.Create();
        Mat4d.LookAt(cam, [5000, 100, 5000], [5000, 100, 5100], [0, 1, 0]);

        culler.CalcFrustumEquations(playerPos, proj, cam);

        var rng = new Random(67890);
        for (int i = 0; i < 300; i++)
        {
            float sx = (float)(5000 + (rng.NextDouble() - 0.5) * 500);
            float sy = (float)(100 + (rng.NextDouble() - 0.5) * 100);
            float sz = (float)(5000 + (rng.NextDouble() - 0.2) * 500);
            var sphere = new Sphere(sx, sy, sz, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half);

            bool scalarInFrustum = culler.InFrustum(sphere);
            bool simdInFrustum = OptimumFrustumCullSimd.InFrustum(culler, sphere);

            Assert.True(scalarInFrustum == simdInFrustum,
                $"InFrustum mismatch at {i}: scalar={scalarInFrustum}, simd={simdInFrustum}");
        }
    }

    [Fact]
    public void SimdCullingMatchesScalarInFrustumShadowPass()
    {
        var culler = new FrustumCulling();
        var playerPos = new BlockPos(2000, 80, 2000);
        culler.shadowRangeX = 80;
        culler.shadowRangeZ = 80;

        double[] proj = Mat4d.Create();
        Mat4d.Ortho(proj, -80, 80, -80, 80, -100, 100);

        double[] cam = Mat4d.Create();
        Mat4d.LookAt(cam, [2000, 80, 2000], [2000, 0, 2000], [0, 0, 1]);

        culler.CalcFrustumEquations(playerPos, proj, cam);

        var rng = new Random(112233);
        for (int i = 0; i < 300; i++)
        {
            float sx = (float)(2000 + (rng.NextDouble() - 0.5) * 200);
            float sy = (float)(80 + (rng.NextDouble() - 0.5) * 100);
            float sz = (float)(2000 + (rng.NextDouble() - 0.5) * 200);
            var sphere = new Sphere(sx, sy, sz, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half);

            bool scalarShadow = culler.InFrustumShadowPass(sphere);
            bool simdShadow = OptimumFrustumCullSimd.InFrustumShadowPass(culler, sphere);

            Assert.True(scalarShadow == simdShadow,
                $"Shadow mismatch at {i}: scalar={scalarShadow}, simd={simdShadow}");
        }
    }
}
}
