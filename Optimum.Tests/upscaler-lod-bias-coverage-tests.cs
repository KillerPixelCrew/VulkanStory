using System;
using System.IO;
using Vintagestory.API.Config;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The texture LOD bias an upscaler asks for: how it is computed, what owns it,
/// how far the player may move it, and how it reaches the GPU.
///
/// <para><b>The rule (DLSS plan, Phase 6).</b> The bias an upscaler asks for (DLSS
/// Programming Guide section 3.5, log2(render / display) - 1) is published when the
/// vendor feature is created, which is after the shader load that made the terrain
/// sampler objects and after the frame's terrain was drawn - and a preset change
/// deliberately reloads no shaders. So the value has to be re-applied at
/// publication, at the shader load, on every preset change and when the upscaler
/// stands down, at both places it reaches the GPU: the parameter on the atlas
/// textures and the chunkopaque / chunktopsoil sampler objects that override
/// it.</para>
///
/// <para><b>"A published plan" is <c>UpscalerRenderScale</c>, never
/// <c>UpscalerLodBias</c>" (PR #3 review).</b> <c>EffectiveTerrainLodBias</c>
/// accumulates the render scale's own term and TAA's mip bias, and an active
/// upscaler is supposed to replace both with its own - "with an upscaler active, its
/// bias is the bias". The test for "active" was a non-zero bias, but zero is a
/// legitimate published bias: DLAA renders at the display size, so log2 of the ratio
/// is 0, and the offset slider makes 0 reachable at any preset. At bias 0 the
/// replacement was skipped and the accumulated terms stood - a -1 texture LOD bias
/// on a frame that is being rendered at the display resolution, which is exactly the
/// over-sharpening the upscaler path exists to avoid.</para>
///
/// <para><b>The offset is a setting (Phase 6 follow-up).</b> The user judged DLSS on
/// an RTX 4070 and reported "the lower the Quality, the more jitter comes back" -
/// clean at DLAA, visibly shimmering on foliage at Performance and Ultra
/// Performance. The bias is the term that grows with exactly that ratio, and the
/// DLSS Programming Guide 310.x says so itself in section 3.5: its recommended
/// <c>log2(render / display) - 1</c> "can sometimes lead to increased temporal
/// instability, in the form of flickering and/or moire", with high-frequency
/// textures (3.5.1) as the caveat case - which is what a 32px pixel-art block atlas
/// with alpha-tested foliage is. The guide's advice is a less aggressive bias, never
/// one above <c>log2(render / display)</c>. So the subtracted 1 is a slider,
/// defaulting to the guide's recommendation (the picture does not move unless the
/// player moves it), clamped so the bound cannot be crossed in either direction of
/// rounding, and applied live through the one applier without a shader reload, a
/// framebuffer rebuild, an NGX feature change or a history reset.</para>
///
/// <para><b>Cleanup.</b> These run against the real, process-global
/// <c>OptimumConfig</c> and this assembly runs its tests one at a time, so every
/// test that moves it restores the whole snapshot it found - never a set of
/// defaults. That rule is itself asserted, by
/// <c>OptimumConfigSnapshotTests.TheLodBiasSuitesRestoreTheConfigTheyFound</c>,
/// which counts the cleanups in this file.</para>
///
/// <para>The GPU half is
/// <c>Optimum.Render.Vulkan.Tests/TerrainLodBiasFollowsThePlanTests</c>, which
/// measures the bias really in effect on a device, and its
/// <c>TheBiasInEffectFollowsTheSharpnessSetting</c>, which measures what two offsets
/// really put on the samplers.</para>
/// </summary>
[Collection("OptimumConfigState")]
public class UpscalerLodBiasCoverageTests
{
    // ---- what the bias is ---------------------------------------------------

    /// <summary>
    /// The LOD bias every vendor asks for is computed from the sizes the SDK's own
    /// query returned, never hard-coded per preset, and it replaces the render-scale
    /// term rather than stacking with it.
    /// </summary>
    [Theory]
    [InlineData(1280, 2560, -2.0f)]   // performance, 1/2 -> log2(0.5) - 1
    [InlineData(1707, 2560, -1.585f)] // quality, 2/3-ish
    [InlineData(2560, 2560, 0f)]      // DLAA renders at display size: no bias at all
    [InlineData(0, 2560, 0f)]
    [InlineData(2560, 0, 0f)]
    public void TheRecommendedLodBiasIsLog2OfTheRatioMinusOne(int renderWidth, int displayWidth, float expected)
    {
        Assert.Equal(expected, OptimumConfig.RecommendedUpscalerLodBias(renderWidth, displayWidth), 3);
    }

    [Fact]
    public void TheUpscalerBiasReplacesTheRenderScaleTermAndIsGoneWhenTheUpscalerIsOff()
    {
        OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();
        try
        {
            OptimumConfig.SetUpscalerPlan(0.5f, -2.0f);
            OptimumConfig.RenderScale = 0.5f;

            // Off: the render-scale term alone, exactly as before this phase.
            OptimumConfig.Upscaler = "off";
            Assert.Equal(-1.0f, OptimumConfig.EffectiveTerrainLodBias, 3);

            // On: the vendor's bias, not the sum of the two.
            OptimumConfig.Upscaler = "dlss";
            Assert.Equal(-2.0f, OptimumConfig.EffectiveTerrainLodBias, 3);

            // Cleared (no feature): back to the old term.
            OptimumConfig.ClearUpscalerPlan();
            Assert.Equal(-1.0f, OptimumConfig.EffectiveTerrainLodBias, 3);
        }
        finally
        {
            snapshot.Restore();
        }
    }

    /// <summary>
    /// The regression: DLAA publishes scale 1 and bias 0, the render scale setting
    /// is at 0.5, and the bias that reaches the atlas samplers must be the plan's 0,
    /// not the -1 the render-scale term would otherwise contribute.
    /// </summary>
    [Fact]
    public void ADlaaPlanPublishesZeroBiasAndStillOwnsTheTerm()
    {
        OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();
        try
        {
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.RenderScale = 0.5f;
            OptimumConfig.Taa = true;
            OptimumConfig.TaaMipBias = -0.5f;

            // What the renderer publishes for DLAA: the ratio is 1, so the vendor's bias
            // is log2(1) - offset clamped at 0.
            OptimumConfig.SetUpscalerPlan(1.0f, 0f);

            Assert.Equal(1.0f, OptimumConfig.UpscalerRenderScale);
            Assert.Equal(0f, OptimumConfig.UpscalerLodBias);
            Assert.Equal(0f, OptimumConfig.EffectiveTerrainLodBias);
        }
        finally
        {
            snapshot.Restore();
        }
    }

    /// <summary>An upscaling plan's own bias still wins, which never regressed.</summary>
    [Fact]
    public void AnUpscalingPlanStillOwnsTheTerm()
    {
        OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();
        try
        {
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.RenderScale = 0.5f;
            OptimumConfig.Taa = true;
            OptimumConfig.TaaMipBias = -0.5f;

            OptimumConfig.SetUpscalerPlan(0.667f, -1.58f);

            Assert.Equal(-1.58f, OptimumConfig.EffectiveTerrainLodBias, 3);
        }
        finally
        {
            snapshot.Restore();
        }
    }

    /// <summary>
    /// And with no plan published the accumulated terms are still the answer: the new
    /// test must not claim a plan where there is none, or every frame before the
    /// first feature would drop the render scale's bias.
    /// </summary>
    [Fact]
    public void WithNoPlanPublishedTheAccumulatedTermsStand()
    {
        OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();
        try
        {
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.RenderScale = 0.5f;
            OptimumConfig.Taa = true;
            OptimumConfig.TaaMipBias = -0.5f;
            OptimumConfig.ClearUpscalerPlan();

            Assert.Equal(0f, OptimumConfig.UpscalerRenderScale);
            // log2(0.5), plus TAA's mip bias when our own resolve is really running
            // (another test in the run may have stood TAA down at runtime).
            float expected = -1.0f + (OptimumConfig.EffectiveTaa ? -0.5f : 0f);
            Assert.Equal(expected, OptimumConfig.EffectiveTerrainLodBias, 3);
        }
        finally
        {
            snapshot.Restore();
        }
    }

    [Fact]
    public void TheAppliedBiasRuleKeepsTheUpscalerOffPathUntouched()
    {
        // Every field this test moves - the setting, the published plan, the atlas
        // registration and the applied marker - is process-global and this assembly
        // does not parallelise, so the cleanup restores what it found instead of
        // forcing defaults onto whatever runs next.
        OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();
        try
        {
            OptimumConfig.Taa = false;
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.InvalidateTerrainLodBias();
            OptimumConfig.RegisterLodBiasedAtlases(new[] { 1 });

            // Nothing asks for a bias: nothing is pending, so no call is made.
            Assert.Equal(0f, OptimumConfig.EffectiveTerrainLodBias);
            Assert.False(OptimumConfig.TerrainLodBiasPending());

            // A plan moves it, and it stays pending until it has been applied.
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.SetUpscalerPlan(0.5f, -2.0f);
            Assert.Equal(-2.0f, OptimumConfig.EffectiveTerrainLodBias, 3);
            Assert.True(OptimumConfig.TerrainLodBiasPending());
            OptimumConfig.NoteTerrainLodBiasApplied(-2.0f, reachedAtlases: true);
            Assert.False(OptimumConfig.TerrainLodBiasPending());
            Assert.Equal(-2.0f, OptimumConfig.AppliedTerrainLodBias, 3);

            // A preset change is a new value, pending again.
            OptimumConfig.SetUpscalerPlan(1f / 3f, -2.585f);
            Assert.True(OptimumConfig.TerrainLodBiasPending());

            // Standing the upscaler down is a restore, not a no-op: 0 is pending
            // because something non-zero was written, and recording it puts the
            // state back to "never touched".
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.Upscaler = "off";
            Assert.Equal(0f, OptimumConfig.EffectiveTerrainLodBias);
            Assert.True(OptimumConfig.TerrainLodBiasPending());
            OptimumConfig.NoteTerrainLodBiasApplied(0f, reachedAtlases: true);
            Assert.True(float.IsNaN(OptimumConfig.AppliedTerrainLodBias));
            Assert.False(OptimumConfig.TerrainLodBiasPending());

            // An apply that reached no atlas is not recorded: the renderer can
            // publish a plan before the client has registered them, and the
            // per-frame poll has to finish the job.
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.SetUpscalerPlan(0.5f, -2.0f);
            OptimumConfig.NoteTerrainLodBiasApplied(-2.0f, reachedAtlases: false);
            Assert.True(OptimumConfig.TerrainLodBiasPending());
        }
        finally
        {
            snapshot.Restore();
        }
    }

    // ---- the offset slider --------------------------------------------------

    [Fact]
    public void TheOffsetDefaultsToTheGuideRecommendationAndCannotPassItsBound()
    {
        OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();
        try
        {
            // The default is the guide's own recommendation, so nothing moves
            // until the player moves it: the numbers the previous stage measured
            // on the atlases and the terrain samplers stand.
            Assert.Contains("public static float UpscalerLodBiasOffset = 1.0f;",
                Read("VintagestoryApi/Config/OptimumConfig.cs"));

            OptimumConfig.UpscalerLodBiasOffset = 1.0f;
            Assert.Equal(MathF.Log2(1707f / 2560f) - 1.0f, OptimumConfig.RecommendedUpscalerLodBias(1707, 2560), 4);
            Assert.Equal(-1.585f, OptimumConfig.RecommendedUpscalerLodBias(1707, 2560), 3);
            Assert.Equal(-2.000f, OptimumConfig.RecommendedUpscalerLodBias(1280, 2560), 3);
            Assert.Equal(-2.585f, OptimumConfig.RecommendedUpscalerLodBias(853, 2560), 2);

            // Half the offset is half a mip less sharp, at every ratio.
            OptimumConfig.UpscalerLodBiasOffset = 0.5f;
            Assert.Equal(-1.085f, OptimumConfig.RecommendedUpscalerLodBias(1707, 2560), 3);
            Assert.Equal(-1.500f, OptimumConfig.RecommendedUpscalerLodBias(1280, 2560), 3);

            // 0 is exactly the guide's upper bound, log2(render / display).
            OptimumConfig.UpscalerLodBiasOffset = 0f;
            Assert.Equal(MathF.Log2(1280f / 2560f), OptimumConfig.RecommendedUpscalerLodBias(1280, 2560), 5);

            // And the bound holds however the value got there: a hand-edited
            // config, a negative rounding, a value past 1.
            OptimumConfig.UpscalerLodBiasOffset = -0.4f;
            Assert.Equal(MathF.Log2(1280f / 2560f), OptimumConfig.RecommendedUpscalerLodBias(1280, 2560), 5);
            OptimumConfig.UpscalerLodBiasOffset = 7.5f;
            Assert.Equal(MathF.Log2(1280f / 2560f) - 1.0f, OptimumConfig.RecommendedUpscalerLodBias(1280, 2560), 5);
            foreach (float candidate in new[] { -1f, -0.0001f, 0f, 0.5f, 1f, 2f })
            {
                OptimumConfig.UpscalerLodBiasOffset = candidate;
                Assert.True(OptimumConfig.RecommendedUpscalerLodBias(1280, 2560) <= MathF.Log2(0.5f));
                Assert.True(OptimumConfig.RecommendedUpscalerLodBiasForScale(1f / 3f) <= MathF.Log2(1f / 3f));
            }

            // DLAA and "no upscale" stay at 0 - the value that means "do not
            // touch the sampler parameter at all" - whatever the offset says.
            OptimumConfig.UpscalerLodBiasOffset = 0.2f;
            Assert.Equal(0f, OptimumConfig.RecommendedUpscalerLodBias(2560, 2560));
            Assert.Equal(0f, OptimumConfig.RecommendedUpscalerLodBiasForScale(1.0f));
            Assert.Equal(0f, OptimumConfig.RecommendedUpscalerLodBiasForScale(0f));
        }
        finally
        {
            snapshot.Restore();
        }
    }

    [Fact]
    public void MovingTheOffsetRepublishesThePublishedPlansBias()
    {
        // The published plan and the applied marker are moved here too, and both
        // are process-global: restore the caller's, do not force defaults.
        OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();
        try
        {
            OptimumConfig.Taa = false;
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.UpscalerLodBiasOffset = 1.0f;
            OptimumConfig.SetUpscalerPlan(0.5f, OptimumConfig.RecommendedUpscalerLodBiasForScale(0.5f));
            Assert.Equal(-2.0f, OptimumConfig.EffectiveTerrainLodBias, 3);
            OptimumConfig.NoteTerrainLodBiasApplied(OptimumConfig.EffectiveTerrainLodBias, reachedAtlases: true);
            Assert.False(OptimumConfig.TerrainLodBiasPending());

            // The slider moves: the plan's ratio is untouched, the published bias
            // is re-derived from it, and the samplers are pending again.
            OptimumConfig.UpscalerLodBiasOffset = 0.25f;
            Assert.True(OptimumConfig.RepublishUpscalerLodBias());
            Assert.Equal(0.5f, OptimumConfig.UpscalerRenderScale, 5);
            Assert.Equal(-1.25f, OptimumConfig.UpscalerLodBias, 3);
            Assert.Equal(-1.25f, OptimumConfig.EffectiveTerrainLodBias, 3);
            Assert.True(OptimumConfig.TerrainLodBiasPending());

            // Idempotent: the same offset twice is not a second write.
            OptimumConfig.NoteTerrainLodBiasApplied(OptimumConfig.EffectiveTerrainLodBias, reachedAtlases: true);
            Assert.False(OptimumConfig.RepublishUpscalerLodBias());
            Assert.False(OptimumConfig.TerrainLodBiasPending());

            // With no plan published there is nothing to re-derive.
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.UpscalerLodBiasOffset = 0.75f;
            Assert.False(OptimumConfig.RepublishUpscalerLodBias());
            Assert.Equal(0f, OptimumConfig.UpscalerLodBias);
        }
        finally
        {
            snapshot.Restore();
        }
    }

    [Fact]
    public void TheRowShowsTheBiasThePresetEndsUpWith()
    {
        OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();
        try
        {
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.UpscalerLodBiasOffset = 1.0f;

            // Before a feature exists the preset's nominal ratio describes it -
            // the numbers the user was shown in game.
            OptimumConfig.UpscalerQuality = "quality";
            Assert.Equal(-1.585f, OptimumConfig.PreviewUpscalerLodBias(), 3);
            OptimumConfig.UpscalerQuality = "performance";
            Assert.Equal(-2.0f, OptimumConfig.PreviewUpscalerLodBias(), 3);
            OptimumConfig.UpscalerQuality = "ultraperformance";
            Assert.Equal(-2.585f, OptimumConfig.PreviewUpscalerLodBias(), 3);
            OptimumConfig.UpscalerQuality = "dlaa";
            Assert.Equal(0f, OptimumConfig.PreviewUpscalerLodBias());

            // Once a plan is published the row shows the vendor's real ratio.
            OptimumConfig.SetUpscalerPlan(0.6f, OptimumConfig.RecommendedUpscalerLodBiasForScale(0.6f));
            Assert.Equal(MathF.Log2(0.6f) - 1.0f, OptimumConfig.PreviewUpscalerLodBias(), 3);

            OptimumConfig.UpscalerLodBiasOffset = 0.4f;
            Assert.Equal(MathF.Log2(0.6f) - 0.4f, OptimumConfig.PreviewUpscalerLodBias(), 3);
        }
        finally
        {
            snapshot.Restore();
        }
    }

    [Fact]
    public void TheSettingIsPersistedWithTheOtherUpscalerRows()
    {
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("public static float UpscalerLodBiasOffset = 1.0f;", config);
        Assert.Contains("public float UpscalerLodBiasOffset { get; set; } = 1.0f;", config);
        Assert.Contains("(nameof(OptimumConfigData.UpscalerLodBiasOffset), UpscalerLodBiasOffset.ToString(\"F2\")),", config);
        Assert.Contains("UpscalerLodBiasOffset = Math.Clamp(data.UpscalerLodBiasOffset, 0f, 1.0f);", config);
        Assert.Contains("UpscalerLodBiasOffset = UpscalerLodBiasOffset,", config);
        // The bound is enforced where the bias is computed as well as on load.
        Assert.Contains("return MathF.Min(bound - offset, bound);", config);
    }

    [Fact]
    public void TheTabHasTheRowAndAppliesItLive()
    {
        string gui = PatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");

        // The row itself, in the rhythm of the rows beside it: label, tooltip,
        // slider, placed with the upscaler and preset rows.
        Assert.Contains("Lang.Get(\"optimum-upscalersharpness\")", gui);
        Assert.Contains("Lang.Get(\"optimum-upscalersharpness-tooltip\")", gui);
        Assert.Contains("onOptimumUpscalerSharpnessChanged", gui);
        Assert.Contains("\"optUpscalerSharpness\"", gui);

        // Disabled while nothing upscales, exactly as the quality row is - and
        // "nothing upscales" means any upscaler that owns the resolve, not DLSS
        // alone: the passthrough upscaler samples the atlases at its own render
        // ratio too, so the bias is the player's choice there as well.
        Assert.Contains("sharpness.Enabled = Vintagestory.API.Config.OptimumConfig.UpscalerReplacesTaa;", gui);
        // And it shows the bias the choice produces.
        Assert.Contains("optimumUpscalerLodBiasText()", gui);
        Assert.Contains("OptimumConfig.PreviewUpscalerLodBias()", gui);

        // The handler: persist, re-derive the published plan's bias, one applier.
        Assert.Contains("OptimumConfig.UpscalerLodBiasOffset = Math.Clamp(val / 100f, 0f, 1f);", gui);
        Assert.Contains("OptimumConfig.RepublishUpscalerLodBias();", gui);
        Assert.Contains("ShaderRegistry.ApplyOptimumLodBias();", gui);

        // Nothing structural: the render size does not change with the bias, so
        // the handler must not rebuild targets, reload shaders, re-plan through
        // the platform or throw the history away.
        string handler = Between(gui, "private bool onOptimumUpscalerSharpnessChanged(int val)", "return true;");
        Assert.DoesNotContain("RebuildFrameBuffers", handler);
        Assert.DoesNotContain("ReloadShaders", handler);
        Assert.DoesNotContain("ApplyOptimumUpscalerSettings", handler);
        Assert.DoesNotContain("RequestReset", handler);
    }

    [Fact]
    public void CecilPatcherShipsTheRowAndItsHandler()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"onOptimumUpscalerSharpnessChanged\"", patcher);
        Assert.Contains("\"optimumUpscalerLodBiasText\"", patcher);
        // The composer and the row updater carry the row, so both are transplanted.
        Assert.Contains("\"OnOptimumOptions\"", patcher);
        Assert.Contains("\"optimumUpdateUpscalerRows\"", patcher);
    }

    [Fact]
    public void TheLanguageKeysExist()
    {
        string lang = Read("sources/lang/en.json");

        Assert.Contains("\"optimum-upscalersharpness\":", lang);
        Assert.Contains("\"optimum-upscalersharpness-tooltip\":", lang);
        // The tooltip says what the trade is, in the player's terms.
        int tip = lang.IndexOf("\"optimum-upscalersharpness-tooltip\":", StringComparison.Ordinal);
        string line = lang.Substring(tip, lang.IndexOf('\n', tip) - tip);
        Assert.Contains("temporal stability", line);
        Assert.Contains("sharpness", line);
    }

    // ---- how the bias reaches the GPU --------------------------------------

    [Fact]
    public void OneApplierWritesBothCallSitesAndRemembersWhatItWrote()
    {
        string registry = PatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");

        Assert.Contains("public static void ApplyOptimumLodBias()", registry);
        Assert.Contains("if (!OptimumConfig.TerrainLodBiasPending())", registry);
        Assert.Contains("int[] atlases = OptimumConfig.LodBiasedAtlases;", registry);
        Assert.Contains("platform.SetTextureLodBias(atlases, bias);", registry);
        Assert.Contains("ApplyOptimumTerrainSamplerLodBias(bias);", registry);
        Assert.Contains("OptimumConfig.NoteTerrainLodBiasApplied(bias, reachedAtlases);", registry);
        // The shader load creates new sampler objects at the driver default, so
        // it forgets what was applied and writes again - no reload needed
        // anywhere else for the same reason.
        Assert.Contains("OptimumConfig.InvalidateTerrainLodBias();", registry);
        Assert.Contains("ApplyOptimumLodBias();", registry);
    }

    /// <summary>
    /// The stand-down re-applies the bias only once the game is running - and the reason is
    /// not the bias. ShaderRegistry's static constructor runs registerDefaultShaderProgramsPre,
    /// which publishes a fresh, uncompiled program into every ShaderPrograms.* field,
    /// ShaderPrograms.Gui included. Vanilla first touches the type in
    /// ScreenManager.DoGameInitStage2; the startup stand-down (the GL framebuffer setup with the
    /// setting on "dlss", or NGX refusing on Vulkan) runs long before that, so an unguarded call
    /// ran the constructor early and the loading screen's next ShaderProgramBase.Use() found
    /// ShaderPrograms.Gui with no uniforms compiled: KeyNotFoundException 'lightPosition', and
    /// OpenGL with an upscaler configured never reached a world (2026-09-13 - 3 of 3 runs
    /// crashed, and the same build with only this call removed reached the world). A string
    /// test is the right tool here: the defect is a type-initialisation order inside the
    /// patched client, which no device test can see, and the in-game A/B above is the evidence
    /// that the guard is what fixes it.
    /// </summary>
    [Fact]
    public void TheStandDownReachesShaderRegistryOnlyFromTheRunningGame()
    {
        string platform = PatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        int start = platform.IndexOf("public void DisableOptimumUpscaler(string reason)", StringComparison.Ordinal);
        Assert.True(start > 0, "DisableOptimumUpscaler not found");
        // PatchedOrSource hands back the raw patch when it exists, so every line may carry
        // its diff prefix: the method's closing brace is "+\t}" there and "\t}" in the source.
        Match close = new Regex(@"\n[+ ]?\t\}").Match(platform, start);
        Assert.True(close.Success, "DisableOptimumUpscaler has no closing brace");
        string body = platform.Substring(start, close.Index - start);

        int guard = body.IndexOf(
            "screenManager.CurrentScreen is Vintagestory.Client.GuiScreenRunningGame", StringComparison.Ordinal);
        int apply = body.IndexOf("ShaderRegistry.ApplyOptimumLodBias();", StringComparison.Ordinal);
        Assert.True(guard > 0, "the re-apply must sit behind the running-game guard");
        Assert.True(apply > guard, "nothing may reach ShaderRegistry before the guard");
        // One touch of the type, and it is the guarded one.
        Assert.Equal(1, Count(body, "ShaderRegistry."));
    }

    [Fact]
    public void TheRendererReAppliesWheneverThePlanMoves()
    {
        string upscale = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs");

        // Four call sites, one per way the effective value moves: a DLSS feature was
        // created (the plan is published), a passthrough frame blitted (the same
        // publication, from the ratio the targets were really allocated at), the tab
        // changed the preset or turned the slot off (the plan is cleared and no shader
        // is reloaded), and the upscaler was shut down with the platform.
        Assert.Equal(4, Count(upscale, "ShaderRegistry.ApplyOptimumLodBias();"));
        int blitted = upscale.IndexOf("PassthroughUpscaler.Publish(plan);", StringComparison.Ordinal);
        Assert.True(blitted > 0);
        Assert.True(upscale.IndexOf("ShaderRegistry.ApplyOptimumLodBias();", blitted, StringComparison.Ordinal) > blitted);
        int created = upscale.IndexOf("if (!upscaler.EnsureFeature(plan)) return false;", StringComparison.Ordinal);
        Assert.True(created > 0);
        Assert.True(upscale.IndexOf("ShaderRegistry.ApplyOptimumLodBias();", created, StringComparison.Ordinal) > created);

        int settings = upscale.IndexOf("base.ApplyOptimumUpscalerSettings();", StringComparison.Ordinal);
        Assert.True(settings > 0);
        Assert.True(upscale.IndexOf("ShaderRegistry.ApplyOptimumLodBias();", settings, StringComparison.Ordinal) > settings);
    }

    [Fact]
    public void ThePollRegistersEveryAtlasThatIsSampledWithDerivatives()
    {
        string chunkRenderer = PatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");

        Assert.Contains("RegisterLodBiasedAtlases(CollectOptimumLodBiasedAtlases())", chunkRenderer);
        // Block atlases and entity atlases: both are world geometry sampled with
        // screen-space derivatives, so both alias the same way below native
        // resolution. The item atlas is left out on purpose - the GUI draws
        // inventory icons from it at display resolution.
        Assert.Contains("EntityTextureAtlasManager entityAtlases = game.EntityAtlasManager;", chunkRenderer);
        Assert.Contains("ids[next++] = entityTextures[j].TextureId;", chunkRenderer);
        Assert.DoesNotContain("ItemAtlasManager", chunkRenderer);
        // A new atlas texture carries the driver default, so both places that
        // produce one forget the applied value.
        Assert.Contains("OptimumConfig.InvalidateTerrainLodBias();", chunkRenderer);

        string clientMain = PatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");
        Assert.Contains("OptimumConfig.InvalidateTerrainLodBias();", clientMain);
    }

    [Fact]
    public void CecilPatcherShipsEveryLodBiasMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"ApplyOptimumLodBias\"", patcher);
        Assert.Contains("\"ApplyOptimumTerrainSamplerLodBias\"", patcher);
        Assert.Contains("\"ApplyOptimumSamplerLodBias\"", patcher);
        Assert.Contains("\"ApplyOptimumTextureLodBias\"", patcher);
        Assert.Contains("\"CollectOptimumLodBiasedAtlases\"", patcher);
        Assert.DoesNotContain("\"SetOptimumTextureLodBias\"", patcher);
        Assert.DoesNotContain("\"optimumTextureLodBias\"", patcher);
        // The bodies the calls live in are transplanted too.
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"OnBeforeRenderOpaque\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"RuntimeAddBlockTextureAtlas\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"loadRegisteredShaderPrograms\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"ReloadTextures\", 0", patcher);
    }

    /// <summary>
    /// The sampler fixture gives its atlas images back to the shared device, and the
    /// config it moved back to whoever set it.
    /// </summary>
    [Fact]
    public void TheTerrainSamplerFixtureReleasesWhatItAllocated()
    {
        string tests = Read("Optimum.Render.Vulkan.Tests/TerrainLodBiasFollowsThePlanTests.cs");
        string dispose = Between(tests, "public void Dispose()", "private void Log(");

        Assert.Contains("_seam.DeleteTexture(_atlases[i]);", dispose);
        Assert.Contains("RegisterLodBiasedAtlases(_previousAtlases)", dispose);
        Assert.Contains("NoteTerrainLodBiasApplied(_previousAppliedBias", dispose);
        // The registration is the fixture's to restore, so no test may clear it.
        Assert.DoesNotContain("RegisterLodBiasedAtlases(Array.Empty<int>())", tests);
    }

    // ---- helpers -----------------------------------------------------------

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

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from > 0, "missing: " + start);
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "missing: " + end);
        return text.Substring(from, to - from);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    private static string PatchedOrSource(string patchPath, string sourcePath)
    {
        try
        {
            return Read(patchPath);
        }
        catch (FileNotFoundException)
        {
            return Read(sourcePath);
        }
    }
}
