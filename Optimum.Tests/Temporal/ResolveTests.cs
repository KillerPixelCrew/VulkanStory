// Source: Optimum.Tests/taa-antiflicker-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.Diagnostics;
using System.IO;
using Xunit;

/// <summary>
/// Source coverage for the 2026-09-11 distant-foliage fix in taa-resolve.fsh
/// (TAA-PLAN.md "Follow-up 2026-09-11: distant foliage jitter was the resolve").
///
/// Root cause: a single-sample disocclusion test rejected history on ~3.7% of
/// distant leaf pixels per frame on both backends (a sub-pixel leaf hits the leaf
/// in one jitter phase and the far background in the next), and a fixed current
/// weight let the clip box drag the history. The 3x3 nearest-depth test and the
/// anti-flicker weight took it to ~1.1%; the user confirmed the flicker gone on
/// Vulkan. These tests pin the shader text, the "do not revert" guard, the
/// documents that record the finding and the rejection-rate script. The numbers
/// are proven by Optimum.Render.Vulkan.Tests/TaaResolveTests.
/// </summary>
public class TaaAntiFlickerCoverageTests
{
    private static readonly string[] GpuTests =
    {
        "AntiFlickerWeightsFollowTheLuminanceDifference",
        "FlippingSubPixelLeafKeepsItsHistory",
        "DisocclusionLargerThanTheNeighbourhoodStillResets",
        "MotionComesFromTheNearestDepthTapAtAnEdge",
    };

    [Fact]
    public void TheCurrentWeightFollowsTheLuminanceDifferenceForKeptPixelsOnly()
    {
        string resolve = Read("sources/shaders/taa-resolve.fsh");

        // Every rejection path marks the pixel, and only unmarked pixels are reweighted.
        Assert.Contains("bool rejected = resetHistory != 0 || offscreen;", resolve);
        int nanReset = resolve.IndexOf("historyLinear = linearDepth;", StringComparison.Ordinal);
        Assert.True(nanReset >= 0);
        Assert.True(resolve.IndexOf("rejected = true;", nanReset, StringComparison.Ordinal)
                    < resolve.IndexOf("float historyNearest", StringComparison.Ordinal),
            "the NaN-history reset no longer marks the pixel as rejected");

        const string weight = "alpha = mix(blendAlpha * 1.2, blendAlpha * 0.3, unbiasedWeight * unbiasedWeight);";
        string[] ordered =
        {
            "vec3 histYcc = clipToBox(clipMin, clipMax, rgbToYCoCg(history.rgb), clipKeep);",
            "vec3 curYcc = rgbToYCoCg(current.rgb);",
            "if (!rejected)",
            "float lumCur = max(curYcc.x, 0.0);",
            "float lumHist = max(histYcc.x, 0.0);",
            "float unbiasedDiff = abs(lumCur - lumHist) / max(lumCur, max(lumHist, 0.2));",
            "float unbiasedWeight = 1.0 - unbiasedDiff;",
            weight,
            "alpha = max(alpha, reactive);",
            "float wCur = alpha / (1.0 + curYcc.x);",
        };
        AssertInOrder(resolve, ordered);

        // Reactive is applied once, after the weighting, never before it.
        Assert.Equal(1, Count(resolve, "alpha = max(alpha, reactive);"));
        Assert.Equal(1, Count(resolve, weight));
    }

    [Fact]
    public void TheDisocclusionTestComparesTheNearestDepthOnBothSides()
    {
        string resolve = Read("sources/shaders/taa-resolve.fsh");

        // Current side: the nearest window depth of the 3x3, its tap and its linear depth.
        AssertInOrder(resolve, new[]
        {
            "float closestDepth = 2.0;",
            "ivec2 closestPixel = pixel;",
            "for (int y = -1; y <= 1; y++)",
            "float tapDepth = texelFetch(depthTex, p, 0).r;",
            "if (tapDepth < closestDepth) { closestDepth = tapDepth; closestPixel = p; }",
            "vec4 current = filteredWeight > 1e-4 ? filtered / filteredWeight : centreSample;",
        });
        Assert.Contains("vec4 closestH = invViewProjJittered * vec4(closestNdc, closestDepth * 2.0 - 1.0, 1.0);", resolve);
        Assert.Contains("float closestLinearDepth = -(viewMatrix * vec4(closestWorld, 1.0)).z;", resolve);

        // History side: the nearest finite depth of the 3x3 around the reprojected point.
        AssertInOrder(resolve, new[]
        {
            "float historyNearest = historyLinear;",
            "for (int hx = -1; hx <= 1; hx++)",
            "float h = texture(historyDepth, historyUv + vec2(hx, hy) * invSize).r;",
            "if (!isnan(h) && !isinf(h)) historyNearest = min(historyNearest, h);",
            "float depthTolerance = 0.5 + 0.08 * closestLinearDepth;",
            "if (abs(historyNearest - closestLinearDepth) > depthTolerance) { alpha = 1.0; rejected = true; }",
        });

        // Not the single-sample test it replaced.
        Assert.DoesNotContain("float depthTolerance = 0.5 + 0.08 * linearDepth;", resolve);
        Assert.DoesNotContain("abs(historyLinear - linearDepth)", resolve);

        // The motion vector comes from the same tap; the lookup anchor, reactive and the
        // stored history depth stay this pixel's own (contract v1 unchanged).
        Assert.Contains("vec4 motion = texelFetch(motionTex, closestPixel, 0);", resolve);
        Assert.Contains("vec2 currentUnjittered = closestCentre - jitterPx;", resolve);
        Assert.Contains("prevViewProj * vec4(closestWorld + cameraDelta, 1.0)", resolve);
        Assert.Contains("vec2 historyUv = (pixelCentre + mv) * invSize;", resolve);
        Assert.Contains("float reactive = clamp(texelFetch(motionTex, pixel, 0).b, 0.0, 1.0);", resolve);
        Assert.Equal(2, Count(resolve, "outDepth = vec4(linearDepth);"));
        Assert.DoesNotContain("outDepth = vec4(closestLinearDepth)", resolve);
    }

    [Fact]
    public void TheShaderSaysNeverToRevertAndNamesTheTestsThatExist()
    {
        string resolve = Read("sources/shaders/taa-resolve.fsh");
        string gpu = Read("Optimum.Render.Vulkan.Tests/TaaResolveTests.cs");

        Assert.Contains("2026-09-11: distant foliage jitter was THIS test", resolve);
        Assert.Contains("DO NOT REVERT to a single-sample depth test.", resolve);
        Assert.Contains("DO NOT REVERT to a fixed blend weight.", resolve);
        Assert.Contains("~3.7%", resolve);
        Assert.Contains("~1.1%", resolve);
        Assert.Contains("scripts/dev/taa-rejection.py", resolve);
        Assert.Contains("TaaAntiFlickerCoverageTests", resolve);

        foreach (string test in GpuTests)
        {
            Assert.Contains("TaaResolveTests." + test, resolve);
            Assert.Contains("public void " + test + "()", gpu);
        }
    }

    [Fact]
    public void TheFindingIsRecordedInThePlanTheContractAndBothAcceptanceDocuments()
    {
        string contract = Read("docs/temporal-frame-contract.md");
        string section4 = Between(contract, "## 4. The resolve's own inputs", "## 5. Reset");
        Assert.Contains("Note (2026-09-11): anti-flicker weighting and nearest-depth disocclusion.", section4);
        Assert.Contains("the contract stays **v1**", section4);
        Assert.Contains("~3.7%", section4);
        Assert.Contains("~1.1%", section4);
        Assert.Contains("**Never revert**", section4);
        Assert.Contains("`alpha = mix(blendAlpha * 1.2, blendAlpha * 0.3, w * w)`", section4);
        Assert.Contains("`0.5 + 0.08 * closestLinearDepth`", section4);
        Assert.Contains("scripts/dev/taa-rejection.py", section4);
        foreach (string test in GpuTests)
        {
            Assert.Contains(test, section4);
        }

        string plan = Read("TAA-PLAN.md");
        string followUp = Between(plan, "## Follow-up 2026-09-11: distant foliage jitter was the resolve", "## Follow-up (not part of this plan)");
        foreach (string needle in new[] { "3.7%", "1.1%", "scripts/dev/taa-rejection.py", "Do not revert" })
        {
            Assert.Contains(needle, followUp);
        }
        foreach (string test in GpuTests)
        {
            Assert.Contains(test, followUp);
        }

        string taaAcceptance = Read("docs/taa-acceptance.md");
        string row = Between(taaAcceptance, "### A19. distant foliage stability", "## 3. Performance");
        Assert.Contains("- Scene:", row);
        Assert.Contains("- Commands:", row);
        Assert.Contains("- Pass:", row);
        Assert.Contains("- Record:", row);
        Assert.Contains("scripts/dev/parity-capture.sh", row);
        Assert.Contains("python3 scripts/dev/taa-rejection.py", row);
        Assert.Contains("<= 1.5 percent", row);
        Assert.Contains("default two frames in flight", row);

        string vulkanAcceptance = Read("docs/vulkan-acceptance.md");
        string m17 = Between(vulkanAcceptance, "#### M1.7 TAA still-frame stability", "#### M1.8");
        Assert.Contains("python3 scripts/dev/taa-rejection.py", m17);
        Assert.Contains("**required**", m17);
    }

    [Fact]
    public void TaaRejectionSelfTestPasses()
    {
        string script = PatchReader.FindRepositoryFile("scripts/dev/taa-rejection.py");
        string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(script)))!;
        var start = new ProcessStartInfo(TestToolchain.Python)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("scripts/dev/taa-rejection.py");
        start.ArgumentList.Add("--self-test");
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(180_000), "taa-rejection.py --self-test did not finish");
        Assert.True(process.ExitCode == 0, "taa-rejection.py --self-test exited " + process.ExitCode + ": " + stdout + stderr);
        Assert.Contains("taa-rejection.py self-test: ok", stdout);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertInOrder(string source, string[] needles)
    {
        int last = -1;
        foreach (string needle in needles)
        {
            int index = source.IndexOf(needle, last + 1, StringComparison.Ordinal);
            Assert.True(index > last, "missing or out of order in taa-resolve.fsh: " + needle);
            last = index;
        }
    }

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing: " + start);
        int to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, "missing after '" + start + "': " + end);
        return source.Substring(from, to - from);
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
}

// Source: Optimum.Tests/taa-sharpen-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vintagestory.API.Config;
using Xunit;

/// <summary>
/// TAA-PLAN.md P5: the post-resolve sharpen pass (taa-sharpen) and the TAA mip
/// bias. Covers the pieces the GPU harness cannot see - registration, target
/// lifecycle, the pass's placement in the post chain, the no-double-sharpening
/// rule against FSR 1's RCAS, and the two LOD-bias call sites.
/// </summary>
public class TaaSharpenCoverageTests
{
    // --- the shader pair ----------------------------------------------------

    [Fact]
    public void SharpenShaderPairExistsAndIsAFullscreenTriangle()
    {
        string vertex = Read("sources/shaders/taa-sharpen.vsh");
        Assert.Contains("gl_VertexID", vertex);
        // No vertex inputs: the pass is drawn with RenderFullscreenTriangle,
        // which binds no vertex buffer on the device path.
        Assert.DoesNotContain("in vec", vertex);
    }

    [Fact]
    public void SharpenShaderTakesASharpnessUniformInsteadOfTheBakedRcasConstant()
    {
        string fragment = Read("sources/shaders/taa-sharpen.fsh");
        Assert.Contains("uniform float sharpness;", fragment);
        Assert.Contains("uniform sampler2D inputScene;", fragment);
        Assert.Contains("uniform vec2 inputTexelSize;", fragment);
        // fsr-rcas.fsh bakes the strength in as exp2(-0.2); this one must not.
        Assert.DoesNotContain("lobe *= exp2(-0.2);", fragment);
        Assert.Contains("strength * exp2(", fragment);
    }

    [Fact]
    public void SharpnessZeroIsATrueBypassBeforeAnyFilteringOrClamping()
    {
        string fragment = Read("sources/shaders/taa-sharpen.fsh");

        int bypass = fragment.IndexOf("if (!(sharpness > 0.0))", StringComparison.Ordinal);
        Assert.True(bypass >= 0, "the bypass must be an explicit early-out, not lobe = 0");
        // It returns the centre texel itself, unmodified, and does so before the
        // first ring tap - otherwise "off" would not be bit-for-bit identical.
        int returned = fragment.IndexOf("outColor = center;", bypass, StringComparison.Ordinal);
        Assert.True(returned > bypass);
        int firstTap = fragment.IndexOf("vec3 b = texture(", StringComparison.Ordinal);
        Assert.True(returned < firstTap);
        Assert.True(fragment.IndexOf("return;", returned, StringComparison.Ordinal) > returned);
    }

    [Fact]
    public void SharpenKeepsHdrRangeInsteadOfClampingToOne()
    {
        string fragment = Read("sources/shaders/taa-sharpen.fsh");
        // The resolve writes RGBA16F; fsr-rcas.fsh's clamp(x, 0, 1) would crush
        // every value above 1 that reaches this pass.
        Assert.DoesNotContain("clamp(sharpened, 0.0, 1.0)", fragment);
        Assert.Contains("max(sharpened, vec3(0.0))", fragment);
    }

    // --- registration -------------------------------------------------------

    [Fact]
    public void SharpenProgramIsRegisteredAsAnOptionalOptimumProgram()
    {
        string programs = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderPrograms.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderPrograms.cs");
        Assert.Contains("public static ShaderProgram TaaSharpen;", programs);

        string registry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        Assert.Contains(
            "RegisterOptimumShaderProgram(\"taa-sharpen\", ShaderPrograms.TaaSharpen = new ShaderProgram());",
            registry);
        // Optional exactly like taa-resolve: a failed compile sets LoadError on
        // the program instead of failing the whole shader load.
        Assert.Contains("shaderProgram == ShaderPrograms.TaaSharpen", registry);
    }

    // --- target lifecycle ---------------------------------------------------

    [Fact]
    public void SharpenTargetIsCreatedWithTheHistoryTargetsOnBothPaths()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.Contains("private const int OptimumTaaSharpenIndex = 21;", platform);
        // Device path (VulkanClientPlatform since Phase 1A step 4): RGBA16F, render resolution.
        string vulkan = VulkanPlatformSource.Read();
        Assert.Contains("private const int OptimumTaaSharpenIndex = 21;", vulkan);
        Assert.Contains(
            "list[OptimumTaaSharpenIndex] = CreateOptimumColorTarget(width, height,",
            vulkan);
        Assert.Contains("EnumTextureInternalFormat.Rgba16f);", vulkan);
        // GL path: the same format token (GL_RGBA16F) through setupAttachment.
        Assert.Contains("setupAttachment(optimumSharpen, num, num2, 0, val, (PixelInternalFormat)34842);", platform);
        // Both live inside the taaRequested block, i.e. they are allocated and
        // released with the history slots (DisposeFrameBuffers walks the list).
        int historyDevice = vulkan.IndexOf("list[OptimumTaaHistoryIndexA] = CreateOptimumHistoryTarget(", StringComparison.Ordinal);
        int sharpenDevice = vulkan.IndexOf("list[OptimumTaaSharpenIndex] = CreateOptimumColorTarget(", StringComparison.Ordinal);
        Assert.True(historyDevice >= 0 && sharpenDevice > historyDevice);
        int historyGl = platform.IndexOf("list[OptimumTaaHistoryIndexA] = CreateOptimumHistoryTargetGl(", StringComparison.Ordinal);
        int sharpenGl = platform.IndexOf("FrameBufferRef optimumSharpen = (list[OptimumTaaSharpenIndex]", StringComparison.Ordinal);
        Assert.True(historyGl >= 0 && sharpenGl > historyGl);
    }

    [Fact]
    public void ASharpenTargetFailureCostsTheSharpeningNotTaa()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // Neither failure path may call DisableOptimumTaa - TAA without the
        // sharpen pass is a working configuration. GL here, device in VulkanClientPlatform.
        string vulkan = VulkanPlatformSource.Read();
        Assert.Equal(1, Count(platform, "Optimum disabled the TAA sharpen pass"));
        Assert.Equal(1, Count(platform, "list[OptimumTaaSharpenIndex] = null;"));
        Assert.Equal(1, Count(vulkan, "Optimum disabled the TAA sharpen pass"));
        Assert.Equal(1, Count(vulkan, "list[OptimumTaaSharpenIndex] = null;"));
    }

    // --- the pass -----------------------------------------------------------

    [Fact]
    public void SharpenRunsAfterLateOverlaysAndBeforeThePlainBlit()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        int resolve = IndexOfCode(platform, "RenderOptimumTaaResolve();");
        Assert.True(resolve >= 0);
        int bloom = platform.IndexOf("if (RenderBloom)", resolve, StringComparison.Ordinal);
        Assert.True(bloom > resolve);
        Assert.DoesNotContain("postSceneTexture = RenderOptimumTaaSharpen(postSceneTexture);", platform);
        // Bloom and god rays consume the resolved scene. Final composition leaves its composited
        // Primary output for AfterFinalComposition overlays; the blit boundary sharpens that
        // complete image into slot 21. FSR uses Primary instead.
        int finalComposition = platform.IndexOf("public override void RenderFinalComposition()", bloom,
            StringComparison.Ordinal);
        Assert.True(finalComposition > bloom);
        string glBlit = MethodBody(platform, "public override void BlitPrimaryToDefault()");
        Assert.DoesNotContain("RenderOptimumTaaSharpen(frameBuffers[0].ColorTextureIds[0]);",
            MethodBody(platform, "public override void RenderFinalComposition()"));
        Assert.Contains("scene2D = RenderOptimumTaaSharpen(scene2D);", glBlit);
        Assert.True(glBlit.IndexOf("RenderOptimumTaaSharpen(scene2D);", StringComparison.Ordinal) <
            glBlit.IndexOf("ShaderProgramBlit blit = ShaderPrograms.Blit;", StringComparison.Ordinal));
        Assert.Contains("findbright.ColorTex2D = postSceneTexture;", platform);
        Assert.Contains("godrays.InputTexture2D = postSceneTexture;", platform);
    }

    [Fact]
    public void DistinguishableLateOverlayReachesPresentationOnGlAndNativeVulkan()
    {
        // The scheduler patch contains only its own hunk, not the complete Render method. Use the
        // existing platform presentation seam instead: both routes begin from the current Primary
        // image at BlitPrimaryToDefault, after the client's late-composition callback.
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        string glBlit = MethodBody(platform, "public override void BlitPrimaryToDefault()");
        Assert.Contains("scene2D = RenderOptimumTaaSharpen(scene2D);", glBlit);
        Assert.Contains("blit.Scene2D = scene2D;", glBlit);
        Assert.Contains("if (!useFsr)", glBlit);
        Assert.Contains("bool useFsr = OptimumFsrBlitActive();", glBlit);
        Assert.Contains("int primaryScene = scene2D;", glBlit);
        Assert.Contains("OptimumApiBridge.SelectTaaPresentationTexture", glBlit);

        string nativeBlit = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeBlit.cs");
        string nativeFinal = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativePostFinal.cs");
        string nativePresentation = MethodBody(nativeBlit, "private void RenderNativeBlit()");
        Assert.DoesNotContain("PostStepTaaSharpen", nativeFinal);
        Assert.Contains("scene2D = RenderOptimumTaaSharpen(scene2D);", nativePresentation);
        Assert.Contains("int finalScene = NativeFinalBlitSceneTexture(scene2D, useFsr);", nativePresentation);
        Assert.Contains("new[] { finalScene }", nativePresentation);
        Assert.Contains("OptimumApiBridge.SelectTaaPresentationTexture", nativeBlit);
        // Slot 21 remains the sharpen output, while bypass and FSR retain Primary as input.
        Assert.Contains("return target.ColorTextureIds[0];", platform);
        Assert.Contains("SelectTaaPresentationTexture", nativeBlit);
    }

    [Fact]
    public void LatePrimaryOverlaySurvivesPresentationDataflowOnBothRoutes()
    {
        // This drives the production presentation decision with distinct texture identities. A
        // late overlay changes Primary, so a sharpen must be generated from that newer value;
        // FSR and every bypass must keep it, and a stale slot 21 must not be selected.
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        string gl = MethodBody(platform, "public override void BlitPrimaryToDefault()");
        string nativeFile = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeBlit.cs");
        string native = MethodBody(nativeFile, "private void RenderNativeBlit()");
        string nativeSelector = MethodBody(nativeFile, "private int NativeFinalBlitSceneTexture(int fallback, bool fsrActive)");
        string glSharpen = MethodBody(platform, "public override int RenderOptimumTaaSharpen(int resolvedScene)");

        string presentationHelper = Read("optimum-api-contracts/optimum-api-bridge.cs");

        // Tie the executable decision to both implementations' boundaries. If either route stops
        // feeding the post-overlay Primary image into the decision, this test fails structurally.
        AssertOrdered(gl, "bool useFsr = OptimumFsrBlitActive();", "int primaryScene = scene2D;",
            "if (!useFsr)", "scene2D = RenderOptimumTaaSharpen(scene2D);",
            "OptimumApiBridge.SelectTaaPresentationTexture", "blit.Scene2D = scene2D;");
        AssertOrdered(native, "bool useFsr = OptimumFsrBlitActive();", "if (!useFsr)",
            "scene2D = RenderOptimumTaaSharpen(scene2D);", "int finalScene = NativeFinalBlitSceneTexture(scene2D, useFsr);");
        Assert.Contains("OptimumApiBridge.SelectTaaPresentationTexture", nativeSelector);
        Assert.Contains("SelectTaaPresentationTexture", presentationHelper);
        Assert.Contains("frameBuffers[OptimumTaaSharpenIndex]", glSharpen);
        Assert.Contains("buffers[OptimumTaaSharpenIndex]", nativeSelector);
        Assert.Contains("fsrEasu.BindTexture2D(\"inputScene\", scene2D", gl);
        Assert.Contains("new NativeTexture(nativeFsrEasu.Samplers[0], scene2D)", native);

        const int preOverlayPrimary = 101;
        const int postOverlayPrimary = 202;
        const int staleSlot21 = 21;
        const int sharpenedPostOverlayPrimary = 303;
        Assert.NotEqual(preOverlayPrimary, postOverlayPrimary);
        Assert.Equal(preOverlayPrimary,
            OptimumApiBridge.SelectTaaPresentationTexture(
                preOverlayPrimary, staleSlot21, fsrActive: false,
                taaResolvedThisFrame: false, sharpness: 1f, sharpenTargetAvailable: true));
        Assert.Equal(sharpenedPostOverlayPrimary,
            OptimumApiBridge.SelectTaaPresentationTexture(
                postOverlayPrimary, sharpenedPostOverlayPrimary, fsrActive: false,
                taaResolvedThisFrame: true, sharpness: 1f, sharpenTargetAvailable: true));
        Assert.Equal(postOverlayPrimary,
            OptimumApiBridge.SelectTaaPresentationTexture(
                postOverlayPrimary, sharpenedPostOverlayPrimary, fsrActive: true,
                taaResolvedThisFrame: true, sharpness: 1f, sharpenTargetAvailable: true));
        Assert.Equal(postOverlayPrimary,
            OptimumApiBridge.SelectTaaPresentationTexture(
                postOverlayPrimary, sharpenedPostOverlayPrimary, fsrActive: false,
                taaResolvedThisFrame: false, sharpness: 1f, sharpenTargetAvailable: true));
        Assert.Equal(postOverlayPrimary,
            OptimumApiBridge.SelectTaaPresentationTexture(
                postOverlayPrimary, sharpenedPostOverlayPrimary, fsrActive: false,
                taaResolvedThisFrame: true, sharpness: 0f, sharpenTargetAvailable: true));
        Assert.Equal(postOverlayPrimary,
            OptimumApiBridge.SelectTaaPresentationTexture(
                postOverlayPrimary, staleSlot21, fsrActive: false,
                taaResolvedThisFrame: false, sharpness: 1f, sharpenTargetAvailable: true));
    }

    [Fact]
    public void SharpenSkipsWhenThereIsNothingToSharpenAndRestoresRenderState()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        string body = MethodBody(platform, "public override int RenderOptimumTaaSharpen(int resolvedScene)");

        Assert.Contains("if (!TaaResolvedThisFrame || OptimumConfig.TaaSharpness <= 0f)", body);
        Assert.Contains("if (sharpen == null || sharpen.LoadError || target == null || target.Disposed", body);
        Assert.Contains("target.ColorTextureIds == null || target.ColorTextureIds.Length == 0", body);
        // The conditions stay in the pass; the draw is a seam of its own, so a platform that
        // owns the pass natively replaces the draw and inherits every condition above it
        // (docs/vulkan-native-render-systems.md, Phase 3b stage 1).
        Assert.Contains("OptimumTaaSharpenDraw(target, resolvedScene);", body);
        Assert.Contains("return target.ColorTextureIds[0];", body);

        string draw = MethodBody(platform,
            "public override void OptimumTaaSharpenDraw(FrameBufferRef target, int resolvedScene)");
        // Same set/restore discipline as the resolve (TAA-PLAN "Blend state").
        int blendOff = draw.IndexOf("GlToggleBlend(on: false);", StringComparison.Ordinal);
        int depthOff = draw.IndexOf("GlDisableDepthTest();", StringComparison.Ordinal);
        int triangle = draw.IndexOf("RenderFullscreenTriangle(screenQuad);", StringComparison.Ordinal);
        int blendOn = draw.IndexOf("GlToggleBlend(on: true);", StringComparison.Ordinal);
        int depthOn = draw.IndexOf("GlEnableDepthTest();", StringComparison.Ordinal);
        int primary = draw.IndexOf("LoadFrameBuffer(EnumFrameBuffer.Primary);", StringComparison.Ordinal);
        Assert.True(blendOff >= 0 && depthOff > blendOff && triangle > depthOff);
        Assert.True(blendOn > triangle && depthOn > blendOn && primary > depthOn);
        // The strength reaching the shader is the configured one, clamped.
        Assert.Contains("sharpen.Uniform(\"sharpness\", GameMath.Clamp(OptimumConfig.TaaSharpness, 0f, 1f));", draw);
    }

    [Fact]
    public void NoDoubleSharpeningWhenTheFsrRcasBlitIsActive()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // One shared condition, asked by both passes: the sharpen pass skips
        // itself when the blit is going to run FSR's own RCAS at native
        // resolution, so the same pixels are never sharpened twice.
        Assert.Contains("public override bool OptimumFsrBlitActive()", platform);
        Assert.Contains("bool useFsr = OptimumFsrBlitActive();", platform);

        string body = MethodBody(platform, "public override int RenderOptimumTaaSharpen(int resolvedScene)");
        int guard = body.IndexOf("if (OptimumFsrBlitActive())", StringComparison.Ordinal);
        int draw = body.IndexOf("OptimumTaaSharpenDraw(target, resolvedScene);", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < draw);
        // And the rule is written down where the next reader will look.
        Assert.Contains("two RCAS passes to the same pixels", platform);

        // The shared test still carries every term the old inline condition had.
        string helper = MethodBody(platform, "public override bool OptimumFsrBlitActive()");
        Assert.Contains("!optimumFsrDisabled", helper);
        Assert.Contains("ClientSettings.OptimumRenderScale < 1.0f", helper);
        Assert.Contains("frameBuffers[OptimumFsrFramebufferIndex] != null", helper);
        Assert.Contains("!fsrEasu.LoadError", helper);
        Assert.Contains("!fsrRcas.LoadError", helper);
    }

    // --- mip bias -----------------------------------------------------------

    [Fact]
    public void TerrainLodBiasIsZeroWithTaaOffAtNativeScale()
    {
        WithConfig(() =>
        {
            OptimumConfig.Taa = false;
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.TaaMipBias = -0.5f;
            Assert.Equal(0f, OptimumConfig.EffectiveTerrainLodBias);
        });
    }

    [Fact]
    public void TerrainLodBiasAddsTheMipBiasWhileTaaIsOn()
    {
        WithConfig(() =>
        {
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.TaaMipBias = -0.5f;
            OptimumConfig.Taa = true;
            Assert.Equal(-0.5f, OptimumConfig.EffectiveTerrainLodBias, 5);

            // And it adds to the render scale's own bias rather than replacing it.
            OptimumConfig.RenderScale = 0.5f;
            Assert.Equal(-1.5f, OptimumConfig.EffectiveTerrainLodBias, 5);
        });
    }

    [Fact]
    public void TerrainLodBiasKeepsTheRenderScaleTermWhenTaaIsOff()
    {
        WithConfig(() =>
        {
            OptimumConfig.Taa = false;
            OptimumConfig.TaaMipBias = -0.5f;
            OptimumConfig.RenderScale = 0.5f;
            Assert.Equal(-1f, OptimumConfig.EffectiveTerrainLodBias, 5);
        });
    }

    [Fact]
    public void TerrainLodBiasClampsTheConfiguredMipBias()
    {
        WithConfig(() =>
        {
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.Taa = true;
            OptimumConfig.TaaMipBias = -9f;
            Assert.Equal(-2f, OptimumConfig.EffectiveTerrainLodBias, 5);
            OptimumConfig.TaaMipBias = 9f;
            Assert.Equal(1f, OptimumConfig.EffectiveTerrainLodBias, 5);
        });
    }

    [Fact]
    public void BothLodBiasCallSitesReadTheSharedValue()
    {
        string chunkRenderer = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");
        Assert.Contains(
            "float textureLodBias = Vintagestory.API.Config.OptimumConfig.EffectiveTerrainLodBias;",
            chunkRenderer);
        // A total of zero still makes no TexParameter call at all, which is what
        // keeps TAA off at native scale identical to vanilla.
        Assert.Contains("if (textureLodBias == 0f)", chunkRenderer);
        // The zero branch is not a plain guard: it restores. optimumTextureLodBias
        // caches the last applied value starting at NaN, so a nonzero -> zero
        // transition (TAA switched off, render scale back to 1.0) writes 0 back
        // through SetOptimumTextureLodBias - which resets the atlas texture
        // parameter AND, through ShaderRegistry.ApplyOptimumTerrainSamplerLodBias,
        // the two terrain sampler objects - before returning.
        Assert.Contains("private float optimumTextureLodBias = float.NaN;", chunkRenderer);
        string zeroBranch = BranchAfter(chunkRenderer, "if (textureLodBias == 0f)");
        Assert.Contains("if (!float.IsNaN(optimumTextureLodBias))", zeroBranch);
        Assert.Contains("SetOptimumTextureLodBias(0f);", zeroBranch);
        Assert.Contains("optimumTextureLodBias = float.NaN;", zeroBranch);
        // ...and the branch really is just that branch: the nonzero path below
        // it is outside it.
        Assert.DoesNotContain("SetOptimumTextureLodBias(textureLodBias)", zeroBranch);
        // Both backends keep getting the same value through the same setter: the platform
        // virtual SetTextureLodBias (Phase 1A step 5).
        Assert.Contains("game.Platform.SetTextureLodBias(textureIds, bias);", chunkRenderer);
        Assert.Contains("device.SetTextureParameter(textureIds[k], OptimumGlConstants.TextureLodBias, bias);", VulkanPlatformSource.Read());
        Assert.Contains("GL.TexParameter((TextureTarget)3553, (TextureParameterName)34049, bias);", VulkanPlatformSource.ReadClientPlatformWindows());

        string registry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        // The sampler objects override the texture parameter on the units they
        // are bound to, so they must carry the same bias.
        Assert.Contains("float terrainLodBias = OptimumConfig.EffectiveTerrainLodBias;", registry);
        Assert.Contains("if (terrainLodBias != 0f)", registry);
        // The load-time call skips zero (vanilla makes no such call), but the
        // shared entry point applies whatever it is handed - the restore above
        // hands it 0f and must reach the samplers.
        string samplerEntry = BranchAfter(registry, "public static void ApplyOptimumTerrainSamplerLodBias(float bias)");
        Assert.DoesNotContain("!= 0f", samplerEntry);
        Assert.Equal(4, Count(samplerEntry, ", bias);"));
    }

    /// <summary>
    /// P5 review: the mip-bias row claims to apply live, and for the two programs
    /// the setting exists for it did not. chunkopaque and chunktopsoil sample the
    /// atlas through sampler OBJECTS, and a bound sampler object overrides the
    /// texture object's parameters on that unit - LOD bias included. So
    /// ChunkRenderer's per-frame TexParameter moved the mip selection of liquid,
    /// transparent and shadow terrain while the two opaque passes kept whatever
    /// bias the last shader load compiled in. Both halves now move together.
    /// </summary>
    [Fact]
    public void ALiveMipBiasChangeReachesTheTerrainSamplerObjectsAsWell()
    {
        string registry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        // The sampler write is a reusable entry point, not inlined into the load.
        Assert.Contains("public static void ApplyOptimumTerrainSamplerLodBias(float bias)", registry);
        Assert.Contains("ApplyOptimumTerrainSamplerLodBias(terrainLodBias);", registry);
        // Both backends, through the same per-sampler helper and the platform virtual.
        Assert.Contains("platform.SetSamplerLodBias(sampler, bias);", registry);
        Assert.Contains(
            "device.SetSamplerParameter(samplerId, OptimumGlConstants.TextureLodBias, bias);",
            VulkanPlatformSource.Read());
        Assert.Contains("GL.SamplerParameter(samplerId, (SamplerParameterName)34049, bias);", VulkanPlatformSource.ReadClientPlatformWindows());
        // Callable before the samplers exist: ChunkRenderer runs a frame before
        // the first shader load has created them.
        Assert.Contains("program == null || !program.customSamplers.TryGetValue(samplerName, out var sampler)", registry);

        string chunkRenderer = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");
        string setter = MethodBody(chunkRenderer, "private void SetOptimumTextureLodBias(float bias)");
        Assert.Contains("ShaderRegistry.ApplyOptimumTerrainSamplerLodBias(bias);", setter);
        // Before the texture half, and on every backend: both go through the platform.
        Assert.True(
            setter.IndexOf("ShaderRegistry.ApplyOptimumTerrainSamplerLodBias(bias);", StringComparison.Ordinal)
            < setter.IndexOf("game.Platform.SetTextureLodBias(textureIds, bias);", StringComparison.Ordinal));

        // And the Cecil transplant carries both new members.
        string patcher = PatcherSource.Read();
        Assert.Contains("\"ApplyOptimumTerrainSamplerLodBias\"", patcher);
        Assert.Contains("\"ApplyOptimumSamplerLodBias\"", patcher);
    }

    /// <summary>
    /// P5 review: with a 0f initialiser the very first OnBeforeRenderOpaque of a
    /// TAA-off, native-scale session sees "0 wanted, not-NaN cached" and writes an
    /// explicit LOD bias of 0 over the driver default on every atlas - and, since
    /// the fix above, on every terrain sampler too. NaN is what "Optimum has never
    /// touched this" has to mean for that configuration to make no call at all.
    /// </summary>
    [Fact]
    public void TheCachedLodBiasStartsAtNanSoTaaOffTouchesNothing()
    {
        string chunkRenderer = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");
        Assert.Contains("private float optimumTextureLodBias = float.NaN;", chunkRenderer);
        Assert.DoesNotContain("private float optimumTextureLodBias;", chunkRenderer);
    }

    // --- manifests and scanner ---------------------------------------------

    [Fact]
    public void CecilPatcherShipsTheSharpenMembers()
    {
        string patcher = PatcherSource.Read();
        Assert.Contains("\"OptimumTaaSharpenIndex\"", patcher);
        Assert.Contains("\"OptimumFsrBlitActive\"", patcher);
        Assert.Contains("\"RenderOptimumTaaSharpen\"", patcher);
        Assert.Contains("\"TaaSharpen\"", patcher);
        // The bodies the calls live in are transplanted.
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"RenderPostprocessingEffects\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"BlitPrimaryToDefault\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"SetupDefaultFrameBuffers\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"loadRegisteredShaderPrograms\", 0", patcher);
        Assert.Contains("\"ApplyOptimumTextureLodBias\"", patcher);
    }

    [Fact]
    public void ScannerVetoesTaaWhenAModOwnsOneOfItsOwnPasses()
    {
        string scanner = Read("Optimum.Launcher/ShaderCompatibilityScanner.cs");
        Assert.Contains("HasExternalShader(report, \"taa-sharpen.vsh\")", scanner);
        Assert.Contains("HasExternalShader(report, \"taa-sharpen.fsh\")", scanner);
        // The resolve and the debug view were missing from the same list.
        Assert.Contains("HasExternalShader(report, \"taa-resolve.fsh\")", scanner);
        Assert.Contains("HasExternalShader(report, \"taa-debug.fsh\")", scanner);
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>
    /// The text from a method's signature through its matching closing brace. This deliberately
    /// ignores indentation: donor sources and generated patches use both tabs and spaces.
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "method not found: " + signature);
        int open = source.IndexOf('{', start + signature.Length);
        Assert.True(open > start, "method block not found: " + signature);
        int depth = 0;
        bool lineComment = false;
        bool blockComment = false;
        bool quoted = false;
        bool character = false;
        bool escaped = false;
        for (int i = open; i < source.Length; i++)
        {
            char current = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (lineComment)
            {
                if (current == '\n') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (current == '*' && next == '/') { blockComment = false; i++; }
                continue;
            }
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') quoted = false;
                continue;
            }
            if (character)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '\'') character = false;
                continue;
            }
            if (current == '/' && next == '/') { lineComment = true; i++; continue; }
            if (current == '/' && next == '*') { blockComment = true; i++; continue; }
            if (current == '"') { quoted = true; continue; }
            if (current == '\'') { character = true; continue; }
            if (current == '{') depth++;
            else if (current == '}' && --depth == 0) return source.Substring(start, i - start);
        }
        Assert.Fail("unbalanced method block: " + signature);
        return string.Empty;
    }

    private static void AssertOrdered(string source, params string[] terms)
    {
        int previous = -1;
        foreach (string term in terms)
        {
            int at = source.IndexOf(term, previous + 1, StringComparison.Ordinal);
            Assert.True(at > previous, "missing or out-of-order term: " + term);
            previous = at;
        }
    }

    /// <summary>
    /// The brace-delimited block that follows <paramref name="header"/>, matched
    /// by brace depth so a nested block cannot end it early.
    /// </summary>
    private static string BranchAfter(string source, string header)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + header);
        int open = source.IndexOf('{', start + header.Length);
        Assert.True(open > start, "no block after: " + header);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        Assert.Fail("unbalanced block after: " + header);
        return string.Empty;
    }

    private static void WithConfig(Action body)
    {
        bool taa = OptimumConfig.Taa;
        float scale = OptimumConfig.RenderScale;
        float mip = OptimumConfig.TaaMipBias;
        try
        {
            body();
        }
        finally
        {
            OptimumConfig.Taa = taa;
            OptimumConfig.RenderScale = scale;
            OptimumConfig.TaaMipBias = mip;
        }
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static int IndexOfCode(string source, string code)
    {
        string pattern = string.Join(@"\s*", code
            .Where(character => !char.IsWhiteSpace(character))
            .Select(character => Regex.Escape(character.ToString())));
        Match match = Regex.Match(source, pattern, RegexOptions.CultureInvariant);
        return match.Success ? match.Index : -1;
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
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Both sharpen shaders carry RCAS's noise limiter (FSR_RCAS_DENOISE, shipped enabled in
    /// FSR 3): the lobe is scaled by 1 - 0.5 * (the centre's deviation from the mean of its four
    /// neighbours over the ring's range). Without it the sharpen multiplied the TAA-converged
    /// residual of the GTAO term 2.7x on flat faces and drew it as grain (2026-09-17).
    /// </summary>
    [Fact]
    public void SharpenLimitsItsLobeOnLonePixelNoise()
    {
        foreach (string path in new[] { "sources/shaders/taa-sharpen.fsh", "sources/shaders-vk/taa-sharpen.frag" })
        {
            string shader = File.ReadAllText(PatchReader.FindRepositoryFile(path));
            Assert.Contains("float nz = 0.25 * (bL + dL + fL + hL) - eL;", shader);
            Assert.Contains("nz = clamp(abs(nz) / max(maxL - minL, 1.0 / 65536.0), 0.0, 1.0);", shader);
            Assert.Contains("nz = -0.5 * nz + 1.0;", shader);
            Assert.Contains("lobe *= nz * strength * exp2(-2.0 * (1.0 - strength));", shader);
        }
    }
}
}
