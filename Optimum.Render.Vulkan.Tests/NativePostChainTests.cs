using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Optimum.Render.Vulkan.Platform;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

using LinkedProgram = Optimum.Render.Vulkan.Tests.VulkanDeviceIntegrationTests.TestProgram;
using LinkedShader = Optimum.Render.Vulkan.Tests.VulkanDeviceIntegrationTests.TestShader;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The post and TAA chain on the Vulkan platform (docs/vulkan-native-render-systems.md, stage 1).
/// Optimum owns the chain's order; its first two passes - the OIT merge and sky motion - draw
/// through the native device API, and the rest run the OpenGL body until their own stage moves
/// them.
///
/// What is asserted here:
///  1. the OIT merge draws the same pixels as the OpenGL body, with and without the motion
///     window, into the shaded image, the glow attachment and the motion attachment;
///  2. sky motion writes the same motion attachment as the OpenGL body and leaves the shaded
///     image alone;
///  3. neither native pass reaches the GL-emulation layer while its pass is open;
///  4. over several frames the chain runs its steps in the declared order and the TAA resolve
///     keeps accumulating - the history parity alternates and the motion attachment the resolve
///     reads was written by the two passes that run before it.
/// </summary>
public class NativePostChainTests(ITestOutputHelper output)
{
    private const int Size = 16;

    private static readonly string[] Programs = { "transparentcompose", "taa-skymotion", "taa-resolve", "blit" };

    /// <summary>The Vulkan platform without a window: the size seam answers for one.</summary>
    private sealed class ChainPlatform : VulkanClientPlatform
    {
        public ChainPlatform() : base(null!)
        {
        }

        public override Size2i OptimumWindowClientSize() => new(NativePostChainTests.Size, NativePostChainTests.Size);

        /// <summary>
        /// No window is opened here, and the base's Primary case sizes its viewport from
        /// NativeWindow.ClientSize, which is GLFW-backed. Primary is the render resolution, so
        /// the full CurrentFrameBuffer setter is the same bind and the same viewport.
        /// </summary>
        public override void LoadFrameBuffer(EnumFrameBuffer framebuffer)
        {
            if (framebuffer == EnumFrameBuffer.Primary)
            {
                CurrentFrameBuffer = FrameBuffers[0];
                return;
            }
            base.LoadFrameBuffer(framebuffer);
        }
    }

    // ------------------------------------------------------------------ the tests

    /// <summary>
    /// The OIT merge with the motion window open: the shaded image, the glow attachment and the
    /// motion attachment all have to come out of the native pass exactly as the OpenGL body
    /// leaves them, including the additive (ONE, ONE) blend that only touches the reactive
    /// channel.
    /// </summary>
    [SkippableFact]
    public void TheOitMergeMatchesTheOpenGlBodyWithTheMotionWindowOpen()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);

        Frame emulated = RunMerge(session, native: false);

        long passesBefore = session.Seam.NativePassesForTests;
        long drawsBefore = session.Seam.NativeDrawsForTests;
        long insideBefore = session.Seam.EmulationCallsInNativePassesForTests;
        Frame nativeRoute = RunMerge(session, native: true);

        Assert.Equal(1, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(1, session.Seam.NativeDrawsForTests - drawsBefore);
        Assert.Equal(0, session.Seam.EmulationCallsInNativePassesForTests - insideBefore);

        Assert.Equal(emulated.Scene, nativeRoute.Scene);
        Assert.Equal(emulated.Glow, nativeRoute.Glow);
        Assert.Equal(emulated.Motion, nativeRoute.Motion);

        // The merge really did add into the reactive channel, or the comparison above would
        // pass on two routes that both wrote nothing.
        Assert.True(Reactive(nativeRoute.Motion, Size / 2, Size / 2) > Reactive(session.MotionSeed, 0, 0),
            "the merge did not accumulate coverage into the reactive channel");

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The same merge with TAA off: the motion window never opens, so the pass writes the two
    /// world attachments and leaves the motion attachment exactly as it found it.
    /// </summary>
    [SkippableFact]
    public void TheOitMergeMatchesTheOpenGlBodyWithoutTheMotionWindow()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: false);

        Frame emulated = RunMerge(session, native: false);
        Frame nativeRoute = RunMerge(session, native: true);

        Assert.Equal(emulated.Scene, nativeRoute.Scene);
        Assert.Equal(emulated.Glow, nativeRoute.Glow);
        Assert.Equal(emulated.Motion, nativeRoute.Motion);
        Assert.Equal(session.MotionSeed, nativeRoute.Motion);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// Sky motion: the motion attachment the native pass writes is the OpenGL body's, and the
    /// shaded image is untouched - the pass writes one colour slot and no depth.
    /// </summary>
    [SkippableFact]
    public void SkyMotionMatchesTheOpenGlBody()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);

        Frame emulated = RunSkyMotion(session, native: false);

        long passesBefore = session.Seam.NativePassesForTests;
        long drawsBefore = session.Seam.NativeDrawsForTests;
        long insideBefore = session.Seam.EmulationCallsInNativePassesForTests;
        Frame nativeRoute = RunSkyMotion(session, native: true);

        Assert.Equal(1, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(1, session.Seam.NativeDrawsForTests - drawsBefore);
        Assert.Equal(0, session.Seam.EmulationCallsInNativePassesForTests - insideBefore);

        Assert.Equal(emulated.Motion, nativeRoute.Motion);
        Assert.Equal(emulated.Scene, nativeRoute.Scene);
        Assert.Equal(session.SceneSeed, nativeRoute.Scene);

        // The pass covered the sky, or "the two routes agree" would be vacuous.
        Assert.True(Reactive(nativeRoute.Motion, Size / 2, Size / 2) > 0.5f,
            "sky motion wrote no reactive value, so the depth test rejected the whole target");

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>The OpenGL body on this device is the emulation layer; the native chain is not.</summary>
    [SkippableFact]
    public void TheOpenGlRouteDrawsThroughTheEmulationLayerAndTheNativeChainDoesNot()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);

        long nativeDrawsBefore = session.Seam.NativeDrawsForTests;
        long emulatedBefore = session.Seam.EmulationCallsForTests;
        RunMerge(session, native: false);
        Assert.Equal(0, session.Seam.NativeDrawsForTests - nativeDrawsBefore);
        Assert.True(session.Seam.EmulationCallsForTests - emulatedBefore > 0);

        long insideBefore = session.Seam.EmulationCallsInNativePassesForTests;
        RunMerge(session, native: true);
        Assert.Equal(0, session.Seam.EmulationCallsInNativePassesForTests - insideBefore);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// Several frames through the native chain: every frame runs the steps in the declared
    /// order, and TAA keeps accumulating - the resolve runs each frame, the history parity
    /// alternates so each frame reads the slot the last one wrote, and the motion attachment it
    /// reads carries what the merge and sky motion wrote before it.
    ///
    /// The final composition is the one declared step not driven here: its body reads
    /// NativeWindow.ClientSize directly, which a windowless test cannot answer. Its position in
    /// the chain is pinned by the source coverage test (Optimum.Tests, native-post-chain).
    /// </summary>
    [SkippableFact]
    public void TheChainKeepsItsOrderAcrossFramesAndTaaKeepsAccumulating()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);

        VulkanDevice seam = session.Seam;
        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = true;

        var expected = new List<VulkanClientPlatform.NativePostStep>();
        foreach (VulkanClientPlatform.NativePostStep step in VulkanClientPlatform.NativePostChainOrder)
        {
            if (step != VulkanClientPlatform.NativePostStep.FinalComposition) expected.Add(step);
        }

        var resolvedTextures = new List<int>();
        const int frames = 4;
        for (int frame = 0; frame < frames; frame++)
        {
            session.AdvanceTemporalFrame();
            var log = new List<VulkanClientPlatform.NativePostStep>();
            platform.NativePostStepLog = log;

            platform.BeginFrame();
            session.SeedFrame();

            platform.CurrentFrameBuffer = session.Primary;
            platform.MergeTransparentRenderPass();
            platform.CurrentFrameBuffer = session.Primary;
            platform.RenderOptimumSkyMotion();
            platform.RenderPostprocessingEffects(null);
            platform.BlitPrimaryToDefault();

            platform.NativePostStepLog = null;
            Assert.Equal(expected, log);
            Assert.True(platform.TaaResolvedThisFrame, "the resolve did not run on frame " + frame);
            resolvedTextures.Add(ResolvedColorTexture(platform));

            byte[] motion = session.ReadMotion();
            Assert.True(Reactive(motion, Size / 2, Size / 2) > 0.5f,
                "frame " + frame + " reached the resolve with an empty motion attachment, " +
                "so the merge and sky motion did not run before it");

            platform.EndFrame();
        }

        // The resolve alternates its history slots, so every frame reads the one the previous
        // frame wrote: that alternation is the accumulation.
        int slotA = session.History(0).ColorTextureIds[0];
        int slotB = session.History(1).ColorTextureIds[0];
        for (int frame = 0; frame < frames; frame++)
        {
            Assert.Equal(frame % 2 == 0 ? slotA : slotB, resolvedTextures[frame]);
        }
        Assert.True(HistoryValid(platform), "the resolve left the history invalid");

        GpuTest.AssertClean(seam);
    }

    // ---------------------------------------------------------------------- driving

    private readonly record struct Frame(byte[] Scene, byte[] Glow, byte[] Motion);

    /// <summary>One OIT merge, on the route under test, from an identically seeded frame.</summary>
    private Frame RunMerge(Session session, bool native)
    {
        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = native;

        platform.BeginFrame();
        session.SeedFrame();
        platform.CurrentFrameBuffer = session.Primary;

        platform.MergeTransparentRenderPass();

        var frame = new Frame(session.ReadScene(), session.ReadGlow(), session.ReadMotion());
        platform.EndFrame();
        return frame;
    }

    /// <summary>One sky-motion pass, on the route under test, from an identically seeded frame.</summary>
    private Frame RunSkyMotion(Session session, bool native)
    {
        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = native;
        session.AdvanceTemporalFrame();

        platform.BeginFrame();
        session.SeedFrame();
        platform.CurrentFrameBuffer = session.Primary;

        bool drawn = platform.RenderOptimumSkyMotion();
        Assert.True(drawn, "the sky motion pass did not run");

        var frame = new Frame(session.ReadScene(), session.ReadGlow(), session.ReadMotion());
        platform.EndFrame();
        return frame;
    }

    /// <summary>The reactive channel of a decoded motion pixel (motion.b, in [0, 1]).</summary>
    private static float Reactive(byte[] decoded, int x, int y) => decoded[(y * Size + x) * 4 + 2] / 255f;

    private static int ResolvedColorTexture(ClientPlatformWindows platform) =>
        (int)typeof(ClientPlatformWindows)
            .GetField("taaResolvedColorTexture", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(platform)!;

    private static bool HistoryValid(ClientPlatformWindows platform) =>
        (bool)typeof(ClientPlatformWindows)
            .GetField("_taaHistoryValid", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(platform)!;

    // ---------------------------------------------------------------------- session

    private Session Open()
    {
        (string manifest, string reason) = NativeManifest.Value;
        Skip.If(manifest.Length == 0, reason);
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        Session? session = Session.TryOpen(output, manifest);
        Skip.If(session == null, "No usable Vulkan device.");
        return session!;
    }

    /// <summary>
    /// The platform, its device, the targets the chain indexes, the programs its passes use and
    /// the client statics they read, all put back afterwards.
    /// </summary>
    private sealed class Session : IDisposable
    {
        public ChainPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;
        public FrameBufferRef Primary { get; private set; } = null!;
        public FrameBufferRef Transparent { get; private set; } = null!;

        /// <summary>The motion attachment's seed, decoded the way <see cref="ReadMotion" /> decodes it.</summary>
        public byte[] MotionSeed { get; private set; } = Array.Empty<byte>();

        /// <summary>The shaded image's seed, as read back.</summary>
        public byte[] SceneSeed { get; private set; } = Array.Empty<byte>();

        private readonly List<FrameBufferRef> buffers = new();
        private int oitReveal;
        private int oitAccumulation;
        private int decodeProgram;
        private int decodeTarget;
        private int decodeFramebuffer;
        private ClientPlatformAbstract? previousPlatform;
        private string dataPath = "";
        private ShaderProgramTransparentcompose? composeBefore;
        private ShaderProgram? skyMotionBefore;
        private ShaderProgram? resolveBefore;
        private ShaderProgramBlit? blitBefore;
        private bool taaBefore;
        private float sharpnessBefore;
        private object? oitRevealBefore;
        private object? oitAccumBefore;
        private DefaultShaderUniforms uniforms = new();

        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

        public FrameBufferRef History(int parity) => buffers[parity == 0 ? 19 : 20];

        public static Session? TryOpen(ITestOutputHelper output, string manifestDirectory)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-native-post-" + Guid.NewGuid().ToString("N"));
            var platform = new ChainPlatform
            {
                DeviceFactory = () =>
                {
                    VulkanDevice created = GpuTest.NewDevice();
                    created.NativeShaderDirectory = manifestDirectory;
                    created.NativeShadersEnabled = true;
                    created.IgnoreModShaderScan = true;
                    return created;
                },
                CrashMarkerDataPath = dataPath,
            };

            if (!platform.InitializeGraphics(IntPtr.Zero, Size, Size, out string reason))
            {
                output.WriteLine("Vulkan unavailable: " + reason);
                platform.ShutdownGraphics();
                return null;
            }

            var session = new Session
            {
                Platform = platform,
                previousPlatform = ScreenManager.Platform,
                dataPath = dataPath,
                composeBefore = ShaderPrograms.Transparentcompose,
                skyMotionBefore = ShaderPrograms.TaaSkyMotion,
                resolveBefore = ShaderPrograms.TaaResolve,
                blitBefore = ShaderPrograms.Blit,
                taaBefore = OptimumConfig.Taa,
                sharpnessBefore = OptimumConfig.TaaSharpness,
            };
            ScreenManager.Platform = platform;
            ScreenManager.FrameProfiler ??= new FrameProfilerUtil(static (string _) => { });
            platform.ShaderUniforms = session.uniforms;

            session.BuildTargets();
            session.LinkPrograms();
            session.InstallState();
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            ShaderPrograms.Transparentcompose = composeBefore!;
            ShaderPrograms.TaaSkyMotion = skyMotionBefore!;
            ShaderPrograms.TaaResolve = resolveBefore!;
            ShaderPrograms.Blit = blitBefore!;
            OptimumConfig.Taa = taaBefore;
            OptimumConfig.TaaSharpness = sharpnessBefore;
            OptimumTemporal.Frame.JitterActive = false;
            typeof(SystemRenderOITLayers).GetField("revealTextureId", HiddenStatic)!.SetValue(null, oitRevealBefore);
            typeof(SystemRenderOITLayers).GetField("accumTextureId", HiddenStatic)!.SetValue(null, oitAccumBefore);
            ScreenManager.Platform = previousPlatform!;
            Platform.ShutdownGraphics();
            try
            {
                Directory.Delete(dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        // ------------------------------------------------------------ per-frame state

        /// <summary>
        /// The state the AfterOIT stage leaves for the merge, and the inputs both routes read:
        /// Primary cleared to the seeds, the Transparent target's OIT attachments cleared, the
        /// world draw-buffer mask, and the OIT textures on the units the OIT renderer uses.
        /// </summary>
        public void SeedFrame()
        {
            VulkanDevice seam = Seam;

            seam.BindFramebuffer(Transparent.FboId);
            seam.SetDrawBuffers(Transparent.FboId, 0b111001);
            seam.ClearColor(0, 0.6f, 0.45f, 0.3f, 1f);
            seam.ClearColor(3, 0.30f, 0.10f, 0.05f, 0.5f);
            seam.ClearColor(4, 0.10f, 0.25f, 0.05f, 0.35f);
            seam.ClearColor(5, 0.05f, 0.10f, 0.30f, 0.2f);

            seam.BindFramebuffer(Primary.FboId);
            seam.SetDrawBuffers(Primary.FboId, 0b111);
            seam.ClearColor(0, 0.25f, 0.5f, 0.75f, 1f);
            seam.ClearColor(1, 0.125f, 0.25f, 0.375f, 1f);
            seam.ClearColor(2, 0f, 0f, 0.125f, 0.5f);
            seam.ClearDepth(1f);
            // Primary's default colour set: two attachments without the SSAO G-buffer.
            seam.SetDrawBuffers(Primary.FboId, 0b011);

            seam.SetViewport(0, 0, Size, Size);
            seam.SetBlend(true, EnumBlendMode.Standard);
            seam.SetDepthTest(false);
            seam.SetDepthMask(true);
            seam.SetCullFace(false);

            // The units SystemRenderOITLayers points the merge's two OIT samplers at.
            ShaderProgramTransparentcompose compose = ShaderPrograms.Transparentcompose;
            seam.SetSamplerUnit(compose.ProgramId, "OITreveal", 6);
            seam.SetSamplerUnit(compose.ProgramId, "OITaccumulation", 7);
            seam.BindTexture(6, oitReveal);
            seam.BindTexture(7, oitAccumulation);
        }

        /// <summary>TAA on, with the jitter window open or closed, and no sharpen pass.</summary>
        public void EnableTaa(bool jitterActive)
        {
            OptimumConfig.Taa = true;
            OptimumConfig.TaaSharpness = 0f;
            OptimumTemporalFrame frame = OptimumTemporal.Frame;
            frame.JitterSequencePx.X = 0.25f;
            frame.JitterSequencePx.Y = -0.375f;
            frame.JitterActive = jitterActive;
            AdvanceTemporalFrame();
            AdvanceTemporalFrame();
            frame.JitterActive = jitterActive;
        }

        /// <summary>
        /// One frame of the temporal contract: the camera and the projection captured, so the
        /// sky-motion and resolve passes have a previous view to reproject through.
        /// </summary>
        public void AdvanceTemporalFrame()
        {
            OptimumTemporalFrame frame = OptimumTemporal.Frame;
            bool jitter = frame.JitterActive;
            frame.Advance(16f, Size, Size, 1f, 0.1f, 100f, 70f, uniforms);
            double[] projection = Mat4d.Perspective(Mat4d.Create(), 70.0 * Math.PI / 180.0, 1.0, 0.1, 100.0);
            double[] view = Mat4d.Identity(Mat4d.Create());
            frame.RecordProjection(EnumTemporalView.World, projection);
            frame.CaptureCamera(view, view);
            frame.JitterActive = jitter;
        }

        // ---------------------------------------------------------------- readback

        public byte[] ReadScene() => ReadAttachmentZero(Primary.FboId);

        public byte[] ReadGlow() => Decode(Primary.ColorTextureIds[1], motion: false);

        public byte[] ReadMotion() => Decode(Primary.ColorTextureIds[2], motion: true);

        private unsafe byte[] ReadAttachmentZero(int framebufferId)
        {
            var pixels = new byte[Size * Size * 4];
            fixed (byte* destination = pixels)
            {
                Seam.BindFramebuffer(framebufferId);
                Seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
            }
            return pixels;
        }

        /// <summary>
        /// Any attachment through an RGBA8 copy, because the seam's readback is four bytes per
        /// pixel from attachment 0. The motion mode encodes the vector into the two low
        /// channels so a difference in it cannot hide behind a clamp.
        /// </summary>
        private unsafe byte[] Decode(int textureId, bool motion)
        {
            VulkanDevice seam = Seam;
            seam.BindFramebuffer(decodeFramebuffer);
            seam.ClearColor(0, 0f, 0f, 0f, 1f);
            seam.UseProgram(decodeProgram);
            seam.SetSamplerUnit(decodeProgram, "source", 15);
            seam.BindTexture(15, textureId);
            SetInt(seam, decodeProgram, "motionMode", motion ? 1 : 0);
            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(false);
            seam.SetDepthMask(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.DrawFullscreenTriangle();
            return ReadAttachmentZero(decodeFramebuffer);
        }

        private static void SetInt(VulkanDevice seam, int program, string name, int value)
        {
            int location = seam.GetUniformLocation(program, name);
            if (location >= 0) seam.SetUniform(program, location, value);
        }

        // ------------------------------------------------------------------- fixture

        private void BuildTargets()
        {
            VulkanDevice seam = Seam;

            Primary = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[]
                {
                    Texture(EnumTextureInternalFormat.Rgba8),
                    Texture(EnumTextureInternalFormat.Rgba8),
                    Texture(EnumTextureInternalFormat.Rgba16f),
                },
                DepthTextureId = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.DepthComponent32,
                    EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false),
            };
            for (int slot = 0; slot < 3; slot++)
            {
                seam.AttachTexture(Primary.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                    Primary.ColorTextureIds[slot], 0);
            }
            seam.AttachTexture(Primary.FboId, EnumFramebufferAttachment.DepthAttachment, Primary.DepthTextureId, 0);
            Assert.True(seam.CheckFramebufferComplete(Primary.FboId, out string primaryStatus), primaryStatus);

            // The Transparent target as the client leaves it once the OIT renderer has replaced
            // attachment 0 with its reveal target and attached the accumulation array's three
            // layers at 3, 4 and 5. ColorTextureIds keeps the vanilla ids, which is what the
            // merge binds as accumulation, revealage and in-glow.
            oitReveal = Texture(EnumTextureInternalFormat.Rgba8);
            oitAccumulation = seam.CreateTexture2DArray(Size, Size, 3,
                EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba);
            Transparent = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[]
                {
                    Seeded(0.7f),
                    Seeded(0.35f),
                    Seeded(0.2f),
                },
            };
            seam.AttachTexture(Transparent.FboId, EnumFramebufferAttachment.ColorAttachment0, oitReveal, 0);
            seam.AttachTexture(Transparent.FboId, EnumFramebufferAttachment.ColorAttachment1,
                Transparent.ColorTextureIds[1], 0);
            seam.AttachTexture(Transparent.FboId, EnumFramebufferAttachment.ColorAttachment2,
                Transparent.ColorTextureIds[2], 0);
            seam.AttachTexture(Transparent.FboId, EnumFramebufferAttachment.ColorAttachment3, oitAccumulation, 0);
            seam.AttachTexture(Transparent.FboId, EnumFramebufferAttachment.ColorAttachment4, oitAccumulation, 1);
            seam.AttachTexture(Transparent.FboId, (EnumFramebufferAttachment)36069, oitAccumulation, 2);
            Assert.True(seam.CheckFramebufferComplete(Transparent.FboId, out string status), status);

            for (int i = 0; i <= 24; i++) buffers.Add(null!);
            buffers[0] = Primary;
            buffers[1] = Transparent;
            buffers[10] = SingleTarget(EnumTextureInternalFormat.Rgba8);
            buffers[19] = HistoryTarget();
            buffers[20] = HistoryTarget();

            decodeTarget = Texture(EnumTextureInternalFormat.Rgba8);
            decodeFramebuffer = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(decodeFramebuffer, EnumFramebufferAttachment.ColorAttachment0, decodeTarget, 0);
            seam.SetDrawBuffers(decodeFramebuffer, 0b1);
        }

        private int Texture(EnumTextureInternalFormat format) =>
            Seam.CreateTexture2D(Size, Size, format, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

        /// <summary>A texture with a per-pixel pattern around <paramref name="level" />, so a difference shows.</summary>
        private unsafe int Seeded(float level)
        {
            var pixels = new byte[Size * Size * 4];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = (y * Size + x) * 4;
                    pixels[i] = (byte)Math.Clamp(level * 255f + x * 3, 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(level * 255f + y * 5, 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp(level * 255f + ((x ^ y) & 7) * 9, 0, 255);
                    pixels[i + 3] = (byte)Math.Clamp(level * 255f, 0, 255);
                }
            }
            fixed (byte* data = pixels)
            {
                return Seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, (IntPtr)data, false);
            }
        }

        private FrameBufferRef SingleTarget(EnumTextureInternalFormat format)
        {
            var target = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = Seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[] { Texture(format) },
            };
            Seam.AttachTexture(target.FboId, EnumFramebufferAttachment.ColorAttachment0, target.ColorTextureIds[0], 0);
            Seam.SetDrawBuffers(target.FboId, 0b1);
            return target;
        }

        /// <summary>A TAA history slot: colour, aux and the linear-depth R32F, as the platform builds them.</summary>
        private FrameBufferRef HistoryTarget()
        {
            var target = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = Seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[]
                {
                    Texture(EnumTextureInternalFormat.Rgba16f),
                    Texture(EnumTextureInternalFormat.Rgba8),
                    Seam.CreateTexture2DRaw(Size, Size, 0x822E, IntPtr.Zero, 0),
                },
            };
            for (int slot = 0; slot < 3; slot++)
            {
                Seam.AttachTexture(target.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                    target.ColorTextureIds[slot], 0);
            }
            Seam.SetDrawBuffers(target.FboId, 0b111);
            return target;
        }

        private void LinkPrograms()
        {
            VulkanDevice seam = Seam;
            ShaderCorpus.ShaderVariant variant = TaaVariant();

            var compose = new ShaderProgramTransparentcompose { PassName = "transparentcompose" };
            Link(seam, compose, "transparentcompose", variant, Array.Empty<string>());
            var skyMotion = new ShaderProgram { PassName = "taa-skymotion" };
            Link(seam, skyMotion, "taa-skymotion", variant, new[]
            {
                "taaRenderSize", "taaJitterPx", "taaInvViewProjJittered", "taaPrevViewProj", "taaCloudReactive",
            });
            var resolve = new ShaderProgram { PassName = "taa-resolve" };
            Link(seam, resolve, "taa-resolve", variant, new[]
            {
                "renderSize", "jitterPx", "invViewProjJittered", "prevViewProj", "viewMatrix",
                "cameraDelta", "resetHistory", "blendAlpha", "varianceGamma",
            });
            var blit = new ShaderProgramBlit { PassName = "blit" };
            Link(seam, blit, "blit", variant, Array.Empty<string>());

            ShaderPrograms.Transparentcompose = compose;
            ShaderPrograms.TaaSkyMotion = skyMotion;
            ShaderPrograms.TaaResolve = resolve;
            ShaderPrograms.Blit = blit;

            decodeProgram = LinkDecode(seam);
        }

        /// <summary>TAA on without the SSAO G-buffer: the motion attachment at colour 2.</summary>
        internal static ShaderCorpus.ShaderVariant TaaVariant()
        {
            foreach (ShaderCorpus.ShaderVariant candidate in ShaderCorpus.Variants())
            {
                if (candidate.Name == "taa-no-ssao") return candidate;
            }
            throw new InvalidOperationException("the corpus has no taa-no-ssao variant");
        }

        private static void Link(VulkanDevice seam, ShaderProgramBase program, string name,
            ShaderCorpus.ShaderVariant variant, string[] uniforms)
        {
            List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(
                name, ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), variant);

            var linked = new LinkedProgram { PassName = name };
            foreach (ShaderStageSource stage in stages)
            {
                var shader = new LinkedShader
                {
                    Type = stage.Stage,
                    Code = stage.Code,
                    PrefixCode = stage.PrefixCode,
                };
                Assert.True(seam.CompileShader(shader));
                if (stage.Stage == EnumShaderType.VertexShader) linked.VertexShader = shader;
                else if (stage.Stage == EnumShaderType.FragmentShader) linked.FragmentShader = shader;
            }

            int id = seam.LinkProgram(linked);
            Assert.True(id > 0, seam.GetError() ?? "link failed");
            program.ProgramId = id;
            foreach (string uniform in uniforms)
            {
                int location = seam.GetUniformLocation(id, uniform);
                Assert.True(location != -1, name + " has no location for " + uniform);
                program.uniformLocations[uniform] = location;
            }
        }

        /// <summary>The readback helper's own program: any attachment into RGBA8.</summary>
        private static int LinkDecode(VulkanDevice seam)
        {
            const string vertex = @"#version 330 core
out vec2 uv;
void main(void)
{
	vec2 position = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	uv = position;
	gl_Position = vec4(position * 2.0 - 1.0, 0.0, 1.0);
}
";
            const string fragment = @"#version 330 core
uniform sampler2D source;
uniform int motionMode;
layout(location = 0) out vec4 outColor;
void main(void)
{
	vec4 texel = texelFetch(source, ivec2(gl_FragCoord.xy), 0);
	if (motionMode != 0) {
		outColor = vec4(
			clamp(texel.r / 32.0 * 0.5 + 0.5, 0.0, 1.0),
			clamp(texel.g / 32.0 * 0.5 + 0.5, 0.0, 1.0),
			clamp(texel.b, 0.0, 1.0),
			clamp(texel.a, 0.0, 1.0));
		return;
	}
	outColor = clamp(texel, 0.0, 1.0);
}
";
            var linked = new LinkedProgram { PassName = "native-post-decode" };
            var vertexShader = new LinkedShader { Type = EnumShaderType.VertexShader, Code = vertex, PrefixCode = "" };
            var fragmentShader = new LinkedShader { Type = EnumShaderType.FragmentShader, Code = fragment, PrefixCode = "" };
            Assert.True(seam.CompileShader(vertexShader), seam.GetError() ?? "decode vertex shader");
            Assert.True(seam.CompileShader(fragmentShader), seam.GetError() ?? "decode fragment shader");
            linked.VertexShader = vertexShader;
            linked.FragmentShader = fragmentShader;
            int id = seam.LinkProgram(linked);
            Assert.True(id > 0, seam.GetError() ?? "decode link failed");
            return id;
        }

        /// <summary>The platform's frame buffers, motion attachment and TAA readiness, as the setup leaves them.</summary>
        private void InstallState()
        {
            typeof(ClientPlatformWindows).GetField("frameBuffers", Hidden)!.SetValue(Platform, buffers);
            typeof(ClientPlatformWindows).GetField("ssaaLevel", Hidden)!.SetValue(Platform, 1f);
            Platform.SetOptimumMotionAttachmentIndex(2);
            typeof(ClientPlatformWindows).GetField("optimumTaaTargetsReady", Hidden)!.SetValue(Platform, true);

            FieldInfo reveal = typeof(SystemRenderOITLayers).GetField("revealTextureId", HiddenStatic)!;
            FieldInfo accumulation = typeof(SystemRenderOITLayers).GetField("accumTextureId", HiddenStatic)!;
            oitRevealBefore = reveal.GetValue(null);
            oitAccumBefore = accumulation.GetValue(null);
            reveal.SetValue(null, oitReveal);
            accumulation.SetValue(null, oitAccumulation);

            // The seeds the comparisons quote, read once through the same decode the tests use.
            Platform.BeginFrame();
            SeedFrame();
            SceneSeed = ReadScene();
            MotionSeed = ReadMotion();
            Platform.EndFrame();
        }
    }

    // ---------------------------------------------------------------- native shaders

    /// <summary>The manifest of the programs the chain's two native passes and the resolve use.</summary>
    private static readonly Lazy<(string Directory, string Reason)> NativeManifest = new(BuildNativeShaders);

    private static (string, string) BuildNativeShaders()
    {
        if (!NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason)) return ("", reason);
        using (compiler)
        {
            var builder = new NativeShaderBuilder(compiler!);
            var merged = new NativeShaderBuildResult();
            merged.Manifest.Toolchain = compiler!.Identity;
            string source = Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders-vk");
            foreach (string program in Programs)
            {
                NativeShaderBuildResult one = builder.Build(source, program);
                merged.Errors.AddRange(one.Errors);
                merged.Manifest.Programs.AddRange(one.Manifest.Programs);
                foreach ((string file, byte[] bytes) in one.Files) merged.Files[file] = bytes;
            }
            if (!merged.Success) return ("", string.Join("\n", merged.Errors));

            string root = Path.Combine(Path.GetTempPath(), "optimum-native-post-shaders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            NativeShaderBuilder.Write(merged, root);
            return (Path.Combine(root, NativeShaderManifest.DirectoryName), "");
        }
    }
}
