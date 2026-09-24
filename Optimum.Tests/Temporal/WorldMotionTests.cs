// Source: Optimum.Tests/taa-liquid-motion-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

/// <summary>
/// Source coverage for the TAA P4 liquid velocity pass (TAA-PLAN.md accuracy
/// rule 7): the chunkliquidmotion program, the motion-only draw-buffer window,
/// the ChunkRenderer pass that drives it, its placement in the frame, and the
/// plumbing that has to ship it (patcher entries, shader registration, the
/// compatibility scanner).
///
/// Text assertions only prove the wiring exists - the GPU test
/// (Optimum.Render.Vulkan.Tests/TaaLiquidMotionTests) proves the numbers and
/// that colour attachment 0 is untouched. What these catch is the failure this
/// project keeps hitting: a change that works in the build tree and never
/// reaches the installed runtime because a patcher entry or a registration was
/// missed.
/// </summary>
public class TaaLiquidMotionCoverageTests
{
    // ------------------------------------------------------------- the shader

    [Fact]
    public void TheLiquidWriterEmitsTheMotionContractAndNothingElse()
    {
        string vertex = Read("sources/shaders/chunkliquidmotion.vsh");
        string fragment = Read("sources/shaders/chunkliquidmotion.fsh");

        // Compiled in only while TAA is on, exactly as the P3 writers are.
        Assert.Contains("#if TAAMOTION > 0", vertex);
        Assert.Contains("#if TAAMOTION > 0", fragment);

        // Previous transforms and the camera's own movement.
        Assert.Contains("uniform mat4 prevProjectionMatrix;", vertex);
        Assert.Contains("uniform mat4 prevModelViewMatrix;", vertex);
        Assert.Contains("uniform vec3 cameraPosDelta;", vertex);
        Assert.Contains("out vec4 taaPrevClip;", vertex);

        // prevRel = truePos + cameraPosDelta, warped with the previous state,
        // through the previous unjittered projection and view (accuracy rule 4).
        Assert.Contains("WarpState taaPrev = previousWarpState();", vertex);
        Assert.Contains("vec4 taaPrevPos = vec4(truePos.xyz + cameraPosDelta, 1.0);", vertex);
        Assert.Contains("taaPrevPos = taaLiquidWorldPos(taaPrev, taaPrevPos);", vertex);
        Assert.Contains("taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);", vertex);

        // The fragment contract taa-resolve.fsh consumes.
        Assert.Contains("layout(location = TAAMOTIONLOCATION) out vec4 outMotion;", fragment);
        Assert.Contains("uniform vec2 taaRenderSize;", fragment);
        Assert.Contains("uniform vec2 taaJitterPx;", fragment);
        Assert.Contains("vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;", fragment);
        Assert.Contains("vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;", fragment);
        Assert.Contains(
            "outMotion = vec4(prevPixel - currentPixel, clamp(taaLiquidReactive, 0.0, 1.0), gl_FragCoord.z);",
            fragment);
        Assert.Contains("if (taaPrevClip.w <= 1e-6) {", fragment);
        // ... and the reactive value crosses that branch. taa-resolve.fsh reads
        // motion.b whether or not the writer-depth test accepted the pixel
        // (TAA-PLAN.md finding (h)), and the foam and flow-UV animation 0.3
        // stands for is happening on this fragment either way. P4 review fix -
        // the GPU proof is TaaLiquidMotionTests
        // .APreviousPositionBehindThePreviousCameraStillCarriesTheReactiveValue.
        string behindCamera = Between(fragment, "if (taaPrevClip.w <= 1e-6) {", "}", 0);
        Assert.Contains("clamp(taaLiquidReactive, 0.0, 1.0)", behindCamera);
        Assert.DoesNotContain("outMotion = vec4(0.0);", behindCamera);

        // Foam and the flow-UV scroll animate in place, so the surface is
        // reactive even where it reprojects perfectly.
        Assert.Contains("uniform float taaLiquidReactive = 0.3;", fragment);

        // And NOTHING else is written. The pass re-draws geometry that has
        // already been shaded into Primary through the OIT merge; a second
        // colour output would overwrite that image.
        foreach (string forbidden in new[] { "outColor", "outGlow", "outGNormal", "outGPosition", "OIT(" })
        {
            Assert.False(fragment.Contains(forbidden, StringComparison.Ordinal),
                "the liquid velocity pass must write only the motion attachment, found: " + forbidden);
        }
        // Exactly two output declarations: the real one and the TAA-off dummy
        // that keeps the program compilable when the writer is preprocessed out.
        Assert.Equal(2, Count(fragment, "layout(location"));
        Assert.Contains("#else", fragment);
        Assert.Contains("layout(location = 0) out vec4 outMotion;", fragment);
    }

    /// <summary>
    /// The velocity pass has to land on exactly the surface chunkliquid.vsh
    /// shaded, or it depth-tests against a fragment a fraction of a pixel away
    /// and reports another surface's motion. That means the same liquid wave
    /// warp, the same divisor from the same water flags, and the same
    /// "pretend the surface is closer" w-offset.
    /// </summary>
    [Fact]
    public void TheVertexPathIsChunkliquidsPositionPathVerbatim()
    {
        string ours = Read("sources/shaders/chunkliquidmotion.vsh");

        // The offset is applied to BOTH clip positions: it moves where the
        // fragment lands, so leaving it off the previous one reports it as motion.
        Assert.Contains("gl_Position.w += 0.0008 / max(0.1, gl_Position.z);", ours);
        Assert.Contains("taaPrevClip.w += 0.0008 / max(0.1, taaPrevClip.z);", ours);

        // Both evaluations go through one function, so the current and previous
        // positions cannot drift apart.
        Assert.Contains("vec4 taaLiquidWorldPos(WarpState st, vec4 worldPos)", ours);
        Assert.Contains("vec4 worldPos = taaLiquidWorldPos(currentWarpState(), truePos);", ours);
        Assert.Contains("#include vertexwarp.vsh", ours);

        string? vanillaPath = TryFind(".vanilla/win-x64/vintagestory/assets/game/shaders/chunkliquid.vsh");
        // The vanilla shaders are proprietary and never committed; a checkout
        // that has not bootstrapped has nothing to compare against.
        if (vanillaPath == null) return;

        string vanilla = File.ReadAllText(vanillaPath);

        // The warp branch, from vanilla's main() and from our function, with the
        // only permitted difference undone: the warp reaches the state-taking
        // overload instead of the currentWarpState() wrapper.
        string vanillaBranch = Between(vanilla, "if ((waterFlagsIn & 1) == 1) {", "vec4 cameraPos", 0);
        int ourFunction = ours.IndexOf("vec4 taaLiquidWorldPos(WarpState st, vec4 worldPos)", StringComparison.Ordinal);
        Assert.True(ourFunction > 0);
        string ourBranch = Between(ours, "if ((waterFlagsIn & 1) == 1) {", "return worldPos;", ourFunction)
            .Replace("applyLiquidWarpingState(st, ", "applyLiquidWarping(");

        Assert.Equal(Squash(vanillaBranch), Squash(ourBranch));
    }

    // ------------------------------------------------- the draw-buffer window

    /// <summary>
    /// The window this pass opens is narrower than the P3 one: the motion
    /// attachment is not ADDED to the set a shading pass writes, it REPLACES it,
    /// so a second draw of already-shaded geometry cannot touch the colour, glow
    /// or G-buffer attachments.
    /// </summary>
    [Fact]
    public void ThePlatformOpensAMotionOnlyWindowOnBothBackends()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.Contains("public override bool BeginMotionOnlyWrite()", platform);
        Assert.Contains("public override void EndMotionOnlyWrite()", platform);

        // Device path (VulkanClientPlatform since Phase 1A step 4): the mask is the single
        // motion bit, not the prefix mask.
        string vulkan = VulkanPlatformSource.Read();
        Assert.Contains("StateDrawBuffers(FrameBuffers[0].FboId, 1 << MotionAttachmentIndex);",
            vulkan.Substring(vulkan.IndexOf("public override void EnableMotionOnlyDrawBuffers()", StringComparison.Ordinal)));

        // GL path: GL_NONE in every slot below the motion attachment, and a
        // cached array - the pass runs once a frame, but the P3 window's rule
        // that the draw-buffer sets are built once and kept applies here too.
        Assert.Contains("private DrawBuffersEnum[] optimumMotionOnlyDrawBuffers;", platform);
        Assert.Contains("optimumMotionOnlyDrawBuffers[optimumDb] = (DrawBuffersEnum)0;", platform);
        Assert.Contains(
            "optimumMotionOnlyDrawBuffers[MotionAttachmentIndex] = (DrawBuffersEnum)(36064 + MotionAttachmentIndex);",
            platform);
        Assert.Contains("GL.DrawBuffers(optimumMotionOnlyDrawBuffers.Length, optimumMotionOnlyDrawBuffers);", platform);

        // The same guards as the P3 window, including the one that keeps the two
        // backends from disagreeing about which framebuffer the mask belongs to.
        int begin = platform.IndexOf("public override bool BeginMotionOnlyWrite()", StringComparison.Ordinal);
        int drawBuffers = platform.IndexOf("EnableMotionOnlyDrawBuffers();", begin, StringComparison.Ordinal);
        Assert.True(drawBuffers > begin);
        string guards = platform.Substring(begin, drawBuffers - begin);
        Assert.Contains("if (OptimumMotionWriteActive) return false;", guards);
        Assert.Contains("if (MotionAttachmentIndex < 0 || !TaaTargetsReady) return false;", guards);
        Assert.Contains("if (!Vintagestory.API.Config.OptimumConfig.EffectiveTaa) return false;", guards);
        Assert.Contains("if (!ReferenceEquals(CurrentFrameBuffer, frameBuffers[0])) return false;", guards);

        // Replace blending on the motion attachment, same as the P3 window.
        int end = platform.IndexOf("public override void EndMotionOnlyWrite()", begin, StringComparison.Ordinal);
        Assert.True(end > begin);
        Assert.Contains("ApplyOptimumMotionBlendState();", platform.Substring(begin, end - begin));

        // Closing restores Primary's default set - the same restore the P3
        // window does, reached through it rather than duplicated.
        Assert.Contains("EndMotionWrite();", platform.Substring(end));
    }

    // --------------------------------------------------------------- the pass

    [Fact]
    public void ChunkRendererDrawsTheLiquidPoolsIntoTheMotionAttachmentOnly()
    {
        string chunk = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");

        string pass = MethodBodyAfter(chunk, "internal void RenderLiquidMotion(float deltaTime)");

        // Off entirely with TAA off, and never without the window.
        Assert.Contains("if (!Vintagestory.API.Config.OptimumConfig.EffectiveTaa)", pass);
        Assert.Contains("if (!optimumPlatform.BeginMotionOnlyWrite())", pass);
        Assert.Contains("optimumPlatform.EndMotionOnlyWrite();", pass);
        // Closed on every path, including a throwing draw.
        Assert.Contains("finally", pass);

        // Depth test on against Primary's depth, and the depth WRITE on: the OIT
        // liquid draw writes no depth (LoadFrameBuffer(Transparent) drops the
        // mask), so without this the resolve's writer-depth test rejects every
        // liquid pixel and the pass buys nothing.
        Assert.Contains("platform.GlEnableDepthTest();", pass);
        Assert.Contains("platform.GlDepthMask(flag: true);", pass);
        // No blending: the motion attachment is a vector, not a colour.
        Assert.Contains("platform.GlToggleBlend(on: false);", pass);
        // Culling off, as the OIT liquid draw runs.
        Assert.Contains("platform.GlDisableCullFace();", pass);
        // And the state the AfterOIT stage left is handed back, because this
        // pass runs after that stage and the post chain starts from it.
        Assert.Contains("platform.GlToggleBlend(on: true);", pass);

        // The same jittered projection and terrain view the OIT liquid draw
        // used, the previous-frame transforms from the shared contract helper,
        // and the reactive value.
        Assert.Contains("liquidMotion.UniformMatrix(\"projectionMatrix\", game.CurrentProjectionMatrix);", pass);
        Assert.Contains("liquidMotion.UniformMatrix(\"modelViewMatrix\", game.CurrentModelViewMatrix);", pass);
        Assert.Contains("game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);", pass);
        Assert.Contains("SetOptimumMotionUniforms(liquidMotion);", pass);
        Assert.Contains("liquidMotion.Uniform(\"taaLiquidReactive\", OptimumLiquidReactive);", pass);

        // Pool 4 is the liquid pool - the same one RenderOIT and the LiquidDepth
        // prepass draw - and the SSBO path is off for it there too.
        Assert.Contains("poolsByRenderPass[4][i].Render(cameraPos, \"origin\");", pass);
        Assert.Contains("game.api.renderapi.useSSBOs = false;", pass);

        // The reactive value the plan starts from.
        Assert.Contains("internal const float OptimumLiquidReactive = 0.3f;", chunk);
    }

    /// <summary>
    /// Review finding: the pass used to restore useSSBOs, pop the matrix and
    /// turn blending back on INSIDE the try, after the pool draws. A throwing
    /// draw then left the renderer with SSBOs off, an unbalanced matrix stack
    /// and blending off for the rest of the frame. Every restore now sits in
    /// the finally, and the useSSBOs snapshot is taken before the try so the
    /// finally always has something to hand back.
    /// </summary>
    [Fact]
    public void EveryLiquidPassRestoreRunsInTheFinallyBlock()
    {
        string chunk = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");

        string pass = MethodBodyAfter(chunk, "internal void RenderLiquidMotion(float deltaTime)");

        var tryMatch = System.Text.RegularExpressions.Regex.Match(pass, @"try\s*\{");
        Assert.True(tryMatch.Success, "the pass no longer has a try block");
        int tryStart = tryMatch.Index;
        int capture = pass.IndexOf("bool useSSBOs = game.api.renderapi.useSSBOs;", StringComparison.Ordinal);
        Assert.True(capture >= 0 && capture < tryStart,
            "the useSSBOs snapshot must be taken before the try, or the finally cannot restore it");

        string restores = FinallyBlock(pass);
        Assert.Contains("game.api.renderapi.useSSBOs = useSSBOs;", restores);
        Assert.Contains("game.GlPopMatrix();", restores);
        Assert.Contains("platform.GlToggleBlend(on: true);", restores);
        Assert.Contains("optimumPlatform.EndMotionOnlyWrite();", restores);

        // ...and nowhere else: a restore left in the try is a restore a throwing
        // pool draw skips.
        string guarded = pass.Substring(tryStart, pass.IndexOf(restores, StringComparison.Ordinal) - tryStart);
        Assert.DoesNotContain("game.api.renderapi.useSSBOs = useSSBOs;", guarded);
        Assert.DoesNotContain("GlPopMatrix", guarded);
        Assert.DoesNotContain("GlToggleBlend(on: true)", guarded);

        // The pop is balanced against the push that actually happened.
        Assert.Contains("pushedMatrix = true;", pass);
        Assert.Contains("if (pushedMatrix)", restores);
    }

    /// <summary>
    /// Placement is the whole reason this is a separate pass. It has to run
    /// after the OIT merge (the liquid it re-draws was shaded into the
    /// Transparent target), after every AfterOIT renderer (it writes depth for
    /// the water surface, which would otherwise occlude geometry drawn later
    /// that is legitimately visible through water), and before the resolve,
    /// which only reads the motion attachment in RenderPostprocessingEffects.
    /// </summary>
    [Fact]
    public void TheVelocityPassRunsAfterTheAfterOitStageAndInsideTheTemporalWindow()
    {
        // Read from the source of truth, not the patch: ordering is a property of
        // the whole method, and a patch only carries its hunks plus three lines
        // of context. That the method ships at all is asserted separately, by
        // the patcher-target check below.
        string main = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        int merge = main.IndexOf("Platform.MergeTransparentRenderPass();", StringComparison.Ordinal);
        int afterOit = main.IndexOf("TriggerRenderStage(EnumRenderStage.AfterOIT, dt);", StringComparison.Ordinal);
        int liquidMotion = main.IndexOf("chunkRenderer.RenderLiquidMotion(dt);", StringComparison.Ordinal);
        int closeJitter = main.IndexOf("OptimumTemporal.Frame.JitterActive = false;", StringComparison.Ordinal);

        Assert.True(merge >= 0, "the OIT merge is not in the patched body");
        Assert.True(afterOit >= 0, "the AfterOIT stage is not in the patched body");
        Assert.True(liquidMotion >= 0, "the liquid velocity pass is never called");
        Assert.True(closeJitter >= 0, "the jitter window is never closed");

        Assert.True(merge < liquidMotion, "the velocity pass must run after the OIT merge");
        Assert.True(afterOit < liquidMotion, "the velocity pass must run after every AfterOIT renderer");
        Assert.True(liquidMotion < closeJitter, "the velocity pass must run inside the temporal window");

        // Skipped when the transparent pass that shaded the liquid did not run.
        Assert.Contains("if (doTransparentRenderPass && chunkRenderer != null)", main);
    }

    // -------------------------------------------------------- the ship

    [Fact]
    public void TheProgramIsRegisteredAndOptional()
    {
        string registry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");

        Assert.Contains(
            "RegisterOptimumShaderProgram(\"chunkliquidmotion\", ShaderPrograms.ChunkLiquidMotion = new ShaderProgram());",
            registry);

        // Optimum-only programs mark LoadError on a failed compile instead of
        // failing the whole shader load, the way the FSR and TAA programs do.
        Assert.Contains("shaderProgram == ShaderPrograms.ChunkLiquidMotion", registry);
    }

    [Fact]
    public void CecilPatcherShipsEveryLiquidMotionMethodAndMember()
    {
        string patcher = PatcherSource.Read();

        Assert.Contains("\"BeginMotionOnlyWrite\"", patcher);
        Assert.Contains("\"EndMotionOnlyWrite\"", patcher);
        Assert.Contains("\"optimumMotionOnlyDrawBuffers\"", patcher);
        Assert.Contains("\"ChunkLiquidMotion\"", patcher);
        Assert.Contains("\"RenderLiquidMotion\"", patcher);
        Assert.Contains("\"OptimumLiquidReactive\"", patcher);

        // The two bodies that call into all of it.
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"MainRenderLoop\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"registerDefaultShaderProgramsPre\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"loadRegisteredShaderPrograms\", 0", patcher);
    }

    /// <summary>
    /// A mod that ships its own chunkliquid.vsh moves the water surface the
    /// velocity pass is aiming at; one that ships chunkliquidmotion replaces the
    /// writer outright. Either way the vectors stop describing the surface that
    /// was shaded, so TAA is disabled rather than fed wrong data.
    /// </summary>
    [Fact]
    public void TheCompatibilityScannerDisablesTaaForAnExternalLiquidShader()
    {
        string scanner = Read("Optimum.Launcher/ShaderCompatibilityScanner.cs");

        foreach (string shader in new[]
        {
            "chunkliquid.vsh", "chunkliquidmotion.vsh", "chunkliquidmotion.fsh",
        })
        {
            Assert.Contains("HasExternalShader(report, \"" + shader + "\")", scanner);
        }
        Assert.Contains("AddFeatureDecision(report, \"Taa\", externalMotionShader,", scanner);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>The braced block of the method's finally clause.</summary>
    /// <summary>
    /// The pass's own restore block: the LAST finally in the method body. Phase 3b stage 2 put a
    /// short inner try/finally around each pool loop (the native chunk-pass scope), so the first
    /// finally in the method is no longer the one that restores the pass's state.
    /// </summary>
    private static string FinallyBlock(string body)
    {
        System.Text.RegularExpressions.MatchCollection matches =
            System.Text.RegularExpressions.Regex.Matches(body, @"finally\s*\{");
        Assert.True(matches.Count > 0, "no finally block");
        var match = matches[matches.Count - 1];
        int open = body.IndexOf('{', match.Index);
        int depth = 0;
        for (int i = open; i < body.Length; i++)
        {
            if (body[i] == '{') depth++;
            else if (body[i] == '}')
            {
                depth--;
                if (depth == 0) return body.Substring(open, i - open + 1);
            }
        }
        throw new InvalidOperationException("unterminated finally block");
    }

    private static string MethodBodyAfter(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "no such method: " + signature);
        return signature + BodyOf(source, signature);
    }

    private static string BodyOf(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "no such function: " + signature);
        int open = source.IndexOf('{', start);
        Assert.True(open > start);

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, i - open + 1);
            }
        }
        throw new InvalidOperationException("unterminated function body: " + signature);
    }

    /// <summary>The text between two markers, searching from <paramref name="from" />.</summary>
    private static string Between(string source, string start, string end, int from)
    {
        int begin = source.IndexOf(start, from, StringComparison.Ordinal);
        Assert.True(begin >= 0, "marker not found: " + start);
        int stop = source.IndexOf(end, begin, StringComparison.Ordinal);
        Assert.True(stop > begin, "marker not found: " + end);
        return source.Substring(begin, stop - begin);
    }

    /// <summary>Every whitespace run collapsed to one space, so indentation and
    /// vanilla's trailing whitespace cannot fail the comparison.</summary>
    private static string Squash(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && builder.Length > 0) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
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
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
}

// Source: Optimum.Tests/taa-mover-motion-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

/// <summary>
/// Source coverage for the TAA P4 "movers" carry-over: the standard-shader block
/// entity renderers that actually move and therefore have to write exact motion
/// instead of ghosting on the resolve's camera fallback.
///
/// The load-bearing test here is <see cref="EveryStandardShaderUserInTheModForksIsInstrumentedOrExplicitlyExempt" />.
/// It enumerates the mod forks rather than listing files, so a renderer added or
/// un-instrumented later fails the build instead of silently ghosting: the failure
/// mode this phase exists to fix is invisible in a screenshot of a still scene and
/// only shows up as a smear while something on screen is animating.
///
/// Text assertions only prove the wiring exists - the GPU test
/// (Optimum.Render.Vulkan.Tests/TaaMoverMotionTests) proves the numbers.
/// </summary>
public class TaaMoverMotionCoverageTests
{
    /// <summary>The mod forks that are sources of truth for mod code.</summary>
    private static readonly string[] ModForks = { "VSEssentials", "VSSurvivalMod", "VSCreativeMod" };

    /// <summary>
    /// Every standard-shader user that is deliberately NOT instrumented, with the
    /// reason. Adding a file here is a decision, not a shortcut: "it does not move"
    /// has to be true, because the resolve's camera reprojection is exact only for
    /// a surface that is static in the world.
    ///
    /// Keys are repository-relative paths with forward slashes.
    /// </summary>
    private static readonly Dictionary<string, string> NotInstrumented = new()
    {
        ["VSSurvivalMod/BlockEntityRenderer/AnvilPartRenderer.cs"] =
            "The anvil base, flux and work item sit at the block position. The top mesh sinks by " +
            "hammerHits/250 - a discrete step on a hit, not a per-frame animation - so a wrong " +
            "vector would last one frame; the hot work item itself is drawn by AnvilWorkItemRenderer " +
            "on the mod's own smithing program, which declares no motion output at all.",

        ["VSSurvivalMod/BlockEntityRenderer/BlockEntitySignPostRenderer.cs"] =
            "A text quad nailed to the sign post at a fixed offset from the block position.",

        ["VSSurvivalMod/BlockEntityRenderer/ChestLabelRenderer.cs"] =
            "A label quad nailed to the chest at a fixed offset from the block position.",

        ["VSSurvivalMod/BlockEntityRenderer/ClayFormRenderer.cs"] =
            "The work item is drawn at the block position and only changes when a voxel is added " +
            "or removed, which re-uploads the mesh. Its second draw is the recipe outline on " +
            "AfterFinalComposition, which is outside the temporal window entirely.",

        ["VSSurvivalMod/BlockEntityRenderer/CrucibleInFirepitRenderer.cs"] =
            "The crucible sits still in the firepit; only its glow changes.",

        ["VSSurvivalMod/BlockEntityRenderer/GroundStorageRenderer.cs"] =
            "Stacks are drawn at fixed offsets inside the block; the per-frame work is a " +
            "once-a-second temperature refresh, not motion.",

        ["VSSurvivalMod/BlockEntityRenderer/IngotMoldRenderer.cs"] =
            "The fill quad's height changes only when metal is poured in or the mold is emptied, " +
            "in discrete steps, and a step swaps the mesh, so history would be rejected anyway.",

        ["VSSurvivalMod/BlockEntityRenderer/KnappingRenderer.cs"] =
            "The knapping surface is drawn at the block position and changes only when a voxel is " +
            "knocked off, which re-uploads the mesh. Its AfterFinalComposition guide draw is " +
            "outside the temporal window entirely.",

        ["VSSurvivalMod/BlockEntityRenderer/SignRenderer.cs"] =
            "A text quad nailed to the sign at a fixed offset from the block position.",

        ["VSSurvivalMod/BlockEntityRenderer/ToolMoldRenderer.cs"] =
            "As IngotMoldRenderer: the fill level changes in discrete steps and each step picks a " +
            "different quad mesh, so there is no continuous motion to record.",

        ["VSSurvivalMod/Systems/SupportBeams/ModSystemSupportBeamPlacer.cs"] =
            "The beam preview follows the player's aim, but its model matrix is the fixed start " +
            "block and the shape is re-uploaded (reloadMeshRef) on every change of the end offset, " +
            "so a previous model matrix would never be valid history.",
    };

    /// <summary>
    /// Standard-shader users that must carry the writer. The P3 three (held items,
    /// dropped items, the quern) plus the P4 movers.
    /// </summary>
    private static readonly string[] Instrumented =
    {
        "VSEssentials/EntityRenderer/EntityShapeRenderer.cs",
        "VSEssentials/EntityRenderer/EntityItemRenderer.cs",
        "VSEssentials/Entities/EntityBlockFalling.cs",
        "VSSurvivalMod/BlockEntityRenderer/QuernTopRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/HelveHammerRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/FruitpressContentsRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/ResonatorRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/BloomeryContentsRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/ForgeContentsRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/FirepitContentsRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/PotInFirepitRenderer.cs",
    };

    /// <summary>
    /// The named list and the scan have to agree: the scan is what catches a new
    /// renderer, the list is what catches an instrumented one quietly losing its
    /// writer while still being found by the scan.
    /// </summary>
    [Fact]
    public void EveryNamedInstrumentedRendererIsFoundByTheScanAndStillWritesMotion()
    {
        List<string> users = StandardShaderUsers();

        foreach (string source in Instrumented)
        {
            Assert.Contains(source, users);
            Assert.Contains("OptimumStandardMotion.Apply(", ReadRepositoryFile(source));
        }
    }

    // ------------------------------------------------------------- the census

    /// <summary>
    /// The table that makes the phase checkable: every file in the mod forks that
    /// draws through the standard shader program is either a motion writer or has
    /// a written reason not to be. Nothing may be silently absent.
    /// </summary>
    [Fact]
    public void EveryStandardShaderUserInTheModForksIsInstrumentedOrExplicitlyExempt()
    {
        List<string> users = StandardShaderUsers();

        // If the discovery itself breaks, everything below passes vacuously.
        Assert.True(users.Count >= 20,
            "only " + users.Count + " standard-shader users found; the scan is broken");

        var missing = new List<string>();
        foreach (string user in users)
        {
            string text = ReadRepositoryFile(user);
            bool instrumented = text.Contains("OptimumStandardMotion.Apply(", StringComparison.Ordinal);
            bool exempt = NotInstrumented.ContainsKey(user);

            if (instrumented && exempt)
            {
                missing.Add(user + " is instrumented AND on the exemption list");
            }
            else if (!instrumented && !exempt)
            {
                missing.Add(user + " draws on the standard shader, writes no motion, and has no " +
                            "reason on TaaMoverMotionCoverageTests.NotInstrumented");
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>
    /// An exemption without a reason is a to-do pretending to be a decision.
    /// </summary>
    [Fact]
    public void EveryExemptionCarriesARealReasonAndNamesAFileThatStillExists()
    {
        foreach ((string path, string reason) in NotInstrumented)
        {
            Assert.True(reason.Length >= 60, path + ": the exemption reason is too thin to be one");
            Assert.True(
                File.Exists(Path.Combine(RepositoryRoot(), path.Replace('/', Path.DirectorySeparatorChar))),
                path + " is on the exemption list but no longer exists");
        }
    }

    /// <summary>
    /// The exemption list must not grow stale in the other direction either: a file
    /// listed there that no longer draws on the standard shader is a leftover.
    /// </summary>
    [Fact]
    public void NoExemptionNamesAFileThatNoLongerDrawsOnTheStandardShader()
    {
        List<string> users = StandardShaderUsers();
        foreach (string path in NotInstrumented.Keys)
        {
            Assert.Contains(path, users);
        }
    }

    // -------------------------------------------------------- the instrumented

    /// <summary>
    /// Each mover names itself to the per-object store and opens the draw-buffer
    /// window around its own draw. The window has to be narrow: a standard-shader
    /// draw that is not instrumented must stay outside it, or the attachment would
    /// keep whatever surface wrote there before.
    /// </summary>
    [Theory]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/HelveHammerRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, meshref, ModelMat.Values);")]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/FruitpressContentsRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, mashMeshref, ModelMat.Values);")]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/ResonatorRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, cylinderMeshRef, ModelMat.Values);")]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/BloomeryContentsRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, cubeModelRef, ModelMat.Values);")]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/ForgeContentsRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, coalMeshRef, ModelMat.Values);")]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/FirepitContentsRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, meshref, ModelMat.Values);")]
    [InlineData("VSEssentials/Entities/EntityBlockFalling.cs",
        "OptimumStandardMotion.Apply(prog, entity, entity.meshRef, ModelMat.Values);")]
    public void EveryMoverStoresItsPreviousTransformAndOpensTheWindow(string source, string apply)
    {
        string renderer = ReadRepositoryFile(source);

        Assert.Contains(apply, renderer);
        Assert.Contains("OptimumMotionWrite.Begin();", renderer);
        Assert.Contains("OptimumMotionWrite.End();", renderer);

        // Paired, and closed on the exception path: a window left open would put
        // the motion attachment in every later draw's mask. A file-wide search for
        // "finally" would pass on a renderer whose window is closed by a bare call
        // while some unrelated method has the keyword, so check it per window.
        Assert.Equal(
            Count(renderer, "OptimumMotionWrite.Begin();"),
            Count(renderer, "OptimumMotionWrite.End();"));
        AssertEveryWindowClosesInAFinally(source, renderer);
    }

    /// <summary>
    /// For every <c>OptimumMotionWrite.Begin();</c>, the <c>End();</c> that closes it
    /// must sit in a <c>finally</c> that opens after that Begin.
    /// </summary>
    private static void AssertEveryWindowClosesInAFinally(string source, string renderer)
    {
        const string beginCall = "OptimumMotionWrite.Begin();";
        const string endCall = "OptimumMotionWrite.End();";

        for (int begin = renderer.IndexOf(beginCall, StringComparison.Ordinal); begin >= 0;
             begin = renderer.IndexOf(beginCall, begin + beginCall.Length, StringComparison.Ordinal))
        {
            int end = renderer.IndexOf(endCall, begin, StringComparison.Ordinal);
            Assert.True(end > begin, source + ": a motion window opens and is never closed");

            int keyword = renderer.IndexOf("finally", begin, StringComparison.Ordinal);
            Assert.True(keyword > begin && keyword < end,
                source + ": the motion window opened at offset " + begin +
                " is not closed inside a finally block");
        }
    }

    /// <summary>
    /// The resonator runs the same body again on AfterFinalComposition, where
    /// Begin() refuses because the temporal window is closed. Rolling the transform
    /// history there would overwrite the identity's current transform with a later,
    /// time-driven ModelMat, so next frame's previous transform would be off by a
    /// sub-frame delta. Apply must therefore sit inside the window.
    /// </summary>
    [Fact]
    public void TheResonatorRollsItsHistoryOnlyInsideTheWindow()
    {
        string renderer = ReadRepositoryFile("VSSurvivalMod/BlockEntityRenderer/ResonatorRenderer.cs");

        int begin = renderer.IndexOf("OptimumMotionWrite.Begin();", StringComparison.Ordinal);
        int apply = renderer.IndexOf("OptimumStandardMotion.Apply(", StringComparison.Ordinal);
        int end = renderer.IndexOf("OptimumMotionWrite.End();", StringComparison.Ordinal);

        Assert.True(begin >= 0 && apply > begin && apply < end,
            "the resonator must roll its transform history inside the motion window");
        Assert.Contains("if (optimumMotionWrite)", renderer);
    }

    /// <summary>
    /// Two draws in one renderer need two identities. The pot body and its lid
    /// share a renderer instance and a Matrixf, so keying both on <c>this</c> would
    /// hand the lid the body's previous matrix - a zero vector on the one part of
    /// the pot that actually moves.
    /// </summary>
    [Fact]
    public void ThePotAndItsLidKeepSeparateHistories()
    {
        string renderer = ReadRepositoryFile("VSSurvivalMod/BlockEntityRenderer/PotInFirepitRenderer.cs");

        Assert.Contains(
            "OptimumStandardMotion.Apply(prog, this, potRef == null ? potWithFoodRef : potRef, ModelMat.Values);",
            renderer);
        Assert.Contains("OptimumStandardMotion.Apply(prog, lidRef, lidRef, ModelMat.Values);", renderer);
        Assert.Equal(2, Count(renderer, "OptimumStandardMotion.Apply("));
        Assert.Equal(2, Count(renderer, "OptimumMotionWrite.Begin();"));
        Assert.Equal(2, Count(renderer, "OptimumMotionWrite.End();"));
    }

    /// <summary>
    /// The falling-block renderer is one renderer for every falling block in view,
    /// so the identity has to be the entity. Keying on the renderer would give
    /// every block the last block's previous matrix.
    /// </summary>
    [Fact]
    public void FallingBlocksKeyTheirHistoryOnTheEntityAndShareOneWindow()
    {
        string renderer = ReadRepositoryFile("VSEssentials/Entities/EntityBlockFalling.cs");

        Assert.Contains("OptimumStandardMotion.Apply(prog, entity, entity.meshRef, ModelMat.Values);", renderer);
        Assert.DoesNotContain("OptimumStandardMotion.Apply(prog, this,", renderer);

        // One window around the loop, not one per block.
        Assert.Equal(1, Count(renderer, "OptimumMotionWrite.Begin();"));
        int begin = renderer.IndexOf("OptimumMotionWrite.Begin();", StringComparison.Ordinal);
        int loop = renderer.IndexOf("foreach (var entity in fallingBlocks.Values)", StringComparison.Ordinal);
        Assert.True(begin >= 0 && loop > begin, "the window must open before the loop, not inside it");
    }

    /// <summary>
    /// The helve hammer runs the same method for the shadow stages with a program
    /// that has no motion output, so the window must sit inside its Opaque branch.
    /// </summary>
    [Fact]
    public void TheHelveHammerOpensNoWindowInItsShadowBranch()
    {
        string renderer = ReadRepositoryFile("VSSurvivalMod/BlockEntityRenderer/HelveHammerRenderer.cs");

        int opaque = renderer.IndexOf("if (stage == EnumRenderStage.Opaque)", StringComparison.Ordinal);
        int elseBranch = renderer.IndexOf("} else", opaque, StringComparison.Ordinal);
        int begin = renderer.IndexOf("OptimumMotionWrite.Begin();", StringComparison.Ordinal);

        Assert.True(opaque >= 0 && elseBranch > opaque, "the stage branch moved");
        Assert.InRange(begin, opaque, elseBranch);
    }

    // --------------------------------------------------------------- the ship

    /// <summary>
    /// P3 finding (f): a mod-fork change only reaches the installed runtime through
    /// a mod-patcher manifest entry. Without these the vanilla bodies stay and every
    /// mover ghosts there while the build tree looks correct.
    /// </summary>
    [Theory]
    [InlineData("Vintagestory.GameContent.ModSystemRenderFallingBlocksFast")]
    [InlineData("Vintagestory.GameContent.HelveHammerRenderer")]
    [InlineData("Vintagestory.GameContent.FruitpressContentsRenderer")]
    [InlineData("Vintagestory.GameContent.ResonatorRenderer")]
    [InlineData("Vintagestory.GameContent.BloomeryContentsRenderer")]
    [InlineData("Vintagestory.GameContent.ForgeContentsRenderer")]
    [InlineData("Vintagestory.GameContent.FirepitContentsRenderer")]
    [InlineData("Vintagestory.GameContent.PotInFirepitRenderer")]
    public void ModPatcherManifestsCarryEveryChangedMover(string type)
    {
        string manifest = ReadRepositoryFile("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("new(\"" + type + "\", \"OnRenderFrame\", 2)", manifest);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Every C# file in the mod forks that draws through the standard shader
    /// program, repository-relative with forward slashes. Discovery is by scan so
    /// that a new renderer cannot be missed by being absent from a list.
    /// </summary>
    private static List<string> StandardShaderUsers()
    {
        string root = RepositoryRoot();
        var users = new List<string>();

        foreach (string fork in ModForks)
        {
            string forkRoot = Path.Combine(root, fork);
            if (!Directory.Exists(forkRoot)) continue;

            foreach (string file in Directory.EnumerateFiles(forkRoot, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (!UsesStandardShader(text)) continue;

                users.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        users.Sort(StringComparer.Ordinal);
        return users;
    }

    /// <summary>
    /// A file draws on the standard shader when it names it at all - the type
    /// <c>IStandardShaderProgram</c>, the <c>StandardShader</c> property or
    /// <c>PreparedStandardShader</c>, every one of which contains the same
    /// substring - and then draws a mesh with it. Deliberately loose on the first
    /// half: over-reporting costs an exemption line, under-reporting costs a
    /// ghosting renderer nobody notices.
    /// </summary>
    private static bool UsesStandardShader(string text)
    {
        if (!text.Contains("StandardShader", StringComparison.Ordinal)) return false;

        return text.Contains("RenderMesh(", StringComparison.Ordinal) ||
               text.Contains("RenderMultiTextureMesh(", StringComparison.Ordinal);
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

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(RepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string RepositoryRoot()
    {
        return Path.GetDirectoryName(PatchReader.FindRepositoryFile("TAA-PLAN.md"))!;
    }
}
}
