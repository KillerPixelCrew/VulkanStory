using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// DLSS-FG design, step 3: the UI target - the guide's <c>pUI</c>, and the compose that
/// puts it back.
///
/// The claim is about three images at two moments in one frame, so it is tested at those
/// moments through the real platform: the display image is painted, the real
/// <c>OptimumBindUiTarget</c> redirects everything that follows, a UI-shaped draw lands in
/// the target, and the real <c>OptimumComposeUiTarget</c> puts it back.
/// <list type="number">
/// <item>Before the compose the display image holds the scene and no overlay pixel at all -
/// which is what "the presented scene is genuinely HUD-less" means.</item>
/// <item>The UI target holds exactly the overlay: the drawn half carries it, the rest is
/// transparent black, because the bind clears it to (0,0,0,0) and not to opaque black.</item>
/// <item>After the compose the display image is the over-operator's result,
/// ui.rgb + scene * (1 - ui.a), within 1/255 - the arithmetic of the premultiplied blend,
/// not an eyeball comparison.</item>
/// </list>
///
/// A real window, because the compose writes the default framebuffer and a headless device
/// has a 1x1 one; the window is hidden and created with no graphics API, exactly as
/// <c>SwapchainTests</c> does. Several frames run before the judged one, with
/// <c>EndFrame</c> (Present) between them and no readback in the loop, so a target that is
/// really the previous frame's image fails here rather than passing. Validation runs with
/// sync and best practices on, as every GPU test does.
/// </summary>
public class UiTargetComposeTests
{
    private const int Size = 64;
    private const int UiTargetSlot = 24;

    // The scene under the UI, and the UI itself: premultiplied colour with half coverage,
    // which is what the target holds once the GUI has drawn into it. Distinguishable in
    // every channel, so a compose that read the wrong image cannot pass by coincidence.
    private static readonly byte[] Scene = { 40, 90, 200, 255 };
    private static readonly byte[] Ui = { 100, 50, 25, 128 };
    private static readonly byte[] Transparent = { 0, 0, 0, 0 };

    private readonly ITestOutputHelper _output;

    public UiTargetComposeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The platform, with the one thing a test cannot have substituted: the window size.
    /// <c>DisplayWidth</c>/<c>DisplayHeight</c> are the only members of the compose path
    /// that ask the OpenTK window object, which no test owns; everything else - the bind,
    /// the clear, the blend state, the pass and the readback - is the shipped body.
    /// </summary>
    private sealed class WindowSizedPlatform : VulkanClientPlatform
    {
        public WindowSizedPlatform() : base(null!) { }

        public override int DisplayWidth => Size;

        public override int DisplayHeight => Size;
    }

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// What the GUI leaves in the target: premultiplied colour and real coverage. The
    /// accumulation that produces it from many straight-alpha draws is the scoped blend
    /// state's subject; what this test needs is a target holding pUI.
    /// </summary>
    private const string UiFragment = """
        #version 330 core
        out vec4 outColor;
        void main(void) { outColor = vec4(100.0 / 255.0, 50.0 / 255.0, 25.0 / 255.0, 128.0 / 255.0); }
        """;

    // The shipped sources/shaders/ui-compose.{vsh,fsh}, inlined: this test links its own
    // program because there is no shader registry here. The shipped pair's content is
    // pinned by Optimum.Tests/ui-target-coverage-tests.cs.
    private const string ComposeVertex = """
        #version 330 core
        out vec2 texCoord;
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0, 1);
            texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
        }
        """;

    private const string ComposeFragment = """
        #version 330 core
        uniform sampler2D uiTex;
        in vec2 texCoord;
        out vec4 outColor;
        void main(void) { outColor = texture(uiTex, texCoord); }
        """;

    [SkippableFact]
    public unsafe void TheUiLandsInItsOwnTargetAndComesBackOverTheScene()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Size, Size, out Window* window),
            "No usable window system.");

        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-ui-target-test-" + Guid.NewGuid().ToString("N"));
        var platform = new WindowSizedPlatform
        {
            DeviceFactory = GpuTest.NewDevice,
            CrashMarkerDataPath = dataPath,
        };
        ShaderProgram? previousCompose = ShaderPrograms.UiCompose;
        try
        {
            bool installed = platform.InitializeGraphics((IntPtr)window, Size, Size, out string reason);
            if (!installed) _output.WriteLine("Vulkan unavailable: " + reason);
            Skip.IfNot(installed, "No usable Vulkan device.");
            VulkanDevice seam = platform.GraphicsDevice!;

            FrameBufferRef uiTarget = platform.CreateFramebuffer(ColorTarget("ui-target"));
            InstallFrameBuffers(platform, uiTarget);

            // Published the way the HUD-less snapshot's slot is: the index is the contract.
            platform.SetOptimumUiTargetIndex(UiTargetSlot);
            Assert.Equal(UiTargetSlot, platform.UiTargetFrameBufferIndex);
            Assert.Same(uiTarget, platform.OptimumUiTargetFrameBuffer);

            int uiProgram = VulkanDeviceIntegrationTests.LinkProgram(
                seam, FullscreenVertex, UiFragment, "ui-target-overlay");
            int composeProgram = VulkanDeviceIntegrationTests.LinkProgram(
                seam, ComposeVertex, ComposeFragment, "ui-compose");
            ShaderPrograms.UiCompose = new ShaderProgram { ProgramId = composeProgram };

            // Frames that are not judged, so a target that lags by a frame, or a compose
            // ordered against the wrong frame's work, has somewhere to show it.
            for (int frame = 0; frame < 3; frame++)
            {
                platform.BeginFrame();
                PaintDisplay(platform, Scene);
                platform.OptimumBindUiTarget();
                DrawUi(platform, uiProgram);
                platform.OptimumComposeUiTarget();
                platform.EndFrame();
            }

            platform.BeginFrame();
            PaintDisplay(platform, Scene);

            // The real bind: the clear to transparent black and the redirect, and the flag
            // the scoped blend state is driven by.
            platform.OptimumBindUiTarget();
            Assert.True(platform.OptimumUiTargetBound, "the bind refused the UI target");
            DrawUi(platform, uiProgram);

            byte[] uiPixels = seam.ReadBackLevel0ForTests(uiTarget.ColorTextureIds[0]);
            platform.LoadFrameBuffer(EnumFrameBuffer.Default);
            byte[] displayBefore = ReadDisplay(seam);

            platform.OptimumComposeUiTarget();
            Assert.False(platform.OptimumUiTargetBound, "the compose left the scoped blend state armed");
            byte[] displayAfter = ReadDisplay(seam);
            platform.EndFrame();

            // 1. The display image before the compose holds the scene and nothing else.
            int overlayPixelsBefore = CountDifferences(displayBefore, Scene);
            _output.WriteLine("display pixels that are not the scene before the compose: " +
                overlayPixelsBefore + "/" + (Size * Size));
            Assert.Equal(0, overlayPixelsBefore);

            // 2. The UI target holds exactly the overlay: the drawn half, and transparent
            //    black everywhere else.
            int uiPixels_ = CountMatches(uiPixels, Ui, leftHalf: true);
            int clearPixels = CountMatches(uiPixels, Transparent, leftHalf: false);
            _output.WriteLine("UI target: overlay pixels " + uiPixels_ + "/" + (Size / 2 * Size) +
                ", transparent pixels " + clearPixels + "/" + (Size / 2 * Size));
            Assert.Equal(Size / 2 * Size, uiPixels_);
            Assert.Equal(Size / 2 * Size, clearPixels);

            // 3. And after the compose the display image is the over-operator's result.
            byte[] expected = Over(Ui, Scene);
            _output.WriteLine("expected composed colour: " + expected[0] + "," + expected[1] + "," +
                expected[2] + "," + expected[3]);
            int composedWrong = CountBeyondTolerance(displayAfter, expected, leftHalf: true, tolerance: 1);
            int sceneWrong = CountBeyondTolerance(displayAfter, Scene, leftHalf: false, tolerance: 1);
            _output.WriteLine("display after the compose: wrong composed pixels " + composedWrong +
                "/" + (Size / 2 * Size) + ", wrong untouched pixels " + sceneWrong +
                "/" + (Size / 2 * Size));
            Assert.Equal(0, composedWrong);
            Assert.Equal(0, sceneWrong);

            seam.DeleteProgram(uiProgram);
            seam.DeleteProgram(composeProgram);
            platform.DisposeFrameBuffer(uiTarget);
            GpuTest.AssertClean(seam);
        }
        finally
        {
            ShaderPrograms.UiCompose = previousCompose;
            platform.ShutdownGraphics();
            GLFW.DestroyWindow(window);
            try
            {
                Directory.Delete(dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// The reconciliation of the two halves of step 3, as a measurement.
    ///
    /// <para>What could go wrong, and did not.</para> The UI target and the scoped blend
    /// state were built separately: one half sets <c>OptimumUiTargetBound</c>, the other
    /// branches on it inside <c>GlToggleBlend</c>. If the scope survived the frame that
    /// opened it - an early return out of the blit that binds without arming, a compose
    /// that never ran, two merged declarations so the writer and the reader are different
    /// fields - then every <c>EnumBlendMode.Standard</c> draw in the next frame, which is
    /// world transparency, particles, decals and OIT, would silently use the separate-alpha
    /// factors. That is invisible on a screenshot and is exactly the class of difference
    /// CLAUDE.md rule 9 is about, so it is pinned here on the device rather than read.
    ///
    /// The frame after a composed one draws one straight-alpha layer under Standard into a
    /// target cleared to (0,0,0,0) - the same physical target the UI used, bound directly
    /// rather than through <c>OptimumBindUiTarget</c>, so what is being measured is the
    /// scope and not the target. Vanilla's non-separate factors square the coverage:
    /// out_a = src_a * src_a = 0.16. The scoped form would leave 0.4. Those are 41 and 102
    /// in a byte, so the two cannot be confused.
    /// </summary>
    [SkippableFact]
    public unsafe void TheScopeIsClosedBeforeTheNextFrameDrawsTheWorld()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Size, Size, out Window* window),
            "No usable window system.");

        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-ui-scope-test-" + Guid.NewGuid().ToString("N"));
        var platform = new WindowSizedPlatform
        {
            DeviceFactory = GpuTest.NewDevice,
            CrashMarkerDataPath = dataPath,
        };
        ShaderProgram? previousCompose = ShaderPrograms.UiCompose;
        try
        {
            bool installed = platform.InitializeGraphics((IntPtr)window, Size, Size, out string reason);
            if (!installed) _output.WriteLine("Vulkan unavailable: " + reason);
            Skip.IfNot(installed, "No usable Vulkan device.");
            VulkanDevice seam = platform.GraphicsDevice!;

            FrameBufferRef uiTarget = platform.CreateFramebuffer(ColorTarget("ui-target"));
            InstallFrameBuffers(platform, uiTarget);
            platform.SetOptimumUiTargetIndex(UiTargetSlot);

            int uiProgram = VulkanDeviceIntegrationTests.LinkProgram(
                seam, FullscreenVertex, StraightAlphaFragment, "ui-target-straight-alpha");
            int composeProgram = VulkanDeviceIntegrationTests.LinkProgram(
                seam, ComposeVertex, ComposeFragment, "ui-compose");
            ShaderPrograms.UiCompose = new ShaderProgram { ProgramId = composeProgram };

            // Frame 1: the whole step-3 path, the way the in-game frame runs it.
            platform.BeginFrame();
            PaintDisplay(platform, Scene);
            platform.OptimumBindUiTarget();
            Assert.True(platform.OptimumUiTargetBound);
            DrawStraightAlphaLayer(platform, uiProgram);
            byte[] scoped = seam.ReadBackLevel0ForTests(uiTarget.ColorTextureIds[0]);
            platform.OptimumComposeUiTarget();
            platform.EndFrame();

            // Frame 2 is the next frame's world drawing. The flag is the state the scope
            // lives in, so it is checked before anything else happens in it.
            platform.BeginFrame();
            Assert.False(platform.OptimumUiTargetBound,
                "the scope survived the frame that opened it - every Standard blend in this frame is wrong");

            platform.ClearFrameBuffer(uiTarget, new[] { 0f, 0f, 0f, 0f });
            platform.GlViewport(0, 0, Size, Size);
            DrawStraightAlphaLayer(platform, uiProgram);
            byte[] unscoped = seam.ReadBackLevel0ForTests(uiTarget.ColorTextureIds[0]);
            platform.EndFrame();

            // 0.4 coverage in the scope, 0.4 * 0.4 out of it. The rgb factors are the same
            // either way (src_rgb * src_a), which is why only alpha is asserted to differ.
            _output.WriteLine("alpha in the UI scope: " + scoped[3] + ", alpha in the next frame: " + unscoped[3]);
            Assert.InRange(scoped[3], 101, 103);
            Assert.InRange(unscoped[3], 40, 42);
            // And the colour is the same in both: src_rgb * src_a = 0.8 * 0.4 = 0.32.
            Assert.InRange(unscoped[0], 81, 83);
            Assert.InRange(scoped[0], 81, 83);

            seam.DeleteProgram(uiProgram);
            seam.DeleteProgram(composeProgram);
            platform.DisposeFrameBuffer(uiTarget);
            GpuTest.AssertClean(seam);
        }
        finally
        {
            ShaderPrograms.UiCompose = previousCompose;
            platform.ShutdownGraphics();
            GLFW.DestroyWindow(window);
            try
            {
                Directory.Delete(dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// The compose is its own frame-graph pass, and the UI target is its declared read.
    ///
    /// Without the declaration the compose runs under the enclosing "Frame" context, whose
    /// reads are empty and which does not carry <c>PassFlags.OpenSampling</c> - so
    /// <c>PassRecorder.Prepare</c> queues no pass-entry barrier for the image, and the UI
    /// target is still in its colour-attachment layout when the draw binds it. The per-draw
    /// safety net in <c>VulkanDevice</c> catches that and is why it renders correctly, but it
    /// catches it by ending the rendering scope mid-pass: one extra split every frame the UI
    /// target is on. Declared, the pass opens with the image already shader-readable.
    ///
    /// No device: this is the declaration the recorder is driven by, and it is a pure function
    /// of the context and the framebuffer list.
    /// </summary>
    [Fact]
    public void TheComposePassDeclaresTheUiTargetAsItsRead()
    {
        var platform = new WindowSizedPlatform();
        var uiTarget = new FrameBufferRef { Width = Size, Height = Size, ColorTextureIds = new[] { 77 } };
        InstallFrameBuffers(platform, uiTarget);

        // Bound target = the default framebuffer, which is where the compose draws.
        Assert.Equal(new[] { 77 }, platform.PassReads("UiCompose", -1));
        // The context is set one statement before the compose rebinds, while the UI target is
        // still bound: an attachment of the bound framebuffer must never be its own pass read.
        Assert.Empty(platform.PassReads("UiCompose", UiTargetSlot));
        // And the context the compose used to run under declares nothing - the regression.
        Assert.Empty(platform.PassReads("Frame", -1));
    }

    /// <summary>
    /// The frame that never composes, which is the leak the two-call design does not cover.
    ///
    /// <para>Why this frame exists.</para> <c>OptimumBindUiTarget</c> arms the scope at the
    /// blit and the two <c>OptimumComposeUiTarget</c> call sites close it two stages later -
    /// but nothing guarantees either is reached. <c>eventManager.TriggerRenderStage</c>
    /// catches nothing (ClientMain.cs:1286-1297), so a GUI renderer that throws at AfterBlit
    /// or Ortho unwinds out of <c>ScreenManager.Render</c> past both of them. Nothing re-arms
    /// the flag before the next frame's world pass either: the bind is not reached again
    /// until that frame's blit, which is after everything the world draws. Every unqualified
    /// <c>GlToggleBlend(on: true)</c> in between - world transparency, particles, decals,
    /// OIT - would take the scoped separate-alpha factors with no UI target bound at all.
    ///
    /// So the frame's first clear heals it, next to the motion-window reset that is there for
    /// the same reason (<c>ClearFrameBuffer(EnumFrameBuffer.Primary)</c>). Measured, not read:
    /// the layer drawn after that clear must carry vanilla's squared coverage, 41/255, and not
    /// the scope's 102/255. Before the heal this test reads 102.
    /// </summary>
    [SkippableFact]
    public unsafe void TheScopeIsHealedByTheNextFrameWhenNothingEverComposed()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Size, Size, out Window* window),
            "No usable window system.");

        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-ui-heal-test-" + Guid.NewGuid().ToString("N"));
        var platform = new WindowSizedPlatform
        {
            DeviceFactory = GpuTest.NewDevice,
            CrashMarkerDataPath = dataPath,
        };
        try
        {
            bool installed = platform.InitializeGraphics((IntPtr)window, Size, Size, out string reason);
            if (!installed) _output.WriteLine("Vulkan unavailable: " + reason);
            Skip.IfNot(installed, "No usable Vulkan device.");
            VulkanDevice seam = platform.GraphicsDevice!;

            FrameBufferRef uiTarget = platform.CreateFramebuffer(ColorTarget("ui-target"));
            InstallFrameBuffers(platform, uiTarget);
            platform.SetOptimumUiTargetIndex(UiTargetSlot);

            int uiProgram = VulkanDeviceIntegrationTests.LinkProgram(
                seam, FullscreenVertex, StraightAlphaFragment, "ui-target-straight-alpha");

            // Frame 1, the frame that threw: bound, drawn into, never composed.
            platform.BeginFrame();
            PaintDisplay(platform, Scene);
            platform.OptimumBindUiTarget();
            Assert.True(platform.OptimumUiTargetBound, "the bind refused the UI target");
            DrawStraightAlphaLayer(platform, uiProgram);
            platform.EndFrame();

            // Frame 2, the world frame. The scope really is still open on the way in - that
            // is the hazard, and asserting it here is what keeps this test honest if someone
            // later closes the flag somewhere else and calls the leak fixed.
            platform.BeginFrame();
            Assert.True(platform.OptimumUiTargetBound,
                "nothing re-arms the scope between frames, so it must still be open here");

            // ScreenManager.Render's second statement, before anything in this frame draws.
            PaintDisplay(platform, Scene);
            platform.ClearFrameBuffer(EnumFrameBuffer.Primary);
            Assert.False(platform.OptimumUiTargetBound,
                "the frame's first clear left the scope armed - the world blends on the UI factors");

            platform.ClearFrameBuffer(uiTarget, new[] { 0f, 0f, 0f, 0f });
            platform.GlViewport(0, 0, Size, Size);
            DrawStraightAlphaLayer(platform, uiProgram);
            byte[] healed = seam.ReadBackLevel0ForTests(uiTarget.ColorTextureIds[0]);
            platform.EndFrame();

            _output.WriteLine("alpha in the frame after the leak: " + healed[3] + " (vanilla 41, scoped 102)");
            Assert.InRange(healed[3], 40, 42);
            // The rgb factors were never in the scope's remit, so they are the same number
            // either way: src_rgb * src_a = 0.8 * 0.4 = 0.32.
            Assert.InRange(healed[0], 81, 83);

            seam.DeleteProgram(uiProgram);
            platform.DisposeFrameBuffer(uiTarget);
            GpuTest.AssertClean(seam);
        }
        finally
        {
            platform.ShutdownGraphics();
            GLFW.DestroyWindow(window);
            try
            {
                Directory.Delete(dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// What gui.fsh writes: straight (non-premultiplied) alpha. Under Standard that is the
    /// draw whose result differs between the scoped and the unscoped factors.
    /// </summary>
    private const string StraightAlphaFragment = """
        #version 330 core
        out vec4 outColor;
        void main(void) { outColor = vec4(0.8, 0.4, 0.2, 0.4); }
        """;

    /// <summary>One GUI-shaped layer, drawn under EnumBlendMode.Standard - the mode the whole
    /// Ortho stage inherits from ScreenManager.Render's GlToggleBlend(on: true).</summary>
    private static void DrawStraightAlphaLayer(VulkanClientPlatform platform, int program)
    {
        platform.GlDisableDepthTest();
        platform.GlDisableCullFace();
        platform.GlToggleBlend(true);
        platform.UseShaderProgram(program);
        platform.RenderFullscreenTriangle(null!);
        platform.UseShaderProgram(0);
        platform.GlToggleBlend(false);
    }

    /// <summary>The display image this frame started with: the composited world, blitted into the window.</summary>
    private static void PaintDisplay(VulkanClientPlatform platform, byte[] color)
    {
        platform.LoadFrameBuffer(EnumFrameBuffer.Default);
        platform.GlDisableDepthTest();
        platform.GlDisableCullFace();
        platform.GlToggleBlend(false);
        platform.GlClearColorRgbaf(color[0] / 255f, color[1] / 255f, color[2] / 255f, color[3] / 255f);
        platform.ClearFrameBuffer(EnumFrameBuffer.Default);
    }

    /// <summary>
    /// What the Ortho stage leaves in the UI target: premultiplied colour and coverage over
    /// the left half, so the assertions can say exactly which pixels the compose moved.
    /// Blending is off - the accumulation of many straight-alpha GUI draws into this target
    /// is the scoped blend state's subject, not this one's.
    /// </summary>
    private static void DrawUi(VulkanClientPlatform platform, int program)
    {
        platform.GlDisableDepthTest();
        platform.GlDisableCullFace();
        platform.GlToggleBlend(false);
        platform.UseShaderProgram(program);
        platform.GlScissor(0, 0, Size / 2, Size);
        platform.GlScissorFlag(true);
        platform.RenderFullscreenTriangle(null!);
        platform.GlScissorFlag(false);
        platform.UseShaderProgram(0);
    }

    /// <summary>
    /// The bound target's pixels, in its own channel order (R G B A for the default colour
    /// target) - the device-level readback, not the platform's, which converts to the
    /// GL_BGRA the screenshot paths expect.
    /// </summary>
    private static unsafe byte[] ReadDisplay(VulkanDevice seam)
    {
        byte[] pixels = new byte[Size * Size * 4];
        fixed (byte* destination = pixels)
        {
            seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
        }
        return pixels;
    }

    /// <summary>The over-operator on premultiplied source: dst = src.rgb + dst * (1 - src.a).</summary>
    private static byte[] Over(byte[] source, byte[] destination)
    {
        float inverse = 1f - source[3] / 255f;
        byte[] result = new byte[4];
        for (int channel = 0; channel < 4; channel++)
        {
            float value = source[channel] / 255f + destination[channel] / 255f * inverse;
            result[channel] = (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);
        }
        return result;
    }

    private static FramebufferAttrs ColorTarget(string name) =>
        new FramebufferAttrs(name, Size, Size)
        {
            Attachments = new[]
            {
                new FramebufferAttrsAttachment
                {
                    AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                    Texture = new RawTexture
                    {
                        Width = Size,
                        Height = Size,
                        PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
                        PixelFormat = EnumTexturePixelFormat.Rgba,
                        MinFilter = EnumTextureFilter.Nearest,
                        MagFilter = EnumTextureFilter.Nearest,
                    },
                },
            },
        };

    /// <summary>
    /// The platform's framebuffer list, which only SetupDefaultFrameBuffers builds and which
    /// needs a window to build. The bind and the compose read it by the published index, so
    /// the test installs exactly that entry rather than reimplementing either against a
    /// target of its own.
    /// </summary>
    private static void InstallFrameBuffers(VulkanClientPlatform platform, FrameBufferRef uiTarget)
    {
        var list = new List<FrameBufferRef>();
        for (int slot = 0; slot <= UiTargetSlot; slot++) list.Add(null!);
        list[UiTargetSlot] = uiTarget;

        FieldInfo? field = typeof(ClientPlatformWindows).GetField(
            "frameBuffers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(platform, list);
        Assert.Same(uiTarget, platform.FrameBuffers[UiTargetSlot]);
    }

    private static int CountDifferences(byte[] pixels, byte[] color)
    {
        Assert.Equal(Size * Size * 4, pixels.Length);
        int differing = 0;
        for (int pixel = 0; pixel < Size * Size; pixel++)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                if (pixels[pixel * 4 + channel] != color[channel])
                {
                    differing++;
                    break;
                }
            }
        }
        return differing;
    }

    private static int CountMatches(byte[] pixels, byte[] color, bool leftHalf)
    {
        Assert.Equal(Size * Size * 4, pixels.Length);
        int matching = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (x < Size / 2 != leftHalf) continue;
                int offset = (y * Size + x) * 4;
                bool same = true;
                for (int channel = 0; channel < 4; channel++)
                {
                    if (pixels[offset + channel] != color[channel]) same = false;
                }
                if (same) matching++;
            }
        }
        return matching;
    }

    private static int CountBeyondTolerance(byte[] pixels, byte[] color, bool leftHalf, int tolerance)
    {
        Assert.Equal(Size * Size * 4, pixels.Length);
        int wrong = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (x < Size / 2 != leftHalf) continue;
                int offset = (y * Size + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    if (Math.Abs(pixels[offset + channel] - color[channel]) > tolerance)
                    {
                        wrong++;
                        break;
                    }
                }
            }
        }
        return wrong;
    }
}
