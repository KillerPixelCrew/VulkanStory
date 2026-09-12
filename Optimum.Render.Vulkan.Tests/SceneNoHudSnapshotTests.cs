using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// DLSS-FG design, step 2: the HUD-less scene snapshot - the guide's <c>pHudless</c>.
///
/// The claim the snapshot rests on is a claim about one moment in the frame, so it is
/// tested at that moment through the real platform: the composited image is built in the
/// target the platform calls the composite, <c>OptimumCaptureSceneNoHud</c> is called where
/// <c>RenderFinalComposition</c> ends, and only then does an overlay draw - which is what
/// <c>RenderAfterFinalComposition</c> does to that same image on every real frame.
///
/// Three things are asserted on the GPU, none of which a host-level test can see:
/// <list type="number">
/// <item>With nothing drawn after the capture, the snapshot equals the composited image
/// byte for byte - the whole image, not a sample.</item>
/// <item>With an overlay drawn after the capture, the snapshot still holds the scene alone
/// and the composite provably differs from it, in exactly the overlay's pixels.</item>
/// <item>With the slot unpublished - no upscaler, no frame generation - the capture does
/// nothing at all and reports that it did nothing, which is what "a normal frame pays
/// nothing" means.</item>
/// </list>
///
/// Several frames run before the judged ones, with <c>EndFrame</c> (Present) between them
/// and no readback in the loop: a snapshot that is really the previous frame's image, or
/// one whose copy is ordered against the wrong frame's work, passes a single-frame readback
/// and fails here. Validation runs with sync and best practices on, as every GPU test does.
/// </summary>
public class SceneNoHudSnapshotTests
{
    private const int Size = 32;
    private const int SceneNoHudSlot = 23;

    // The scene, the overlay drawn onto the composite after the capture, and the second
    // scene used for the frame that draws no overlay. All three are distinguishable in
    // every channel, so a copy that reached the wrong target or the wrong frame cannot
    // pass by coincidence.
    private static readonly byte[] Scene = { 30, 160, 210, 255 };
    private static readonly byte[] Overlay = { 240, 60, 20, 255 };
    private static readonly byte[] SecondScene = { 90, 25, 180, 255 };

    private readonly ITestOutputHelper _output;

    public SceneNoHudSnapshotTests(ITestOutputHelper output) => _output = output;

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    private const string OverlayFragment = """
        #version 330 core
        out vec4 outColor;
        void main(void) { outColor = vec4(240.0 / 255.0, 60.0 / 255.0, 20.0 / 255.0, 1.0); }
        """;

    [SkippableFact]
    public unsafe void TheSnapshotIsTheCompositeBeforeTheOverlaysAndDiffersOnceTheyDraw()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-scene-no-hud-test-" + Guid.NewGuid().ToString("N"));
        var platform = new VulkanClientPlatform(null!)
        {
            DeviceFactory = GpuTest.NewDevice,
            CrashMarkerDataPath = dataPath,
        };
        try
        {
            bool installed = platform.InitializeGraphics(IntPtr.Zero, 0, 0, out string reason);
            if (!installed) _output.WriteLine("Vulkan unavailable: " + reason);
            Skip.IfNot(installed, "No usable Vulkan device.");
            VulkanDevice seam = platform.GraphicsDevice!;

            FrameBufferRef composite = platform.CreateFramebuffer(ColorTarget("composite"));
            FrameBufferRef snapshot = platform.CreateFramebuffer(ColorTarget("scene-no-hud"));
            InstallFrameBuffers(platform, composite, snapshot);

            // Published the way MotionAttachmentIndex is: the index is the contract, and it
            // is what the capture and every consumer index FrameBuffers by.
            platform.SetOptimumSceneNoHudIndex(SceneNoHudSlot);
            Assert.Equal(SceneNoHudSlot, platform.SceneNoHudFrameBufferIndex);
            Assert.Same(snapshot, platform.OptimumSceneNoHudFrameBuffer);
            // No upscaler here, so the composite is Primary - slot 0 - exactly as on every
            // frame the client renders without one.
            Assert.Same(composite, platform.OptimumCompositeFrameBuffer);

            int overlayProgram = VulkanDeviceIntegrationTests.LinkProgram(
                seam, FullscreenVertex, OverlayFragment, "scene-no-hud-overlay");

            // Frames that are not judged, so a snapshot that lags by a frame or is ordered
            // against the wrong frame's work has somewhere to show it.
            for (int frame = 0; frame < 3; frame++)
            {
                platform.BeginFrame();
                ComposeScene(platform, composite, Scene);
                platform.OptimumCaptureSceneNoHud();
                DrawOverlay(platform, composite, overlayProgram);
                platform.EndFrame();
            }

            // The judged frame with an overlay: capture, then draw over the left half of
            // the composite, then read both back inside the same frame.
            platform.BeginFrame();
            ComposeScene(platform, composite, Scene);
            platform.OptimumCaptureSceneNoHud();
            Assert.True(platform.OptimumSceneNoHudCaptured, "the capture refused the copy");
            DrawOverlay(platform, composite, overlayProgram);
            byte[] snapshotWithOverlay = seam.ReadBackLevel0ForTests(snapshot.ColorTextureIds[0]);
            byte[] compositeWithOverlay = seam.ReadBackLevel0ForTests(composite.ColorTextureIds[0]);
            platform.EndFrame();

            // The snapshot is the scene alone, every pixel of it.
            AssertUniform(snapshotWithOverlay, Scene, "the snapshot after an overlay drew");
            // The composite is not: the overlay's half differs, the rest does not.
            AssertHalves(compositeWithOverlay, Overlay, Scene);
            int differing = CountDifferences(snapshotWithOverlay, compositeWithOverlay);
            _output.WriteLine("pixels where the snapshot and the composite differ: " +
                differing + "/" + (Size * Size) + " (the overlay covers " + (Size / 2 * Size) + ")");
            Assert.Equal(Size / 2 * Size, differing);

            // The judged frame with nothing drawn after the capture: the snapshot is the
            // composited image byte for byte. A different scene colour, so a stale copy
            // from any earlier frame fails this rather than passing it.
            platform.BeginFrame();
            ComposeScene(platform, composite, SecondScene);
            platform.OptimumCaptureSceneNoHud();
            Assert.True(platform.OptimumSceneNoHudCaptured, "the capture refused the copy");
            byte[] snapshotAlone = seam.ReadBackLevel0ForTests(snapshot.ColorTextureIds[0]);
            byte[] compositeAlone = seam.ReadBackLevel0ForTests(composite.ColorTextureIds[0]);
            platform.EndFrame();

            AssertUniform(compositeAlone, SecondScene, "the composite with no overlay");
            Assert.Equal(compositeAlone, snapshotAlone);
            Assert.Equal(0, CountDifferences(snapshotAlone, compositeAlone));

            // And the frame nothing asked for a snapshot on: the slot is unpublished, the
            // capture copies nothing and says so, and the snapshot still holds what the
            // last real capture left in it.
            platform.SetOptimumSceneNoHudIndex(-1);
            Assert.Equal(-1, platform.SceneNoHudFrameBufferIndex);
            Assert.Null(platform.OptimumSceneNoHudFrameBuffer);
            platform.BeginFrame();
            ComposeScene(platform, composite, Overlay);
            platform.OptimumCaptureSceneNoHud();
            Assert.False(platform.OptimumSceneNoHudCaptured, "the capture ran with no slot published");
            byte[] snapshotUntouched = seam.ReadBackLevel0ForTests(snapshot.ColorTextureIds[0]);
            platform.EndFrame();
            AssertUniform(snapshotUntouched, SecondScene, "the snapshot on a frame that wanted none");

            seam.DeleteProgram(overlayProgram);
            platform.DisposeFrameBuffer(composite);
            platform.DisposeFrameBuffer(snapshot);
            GpuTest.AssertClean(seam);
        }
        finally
        {
            platform.ShutdownGraphics();
            try
            {
                Directory.Delete(dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>The composited image of this frame: one flat colour in the composite target.</summary>
    private static void ComposeScene(VulkanClientPlatform platform, FrameBufferRef composite, byte[] color)
    {
        platform.CurrentFrameBuffer = composite;
        platform.GlDisableDepthTest();
        platform.GlDisableCullFace();
        platform.GlToggleBlend(false);
        platform.ClearFrameBuffer(composite,
            new[] { color[0] / 255f, color[1] / 255f, color[2] / 255f, color[3] / 255f },
            clearDepthBuffer: false);
    }

    /// <summary>
    /// What RenderAfterFinalComposition does to the composited image: a world-space overlay
    /// drawn straight onto it, after the composition and after the capture. The left half,
    /// so the assertion can say exactly which pixels moved.
    /// </summary>
    private static void DrawOverlay(VulkanClientPlatform platform, FrameBufferRef composite, int program)
    {
        platform.CurrentFrameBuffer = composite;
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
    /// needs a window to build. The capture reads it by the published index and reads the
    /// composite out of slot 0, so the test installs exactly those two entries rather than
    /// reimplementing the capture against targets of its own.
    /// </summary>
    private static void InstallFrameBuffers(VulkanClientPlatform platform, FrameBufferRef composite, FrameBufferRef snapshot)
    {
        var list = new List<FrameBufferRef>();
        for (int slot = 0; slot <= 24; slot++) list.Add(null!);
        list[0] = composite;
        list[SceneNoHudSlot] = snapshot;

        FieldInfo? field = typeof(ClientPlatformWindows).GetField(
            "frameBuffers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(platform, list);
        Assert.Same(composite, platform.FrameBuffers[0]);
    }

    private static void AssertUniform(byte[] pixels, byte[] color, string what)
    {
        Assert.Equal(Size * Size * 4, pixels.Length);
        for (int pixel = 0; pixel < Size * Size; pixel++)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                Assert.True(pixels[pixel * 4 + channel] == color[channel],
                    what + ": pixel " + pixel + " channel " + channel + " is " +
                    pixels[pixel * 4 + channel] + ", expected " + color[channel]);
            }
        }
    }

    /// <summary>Left half one colour, right half another - the overlay's own footprint.</summary>
    private static void AssertHalves(byte[] pixels, byte[] left, byte[] right)
    {
        Assert.Equal(Size * Size * 4, pixels.Length);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                byte[] expected = x < Size / 2 ? left : right;
                int offset = (y * Size + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    Assert.True(pixels[offset + channel] == expected[channel],
                        "composite pixel " + x + "," + y + " channel " + channel + " is " +
                        pixels[offset + channel] + ", expected " + expected[channel]);
                }
            }
        }
    }

    private static int CountDifferences(byte[] left, byte[] right)
    {
        Assert.Equal(left.Length, right.Length);
        int differing = 0;
        for (int pixel = 0; pixel < left.Length / 4; pixel++)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                if (left[pixel * 4 + channel] != right[pixel * 4 + channel])
                {
                    differing++;
                    break;
                }
            }
        }
        return differing;
    }
}
