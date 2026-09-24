// Source: Optimum.Tests/taa-acceptance-harness-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// TAA P5 acceptance + performance harness. Mechanical coverage only: the capture
/// script and the acceptance document exist, the document names every row of the
/// P5 acceptance matrix as TAA-PLAN.md words it, and the client's per-second
/// frame-time log is wired and shipped by the Cecil patcher.
/// </summary>
public class TaaAcceptanceHarnessCoverageTests
{
    /// <summary>
    /// The P5 acceptance matrix, verbatim from TAA-PLAN.md. Both the plan and
    /// docs/taa-acceptance.md must name every one of these.
    /// </summary>
    private static readonly string[] MatrixRows =
    {
        "moving silhouettes on contrast",
        "transparent foreground and background motion",
        "thin fences",
        "hand/world FOV",
        "quern/gear",
        "dropped items",
        "fire",
        "rain",
        "clouds",
        "aurora",
        "underwater transitions",
        "camera modes (shake, third person, mounted)",
        "reference rebase",
        "chunk replacement",
        "shader reload",
        "missing-resource fallback",
        "normal/scaled/mega screenshots",
    };

    [Fact]
    public void AcceptanceDocumentNamesEveryMatrixRowThePlanLists()
    {
        string plan = Collapse(Read("TAA-PLAN.md"));
        string doc = Collapse(Read("docs/taa-acceptance.md"));

        foreach (string row in MatrixRows)
        {
            Assert.Contains(row, plan, StringComparison.Ordinal);
            Assert.Contains(row, doc, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryMatrixRowIsANumberedChecklistRowWithItsFourParts()
    {
        string doc = Read("docs/taa-acceptance.md");

        for (int i = 0; i < MatrixRows.Length; i++)
        {
            string heading = "### A" + (i + 1) + ". " + MatrixRows[i];
            Assert.Contains(heading, doc, StringComparison.Ordinal);

            // Each row carries the scene, the commands, the pass criterion and the
            // measurement to record - the four parts the task asks a row to have.
            string body = Section(doc, heading);
            Assert.Contains("- Scene:", body);
            Assert.Contains("- Commands:", body);
            Assert.Contains("- Pass:", body);
            Assert.Contains("- Record:", body);
        }

        // The two non-visual rows the plan also demands, plus the byte-identical check.
        Assert.Contains("### A18. TAA off is byte-identical", doc);
        Assert.Contains("### P1. Performance on the Arc 140V", doc);
        Assert.Contains("### P2. Memory at 1080p", doc);
    }

    [Fact]
    public void AcceptanceDocumentFixesTheMeasurementAndTheFrozenScene()
    {
        string doc = Read("docs/taa-acceptance.md");

        // Still-frame luminance diff, from the parity skill: centre 60% crop, seven
        // pairs, compare medians.
        Assert.Contains("centre 60% crop", doc);
        Assert.Contains("seven pairs", doc);
        Assert.Contains("medians", doc);
        Assert.Contains("scripts/dev/luma-diff.py", doc);

        // Wind stilled, storms off, creative - otherwise the scene moves on its own.
        Assert.Contains("/weather setw still", doc);
        Assert.Contains("/weather setprecip -1", doc);
        Assert.Contains("/gamemode creative", doc);

        // A launch is not a verification.
        Assert.Contains("scripts/dev/client-renderer.sh", doc);
        Assert.Contains("scripts/dev/perf-capture.sh", doc);
    }

    [Fact]
    public void PerfCaptureScriptDrivesTheDocumentedRunThroughTheDevScripts()
    {
        string script = Read("scripts/dev/perf-capture.sh");

        Assert.Contains("scripts/dev/run-client.sh", script);
        Assert.Contains("scripts/dev/kill-client.sh", script);
        Assert.Contains("RENDERER=\"$RENDERER_ARG\"", script);
        Assert.Contains("[Client Chat] Welcome", script);
        Assert.Contains("OPTIMUM_FPS_LOG", script);
        Assert.Contains("OPTIMUM_VULKAN_STATS", script);
        Assert.Contains("SECONDS_WINDOW=30", script);
        Assert.Contains("WARMUP=8", script);
        Assert.Contains("data[\"Taa\"] = value", script);
        Assert.Contains("mean frame time", script);
        Assert.Contains("1%% low frame time", script);

        // The renderer is read back from the log, never assumed from the argument.
        Assert.Contains("(Vulkan|OpenGL) renderer", script);
        Assert.Contains("refusing to report numbers", script);

        // Rule 5: pattern-killing belongs to kill-client.sh alone.
        Assert.DoesNotContain("pkill", script);
        Assert.DoesNotContain("pgrep", script);
    }

    [Fact]
    public void ClientLogsFrameTimesPerSecondOnlyWhenTheEnvVarIsSet()
    {
        string clientMain = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        Assert.Contains("OptimumLogFrameTime(dt);", clientMain);
        Assert.Contains("Environment.GetEnvironmentVariable(\"OPTIMUM_FPS_LOG\")", clientMain);
        Assert.Contains("\"[Optimum] fps window={0:F3} frames={1} mean={2:F3} min={3:F3} max={4:F3} p99={5:F3} stddev={6:F3}\"", clientMain);
        // Off by default: no path, no work beyond the null check, so TAA off (and
        // every ordinary run) is unchanged.
        Assert.Contains("if (optimumFpsLogPath == null", clientMain);
        // One line per second, not per frame.
        Assert.Contains("if (optimumFpsLogSeconds < 1.0)", clientMain);
    }

    [Fact]
    public void CecilPatcherShipsTheFrameTimeLogMembers()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"optimumFpsLogPath\"", patcher);
        Assert.Contains("\"optimumFpsLogResolved\"", patcher);
        Assert.Contains("\"optimumFpsLogSamples\"", patcher);
        Assert.Contains("\"optimumFpsLogFrames\"", patcher);
        Assert.Contains("\"optimumFpsLogSeconds\"", patcher);
        Assert.Contains("\"OptimumLogFrameTime\"", patcher);
        // The caller is an existing transplant target; without it the injected
        // method would never run.
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"MainRenderLoop\", 1", patcher);
    }

    private static string Section(string document, string heading)
    {
        int start = document.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing heading: " + heading);
        int end = document.IndexOf("\n###", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? document.Substring(start) : document.Substring(start, end - start);
    }

    private static string Collapse(string value)
    {
        return Regex.Replace(value, "\\s+", " ");
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolvedPatch = TryFind(patchPath);
        return resolvedPatch != null ? PatchReader.ReadPatchedContent(resolvedPatch) : Read(sourcePath);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }

    private static string? TryFind(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
}

// Source: Optimum.Tests/taa-runtime-donor-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

/// <summary>
/// The TAA motion writers live in two places at once. The fork trees
/// (VSEssentials/, VSSurvivalMod/) are what a from-source build compiles, and
/// `patches/&lt;mod&gt;/**` is their checked-in record. The installed-launcher path
/// never sees those: it decompiles the user's own mod assemblies, applies
/// `patches/runtime/&lt;mod&gt;/**` to that decompiled tree, compiles it, and lets
/// Cecil transplant the method bodies named in Optimum.Patcher/mod-patcher.cs.
/// A mover instrumented only in the fork therefore keeps its vanilla body for
/// every installed player: it draws with no motion vector and ghosts on the
/// camera fallback, silently, with every fork test still green.
///
/// These tests pin the two sides together. They read only checked-in patch
/// files, never the git-ignored fork trees, so they run the same in a clean
/// clone as on a developer machine.
/// </summary>
public sealed class TaaRuntimeDonorCoverageTests
{
    /// <summary>
    /// The calls that mark a class as a TAA motion writer. A fork patch that
    /// adds any of them describes work the installed runtime needs too.
    /// </summary>
    private static readonly string[] MotionMarkers =
    [
        "OptimumStandardMotion.Apply",
        "OptimumMotionWrite.Begin",
        "OptimumMotionWrite.End",
        "OptimumInstanceMotion.CreateInstanceFloats",
        "OptimumInstanceMotion.WriteInstance",
        "OptimumInstanceMotion.NoteDevice",
        "OptimumInstanceMotion.ApplyPassUniforms",
        "OptimumInstanceMotion.InstanceFloats",
        "OptimumConfig.EffectiveTaa",
    ];

    /// <summary>
    /// Fork patch -> runtime donor patch, both repository-relative. The mapping
    /// is spelled out rather than derived because the two trees are shaped
    /// differently: the fork keeps its own folders and file names
    /// (Entities/EntityBlockFalling.cs holds ModSystemRenderFallingBlocksFast;
    /// AngledGearBlockRenderer.cs holds AngledGearsBlockRenderer) while ILSpy
    /// lays the donor out by namespace and names each file after its type.
    /// <see cref="EveryInstrumentedForkPatchIsListedHere"/> fails if a new
    /// motion writer appears in a fork patch without an entry here.
    /// </summary>
    private static readonly Dictionary<string, string> DonorByForkPatch = new()
    {
        ["patches/VSEssentials/Entities/EntityBlockFalling.cs.patch"] =
            "patches/runtime/VSEssentials/Vintagestory/GameContent/ModSystemRenderFallingBlocksFast.cs.patch",
        ["patches/VSEssentials/EntityRenderer/EntityItemRenderer.cs.patch"] =
            "patches/runtime/VSEssentials/Vintagestory/GameContent/EntityItemRenderer.cs.patch",
        ["patches/VSEssentials/EntityRenderer/EntityPlayerShapeRenderer.cs.patch"] =
            "patches/runtime/VSEssentials/Vintagestory/GameContent/EntityPlayerShapeRenderer.cs.patch",
        ["patches/VSEssentials/EntityRenderer/EntityShapeRenderer.cs.patch"] =
            "patches/runtime/VSEssentials/Vintagestory/GameContent/EntityShapeRenderer.cs.patch",
        ["patches/VSEssentials/EntityRenderer/ModSystemFpHands.cs.patch"] =
            "patches/runtime/VSEssentials/Vintagestory/GameContent/ModSystemFpHands.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/BloomeryContentsRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BloomeryContentsRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/FirepitContentsRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/FirepitContentsRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/ForgeContentsRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/ForgeContentsRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/FruitpressContentsRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/FruitpressContentsRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/HelveHammerRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/HelveHammerRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/PotInFirepitRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/PotInFirepitRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/QuernTopRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/QuernTopRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/ResonatorRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/ResonatorRenderer.cs.patch",
        ["patches/VSSurvivalMod/Lore/ResoArchives/EchoChamberRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/EchoChamberRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/AngledCageGearRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/AngledCageGearRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/AngledGearBlockRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/AngledGearsBlockRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/ClutchBlockRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/ClutchBlockRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/CreativeRotorRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/CreativeRotorRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/GenericMechBlockRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/GenericMechBlockRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/MechBlockRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/MechBlockRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/MechNetworkRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/MechNetworkRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/PulverizerRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/PulverizerRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/TransmissionBlockRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/TransmissionBlockRenderer.cs.patch",
    };

    public static IEnumerable<object[]> DonorPairs =>
        DonorByForkPatch.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new object[] { pair.Key, pair.Value });

    [Theory]
    [MemberData(nameof(DonorPairs))]
    public void RuntimeDonorCarriesTheSameMotionWritersAsTheFork(string forkPatch, string runtimePatch)
    {
        string forkPath = Path.Combine(RepoRoot(), forkPatch);
        string runtimePath = Path.Combine(RepoRoot(), runtimePatch);

        Assert.True(File.Exists(forkPath), $"{forkPatch} is missing; refresh it with scripts/extract-patches.sh.");
        Assert.True(
            File.Exists(runtimePath),
            $"{runtimePatch} is missing. The fork instruments this class for TAA but the installed-launcher " +
            "path has no donor for it, so Cecil would transplant a vanilla body and the surface would ghost.");

        var forkMarkers = MarkersAdded(forkPath);
        var runtimeMarkers = MarkersAdded(runtimePath);

        Assert.True(
            forkMarkers.Count > 0,
            $"{forkPatch} no longer adds any TAA motion call; drop its entry from DonorByForkPatch.");

        var missing = forkMarkers.Except(runtimeMarkers).OrderBy(m => m, StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            $"{runtimePatch} is behind {forkPatch}: the fork adds {string.Join(", ", missing)} but the runtime " +
            "donor does not. Regenerate the donor patch against a pristine .build/runtime-donors decompile.");

        // File-wide sets are not enough: a donor that writes the same markers
        // into some OTHER method of the same class passes that check while the
        // transplanted body still draws without motion. Compare per method
        // wherever the two trees are on disk (both git-ignored, so a clean clone
        // keeps only the file-wide check above).
        string? forkTree = PatchMethodScopes.FindPatchedTreeFile(RepoRoot(), forkPatch);
        string? donorTree = PatchMethodScopes.FindPatchedTreeFile(RepoRoot(), runtimePatch);
        if (forkTree is null || donorTree is null)
        {
            return;
        }

        var forkByMethod = PatchMethodScopes.MarkersByMethod(
            forkPath, File.ReadAllText(forkTree), MotionMarkers);
        var donorByMethod = PatchMethodScopes.MarkersByMethod(
            runtimePath, File.ReadAllText(donorTree), MotionMarkers);

        var misplaced = new List<string>();
        foreach (var (method, markers) in forkByMethod.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            donorByMethod.TryGetValue(method, out var donorMarkers);
            foreach (string marker in markers.OrderBy(m => m, StringComparer.Ordinal))
            {
                if (donorMarkers is null || !donorMarkers.Contains(marker))
                {
                    misplaced.Add($"{method}: {marker}");
                }
            }
        }

        Assert.True(
            misplaced.Count == 0,
            $"{runtimePatch} carries the fork's motion markers, but not in the same methods as " +
            $"{forkPatch}. Cecil transplants per method, so a marker in the wrong body still ships a " +
            "vanilla one:\n  " + string.Join("\n  ", misplaced));
    }

    [Fact]
    public void EveryInstrumentedForkPatchIsListedHere()
    {
        var instrumented = new List<string>();
        foreach (string project in new[] { "VSEssentials", "VSSurvivalMod", "VSCreativeMod" })
        {
            string dir = Path.Combine(RepoRoot(), "patches", project);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(dir, "*.patch", SearchOption.AllDirectories))
            {
                if (MarkersAdded(file).Count > 0)
                {
                    instrumented.Add(Relative(file));
                }
            }
        }

        var unlisted = instrumented
            .Where(path => !DonorByForkPatch.ContainsKey(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unlisted.Count == 0,
            "These fork patches add TAA motion calls but have no runtime donor mapping in " +
            "TaaRuntimeDonorCoverageTests.DonorByForkPatch, so the installed-launcher path would keep " +
            "vanilla bodies for them:\n  " + string.Join("\n  ", unlisted));
    }

    [Fact]
    public void EveryMappedForkPatchStillExists()
    {
        var gone = DonorByForkPatch.Keys
            .Where(path => !File.Exists(Path.Combine(RepoRoot(), path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            gone.Count == 0,
            "DonorByForkPatch names fork patches that no longer exist:\n  " + string.Join("\n  ", gone));
    }

    /// <summary>
    /// The motion calls a patch <em>adds</em> (+ lines only). Context lines are
    /// excluded on purpose: a call that merely sits next to an unrelated hunk is
    /// not this patch's work.
    /// </summary>
    private static HashSet<string> MarkersAdded(string patchFile)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(patchFile))
        {
            if (!line.StartsWith("+", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (string marker in MotionMarkers)
            {
                if (line.Contains(marker, StringComparison.Ordinal))
                {
                    found.Add(marker);
                }
            }
        }
        return found;
    }

    private static string RepoRoot() =>
        Path.GetDirectoryName(PatchReader.FindRepositoryFile("VERSION"))!;

    private static string Relative(string path) =>
        Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');
}
}
