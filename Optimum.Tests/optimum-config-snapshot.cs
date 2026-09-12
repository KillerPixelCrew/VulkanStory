using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The whole of the upscaler state a test may move, captured and put back.
///
/// <c>OptimumConfig</c> is process-global static state and this assembly runs its
/// tests one at a time (<c>CollectionBehavior(DisableTestParallelization = true)</c>
/// in <c>AssemblyInfo.cs</c>), so a cleanup that <i>forces defaults</i> instead of
/// restoring what it found is not a cleanup: it hands the next test in the run a
/// different world than the one it would have had. Two of the LOD-bias tests used
/// to end with <c>Upscaler = "off"</c> and <c>ClearUpscalerPlan()</c> whatever the
/// caller's state had been (CodeRabbit, PR #3, second round).
///
/// Restoring is ordered: the plan first (publishing or clearing one invalidates the
/// applied marker), then the atlas registration, then the applied marker last, so
/// the "Optimum never touched the sampler parameter" state (NaN) is restored as
/// itself rather than as a concrete bias.
/// </summary>
internal readonly struct OptimumConfigSnapshot
{
    private readonly string _upscaler;
    private readonly string _quality;
    private readonly bool _taa;
    private readonly float _taaMipBias;
    private readonly float _renderScale;
    private readonly float _lodBiasOffset;
    private readonly float _planRenderScale;
    private readonly float _planLodBias;
    private readonly int[] _atlases;
    private readonly float _appliedBias;

    private OptimumConfigSnapshot(
        string upscaler, string quality, bool taa, float taaMipBias, float renderScale, float lodBiasOffset,
        float planRenderScale, float planLodBias, int[] atlases, float appliedBias)
    {
        _upscaler = upscaler;
        _quality = quality;
        _taa = taa;
        _taaMipBias = taaMipBias;
        _renderScale = renderScale;
        _lodBiasOffset = lodBiasOffset;
        _planRenderScale = planRenderScale;
        _planLodBias = planLodBias;
        _atlases = atlases;
        _appliedBias = appliedBias;
    }

    public static OptimumConfigSnapshot Capture() => new(
        OptimumConfig.Upscaler,
        OptimumConfig.UpscalerQuality,
        OptimumConfig.Taa,
        OptimumConfig.TaaMipBias,
        OptimumConfig.RenderScale,
        OptimumConfig.UpscalerLodBiasOffset,
        OptimumConfig.UpscalerRenderScale,
        OptimumConfig.UpscalerLodBias,
        OptimumConfig.LodBiasedAtlases,
        OptimumConfig.AppliedTerrainLodBias);

    public void Restore()
    {
        OptimumConfig.Upscaler = _upscaler;
        OptimumConfig.UpscalerQuality = _quality;
        OptimumConfig.Taa = _taa;
        OptimumConfig.TaaMipBias = _taaMipBias;
        OptimumConfig.RenderScale = _renderScale;
        OptimumConfig.UpscalerLodBiasOffset = _lodBiasOffset;

        // "A plan is published" is the render scale, never the bias: a DLAA plan
        // carries a bias of 0 and is still a plan.
        if (_planRenderScale > 0f) OptimumConfig.SetUpscalerPlan(_planRenderScale, _planLodBias);
        else OptimumConfig.ClearUpscalerPlan();

        OptimumConfig.RegisterLodBiasedAtlases(_atlases);

        OptimumConfig.InvalidateTerrainLodBias();
        if (!float.IsNaN(_appliedBias)) OptimumConfig.NoteTerrainLodBiasApplied(_appliedBias, reachedAtlases: true);
    }
}

/// <summary>
/// The snapshot itself, and the rule that the suites which move
/// <c>OptimumConfig</c> restore through it (CodeRabbit, PR #3, second round).
///
/// The failure this guards is not a wrong value inside one test: it is the state a
/// test leaves behind for the next one. The old cleanups forced
/// <c>Upscaler = "off"</c> and <c>ClearUpscalerPlan()</c>, so a run that reached
/// them with an upscaler selected and a plan published handed the next test a
/// different world - and this assembly runs its tests one at a time, so that is the
/// run.
/// </summary>
[Collection("OptimumConfigState")]
public class OptimumConfigSnapshotTests
{
    /// <summary>
    /// The whole snapshot goes back, not a set of defaults.
    ///
    /// This is the finding itself, driven on the config: put the process into a
    /// state no default matches, let a "test" move every field, restore, and
    /// require each one back.
    /// </summary>
    [Fact]
    public void TheConfigSnapshotRestoresEveryFieldItCaptured()
    {
        OptimumConfigSnapshot outer = OptimumConfigSnapshot.Capture();
        try
        {
            int[] atlases = { 7, 11 };
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.UpscalerQuality = "quality";
            OptimumConfig.Taa = true;
            OptimumConfig.TaaMipBias = -0.5f;
            OptimumConfig.RenderScale = 0.75f;
            OptimumConfig.UpscalerLodBiasOffset = 0.25f;
            OptimumConfig.SetUpscalerPlan(0.6f, -1.25f);
            OptimumConfig.RegisterLodBiasedAtlases(atlases);
            OptimumConfig.NoteTerrainLodBiasApplied(-1.25f, reachedAtlases: true);

            OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();

            // What a test does to it, ending the way the old cleanups ended.
            OptimumConfig.Upscaler = "off";
            OptimumConfig.UpscalerQuality = "ultraperformance";
            OptimumConfig.Taa = false;
            OptimumConfig.TaaMipBias = 0f;
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.UpscalerLodBiasOffset = 1.0f;
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.RegisterLodBiasedAtlases(Array.Empty<int>());
            OptimumConfig.InvalidateTerrainLodBias();

            snapshot.Restore();

            Assert.Equal("dlss", OptimumConfig.Upscaler);
            Assert.Equal("quality", OptimumConfig.UpscalerQuality);
            Assert.True(OptimumConfig.Taa);
            Assert.Equal(-0.5f, OptimumConfig.TaaMipBias, 5);
            Assert.Equal(0.75f, OptimumConfig.RenderScale, 5);
            Assert.Equal(0.25f, OptimumConfig.UpscalerLodBiasOffset, 5);
            Assert.Equal(0.6f, OptimumConfig.UpscalerRenderScale, 5);
            Assert.Equal(-1.25f, OptimumConfig.UpscalerLodBias, 5);
            Assert.Same(atlases, OptimumConfig.LodBiasedAtlases);
            Assert.Equal(-1.25f, OptimumConfig.AppliedTerrainLodBias, 5);

            // "Optimum has never touched the parameter" is a state of its own and
            // must not come back as a concrete bias.
            OptimumConfig.InvalidateTerrainLodBias();
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfigSnapshot untouched = OptimumConfigSnapshot.Capture();
            OptimumConfig.SetUpscalerPlan(0.5f, -2.0f);
            OptimumConfig.NoteTerrainLodBiasApplied(-2.0f, reachedAtlases: true);
            untouched.Restore();
            Assert.True(float.IsNaN(OptimumConfig.AppliedTerrainLodBias));
            Assert.Equal(0f, OptimumConfig.UpscalerRenderScale);
        }
        finally
        {
            outer.Restore();
        }
    }

    /// <summary>
    /// The LOD-bias suite restores through the snapshot instead of forcing defaults.
    /// </summary>
    [Fact]
    public void TheLodBiasSuitesRestoreTheConfigTheyFound()
    {
        foreach (string path in new[]
        {
            "Optimum.Tests/upscaler-lod-bias-coverage-tests.cs",
        })
        {
            string source = Read(path);
            Assert.Contains("OptimumConfigSnapshot.Capture()", source);
            // Every cleanup in these files is the snapshot's, nothing else: a
            // hand-written finally is what dropped a field on the floor.
            Assert.Equal(Count(source, "finally"), Count(source, "snapshot.Restore();"));
            Assert.Equal(Count(source, "finally"), Count(source, "OptimumConfigSnapshot.Capture()"));
        }
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
