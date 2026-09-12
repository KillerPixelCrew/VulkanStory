using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS-FG design, step 3: the UI target - the guide's <c>pUI</c>.
///
/// Everything the frame draws after <c>BlitPrimaryToDefault</c> - the AfterBlit stage, the
/// main-menu background and the whole Ortho stage - goes into a target of its own instead
/// of onto the display image, and is composed back one fullscreen premultiplied-alpha pass
/// later. Two things about that are positions in the frame rather than pixels, and a
/// position is what a later change silently moves:
/// <list type="number">
/// <item>The bind is the last thing the blit does, on every path out of it, because that
/// call is the boundary between the world and the UI.</item>
/// <item>The compose sits in <c>ClientMain.RenderToDefaultFramebuffer</c> after the Ortho
/// stage and before <c>TriggerRenderStage(Done)</c> - see the test for why that is the only
/// position that works.</item>
/// </list>
/// And one thing about it is a cost: with the gate off nothing is allocated, nothing is
/// bound and nothing is composed, which is asserted here as control flow rather than
/// claimed in a comment.
///
/// What the compose really does to the pixels is <c>UiTargetComposeTests</c>, on the GPU,
/// through the platform.
/// </summary>
public class UiTargetCoverageTests
{
    private const string PlatformSource =
        "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    /// <summary>
    /// The compose runs between the Ortho stage and the Done stage, and outside the
    /// <c>ShouldRender2DOverlays</c> block.
    ///
    /// <para>Why that position, and why it is pinned.</para> The with-HUD screenshot
    /// (<c>SystemScreenshot</c>, registered at stage Done) and the AVI writer
    /// (<c>SystemVideoRecorder</c>, also at Done) read the default framebuffer, and the
    /// parity dump and the headless capture read it once <c>OnNewFrame</c> returns. A
    /// compose that happens after any of them - later in this method, or back in
    /// <c>ScreenManager.Render</c> - leaves the HUD-less image in the default framebuffer
    /// at the moment they read, so every one of those capture paths silently records a
    /// frame with no GUI in it. Outside the overlays block because a frame that draws no 2D
    /// overlays still has whatever the AfterBlit stage put in the target.
    /// </summary>
    [Fact]
    public void TheComposeSitsBetweenTheOrthoStageAndTheDoneStage()
    {
        string clientMain = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");
        string body = MethodBody(clientMain, "public void RenderToDefaultFramebuffer(float dt)");

        int ortho = body.IndexOf("TriggerRenderStage(EnumRenderStage.Ortho, dt);", StringComparison.Ordinal);
        int compose = body.IndexOf("Platform.OptimumComposeUiTarget();", StringComparison.Ordinal);
        int done = body.IndexOf("TriggerRenderStage(EnumRenderStage.Done, dt);", StringComparison.Ordinal);
        Assert.True(ortho >= 0, "the Ortho stage is no longer triggered here");
        Assert.True(compose > ortho, "the compose must come after the Ortho stage, or the GUI is not in the target yet");
        Assert.True(done > compose,
            "the compose must come before the Done stage: the with-HUD screenshot and the AVI writer " +
            "are registered there and would record the HUD-less image as the final frame");

        // Outside the 2D-overlay block: the closing brace of that block comes before the
        // compose, so a frame with no overlays still composes.
        int overlayBlock = body.IndexOf("if (ShouldRender2DOverlays)", StringComparison.Ordinal);
        int overlayStop = body.IndexOf("guiShaderProg.Stop();", StringComparison.Ordinal);
        Assert.True(overlayBlock >= 0 && overlayStop > overlayBlock && compose > overlayStop,
            "the compose must sit outside the ShouldRender2DOverlays block");

        // And the capture paths this position exists for are still where they were.
        string screenshot = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemScreenshot.cs");
        Assert.Contains("EnumRenderStage.Done", screenshot);
        string recorder = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemVideoRecorder.cs");
        Assert.Contains("EnumRenderStage.Done", recorder);
    }

    /// <summary>
    /// The bind is the last thing the blit does, on every path out of it - the TAA debug
    /// early return, the FSR success return and the normal tail, which the FSR exception
    /// fallback also falls through to. A path that forgets it draws that frame's GUI onto
    /// the display image while the compose still runs, which double-composites the UI.
    /// </summary>
    [Fact]
    public void EveryPathOutOfTheBlitRedirectsTheUi()
    {
        string platform = Platform();
        string body = MethodBody(platform, "public override void BlitPrimaryToDefault()");

        int binds = 0;
        int at = body.IndexOf("OptimumBindUiTarget();", StringComparison.Ordinal);
        while (at >= 0)
        {
            binds++;
            at = body.IndexOf("OptimumBindUiTarget();", at + 1, StringComparison.Ordinal);
        }
        Assert.Equal(3, binds);

        // The tail one is outside the OffscreenBuffer branch: a client with no offscreen
        // buffer blits nothing, but its GUI is still the GUI.
        int lastBind = body.LastIndexOf("OptimumBindUiTarget();", StringComparison.Ordinal);
        int branchEnd = body.LastIndexOf("blit.Stop();", StringComparison.Ordinal);
        Assert.True(branchEnd >= 0 && lastBind > branchEnd);
        string tail = body.Substring(lastBind + "OptimumBindUiTarget();".Length);
        Assert.DoesNotContain("RenderFullscreenTriangle", tail);

        // And the boundary it is the last statement of is still where ScreenManager puts
        // the UI: the blit, then AfterBlit, the menu background and the Ortho stage.
        string screenManager = Read("build/VintagestoryLib/Vintagestory.Client/ScreenManager.cs");
        int blit = screenManager.IndexOf("Platform.BlitPrimaryToDefault();", StringComparison.Ordinal);
        int afterBlit = screenManager.IndexOf("CurrentScreen.RenderAfterBlit(dt);", StringComparison.Ordinal);
        int ortho = screenManager.IndexOf("CurrentScreen.RenderToDefaultFramebuffer(dt);", StringComparison.Ordinal);
        Assert.True(blit >= 0 && afterBlit > blit && ortho > afterBlit,
            "the UI no longer follows BlitPrimaryToDefault; the bind's position must be re-derived");
    }

    /// <summary>
    /// Off is vanilla, as control flow: one gate of its own - not the upscaler's - read by
    /// both framebuffer setups, and with it false nothing is allocated, the published index
    /// stays -1, and the bind and the compose reach nothing. The bind's first three
    /// statements are that proof: the flag is cleared, and an unpublished index returns
    /// before any bind or clear is issued.
    /// </summary>
    [Fact]
    public void NothingHappensWhenNothingWantsTheUiTarget()
    {
        string platform = Platform();

        string gate = MethodBody(platform, "public bool OptimumUiTargetRequested");
        Assert.Contains("Vintagestory.API.Config.OptimumUiTarget.Enabled", gate);
        // Its own flag, deliberately not the upscaler's: an upscaler wants the HUD-less
        // scene, frame generation wants the UI separated as well.
        Assert.DoesNotContain("UpscalerReplacesTaa", gate);

        // Both allocations are behind that one question, and both setups start unpublished.
        Assert.Contains("if (OptimumUiTargetRequested)", platform);
        Assert.Contains("SetOptimumUiTargetIndex(-1);", platform);
        string vulkan = VulkanPlatformSource.Read();
        Assert.Contains("if (OptimumUiTargetRequested)", vulkan);
        Assert.Contains("SetOptimumUiTargetIndex(-1);", vulkan);

        Assert.Contains("private int optimumUiTargetIndex = -1;", platform);

        // The bind pays nothing with no slot published: no LoadFrameBuffer, no clear.
        string bind = MethodBody(platform, "public void OptimumBindUiTarget()");
        Assert.Contains("optimumUiTargetBound = false;", bind);
        Assert.Contains("if (optimumUiTargetIndex < 0 || frameBuffers == null) return;", bind);
        int firstReturn = bind.IndexOf("return;", StringComparison.Ordinal);
        int firstClear = bind.IndexOf("ClearFrameBuffer(", StringComparison.Ordinal);
        Assert.True(firstClear > firstReturn, "the gate must come before the clear");

        // And the compose is the same shape: the flag is the first question it asks, so a
        // frame that never bound the target issues no bind, no clear and no draw.
        string compose = MethodBody(platform, "public override void OptimumComposeUiTarget()");
        Assert.StartsWith("{\n\t\tif (!optimumUiTargetBound) return;", compose.Replace("\r\n", "\n"));
        int flagReturn = compose.IndexOf("return;", StringComparison.Ordinal);
        int load = compose.IndexOf("LoadFrameBuffer(EnumFrameBuffer.Default);", StringComparison.Ordinal);
        Assert.True(load > flagReturn, "the compose must ask the flag before it binds anything");
    }

    /// <summary>
    /// The compose is one fullscreen premultiplied-alpha pass, and the target it reads is
    /// RGBA8 with depth and is not a transient.
    ///
    /// The depth is not decoration: ScreenManager clears the GUI's depth to 20000 right
    /// after the blit and the GUI depth-sorts itself over that range, so a colour-only
    /// target would pass every depth test and draw dialogs in submission order. Not a
    /// transient because the transient pool may hand a slot's image to another slot once
    /// aliasing is on, and with frame generation the vendor reads this image after the
    /// frame that produced it has ended.
    /// </summary>
    [Fact]
    public void TheComposeIsOnePremultipliedPassOverATargetThatIsNotTransient()
    {
        string platform = Platform();
        string compose = MethodBody(platform, "public override void OptimumComposeUiTarget()");
        Assert.Contains("GlToggleBlend(on: true, EnumBlendMode.PremultipliedAlpha);", compose);
        Assert.Contains("RenderFullscreenTriangle(screenQuad);", compose);
        // And it hands the state back: Standard blending and the depth test, which is what
        // RenderToDefaultFramebuffer had on the way in and what the Done stage expects.
        Assert.Contains("GlToggleBlend(on: true);", compose);
        Assert.Contains("GlEnableDepthTest();", compose);

        // Transparent black on the way in: a cleared alpha of 1 would make the compose
        // cover the world everywhere the GUI drew nothing.
        Assert.Contains("private float[] optimumUiTargetClearColor = new float[4] { 0f, 0f, 0f, 0f };", platform);

        // The GL target: colour plus a depth attachment, at the native window size.
        Assert.Contains("setupAttachment(optimumUiTarget, uiNativeWidth, uiNativeHeight, 0, val, (PixelInternalFormat)32856);", platform);
        Assert.Contains("optimumUiTarget.DepthTextureId = GL.GenTexture();", platform);

        string vulkan = VulkanPlatformSource.Read();
        string target = MethodBody(vulkan, "private FrameBufferRef CreateOptimumUiTarget(int width, int height)");
        Assert.Contains("EnumTextureInternalFormat.Rgba8", target);
        Assert.Contains("EnumFramebufferAttachment.DepthAttachment", target);
        Assert.DoesNotContain("CreateTransientTexture2D", target);

        // The shader is its own program, because blit.fsh forces alpha to 1 - under
        // premultiplied blending that would cover the whole world with the UI image.
        string fragment = Read("sources/shaders/ui-compose.fsh");
        Assert.Contains("outColor = texture(uiTex, texCoord);", fragment);
        // No statement touches the alpha channel (the comment above the shader names
        // blit.fsh's "outColor.a = 1", which is exactly what this must not do).
        Assert.DoesNotContain("\toutColor.a", fragment);
    }

    /// <summary>
    /// No frame may end with the UI target still bound, on any screen.
    ///
    /// <para>What integration found, 2026-09-12.</para> The compose was placed in
    /// <c>ClientMain.RenderToDefaultFramebuffer</c>, which is the right position for the
    /// capture paths - but that body is reached only through
    /// <c>GuiScreenRunningGame.RenderToDefaultFramebuffer</c>, and only when
    /// <c>runningGame.exitToMainMenu</c> is false. Every menu screen (the main menu, login,
    /// loading, connecting, disconnected, public servers, download mods) runs the base
    /// <c>GuiScreen.RenderToDefaultFramebuffer</c>, which has no Done stage and never
    /// composes. The blit binds the UI target on those frames all the same, so the whole
    /// menu would draw into it and never come back - a black screen - and the scope flag
    /// would still be armed when the next frame drew the world, which is the one state
    /// that makes every Standard blend in the engine use the separate-alpha factors.
    ///
    /// So <c>ScreenManager.Render</c> calls the compose once more after
    /// <c>CurrentScreen.RenderToDefaultFramebuffer</c>. It is idempotent - the compose
    /// returns immediately once the scope is closed - so the in-game frame is unchanged and
    /// still composes at the position the capture paths need.
    /// </summary>
    [Fact]
    public void NoFrameEndsWithTheUiTargetStillBound()
    {
        string screenManager = Read("build/VintagestoryLib/Vintagestory.Client/ScreenManager.cs");
        string body = MethodBody(screenManager, "internal void Render(float dt)");

        int screenDraw = body.IndexOf("CurrentScreen.RenderToDefaultFramebuffer(dt);", StringComparison.Ordinal);
        int compose = body.IndexOf("Platform.OptimumComposeUiTarget();", StringComparison.Ordinal);
        Assert.True(screenDraw > 0, "ScreenManager.Render no longer draws the screen");
        Assert.True(compose > screenDraw,
            "the safety compose must run after the screen has drawn, or a menu frame is never composed");

        // And it is genuinely the last thing in the frame that touches the target: nothing
        // between it and the end of the method binds a framebuffer again.
        string tail = body.Substring(compose);
        Assert.DoesNotContain("LoadFrameBuffer", tail);

        // The first call site is still where the capture paths need it.
        string clientMain = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");
        Assert.Contains("Platform.OptimumComposeUiTarget();",
            MethodBody(clientMain, "public void RenderToDefaultFramebuffer(float dt)"));

        // The compose's own first statement is the one that makes the second call free.
        string composeBody = MethodBody(Platform(), "public override void OptimumComposeUiTarget()");
        Assert.Contains("if (!optimumUiTargetBound) return;", composeBody);
    }

    /// <summary>
    /// The scope is GL state as well as a flag, and the compose has three give-up returns.
    ///
    /// Clearing <c>optimumUiTargetBound</c> only changes what the NEXT GlToggleBlend picks;
    /// the factors themselves are still the ones ScreenManager set two statements after the
    /// bind. A compose that returns before its tail restore - no target, no colour texture, no
    /// linked program - would leave the separate-alpha factors armed for the Done stage and
    /// for the next frame up to its first GlToggleBlend. So the restore has to come before the
    /// first of those returns, which is an ordering claim, not a "the call is in there" one.
    /// </summary>
    [Fact]
    public void TheComposeRestoresVanillaBlendBeforeEveryGiveUpReturn()
    {
        string compose = WithoutComments(MethodBody(Platform(), "public override void OptimumComposeUiTarget()"));

        int scopeClosed = compose.IndexOf("optimumUiTargetBound = false;", StringComparison.Ordinal);
        Assert.True(scopeClosed >= 0, "the compose no longer closes the scope");
        int restore = compose.IndexOf("GlToggleBlend(on: true);", scopeClosed, StringComparison.Ordinal);
        Assert.True(restore > scopeClosed, "the compose never restores vanilla Standard");

        // Every return after the scope is closed - all three give-up guards and any later one.
        int searched = scopeClosed;
        int returns = 0;
        while (true)
        {
            int next = compose.IndexOf("return;", searched, StringComparison.Ordinal);
            if (next < 0) break;
            returns++;
            Assert.True(restore < next,
                "a give-up return leaves the scoped alpha factors armed for the rest of the frame");
            searched = next + 1;
        }
        Assert.Equal(3, returns);
    }

    /// <summary>
    /// Nothing re-arms the scope between the compose and the next frame's blit, and nothing
    /// guarantees the compose is reached: eventManager.TriggerRenderStage catches nothing, so
    /// a GUI renderer that throws at AfterBlit or Ortho unwinds past both compose call sites
    /// and the whole of the next frame's world pass blends on the UI factors. The frame's
    /// first clear heals it, beside the motion-window reset that is there for the same reason.
    /// The number this is really made of is in
    /// Optimum.Render.Vulkan.Tests/UiTargetComposeTests.TheScopeIsHealedByTheNextFrameWhenNothingEverComposed.
    /// </summary>
    [Fact]
    public void TheFrameStartsWithTheScopeClosed()
    {
        string clear = WithoutComments(MethodBody(Platform(),
            "public override void ClearFrameBuffer(EnumFrameBuffer framebuffer)"));
        int primary = clear.IndexOf("case EnumFrameBuffer.Primary:", StringComparison.Ordinal);
        Assert.True(primary >= 0, "the Primary clear case is gone");
        int heal = clear.IndexOf("optimumUiTargetBound = false;", primary, StringComparison.Ordinal);
        int pass = clear.IndexOf("ClearFrameBufferPass(framebuffer);", primary, StringComparison.Ordinal);
        Assert.True(heal > primary, "the frame's first clear does not close the UI scope");
        Assert.True(heal < pass, "the heal must happen before the clear itself, not after it");

        // Both backends run this body, and it only ships if the patcher carries it.
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"ClearFrameBuffer\", 1",
            Read("Optimum.Patcher/Program.cs"));
    }

    /// <summary>
    /// The compose is its own Vulkan pass, declaring the UI target as a read - and declares
    /// nothing at all when there is no UI target, because a SetPassContext pair with a new
    /// name renames the pass and forces a split on every frame of a client that never asked
    /// for one. The read itself is measured in
    /// Optimum.Render.Vulkan.Tests/UiTargetComposeTests.TheComposePassDeclaresTheUiTargetAsItsRead.
    /// </summary>
    [Fact]
    public void TheVulkanComposeIsItsOwnPassAndCostsNothingWhenOff()
    {
        string graph = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Graph.cs");
        string wrapper = MethodBody(graph, "public override void OptimumComposeUiTarget()");
        int guard = wrapper.IndexOf("if (!OptimumUiTargetBound)", StringComparison.Ordinal);
        int context = wrapper.IndexOf("SetPassContext(\"UiCompose\"", StringComparison.Ordinal);
        Assert.True(guard >= 0, "the off path declares a pass it does not need");
        Assert.True(context > guard, "the guard must come before any declaration");
        // The context it interrupted is restored, not assumed to be "Frame": the compose is
        // called from ClientMain and, one statement later, from ScreenManager on a menu screen.
        Assert.Contains("SetPassContext(outer, outerFlags);", wrapper);
        Assert.Contains("case \"UiCompose\":", graph);
    }

    /// <summary>
    /// Every new or changed lib member is a Cecil target; a member missing from the patcher
    /// compiles here and is absent from the shipped DLL.
    /// </summary>
    [Fact]
    public void EveryNewMemberIsListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        foreach (string member in new[]
        {
            "OptimumUiTargetIndex", "OptimumUiTargetDepthClear", "UiTargetFrameBufferIndex",
            "optimumUiTargetIndex", "OptimumUiTargetRequested", "OptimumUiTargetFrameBuffer",
            "OptimumUiTargetBound", "optimumUiTargetBound", "optimumUiTargetClearColor",
            "SetOptimumUiTargetIndex", "OptimumBindUiTarget", "OptimumComposeUiTarget",
            "UiCompose",
        })
        {
            Assert.Contains("\"" + member + "\"", patcher);
        }
        // The three bodies that gained a call, and the blit that gained the bind.
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"RenderToDefaultFramebuffer\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"BlitPrimaryToDefault\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.ScreenManager\", \"Render\", 1", patcher);

        // And the members live in the second registry too, or the transplant drops them.
        string regions = Read("Optimum.Tests/client-platform-windows-vanilla-regions-tests.cs");
        foreach (string member in new[]
        {
            "OptimumUiTargetIndex", "UiTargetFrameBufferIndex", "OptimumUiTargetRequested",
            "OptimumBindUiTarget", "OptimumComposeUiTarget",
        })
        {
            Assert.Contains("\"" + member + "\"", regions);
        }
    }

    private static string Platform() => Read(PlatformSource);

    /// <summary>
    /// Ordering assertions read statements, and these bodies carry long "why" comments that
    /// quote the very calls being located - the compose's restore is explained by a paragraph
    /// that names GlToggleBlend(on: true) several lines above the statement itself.
    /// </summary>
    private static string WithoutComments(string source)
    {
        var kept = new System.Text.StringBuilder();
        foreach (string line in source.Split('\n'))
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            kept.Append(line).Append('\n');
        }
        return kept.ToString();
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "no such member: " + signature);
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
        throw new InvalidOperationException("unterminated member: " + signature);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
