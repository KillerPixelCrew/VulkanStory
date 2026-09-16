using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The post and TAA chain as a chain (docs/vulkan-native-render-systems.md, stage 1): the OpenGL
/// body is one virtual per pass, Optimum's platform owns the order and never calls base for the
/// steps the chain holds, the first two passes draw natively, and every remaining step is a
/// legacy helper that names the stage which will replace it.
/// </summary>
public class NativePostChainCoverageTests
{
    private const string ChainFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativePostChain.cs";
    private const string TailFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativePostFinal.cs";

    /// <summary>
    /// The lib body is split into one virtual per pass, and RenderPostprocessingEffects is only
    /// their order. The split is a lift, not a rewrite: each step keeps the body it had.
    /// </summary>
    [Fact]
    public void TheOpenGlPostBodyIsOneVirtualPerPass()
    {
        string platform = Platform();

        foreach (string member in new[]
                 {
                     "public virtual void OptimumPostAmbientOcclusion(float[] projectMatrix)",
                     "public virtual int OptimumPostSceneTexture()",
                     "public virtual int OptimumPostGlowTexture()",
                     "public virtual void OptimumPostBloom(int postSceneTexture, int postGlowTexture)",
                     "public virtual void OptimumPostGodRays(int postSceneTexture, int postGlowTexture)",
                     "public virtual void OptimumPostLuma(int postSceneTexture)",
                     "public virtual void OptimumPostFinish()",
                     "public virtual void OptimumBindKeepViewport(FrameBufferRef value)",
                 })
        {
            Assert.Contains(member, platform);
        }

        // The steps' bodies, still the OpenGL body's own code.
        Assert.Contains("ShaderProgramSsao ssao = ShaderPrograms.Ssao;", platform);
        Assert.Contains("ShaderProgramFindbright findbright = ShaderPrograms.Findbright;", platform);
        Assert.Contains("godrays.SunPos3dIn = ShaderUniforms.LightPosition3D;", platform);
        Assert.Contains("return TaaResolvedThisFrame ? taaResolvedColorTexture : frameBuffers[0].ColorTextureIds[0];", platform);
        Assert.Contains("return TaaResolvedThisFrame ? taaResolvedGlowTexture : frameBuffers[0].ColorTextureIds[1];", platform);

        // Every new lib member is a patcher target and an owned region.
        string patcher = Read("Optimum.Patcher/Program.cs");
        string regions = Read("Optimum.Tests/client-platform-windows-vanilla-regions-tests.cs");
        foreach (string member in new[]
                 {
                     "OptimumPostAmbientOcclusion", "OptimumPostSceneTexture", "OptimumPostGlowTexture",
                     "OptimumPostBloom", "OptimumPostGodRays", "OptimumPostLuma", "OptimumPostFinish",
                     "OptimumBindKeepViewport",
                 })
        {
            Assert.Contains("\"" + member + "\"", patcher);
            Assert.Contains("\"" + member + "\"", regions);
        }
        Assert.Contains("\"OptimumOitRevealTexture\"", patcher);
        Assert.Contains("\"OptimumOitAccumTexture\"", patcher);
    }

    /// <summary>The OpenGL body runs its steps in the order it always ran them.</summary>
    [Fact]
    public void TheOpenGlPostBodyKeepsItsOrder()
    {
        string platform = Platform();
        int start = platform.IndexOf("public override void RenderPostprocessingEffects(float[] projectMatrix)",
            StringComparison.Ordinal);
        Assert.True(start >= 0);

        int previous = start;
        foreach (string step in new[]
                 {
                     "OptimumPostAmbientOcclusion(projectMatrix);",
                     "RenderOptimumTaaResolve();",
                     "int postSceneTexture = OptimumPostSceneTexture();",
                     "postSceneTexture = RenderOptimumTaaSharpen(postSceneTexture);",
                     "OptimumPostBloom(postSceneTexture, postGlowTexture);",
                     "OptimumPostGodRays(postSceneTexture, postGlowTexture);",
                     "OptimumPostLuma(postSceneTexture);",
                     "OptimumPostFinish();",
                 })
        {
            int at = platform.IndexOf(step, previous, StringComparison.Ordinal);
            Assert.True(at > previous, step + " is missing or out of order in RenderPostprocessingEffects");
            previous = at;
        }
    }

    /// <summary>
    /// The Vulkan chain owns the order, in the doc's section 3 sequence, and its
    /// RenderPostprocessingEffects override never calls base on the native route.
    /// </summary>
    [Fact]
    public void TheVulkanChainOwnsTheOrderAndNeverCallsBase()
    {
        string chain = Read(ChainFile);

        int previous = chain.IndexOf("internal static readonly NativePostStep[] NativePostChainOrder", StringComparison.Ordinal);
        Assert.True(previous >= 0);
        foreach (string step in new[]
                 {
                     "NativePostStep.OitMerge,", "NativePostStep.SkyMotion,",
                     "NativePostStep.SsaoAndAmbientOcclusion,", "NativePostStep.TaaResolve,",
                     "NativePostStep.TaaSharpen,", "NativePostStep.Bloom,", "NativePostStep.GodRays,",
                     "NativePostStep.FxaaOrBlit,", "NativePostStep.FinalComposition,", "NativePostStep.Blit,",
                 })
        {
            int at = chain.IndexOf(step, previous, StringComparison.Ordinal);
            Assert.True(at > previous, step + " is missing or out of order in NativePostChainOrder");
            previous = at;
        }

        // The chain's own steps, in the same order, inside the override's body.
        previous = chain.IndexOf("private void RunNativePostChain(float[] projectMatrix)", StringComparison.Ordinal);
        Assert.True(previous >= 0);
        foreach (string step in new[]
                 {
                     "PostStepAmbientOcclusion(projectMatrix);", "PostStepTaaResolve();",
                     "scene = PostStepTaaSharpen(scene);", "PostStepBloom(scene, glow);",
                     "PostStepGodRays(scene, glow);", "PostStepFxaaOrBlit(scene);", "PostStepFinish();",
                 })
        {
            int at = chain.IndexOf(step, previous, StringComparison.Ordinal);
            Assert.True(at > previous, step + " is missing or out of order in the native chain");
            previous = at;
        }

        string graph = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Graph.cs");
        Assert.Contains("if (UseNativePostChain)\n        {\n            RunNativePostChain(projectMatrix);\n            return;\n        }", graph.Replace("\r\n", "\n"));
        Assert.Contains("NativeOitMerge();", graph);
        Assert.Contains("return UseNativePostChain ? NativeSkyMotion() : LegacySkyMotion();", graph);
        Assert.Contains("LegacyFinalComposition();", graph);

        // The test switch that puts the whole chain back on the OpenGL body.
        Assert.Contains("internal bool NativePostChainEnabled { get; set; } = true;", chain);
        Assert.Contains("private bool UseNativePostChain => NativePostChainEnabled && device != null;", chain);
        Assert.Contains("if (NativeBlitEnabled && UseNativePostChain)", graph);
    }

    /// <summary>
    /// The two native passes draw through the device API - a pipeline with fixed state, a
    /// declared pass with explicit reads and colour slots, and a native fullscreen draw - and
    /// keep the OpenGL body's conditions, blend and uniform values.
    /// </summary>
    [Fact]
    public void TheFirstTwoPassesDrawNatively()
    {
        string chain = Read(ChainFile);

        Assert.Contains("device.RequestNativePipeline(new NativePipelineDescription", chain);
        Assert.Contains("device.BeginNativePass(new NativePassDescription", chain);
        Assert.Contains("device.DrawNativeFullscreen(pipeline, new[]", chain);
        Assert.Contains("device.EndNativePass();", chain);

        // The merge: the Transparent target's three attachments plus the OIT pair, the world
        // colour set with the motion attachment added while the window is open, and the
        // additive (ONE, ONE) blend on that attachment alone.
        Assert.Contains("nativeOitMerge = new(\"transparentcompose\"", chain);
        Assert.Contains("\"accumulation\", \"revealage\", \"inGlow\", \"OITreveal\", \"OITaccumulation\"", chain);
        Assert.Contains("SystemRenderOITLayers.OptimumOitRevealTexture", chain);
        Assert.Contains("SystemRenderOITLayers.OptimumOitAccumTexture", chain);
        Assert.Contains("if (motion) slots |= 1u << MotionAttachmentIndex;", chain);
        Assert.Contains("attachment.SrcColor = BlendFactor.One;", chain);
        Assert.Contains("private uint NativeWorldColorSlots() => OptimumRenderSsao ? 0b1111u : 0b11u;", chain);
        Assert.Contains("OptimumBindKeepViewport(primary);", chain);
        Assert.Contains("ApplyTransparentMergeBlendState();", chain);

        // Sky motion: the same guards, the same two matrices, LEQUAL with depth writes off, and
        // the motion attachment as the pass's only colour slot.
        Assert.Contains("nativeSkyMotion = new(\"taa-skymotion\"", chain);
        Assert.Contains("if (!frame.WasViewCaptured(EnumTemporalView.World)) return false;", chain);
        Assert.Contains("OptimumTemporalMath.ApplyProjectionJitter(jittered, frame.JitterPx.X, frame.JitterPx.Y,", chain);
        Assert.Contains("float[] invViewProj = Mat4f.Invert(new float[16], viewProj);", chain);
        Assert.Contains("if (invViewProj == null) return false;", chain);
        Assert.Contains("uint slots = 1u << MotionAttachmentIndex;", chain);
        Assert.Contains("depthTest: true, depthWrite: false, CompareOp.LessOrEqual", chain);
        Assert.Contains("device.WriteNative(pipeline, nativeSkyMotion.Uniforms[4], OptimumCloudReactive);", chain);
        Assert.Contains("GlDepthFunc(EnumDepthFunction.Less);", chain);
        Assert.Contains("GlEnableCullFace();", chain);

        // The motion window's guards, minus the draw-buffer mask a native pass does not use.
        Assert.Contains("private bool NativeMotionAttachmentWritable(FrameBufferRef primary)", chain);
        Assert.Contains("if (!OptimumTemporal.Frame.JitterActive) return false;", chain);
        Assert.Contains("return ReferenceEquals(CurrentFrameBuffer, primary);", chain);
    }

    /// <summary>
    /// Every step that is not native yet has a legacy helper naming the stage that replaces it,
    /// so the chain is complete at every commit and a later stage moves exactly one helper.
    /// </summary>
    [Fact]
    public void EveryRemainingStepHasALegacyHelperNamingItsStage()
    {
        string chain = Read(ChainFile);

        foreach (string helper in new[]
                 {
                     "private void PostStepAmbientOcclusion(float[] projectMatrix) => OptimumPostAmbientOcclusion(projectMatrix);",
                     "private bool PostStepTaaResolve() => RenderOptimumTaaResolve();",
                     "private int PostStepTaaSharpen(int resolvedScene) => RenderOptimumTaaSharpen(resolvedScene);",
                     "private void PostStepBloom(int scene, int glow) => NativeBloom(scene, glow);",
                     "private void PostStepGodRays(int scene, int glow) => NativeGodRays(scene, glow);",
                     "private void PostStepFxaaOrBlit(int scene) => NativePostLuma(scene);",
                     "private void PostStepFinish() => OptimumPostFinish();",
                     "private void LegacyBloom(int scene, int glow) => OptimumPostBloom(scene, glow);",
                     "private void LegacyGodRays(int scene, int glow) => OptimumPostGodRays(scene, glow);",
                     "private void LegacyPostLuma(int scene) => OptimumPostLuma(scene);",
                     "private void LegacyFinalComposition()",
                     "private void LegacyOitMerge()",
                     "private bool LegacySkyMotion()",
                 })
        {
            Assert.Contains(helper, chain);
        }

        foreach (string stage in new[] { "Stage 1c makes it native", "Stage 1d makes it native" })
        {
            Assert.Contains(stage, chain);
        }
        // One old route per pass that is not native yet, plus one per native pass that keeps its
        // OpenGL body reachable for the differential tests: the merge, sky motion, the AO step,
        // the resolve, the sharpen, bloom, god rays, the Luma step and the final composition.
        Assert.Equal(9, Count(chain, "LEGACY -"));
    }

    /// <summary>
    /// The chain's tail - bloom, god rays, the Luma step and the final composition - draws through
    /// the device API, with the OpenGL body's conditions, inputs and uniform values, and the final
    /// composition declares the attachment subset that lets it sample Primary colour 1 while it
    /// writes Primary colour 0.
    /// </summary>
    [Fact]
    public void TheChainTailDrawsNatively()
    {
        string tail = Read(TailFile);

        Assert.Contains("device.BeginNativePass(new NativePassDescription", tail);
        Assert.Contains("device.DrawNativeFullscreen(pipeline, new[]", tail);
        Assert.Contains("device.EndNativePass();", tail);

        // Bloom: find-bright then the two blur ping-pongs, the stale full-resolution frameSize
        // the OpenGL body reuses for all four blur draws, and the block's blend and viewport
        // handoff outside the passes.
        Assert.Contains("private void NativeBloom(int scene, int glow)", tail);
        Assert.Contains("if (!OptimumRenderBloom) return;", tail);
        Assert.Contains("float blurWidth = client.Width * ssaa;", tail);
        Assert.Contains("NativeBlurStep(lowV, nativeBlurLowVertical", tail);
        Assert.Contains("GlToggleBlend(on: false);", tail);
        Assert.Contains("GlToggleBlend(on: true);", tail);

        // God rays: the half-resolution target, the full-resolution input texel size, and
        // LightPosition3D behind sunPos3dIn.
        Assert.Contains("private void NativeGodRays(int scene, int glow)", tail);
        Assert.Contains("if (!OptimumRenderGodRays) return;", tail);
        Assert.Contains("ShaderUniforms.LightPosition3D", tail);
        Assert.Contains("device.WriteNative(pipeline, nativeGodRays.Uniforms[1], OptimumConfig.GodRaysSampleLimit);", tail);

        // The Luma branch: the raw jittered Primary colour for the FXAA prepass, the chain's
        // scene for the blit, and blending left off.
        Assert.Contains("bool fxaa = OptimumRenderFxaa && !TaaResolvedThisFrame;", tail);
        Assert.Contains("int source = fxaa ? primary!.ColorTextureIds[0] : scene;", tail);
        Assert.Contains("SetBlendEnabled(false);", tail);

        // The final composition: the attachment subset, the unconditional uniform writes and the
        // draw-buffer handoff around the pass.
        Assert.Contains("private void NativeFinalComposition()", tail);
        Assert.Contains("const uint slots = ~(1u << 1);", tail);
        Assert.Contains("BeginFinalCompositionDrawBuffers();", tail);
        Assert.Contains("RestoreWorldDrawBuffers(renderSsao);", tail);
        Assert.Contains("device.WriteNative(pipeline, u[1], (OptimumSsaoInScene || !renderSsao) ? 1 : 0);", tail);
        Assert.Contains("device.WriteNative(pipeline, u[2], aoDebugView ? 1 : 0);", tail);
        Assert.Contains("ShaderUniforms.SunPosition3D", tail);
        Assert.Contains("aoDebugView && aoTexture != 0", tail);
        Assert.Contains("int glow = OptimumPostGlowTexture();", tail);

        string graph = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Graph.cs");
        Assert.Contains("NativeFinalComposition();", graph);
    }

    /// <summary>
    /// The client state the native tail reads instead of GL state (decision 3) is a lib accessor,
    /// a patcher target and an owned region, and the two post steps that sized themselves from
    /// NativeWindow.ClientSize now read the same window-size seam the blit does.
    /// </summary>
    [Fact]
    public void TheNativeTailReadsClientStateThroughListedLibSeams()
    {
        string platform = Platform().Replace("\r\n", "\n");
        string patcher = Read("Optimum.Patcher/Program.cs");
        string regions = Read("Optimum.Tests/client-platform-windows-vanilla-regions-tests.cs");

        foreach (string member in new[]
                 {
                     "public bool OptimumRenderBloom => RenderBloom;",
                     "public bool OptimumRenderGodRays => RenderGodRays;",
                     "public bool OptimumRenderFxaa => RenderFXAA;",
                     "public float OptimumSsaaLevel => ssaaLevel;",
                     "public int OptimumAmbientOcclusionTexture => optimumAmbientOcclusionTexture;",
                     "public bool OptimumSsaoInScene => optimumSsaoInScene;",
                 })
        {
            Assert.Contains(member, platform);
        }
        foreach (string member in new[]
                 {
                     "OptimumRenderBloom", "OptimumRenderGodRays", "OptimumRenderFxaa", "OptimumSsaaLevel",
                     "OptimumAmbientOcclusionTexture", "OptimumSsaoInScene",
                 })
        {
            Assert.Contains("\"" + member + "\"", patcher);
            Assert.Contains("\"" + member + "\"", regions);
        }

        // The bloom, god-rays and final-composition bodies take the window size from the seam, so
        // both routes compute the same value; nothing in them reads NativeWindow.ClientSize.
        foreach (string body in new[]
                 {
                     "public virtual void OptimumPostBloom(int postSceneTexture, int postGlowTexture)",
                     "public virtual void OptimumPostGodRays(int postSceneTexture, int postGlowTexture)",
                     "public override void RenderFinalComposition()",
                 })
        {
            int start = platform.IndexOf(body, StringComparison.Ordinal);
            Assert.True(start >= 0, body + " is missing");
            int end = platform.IndexOf("\n\t}\n", start, StringComparison.Ordinal);
            Assert.True(end > start);
            string source = platform.Substring(start, end - start);
            Assert.Contains("Size2i optimumClientSize = OptimumWindowClientSize();", source);
            Assert.DoesNotContain("((NativeWindow)window).ClientSize", source);
        }
    }

    private static string Platform() => ReadPatchedOrSource(
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
        "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

    private static int Count(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
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
}
