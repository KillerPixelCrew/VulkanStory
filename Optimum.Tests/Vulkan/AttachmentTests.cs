// Source: Optimum.Tests/color-write-tier-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

/// <summary>
/// Phase 2 contract C4 (colour write masks) at source level: the optional
/// extensions are negotiated with an env override, draws emit the tier's dynamic
/// state, draw buffers never restart a scope, and a masked-out clear stays a no-op.
/// The pixels are proven per tier by Optimum.Render.Vulkan.Tests/MotionWindowTests.cs.
/// </summary>
public class ColorWriteTierCoverageTests
{
    [Fact]
    public void TheContextNegotiatesTheTiersWithAnOverride()
    {
        string tiers = Read("Optimum.Render.Vulkan/Core/ColorWriteTier.cs");
        Assert.Contains("OPTIMUM_VULKAN_COLOR_WRITE_TIER", tiers);
        Assert.Contains("public static ColorWriteTier SelectColorWriteTier(", tiers);

        string context = Read("Optimum.Render.Vulkan/Core/VulkanContext.cs");
        Assert.Contains("\"VK_EXT_color_write_enable\"", context);
        Assert.Contains("\"VK_EXT_extended_dynamic_state3\"", context);
        Assert.Contains("ExtendedDynamicState3ColorWriteMask = true,", context);
        Assert.Contains("options.ColorWriteTier ?? DeviceCaps.FromEnvironment()", context);
    }

    [Fact]
    public void DrawsEmitTheTiersColourWriteState()
    {
        string device = VulkanDeviceSource.Read();
        Assert.Contains("CmdSetColorWriteEnable(commandBuffer,", device);
        Assert.Contains("CmdSetColorWriteMask(commandBuffer, 0,", device);
        Assert.Contains("CmdSetColorBlendEquation(commandBuffer, 0,", device);

        string cache = Read("Optimum.Render.Vulkan/Core/PipelineCache.cs");
        Assert.Contains("DynamicState.ColorWriteEnableExt", cache);
        Assert.Contains("DynamicState.ColorWriteMaskExt", cache);
        // The undeclared-output masking is kept on every tier.
        Assert.Contains("request.Program.Interface.WrittenFragmentOutputs.Contains(i)", cache);
    }

    [Fact]
    public void DrawBufferChangesNeverRestartTheScope()
    {
        // Draw buffers are the stated route's per-attachment write masks; the render-target
        // manager has no draw-buffer state at all, so a change cannot restart a scope there.
        string targets = Read("Optimum.Render.Vulkan/Core/RenderTargetManager.cs");
        Assert.DoesNotContain("DrawBufferMask", targets);
        Assert.DoesNotContain("SampledExclusion", targets);
        Assert.Contains("VulkanStats.NoteMaskRestart();", targets);

        string state = Read("Optimum.Render.Vulkan/Platform/StatedRenderState.cs");
        Assert.Contains("blend.WriteMask = ((DrawBuffers(framebufferId) >> slot) & 1) != 0 ? ColorMask : 0;", state);
        // A stated draw on the same target and slots coalesces into the open pass.
        Assert.Contains("device.EndNativePass(keepScope: true);", Read("Optimum.Render.Vulkan/Platform/StatedDraw.cs"));

        // A clear on a draw buffer that is off, or through an all-false colour mask, is dropped by
        // the platform before it reaches the device (GL's rule on the stated draw buffers).
        string stated = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeStated.cs");
        Assert.Contains("if (((stated.DrawBuffers(framebufferId) >> slot) & 1) == 0 || stated.ColorMask == 0) return;", stated);

        string stats = Read("Optimum.Render.Vulkan/Core/VulkanStats.cs");
        Assert.Contains("mask_restarts={10} feedback_splits={11}", stats);
    }

    private static string Read(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "VintageStory.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!, relativePath));
    }
}
}

// Source: Optimum.Tests/oit-framebuffer-rebuild-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

/// <summary>
/// Verifies the OIT (Order-Independent Transparency) system correctly handles
/// framebuffer rebuild after resize and does not overwrite vanilla attachment 0.
/// </summary>
public class OitFramebufferRebuildCoverageTests
{
    [Fact]
    public void OitRebuildDetectsFramebufferIdentityChange()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs");

        // The rebuild condition must detect when the framebuffer instance changed
        // (after RebuildFrameBuffers replaces the list entry with a new object).
        Assert.Contains("transparentfb != currentTransparentfb", source);
    }

    [Fact]
    public void OitRebuildComparesPreviousFramebufferToCurrentBeforeReplacingIt()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs");

        int currentFramebuffer = source.IndexOf(
            "FrameBufferRef currentTransparentfb = capi.Render.FrameBuffers[1]",
            StringComparison.Ordinal);
        int identityCheck = source.IndexOf(
            "transparentfb != currentTransparentfb",
            StringComparison.Ordinal);
        int methodEnd = source.IndexOf("private void freeResources", currentFramebuffer, StringComparison.Ordinal);

        Assert.True(currentFramebuffer >= 0, "The current transparent framebuffer must be captured without replacing the previous reference.");
        Assert.True(identityCheck > currentFramebuffer, "The OIT rebuild must compare the previous reference with the current framebuffer.");
        Assert.True(methodEnd > currentFramebuffer, "The OIT render method must have a recognizable boundary.");
        string renderMethod = source.Substring(currentFramebuffer, methodEnd - currentFramebuffer);
        Assert.DoesNotContain("transparentfb = capi.Render.FrameBuffers[1]", renderMethod);
    }

    [Fact]
    public void OitRevealAttachesToColorAttachment0_NotOverwritingVanilla()
    {
        // Phase 1A step 5: SystemRenderOITLayers calls the platform; the GL lines are the
        // ClientPlatformWindows override bodies.
        Assert.Contains("CreateOitTargets(transparentfb, layers, out revealTextureId, out accumTextureId);", Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs"));
        string source = OitPlatformBodies();

        // OIT reveal texture attaches to ColorAttachment0 (36064). This is by design:
        // the oit.fsh shader writes to layout(location = 0) which IS ColorAttachment0.
        // The vanilla accumulation texture ID in ColorTextureIds[0] becomes orphaned
        // from the FBO, but MergeTransparentRenderPass reads it by texture ID from
        // the shader uniform (not from attachment), so it reads the Optimum reveal data.
        Assert.Contains("(FramebufferAttachment)36064", source);
    }

    [Fact]
    public void OitAccumulationLayersAttachToSlots3Through5()
    {
        // Phase 1A step 5: SystemRenderOITLayers calls the platform; the GL lines are the
        // ClientPlatformWindows override bodies.
        Assert.Contains("CreateOitTargets(transparentfb, layers, out revealTextureId, out accumTextureId);", Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs"));
        string source = OitPlatformBodies();

        // Accumulation layers attach to ColorAttachment3-5 (36067, 36068, 36069)
        // matching oit.fsh layout(location = 3/4/5).
        Assert.Contains("(FramebufferAttachment)36067", source);
        Assert.Contains("(FramebufferAttachment)36068", source);
        Assert.Contains("(FramebufferAttachment)36069", source);
    }

    [Fact]
    public void OitDrawBuffersMatchesShaderOutputLocations()
    {
        // Phase 1A step 5: SystemRenderOITLayers calls the platform; the GL lines are the
        // ClientPlatformWindows override bodies.
        Assert.Contains("BeginOitAccumulation(currentTransparentfb);", Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs"));
        string source = OitPlatformBodies();

        // DrawBuffers must declare 6 attachments (0-5) matching oit.fsh outputs.
        // Using 6, not 7: attachment 6 would be unused by shaders.
        Assert.Contains("DrawBuffersEnum[6]", source);
        Assert.Contains("DrawBuffersEnum.ColorAttachment0", source);
        Assert.Contains("DrawBuffersEnum.ColorAttachment5", source);
        Assert.DoesNotContain("DrawBuffersEnum.ColorAttachment6", source);
    }

    [Fact]
    public void OitDisablesFlagPreventsFurtherRenderCalls()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs");

        // First line of OnRenderFrame must bail if OIT is disabled.
        Assert.Contains("if (SystemRenderOITLayers.optimumOitDisabled) return;", source);
    }

    [Fact]
    public void OitBlendFuncPreservesVanillaAttachments0And1()
    {
        // Phase 1A step 5: SystemRenderOITLayers calls the platform; the GL lines are the
        // ClientPlatformWindows override bodies.
        Assert.Contains("BeginOitAccumulation(currentTransparentfb);", Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs"));
        string source = OitPlatformBodies();

        // Attachments 0 and 1 use DST_COLOR * ZERO blend (774 = GL_DST_COLOR, 0 = GL_ZERO).
        // This multiplies existing content by incoming fragment, preserving reveal semantics.
        Assert.Contains("GL.BlendFunc(0, (BlendingFactorSrc)774, (BlendingFactorDest)0)", source);
        Assert.Contains("GL.BlendFunc(1, (BlendingFactorSrc)774, (BlendingFactorDest)0)", source);
    }

    [Fact]
    public void OitAccumulationBlendFuncUsesAdditiveBlend()
    {
        // Phase 1A step 5: SystemRenderOITLayers calls the platform; the GL lines are the
        // ClientPlatformWindows override bodies.
        Assert.Contains("BeginOitAccumulation(currentTransparentfb);", Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs"));
        string source = OitPlatformBodies();

        // Attachments 3-5 use ONE + ONE additive blend (1 = GL_ONE).
        Assert.Contains("GL.BlendFunc(3, (BlendingFactorSrc)1, (BlendingFactorDest)1)", source);
        Assert.Contains("GL.BlendFunc(4, (BlendingFactorSrc)1, (BlendingFactorDest)1)", source);
        Assert.Contains("GL.BlendFunc(5, (BlendingFactorSrc)1, (BlendingFactorDest)1)", source);
    }

    private static string OitPlatformBodies()
    {
        string windows = VulkanPlatformSource.ReadClientPlatformWindows();
        int start = windows.IndexOf("public override void CreateOitTargets(", StringComparison.Ordinal);
        int end = windows.IndexOf("public override int GenOcclusionQuery()", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the GL OIT overrides are missing from ClientPlatformWindows");
        return windows.Substring(start, end - start);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
}

// Source: Optimum.Tests/ui-separation-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// World/UI separation (VulkanClientPlatform.UiSeparation.cs): where each piece sits. The GPU
/// behaviour is UiSeparationTests; these pin the placements a refactor could move across the call
/// they belong to - a compose after the Done stage records the HUD-less image in every screenshot,
/// a bind before the blit's last route leaves the GUI on the window, a scope nobody closes blends
/// the next world pass under the UI factors.
/// </summary>
public class UiSeparationCoverageTests
{
    private const string PlatformPath = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.UiSeparation.cs";
    private const string GraphPath = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Graph.cs";

    [Fact]
    public void ClientMainComposesAfterTheOrthoStageAndBeforeDone()
    {
        string body = StripComments(Body(ReadLib("Vintagestory.Client.NoObf/ClientMain.cs"),
            "public void RenderToDefaultFramebuffer(float dt)"));
        int ortho = body.IndexOf("rendOrthoDone", StringComparison.Ordinal);
        int compose = body.IndexOf("Platform.OptimumComposeUiTarget();", StringComparison.Ordinal);
        int done = body.IndexOf("TriggerRenderStage(EnumRenderStage.Done, dt);", StringComparison.Ordinal);
        Assert.True(ortho >= 0 && compose > ortho && done > compose,
            "the compose must sit between the Ortho stage and the Done stage:\n" + body);
        Assert.Single(Regex.Matches(body, @"OptimumComposeUiTarget\(\)"));

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientMain\", \"RenderToDefaultFramebuffer\", 1),", patcher);
    }

    [Fact]
    public void TheMenuScreensComposeInScreenManager()
    {
        string body = StripComments(Body(ReadLib("Vintagestory.Client/ScreenManager.cs"), "internal void Render(float dt)"));
        int blit = body.IndexOf("Platform.BlitPrimaryToDefault();", StringComparison.Ordinal);
        int screen = body.IndexOf("CurrentScreen.RenderToDefaultFramebuffer(dt);", StringComparison.Ordinal);
        int compose = body.IndexOf("Platform.OptimumComposeUiTarget();", StringComparison.Ordinal);
        Assert.True(blit >= 0 && screen > blit && compose > screen,
            "the menu compose must follow the screen's own drawing:\n" + body);
        Assert.Contains("new(\"Vintagestory.Client.ScreenManager\", \"Render\", 1),", Read("Optimum.Patcher/Program.cs"));
    }

    [Fact]
    public void TheComposeIsANeutralVirtualThatOnlyVulkanOverrides()
    {
        string platform = ReadLib("Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");
        Assert.Equal("{ }", Regex.Replace(Body(platform, "public virtual void OptimumComposeUiTarget()"), @"\s+", " ").Trim());
        Assert.DoesNotContain("OptimumComposeUiTarget", ReadLib("Vintagestory.Client.NoObf/ClientPlatformWindows.cs"));

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"OptimumComposeUiTarget\",", patcher);
        Assert.Contains("new(true, \"OptimumComposeUiTarget\", Array.Empty<string>()),",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs"));
        Assert.Contains("public override void OptimumComposeUiTarget()", Read(PlatformPath));
    }

    [Fact]
    public void TheComposeProgramIsRegisteredAndShipsBothTwins()
    {
        Assert.Contains("public static ShaderProgram UiCompose;", ReadLib("Vintagestory.Client.NoObf/ShaderPrograms.cs"));
        string registry = ReadLib("Vintagestory.Client.NoObf/ShaderRegistry.cs");
        Assert.Contains("RegisterOptimumShaderProgram(\"ui-compose\", ShaderPrograms.UiCompose = new ShaderProgram());", registry);
        Assert.Contains("shaderProgram == ShaderPrograms.UiCompose)", registry);
        Assert.Contains("\"UiCompose\",", Read("Optimum.Patcher/Program.cs"));

        // A pass-through: blit.fsh's forced alpha of 1 would cover the world with the UI image.
        foreach (string file in new[] { "sources/shaders/ui-compose.fsh", "sources/shaders-vk/ui-compose.frag" })
        {
            string fragment = StripComments(Read(file));
            Assert.DoesNotContain(".a = 1", fragment);
            Assert.Matches(new Regex(@"outColor = texture\((optimumTextures2D\[uiTex\]|uiTex), texCoord\);"), fragment);
        }
        Assert.True(File.Exists(PatchReader.FindRepositoryFile("sources/shaders/ui-compose.vsh")));
        Assert.True(File.Exists(PatchReader.FindRepositoryFile("sources/shaders-vk/ui-compose.vert")));
        Assert.True(File.Exists(PatchReader.FindRepositoryFile("sources/shaders-vk/ui-compose.interface.glsl")));
    }

    [Fact]
    public void TheScopeOpensAtTheBlitsEndAndTheSnapshotAtTheCompositionsEnd()
    {
        string graph = Read(GraphPath);

        string blit = Body(graph, "    public override void BlitPrimaryToDefault()");
        int native = blit.IndexOf("RenderNativeBlit();", StringComparison.Ordinal);
        int glRoute = blit.IndexOf("base.BlitPrimaryToDefault();", StringComparison.Ordinal);
        int open = blit.IndexOf("OpenUiScope();", StringComparison.Ordinal);
        Assert.True(native >= 0 && glRoute > native && open > glRoute, "the scope must open after both routes:\n" + blit);
        Assert.DoesNotContain("return;", blit);

        string composition = Body(graph, "    public override void RenderFinalComposition()");
        int legacy = composition.IndexOf("LegacyFinalComposition();", StringComparison.Ordinal);
        int capture = composition.IndexOf("CaptureSceneNoHud();", StringComparison.Ordinal);
        Assert.True(legacy >= 0 && capture > legacy, "the snapshot must follow both routes:\n" + composition);
        Assert.DoesNotContain("return;", composition);
    }

    [Fact]
    public void EveryScopeIsClosedBeforeTheWorldDrawsAgain()
    {
        string frame = Body(Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Frame.cs"),
            "    public override void BeginFrame()");
        int close = frame.IndexOf("CloseUiScope();", StringComparison.Ordinal);
        int begin = frame.IndexOf("device.BeginFrame();", StringComparison.Ordinal);
        Assert.True(close >= 0 && begin > close, "BeginFrame must close the scope first:\n" + frame);

        string framebuffers = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs");
        Assert.Contains("CloseUiScope();", Body(framebuffers, "    public override void DisposeFrameBuffers("));
        Assert.Contains("AllocateUiSeparationTargets(list, width, height);",
            Body(framebuffers, "    public override List<FrameBufferRef> SetupDefaultFrameBuffers()"));

        // The compose closes the scope before anything can return or draw.
        string compose = Body(Read(PlatformPath), "    public override void OptimumComposeUiTarget()");
        int closed = compose.IndexOf("CloseUiScope();", StringComparison.Ordinal);
        int firstReturn = compose.IndexOf("return;", compose.IndexOf("UiScopeOpen", StringComparison.Ordinal) + 20,
            StringComparison.Ordinal);
        int draw = compose.IndexOf("DrawNativeFullscreen", StringComparison.Ordinal);
        Assert.True(closed >= 0 && firstReturn > closed && draw > closed, compose);
    }

    [Fact]
    public void DefaultResolvesToTheUiImageOnlyThroughTheDevicesOneResolver()
    {
        string native = Read("Optimum.Render.Vulkan/VulkanDevice.Native.cs");
        // Expression-bodied, so read up to the member's semicolon.
        int start = native.IndexOf("private int ResolveNativeFramebuffer(int framebufferId) =>", StringComparison.Ordinal);
        Assert.True(start >= 0, "the resolver is gone");
        string resolver = native.Substring(start, native.IndexOf(';', start) - start);
        Assert.Contains("_defaultRedirect > 0 ? _defaultRedirect : _defaultFramebuffer", resolver);
        Assert.Single(Regex.Matches(native, @"_defaultRedirect = "));

        string stated = Read("Optimum.Render.Vulkan/Platform/StatedRenderState.cs");
        Assert.Contains("if (IsUiImage(framebufferId)) blend = blend.ForUiImage();", stated);
        Assert.Contains("stated.IsUiImage(framebufferId)", Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeGui.cs"));
        Assert.Contains("stated.IsUiImage(", Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeWorld.cs"));
    }

    [Fact]
    public void TheParityDumpNamesBothSlots()
    {
        string windows = ReadLib("Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains("private const int OptimumSceneNoHudIndex = 23;", windows);
        Assert.Contains("private const int OptimumUiTargetIndex = 24;", windows);
        Assert.Contains("return \"OptimumSceneNoHud\";", windows);
        Assert.Contains("return \"OptimumUiTarget\";", windows);
        string platform = Read(PlatformPath);
        Assert.Contains("internal const int OptimumSceneNoHudIndex = 23;", platform);
        Assert.Contains("internal const int OptimumUiTargetIndex = 24;", platform);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

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

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*", string.Empty);

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
}
}
