// Source: Optimum.Tests/platform-taa-virtuals-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Vulkan-native plan, Phase 1A step 2: the TAA/FSR members are virtual on
/// ClientPlatformAbstract, ClientPlatformWindows overrides them with the OpenGL bodies,
/// and no lib code casts the platform to ClientPlatformWindows any more. A cast left
/// behind fails silently on the Vulkan platform only in the sense that it still works
/// (VulkanClientPlatform derives from ClientPlatformWindows) while bypassing the design:
/// the next step's overrides would be reached by some callers and not others.
/// </summary>
public class PlatformTaaVirtualsCoverageTests
{
    private const string AbstractPath = "Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";
    private const string WindowsPath = "Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    /// <summary>The virtual, its neutral body on the abstract, and the override signature.</summary>
    private static readonly (string Virtual, string NeutralBody, string Override)[] Members =
    {
        ("public virtual int MotionAttachmentIndex", "return -1;", "public override int MotionAttachmentIndex"),
        ("public virtual bool OptimumMotionWriteActive", "return false;", "public override bool OptimumMotionWriteActive"),
        ("public virtual bool TaaTargetsReady", "return false;", "public override bool TaaTargetsReady"),
        ("public virtual bool TaaResolvedThisFrame", "return false;", "public override bool TaaResolvedThisFrame"),
        ("public virtual FrameBufferRef TaaHistory(int parity)", "return null;", "public override FrameBufferRef TaaHistory(int parity)"),
        ("public virtual bool BeginMotionWrite()", "return false;", "public override bool BeginMotionWrite()"),
        ("public virtual void EndMotionWrite()", "", "public override void EndMotionWrite()"),
        ("public virtual bool BeginMotionOnlyWrite()", "return false;", "public override bool BeginMotionOnlyWrite()"),
        ("public virtual void EndMotionOnlyWrite()", "", "public override void EndMotionOnlyWrite()"),
        ("public virtual bool RenderOptimumSkyMotion()", "return false;", "public override bool RenderOptimumSkyMotion()"),
        ("public virtual bool RenderOptimumTaaResolve()", "return false;", "public override bool RenderOptimumTaaResolve()"),
        ("public virtual int RenderOptimumTaaSharpen(int resolvedScene)", "return resolvedScene;", "public override int RenderOptimumTaaSharpen(int resolvedScene)"),
        ("public virtual bool OptimumFsrBlitActive()", "return false;", "public override bool OptimumFsrBlitActive()"),
        ("public virtual void DisableOptimumTaa(string reason)", "", "public override void DisableOptimumTaa(string reason)"),
    };

    private static readonly Regex PlatformCast = new(@"\b(as|is)\s+ClientPlatformWindows\b|\(\s*ClientPlatformWindows\s*\)\s*[\w(]");

    // The one conversion the plan keeps: the reflective factory returns object.
    private const string AllowedCreation = "OptimumRenderBootstrap.CreatePlatform(logger) as ClientPlatformWindows;";

    [Fact]
    public void NoLibCodeCastsThePlatformToClientPlatformWindows()
    {
        var offenders = new List<string>();
        int allowed = 0;
        foreach ((string file, string line) in LibSourceLines())
        {
            if (!PlatformCast.IsMatch(line)) continue;
            string trimmed = line.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal)) continue;
            if (file.EndsWith("ClientProgram.cs", StringComparison.Ordinal) || file.EndsWith("ClientProgram.cs.patch", StringComparison.Ordinal))
            {
                if (trimmed.EndsWith(AllowedCreation, StringComparison.Ordinal))
                {
                    allowed++;
                    continue;
                }
            }
            offenders.Add(file + ": " + trimmed);
        }

        Assert.True(offenders.Count == 0, "casts to ClientPlatformWindows remain:\n" + string.Join("\n", offenders));
        Assert.Equal(1, allowed);
    }

    [Fact]
    public void TheSevenFormerCastSitesCallThroughTheAbstractPlatform()
    {
        string chunk = ReadLib("Vintagestory.Client.NoObf/ChunkRenderer.cs");
        Assert.Equal(3, Regex.Matches(chunk, Regex.Escape("ClientPlatformAbstract optimumPlatform = platform;")).Count);

        foreach (string system in new[] { "SystemRenderEntities.cs", "SystemRenderDecals.cs", "SystemRenderParticles.cs" })
        {
            string source = ReadLib("Vintagestory.Client.NoObf/" + system);
            Assert.Single(Regex.Matches(source, Regex.Escape("ClientPlatformAbstract optimumPlatform = game.Platform;")));
        }

        string clientMain = ReadLib("Vintagestory.Client.NoObf/ClientMain.cs");
        Assert.Contains("Platform.RenderOptimumSkyMotion();", clientMain);
        Assert.DoesNotContain("optimumSkyMotionPlatform", clientMain);
    }

    [Fact]
    public void TheAbstractPlatformDeclaresEveryTaaMemberWithANeutralBody()
    {
        string platform = ReadLib(AbstractPath);

        foreach ((string signature, string neutral, _) in Members)
        {
            string body = Body(platform, signature);
            string inner = Regex.Replace(body, @"\s+", " ").Trim();
            string expected = signature.Contains('(')
                ? (neutral.Length == 0 ? "{ }" : "{ " + neutral + " }")
                : "{ get { " + neutral + " } }";
            Assert.True(inner == expected, signature + " is not neutral: " + inner);
        }
    }

    [Fact]
    public void ClientPlatformWindowsOverridesEveryTaaMember()
    {
        string platform = ReadLib(WindowsPath);

        foreach ((_, _, string signature) in Members)
        {
            Assert.Single(Regex.Matches(platform, Regex.Escape(signature) + @"(?![\w])"));
        }

        // The state members read private fields, and nothing hides them with a
        // non-virtual declaration of the same name.
        Assert.Contains("private int optimumMotionAttachmentIndex = -1;", platform);
        Assert.Contains("private bool optimumMotionWriteActive;", platform);
        Assert.Contains("private bool optimumTaaTargetsReady;", platform);
        Assert.Contains("private bool optimumTaaResolvedThisFrame;", platform);
        Assert.DoesNotContain("private bool TaaTargetsReady;", platform);
        Assert.DoesNotContain("{ get; private set; }", Body(platform, "public override int MotionAttachmentIndex"));
        Assert.DoesNotContain("internal bool RenderOptimumSkyMotion()", platform);
    }

    [Fact]
    public void ThePatcherInjectsTheVirtualsAndTheOverrideFields()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        string abstractMembers = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()");
        foreach (string name in new[]
        {
            "MotionAttachmentIndex", "OptimumMotionWriteActive", "TaaTargetsReady", "TaaResolvedThisFrame",
            "TaaHistory", "BeginMotionWrite", "EndMotionWrite", "BeginMotionOnlyWrite", "EndMotionOnlyWrite",
            "RenderOptimumSkyMotion", "RenderOptimumTaaResolve", "RenderOptimumTaaSharpen", "OptimumFsrBlitActive",
            "DisableOptimumTaa",
        })
        {
            Assert.Contains("\"" + name + "\",", abstractMembers);
        }

        string windowsMembers = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformWindows\"] = new()");
        foreach (string field in new[]
        {
            "optimumMotionAttachmentIndex", "optimumTaaTargetsReady", "optimumTaaResolvedThisFrame", "optimumMotionWriteActive",
        })
        {
            Assert.Contains("\"" + field + "\",", windowsMembers);
        }
    }

    private static IEnumerable<(string File, string Line)> LibSourceLines()
    {
        string root = RepositoryRoot();
        string lib = Path.Combine(root, "build", "VintagestoryLib");
        if (Directory.Exists(lib))
        {
            foreach (string file in Directory.EnumerateFiles(lib, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(lib, file);
                if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;
                foreach (string line in File.ReadLines(file))
                    yield return (relative, line);
            }
            yield break;
        }

        // Un-bootstrapped checkout: every Optimum line in the lib is an added patch line.
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "patches", "VintagestoryLib"), "*.patch", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadLines(file))
            {
                if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
                    yield return (Path.GetFileName(file), line.Substring(1));
            }
        }
    }

    private static string RepositoryRoot()
    {
        string patcher = PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs");
        return Path.GetDirectoryName(Path.GetDirectoryName(patcher)!)!;
    }

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        int open = source.IndexOf('{', start + signature.Length);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced body: " + signature);
    }

    private static string Block(string source, string header)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf("},", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }

    private static string ReadLib(string relativePath)
    {
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile("build/VintagestoryLib/" + relativePath));
        }
        catch (FileNotFoundException)
        {
            return PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(
                "patches/VintagestoryLib/" + relativePath + ".patch"));
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
}

// Source: Optimum.Tests/taa-pipeline-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

public class TaaPipelineCoverageTests
{
    [Fact]
    public void CecilPatcherShipsEveryTaaMethodAndMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        // Transplant targets.
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"MainRenderLoop\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"Set3DProjection\", 2", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"get_CurrentProjectionMatrix\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"OnFowChanged\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"OnResize\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"Start\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"RenderAfterPostProcessing\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientEventManager\", \"TriggerReloadShaders\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"BlitPrimaryToDefault\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"SetupDefaultFrameBuffers\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"registerDefaultShaderProgramsPre\", 0", patcher);

        // New members needing injection (CompileAndTrackShaderProgram is a
        // genuinely new private helper extracted from
        // loadRegisteredShaderPrograms, not a transplant of a pre-existing
        // vanilla method - it has no vanilla counterpart to transplant onto).
        Assert.Contains("\"CompileAndTrackShaderProgram\"", patcher);
        Assert.Contains("\"CurrentProjectionMatrixUnjittered\"", patcher);
        Assert.Contains("\"TemporalContext\"", patcher);
        Assert.Contains("\"TaaDebug\"", patcher);
        Assert.Contains("\"MotionAttachmentIndex\"", patcher);
        Assert.Contains("\"TaaHistory\"", patcher);
        Assert.Contains("\"DisableOptimumTaa\"", patcher);
        // Phase 1A step 4: the device history target is a VulkanClientPlatform member (the
        // renderer assembly ships as is); only the GL one is transplanted.
        Assert.Contains("private FrameBufferRef CreateOptimumHistoryTarget(int width, int height)", VulkanPlatformSource.Read());
        Assert.Contains("\"CreateOptimumHistoryTargetGl\"", patcher);

        // Both new vanilla-type member-injection dictionaries exist.
        Assert.Contains("[\"Vintagestory.Client.NoObf.RenderAPIGame\"]", patcher);
    }

    [Fact]
    public void MotionAttachmentIndexIsTwoWithoutSsaoAndFourWithIt()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // The motion attachment index logic (2 without SSAO, 4 with) - the
        // attachment is appended after the existing 2/4 slots, matching the
        // property doc comment.
        Assert.Contains(
            "the Primary colour-attachment index that holds per-pixel motion",
            platform);
        Assert.Contains("enabled - 2 without", platform);
        Assert.Contains("SSAO G-buffer, 4 with it", platform);
    }

    [Fact]
    public void DefaultDrawBufferMasksAreUnchangedByTaa()
    {
        // Phase 1A step 4: the device framebuffer setup is VulkanClientPlatform's.
        string platform = VulkanPlatformSource.Read();

        // Primary's draw-buffer mask is still derived only from
        // primaryAttachments (2 or 4 colour targets), never including the new
        // motion attachment - it is enabled per-pass by writers, not by
        // default.
        Assert.Contains("StateDrawBuffers(primary.FboId, (1 << primaryAttachments) - 1);", platform);
        // Transparent (OIT) keeps its untouched six/three-output mask.
        Assert.Contains("StateDrawBuffers(transparent.FboId, 7);", platform);
    }

    [Fact]
    public void ClearFrameBufferClearsTheMotionAttachmentOnBothPaths()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // Device path (VulkanClientPlatform.ClearFrameBufferPass since Phase 1A step 4).
        string vulkan = VulkanPlatformSource.Read();
        Assert.Contains("ClearTargetColor(target, MotionAttachmentIndex, 0f, 0f, 0f, 0f);", vulkan);
        // An excluded attachment is not cleared on either backend. Checking
        // only that ClearColor exists missed Vulkan's silent masked-out no-op.
        int enable = vulkan.IndexOf("StateDrawBuffers(FrameBuffers[0].FboId, (1 << (MotionAttachmentIndex + 1)) - 1);", StringComparison.Ordinal);
        int clear = vulkan.IndexOf("ClearTargetColor(target, MotionAttachmentIndex, 0f, 0f, 0f, 0f);", StringComparison.Ordinal);
        int restore = vulkan.IndexOf("StateDrawBuffers(FrameBuffers[0].FboId, (1 << MotionAttachmentIndex) - 1);", clear, StringComparison.Ordinal);
        Assert.True(enable >= 0 && enable < clear && restore > clear);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"ClearFrameBuffer\", 1", Read("Optimum.Patcher/Program.cs"));
        // GL path.
        Assert.Contains("GL.ClearBuffer((ClearBuffer)6144, MotionAttachmentIndex, new float[4]);", platform);
        // Both are guarded so a failed/absent motion attachment leaves the
        // clear untouched (MotionAttachmentIndex stays -1 via DisableOptimumTaa).
        Assert.Equal(1, Count(platform, "if (MotionAttachmentIndex >= 0)"));
        Assert.Equal(1, Count(vulkan, "if (MotionAttachmentIndex >= 0)"));
    }

    [Fact]
    public void CurrentProjectionMatrixOnlyJittersWhenJitterActive()
    {
        // The transplant patch's diff hunks are not contiguous with the rest
        // of the file (context lines get truncated at hunk boundaries), so
        // read the full Cecil-transplanted source directly rather than via
        // ReadPatchedOrSource here - this test needs to walk from one member
        // declaration to the next.
        string clientMain = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        int getterStart = clientMain.IndexOf("public float[] CurrentProjectionMatrix", StringComparison.Ordinal);
        Assert.True(getterStart >= 0);
        int getterEnd = clientMain.IndexOf("public float[] CurrentProjectionMatrixUnjittered", getterStart, StringComparison.Ordinal);
        Assert.True(getterEnd > getterStart);
        string getter = clientMain.Substring(getterStart, getterEnd - getterStart);

        Assert.Contains("OptimumTemporal.Frame.JitterActive", getter);
        // Only shears when JitterActive AND the matrix on top of PMatrix is the
        // exact one Set3DProjection last recorded - anything else (ortho, HUD,
        // shadow, pushed matrices) falls through to the vanilla tmpMatrix copy.
        Assert.Contains("set3DProjectionTempMat4", getter);
        Assert.Contains("OptimumTemporal.Frame.ApplyJitterCopy(top)", getter);

        // The unjittered companion never shears at all.
        int unjitteredStart = getterEnd;
        int unjitteredEnd = clientMain.IndexOf("public float[] CurrentModelViewMatrix", unjitteredStart, StringComparison.Ordinal);
        Assert.True(unjitteredEnd > unjitteredStart);
        string unjittered = clientMain.Substring(unjitteredStart, unjitteredEnd - unjitteredStart);
        Assert.Contains("PMatrix.Top", unjittered);
        Assert.DoesNotContain("JitterActive", unjittered);
        Assert.DoesNotContain("ApplyJitterCopy", unjittered);
    }

    [Fact]
    public void TaaAndJitterDefaultsAreOff()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("Taa = false", config);
        Assert.Contains("TaaJitterDev = false", config);
        Assert.Contains("EffectiveTaa", config);
    }

    [Fact]
    public void TaaDebugViewIsGatedBehindMotionAttachmentAndFallsThroughOtherwise()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.Contains("OptimumConfig.TaaDebugView != 0 && MotionAttachmentIndex >= 0", platform);
    }

    [Fact]
    public void RenderPostprocessingEffectsResolvesTaaBeforeBloomAndSharpensOnlyAfterFinalComposition()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        int postEffectsStart = platform.IndexOf(
            "public override void RenderPostprocessingEffects(float[] projectMatrix)",
            StringComparison.Ordinal);
        Assert.True(postEffectsStart >= 0);
        int resolveCall = platform.IndexOf("RenderOptimumTaaResolve();", postEffectsStart, StringComparison.Ordinal);
        Assert.True(resolveCall > postEffectsStart);

        // postSceneTexture/postGlowTexture are derived from the resolve result
        // right after the call, before the bloom block reads them. Sharpen is
        // deliberately absent here: bloom and god rays must consume the
        // unsharpened resolved scene.
        // Phase 3b: the choice moved into its own virtual, which the chain calls right after
        // the resolve and before the bloom step reads it.
        Assert.Contains(
            "return TaaResolvedThisFrame ? taaResolvedColorTexture : frameBuffers[0].ColorTextureIds[0];",
            platform);
        Assert.Contains(
            "return TaaResolvedThisFrame ? taaResolvedGlowTexture : frameBuffers[0].ColorTextureIds[1];",
            platform);
        int postSceneDecl = platform.IndexOf(
            "int postSceneTexture = OptimumPostSceneTexture();", resolveCall, StringComparison.Ordinal);
        int postGlowDecl = platform.IndexOf(
            "int postGlowTexture = OptimumPostGlowTexture();", resolveCall, StringComparison.Ordinal);
        Assert.True(postSceneDecl > resolveCall);
        Assert.True(postGlowDecl > postSceneDecl);

        int sharpenBeforeBloom = platform.IndexOf(
            "postSceneTexture = RenderOptimumTaaSharpen(postSceneTexture);", resolveCall, StringComparison.Ordinal);
        Assert.True(sharpenBeforeBloom < 0);

        int bloomStep = platform.IndexOf(
            "OptimumPostBloom(postSceneTexture, postGlowTexture);", postGlowDecl, StringComparison.Ordinal);
        Assert.True(bloomStep > postGlowDecl);
        int bloomBlock = platform.IndexOf("if (RenderBloom)", bloomStep, StringComparison.Ordinal);
        Assert.True(bloomBlock > bloomStep);

        // Bloom's findbright pass reads the resolved colour+glow, not the raw
        // primary attachments.
        int findbrightColor = platform.IndexOf("findbright.ColorTex2D = postSceneTexture;", bloomBlock, StringComparison.Ordinal);
        int findbrightGlow = platform.IndexOf("findbright.GlowTex2D = postGlowTexture;", bloomBlock, StringComparison.Ordinal);
        Assert.True(findbrightColor > bloomBlock);
        Assert.True(findbrightGlow > findbrightColor);

        // God rays read the same resolved pair.
        int godRaysBlock = platform.IndexOf("if (RenderGodRays)", findbrightGlow, StringComparison.Ordinal);
        Assert.True(godRaysBlock > findbrightGlow);
        int godraysInput = platform.IndexOf("godrays.InputTexture2D = postSceneTexture;", godRaysBlock, StringComparison.Ordinal);
        int godraysGlow = platform.IndexOf("godrays.GlowParts2D = postGlowTexture;", godRaysBlock, StringComparison.Ordinal);
        Assert.True(godraysInput > godRaysBlock);
        Assert.True(godraysGlow > godraysInput);

        // The Luma blit target reads postSceneTexture through Blit.Scene2D on
        // the TAA-resolved path (the FXAA branch instead reads the raw primary
        // colour attachment, since FXAA and TAA are mutually exclusive).
        Assert.Contains("if (RenderFXAA && !TaaResolvedThisFrame)", platform);
        Assert.Contains("blit.Scene2D = postSceneTexture;", platform);

        int finalComposition = platform.IndexOf("public override void RenderFinalComposition()", bloomStep,
            StringComparison.Ordinal);
        Assert.True(finalComposition > bloomStep);
        string compositionBody = MethodBody(platform, "public override void RenderFinalComposition()");
        Assert.DoesNotContain("RenderOptimumTaaSharpen", compositionBody);
        string blitBody = MethodBody(platform, "public override void BlitPrimaryToDefault()");
        Assert.Contains("scene2D = RenderOptimumTaaSharpen(scene2D);", blitBody);
        Assert.True(blitBody.IndexOf("RenderOptimumTaaSharpen(scene2D);", StringComparison.Ordinal) <
            blitBody.IndexOf("ShaderProgramBlit blit = ShaderPrograms.Blit;", StringComparison.Ordinal));
    }

    [Fact]
    public void FinalReadsTheResolvedGlowTextureOnlyWhenTaaResolvedThisFrame()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.Contains(
            "final.GlowParts2D = TaaResolvedThisFrame ? taaResolvedGlowTexture : frameBuffers[0].ColorTextureIds[1];",
            platform);
    }

    [Fact]
    public void FxaaDefineIsOffWhenEffectiveTaaIsOn()
    {
        string shaderRegistry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");

        Assert.Contains(
            "#define FXAA \" + (ClientSettings.FXAA && OptimumConfig.EffectiveRenderScale >= 1.0f && !OptimumConfig.EffectiveTaa ? 1 : 0)",
            shaderRegistry);
    }

    [Fact]
    public void TaaResolveIsRegisteredAndOptional()
    {
        string shaderRegistry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");

        Assert.Contains(
            "RegisterOptimumShaderProgram(\"taa-resolve\", ShaderPrograms.TaaResolve = new ShaderProgram());",
            shaderRegistry);

        // taa-resolve is optional: a failed compile only marks LoadError on the
        // program itself, it never flips the global shader-load-succeeded flag
        // (same treatment as FsrEasu/FsrRcas/TaaDebug).
        int compileHelperStart = shaderRegistry.IndexOf(
            "private static void CompileAndTrackShaderProgram",
            StringComparison.Ordinal);
        Assert.True(compileHelperStart >= 0);
        Assert.Contains(
            "shaderProgram == ShaderPrograms.FsrEasu || shaderProgram == ShaderPrograms.FsrRcas || shaderProgram == ShaderPrograms.TaaDebug || shaderProgram == ShaderPrograms.TaaResolve",
            shaderRegistry.Substring(compileHelperStart));
    }

    [Fact]
    public void CecilPatcherShipsEveryTaaResolveMethodAndMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"TaaResolve\"", patcher);
        Assert.Contains("\"RenderOptimumTaaResolve\"", patcher);
        Assert.Contains("\"_taaFrameParity\"", patcher);
        Assert.Contains("\"_taaHistoryValid\"", patcher);
        Assert.Contains("\"taaResolvedColorTexture\"", patcher);
        Assert.Contains("\"taaResolvedGlowTexture\"", patcher);
        Assert.Contains("\"TaaResolvedThisFrame\"", patcher);
    }

    [Fact]
    public void TaaResolveShaderPairExistsAndReadsHistoryAndCurrentColour()
    {
        string vsh = Read("sources/shaders/taa-resolve.vsh");
        string fsh = Read("sources/shaders/taa-resolve.fsh");

        Assert.NotEmpty(vsh);
        Assert.NotEmpty(fsh);
    }

    [Fact]
    public void DisposeFrameBuffersDeletesTheSharedDepthTextureOnlyOnce()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // Transparent shares Primary's depth texture, so the same handle sits
        // in two FrameBufferRefs and a naive loop deletes it twice - a double
        // free on the device path and a double count in VulkanStats. Phase 1A
        // step 4: the device setup and disposal are VulkanClientPlatform overrides.
        string vulkan = VulkanPlatformSource.Read();
        Assert.Contains("transparent.DepthTextureId = primary.DepthTextureId;", vulkan);

        // Virtual since platform substitution (VulkanClientPlatform overrides it).
        int dispose = platform.IndexOf("public virtual void DisposeFrameBuffers(", StringComparison.Ordinal);
        Assert.True(dispose >= 0);
        int end = platform.IndexOf("public override void ClearFrameBuffer(", dispose, StringComparison.Ordinal);
        string body = end > dispose ? platform.Substring(dispose, end - dispose) : platform.Substring(dispose);

        int deviceDispose = vulkan.IndexOf("public override void DisposeFrameBuffers(", StringComparison.Ordinal);
        Assert.True(deviceDispose >= 0);
        int deviceEnd = vulkan.IndexOf("public override void LoadFrameBuffer(", deviceDispose, StringComparison.Ordinal);
        Assert.True(deviceEnd > deviceDispose);
        string deviceBody = vulkan.Substring(deviceDispose, deviceEnd - deviceDispose);

        // Device path and GL path both gate every texture delete on the set.
        Assert.Contains("HashSet<int> deletedTextures = new HashSet<int>();", body);
        Assert.Contains("HashSet<int> deletedTextures = new HashSet<int>();", deviceBody);
        Assert.Contains("if (deletedTextures.Add(buffers[k].DepthTextureId))", deviceBody);
        Assert.Contains("if (deletedTextures.Add(buffers[i].DepthTextureId))", body);
        Assert.Contains("if (deletedTextures.Add(buffers[k].ColorTextureIds[n]))", deviceBody);
        Assert.Contains("if (deletedTextures.Add(buffers[i].ColorTextureIds[j]))", body);
        // No unguarded delete is left behind on either path.
        Assert.Equal(2, Count(body, "deletedTextures.Add("));
        Assert.Equal(2, Count(deviceBody, "deletedTextures.Add("));
        Assert.Equal(1, Count(deviceBody, "device.DeleteTexture(buffers[k].DepthTextureId);"));
        Assert.Equal(1, Count(body, "GL.DeleteTexture(buffers[i].DepthTextureId);"));
    }

    [Fact]
    public void TaaDebugValidityUsesTheResolvePassDepthTolerance()
    {
        string resolve = Read("sources/shaders/taa-resolve.fsh");
        string debug = Read("sources/shaders/taa-debug.fsh");

        // The resolve pass accepts a writer whose recorded depth is within a
        // value-scaled tolerance; the debug validity view has to use the same
        // expression or it paints red where the resolve reprojects happily.
        // The resolve evaluates it at the nearest-depth tap of its 3x3 (2026-09-11);
        // the expression is what has to match.
        Assert.Contains("abs(motion.a - closestDepth) <= max(2e-4, 8e-4 * closestDepth)", resolve);
        Assert.Contains("abs(motion.a - sceneDepth) <= max(2e-4, 8e-4 * sceneDepth)", debug);
        Assert.Equal(Tolerance(resolve, "closestDepth"), Tolerance(debug, "sceneDepth"));
        Assert.DoesNotContain("abs(motion.a - sceneDepth) < 1e-4", debug);
    }

    /// <summary>
    /// Once DisableOptimumTaa has run, no later frame-buffer rebuild may retry
    /// the allocation that just failed. OptimumConfig.TaaRuntimeDisabled already
    /// makes EffectiveTaa false, but the platform's own optimumTaaDisabled flag
    /// is the authority for this platform instance, so both setup paths gate on
    /// it as well - a belt-and-braces guard that costs one field read per
    /// rebuild.
    /// </summary>
    [Fact]
    public void BothFrameBufferSetupPathsHonourTheRuntimeTaaDisable()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // The device path and the GL path, both guarded. Phase 1A step 4: the device setup
        // (VulkanClientPlatform) reads the same guard through OptimumTaaRequested.
        Assert.Equal(1, Count(platform,
            "bool taaRequested = !optimumTaaDisabled && Vintagestory.API.Config.OptimumConfig.EffectiveTaa;"));
        Assert.Equal(1, Count(platform,
            "public bool OptimumTaaRequested => !optimumTaaDisabled && Vintagestory.API.Config.OptimumConfig.EffectiveTaa;"));
        Assert.Contains("bool taaRequested = OptimumTaaRequested;", VulkanPlatformSource.Read());
        Assert.DoesNotContain(
            "bool taaRequested = Vintagestory.API.Config.OptimumConfig.EffectiveTaa;",
            platform);

        // And the flag really is set by the failure path.
        Assert.Contains("optimumTaaDisabled = true;", platform);
    }

    /// <summary>The depth-match tolerance expression, with the depth variable normalised.</summary>
    private static string Tolerance(string shader, string depthName)
    {
        int start = shader.IndexOf("abs(motion.a - " + depthName + ")", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = shader.IndexOf(')', shader.IndexOf("max(", start, StringComparison.Ordinal) + 4);
        return shader.Substring(start, end - start + 1).Replace(depthName, "DEPTH");
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

    [Fact]
    public void ARuntimeTaaFailureFallsBackToFxaa()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("!TaaRuntimeDisabled &&", config);
        Assert.Contains("public static bool DisableTaaAtRuntime()", config);

        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        int disable = platform.IndexOf("public override void DisableOptimumTaa(string reason)", StringComparison.Ordinal);
        Assert.True(disable > 0);
        Assert.Contains("OptimumConfig.DisableTaaAtRuntime()", platform.Substring(disable, 1200));
        // The reload happens outside frame buffer setup, at the resolve decision.
        int resolve = platform.IndexOf("public override bool RenderOptimumTaaResolve()", StringComparison.Ordinal);
        Assert.Contains("OptimumRunPendingTaaShaderReload();", platform.Substring(resolve, 400));
        Assert.Contains("ShaderRegistry.ReloadShaders();", platform);

        // Later rebuilds read EffectiveTaa, which now honours the runtime flag.
        string registry = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        Assert.Contains("!OptimumConfig.EffectiveTaa ? 1 : 0", registry);

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"optimumTaaShaderReloadPending\"", patcher);
        Assert.Contains("\"OptimumRunPendingTaaShaderReload\"", patcher);
    }

    [Fact]
    public void TheEntityMotionWindowOnlyWrapsTheGamesOwnEntityRenderers()
    {
        string entities = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs");
        Assert.Contains("OptimumIsMotionWriter(entityRenderer2.Value)", entities);
        Assert.Contains("StartsWith(\"Vintagestory.GameContent\"", entities);
        // No whole-loop window any more.
        Assert.DoesNotContain("optimumPlatform != null && optimumPlatform.BeginMotionWrite();", entities);

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"OptimumIsMotionWriter\"", patcher);
        Assert.Contains("\"optimumMotionWriterTypes\"", patcher);
        // Injected static fields get no initializer (vanilla's static ctor
        // runs), so the cache must be created lazily; a field initializer
        // crashed the first entity frame with a NullReferenceException.
        Assert.DoesNotContain("optimumMotionWriterTypes = new Dictionary", entities);
        Assert.Contains("optimumMotionWriterTypes ??= new Dictionary<Type, bool>()", entities);
    }

    [Fact]
    public void SkyPixelsReprojectAsDirections()
    {
        string resolve = Read("sources/shaders/taa-resolve.fsh");
        // Sky is decided on the tap the vector comes from (the nearest depth in the
        // 3x3): a pixel next to a finite surface reprojects as that surface.
        Assert.Contains("bool sky = closestDepth >= 0.999999;", resolve);
        // Far point minus near point: the far point alone carries the eye offset
        // of CameraMatrixOrigin (see TaaSkyDecalMotionCoverageTests).
        Assert.Contains("prevViewProj * vec4(skyDirection, 0.0)", resolve);
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "method not found: " + signature);
        int open = source.IndexOf('{', start + signature.Length);
        Assert.True(open >= 0);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
        }
        Assert.Fail("unbalanced method: " + signature);
        return string.Empty;
    }
}
}
