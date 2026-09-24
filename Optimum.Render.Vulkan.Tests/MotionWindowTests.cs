using System;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;
using static Optimum.Render.Vulkan.Tests.MotionFixture;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 2 contract C4 on a real device, once per colour write tier (forced the
/// way OPTIMUM_VULKAN_COLOR_WRITE_TIER forces it, through the context options):
/// draw buffers and the TAA motion windows are write masks inside one rendering
/// scope, never scope restarts. Every test reads back only after the draws, in
/// the frame, and asserts bytes rather than approximations.
/// </summary>
public class MotionWindowTests
{
    private readonly ITestOutputHelper _output;

    public MotionWindowTests(ITestOutputHelper output) => _output = output;

    private const int Size = 8;

    private const string FullscreenVertex = """
        #version 330 core
        out vec2 uv;
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
            uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
        }
        """;

    private static string FourOutputs(string colour, string glow, string motion, string extra) => $$"""
        #version 330 core
        layout(location = 0) out vec4 outColor;
        layout(location = 1) out vec4 outGlow;
        layout(location = 2) out vec4 outMotion;
        layout(location = 3) out vec4 outExtra;
        void main(void)
        {
            outColor = vec4({{colour}});
            outGlow = vec4({{glow}});
            outMotion = vec4({{motion}});
            outExtra = vec4({{extra}});
        }
        """;

    /// <summary>The OPTIMUM_VULKAN_COLOR_WRITE_TIER tokens.</summary>
    public static TheoryData<string> Tiers => new() { "enable", "mask", "pipeline" };

    /// <summary>Every tier with the frame graph on and off (OPTIMUM_VULKAN_FRAMEGRAPH).</summary>
    public static TheoryData<string, bool> TiersWithFrameGraph => new()
    {
        { "enable", true }, { "mask", true }, { "pipeline", true },
        { "enable", false }, { "mask", false }, { "pipeline", false },
    };

    private static ColorWriteTier Tier(string token) =>
        DeviceCaps.ParseColorWriteTier(token) ?? throw new ArgumentException("unknown tier " + token);

    private bool TryCreateDevice(ColorWriteTier tier, out VulkanDevice? device)
    {
        VulkanDevice created = GpuTest.NewDevice();
        Action<VulkanContextOptions>? suite = created.ConfigureContextOptions;
        created.ConfigureContextOptions = options =>
        {
            suite?.Invoke(options);
            options.ColorWriteTier = tier;
        };

        if (!created.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            _output.WriteLine("Vulkan unavailable: " + failureReason);
            created.Dispose();
            device = null;
            return false;
        }
        if (created.ColorWriteTierForTests != tier)
        {
            _output.WriteLine("device lacks tier " + tier + "; selected " + created.ColorWriteTierForTests);
            created.Dispose();
            device = null;
            return false;
        }
        device = created;
        return true;
    }

    private sealed class Scene
    {
        public int Framebuffer;
        public int Colour;
        public int Glow;
        public int Motion;
        public int Extra;
    }

    /// <summary>Colour and glow RGBA8, motion RGBA16F, a fourth RGBA8 slot: Primary's shape with TAA on.</summary>
    private static Scene CreateScene(VulkanDevice seam)
    {
        var scene = new Scene
        {
            Colour = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
            Glow = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
            Motion = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
            Extra = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
        };
        scene.Framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(scene.Framebuffer, EnumFramebufferAttachment.ColorAttachment0, scene.Colour, 0);
        seam.AttachTexture(scene.Framebuffer, EnumFramebufferAttachment.ColorAttachment1, scene.Glow, 0);
        seam.AttachTexture(scene.Framebuffer, EnumFramebufferAttachment.ColorAttachment2, scene.Motion, 0);
        seam.AttachTexture(scene.Framebuffer, EnumFramebufferAttachment.ColorAttachment3, scene.Extra, 0);
        Assert.True(seam.CheckFramebufferComplete(scene.Framebuffer, out string status), status);
        return scene;
    }

    private static void BaseState(VulkanDevice seam)
    {
        seam.SetViewport(0, 0, Size, Size);
        seam.SetScissorEnabled(false);
        seam.SetDepthTest(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.SetColorMask(true, true, true, true);
    }

    /// <summary>A frame that clears every attachment to a known value, with every draw buffer selected.</summary>
    private static void SeedFrame(VulkanDevice seam, Scene scene)
    {
        seam.BeginFrame();
        seam.BindFramebuffer(scene.Framebuffer);
        BaseState(seam);
        seam.SetDrawBuffers(scene.Framebuffer, 0b1111);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.ClearColor(1, 0.2f, 0.4f, 0.6f, 1f);
        seam.ClearColor(2, 0.125f, 0.25f, 0.375f, 0.5f);
        seam.ClearColor(3, 0.4f, 0.6f, 0.8f, 1f);
        seam.Present();
    }

    private static void AssertEveryPixel(byte[] texels, int bytesPerPixel, byte[] expected, string what)
    {
        Assert.Equal(Size * Size * bytesPerPixel, texels.Length);
        for (int p = 0; p < texels.Length; p += bytesPerPixel)
        {
            for (int c = 0; c < bytesPerPixel; c++)
            {
                if (texels[p + c] != expected[c])
                {
                    Assert.Fail($"{what}: pixel {p / bytesPerPixel} byte {c} is {texels[p + c]}, expected {expected[c]}");
                }
            }
        }
    }

    private static byte[] Half(float r, float g, float b, float a)
    {
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 2), (Half)r);
        BitConverter.TryWriteBytes(bytes.AsSpan(2, 2), (Half)g);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 2), (Half)b);
        BitConverter.TryWriteBytes(bytes.AsSpan(6, 2), (Half)a);
        return bytes;
    }

    /// <summary>
    /// A pass over Primary with the default colour set, a motion window (all
    /// three), a motion-only window, a restore, a masked-out clear and an
    /// all-false colour-mask clear. The fourth slot never has its draw buffer on
    /// in the pass, though every program writes it: bit-identical to the previous
    /// frame's seed. The motion-only draw writes exactly the motion attachment.
    /// All of it in one rendering scope; no restart from a mask change.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Tiers))]
    public void MotionWindowsAreWriteMasksInsideOneScope(string tierToken)
    {
        ColorWriteTier tier = Tier(tierToken);
        Skip.IfNot(TryCreateDevice(tier, out VulkanDevice? device), "Vulkan or tier " + tier + " unavailable.");
        using (device)
        {
            VulkanDevice seam = device!;
            int opaque = GpuTest.LinkProgram(seam, FullscreenVertex,
                FourOutputs("1.0, 0.0, 0.0, 1.0", "0.0, 1.0, 0.0, 1.0", "0.75, 0.75, 0.75, 0.75", "1.0, 1.0, 1.0, 1.0"),
                "mw-opaque");
            int motionOnly = GpuTest.LinkProgram(seam, FullscreenVertex,
                FourOutputs("0.0, 0.0, 1.0, 1.0", "0.0, 0.0, 1.0, 1.0", "1.0, 0.5, 0.0, 1.0", "0.0, 0.0, 0.0, 0.0"),
                "mw-motion-only");
            Scene scene = CreateScene(seam);
            SeedFrame(seam, scene);

            seam.BeginFrame();
            seam.BindFramebuffer(scene.Framebuffer);
            BaseState(seam);
            long scopesBefore = device!.ScopesOpenedForTests;

            // Default colour set: colour and glow; motion and extra masked.
            seam.SetDrawBuffers(scene.Framebuffer, 0b0011);
            seam.UseProgram(opaque);
            seam.DrawFullscreenTriangle();

            // Motion window (EnableMotionDrawBuffers), then the motion-only window.
            seam.SetDrawBuffers(scene.Framebuffer, 0b0111);
            seam.DrawFullscreenTriangle();
            seam.SetDrawBuffers(scene.Framebuffer, 0b0100);
            seam.UseProgram(motionOnly);
            seam.DrawFullscreenTriangle();

            // Restore, then two clears that must not land: motion masked out, and
            // colour through an all-false glColorMask.
            seam.SetDrawBuffers(scene.Framebuffer, 0b0011);
            seam.ClearColor(2, 0f, 0f, 0f, 0f);
            seam.SetColorMask(false, false, false, false);
            seam.ClearColor(0, 0f, 0f, 1f, 1f);
            seam.SetColorMask(true, true, true, true);
            seam.UseProgram(opaque);
            seam.SetDrawBuffers(scene.Framebuffer, 0b0001);
            seam.DrawFullscreenTriangle();

            long scopes = device.ScopesOpenedForTests - scopesBefore;
            long maskRestarts = device.MaskRestartsForTests;
            long splits = device.FeedbackSplitsForTests;

            byte[] colour = device.ReadBackLevel0ForTests(scene.Colour);
            byte[] glow = device.ReadBackLevel0ForTests(scene.Glow);
            byte[] motion = device.ReadBackLevel0ForTests(scene.Motion);
            byte[] extra = device.ReadBackLevel0ForTests(scene.Extra);
            seam.Present();

            _output.WriteLine($"tier={tier} scopes={scopes} mask_restarts={maskRestarts} feedback_splits={splits}");
            Assert.Equal(1, scopes);
            Assert.Equal(0, maskRestarts);
            Assert.Equal(0, splits);

            AssertEveryPixel(colour, 4, new byte[] { 255, 0, 0, 255 }, "colour");
            AssertEveryPixel(glow, 4, new byte[] { 0, 255, 0, 255 }, "glow");
            AssertEveryPixel(motion, 8, Half(1f, 0.5f, 0f, 1f), "motion (motion-only window wrote it last)");
            AssertEveryPixel(extra, 4, new byte[] { 102, 153, 204, 255 }, "extra (draw buffer never on)");

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// The OIT merge: standard blending on colour, additive (ONE, ONE) on the
    /// motion attachment (ApplyOptimumMotionAccumulateBlendState), a program that
    /// adds only to motion's blue. Red, green and alpha of the RGBA16F motion
    /// texels stay bit-exact; glow, which the program does not write, is untouched.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Tiers))]
    public void OitMergeAdditiveOnMotionLeavesRedGreenAndAlphaBitExact(string tierToken)
    {
        ColorWriteTier tier = Tier(tierToken);
        Skip.IfNot(TryCreateDevice(tier, out VulkanDevice? device), "Vulkan or tier " + tier + " unavailable.");
        using (device)
        {
            VulkanDevice seam = device!;
            int merge = GpuTest.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outColor;
                layout(location = 2) out vec4 outMotion;
                void main(void)
                {
                    outColor = vec4(1.0, 1.0, 1.0, 0.5);
                    outMotion = vec4(0.0, 0.0, 0.25, 0.0);
                }
                """, "mw-oit-merge");
            Scene scene = CreateScene(seam);
            SeedFrame(seam, scene);

            seam.BeginFrame();
            seam.BindFramebuffer(scene.Framebuffer);
            BaseState(seam);
            seam.SetDrawBuffers(scene.Framebuffer, 0b0111);
            seam.UseProgram(merge);
            // ApplyTransparentMergeBlendState, then the motion accumulate override.
            seam.SetBlend(true, EnumBlendMode.Standard);
            seam.SetBlendFuncSeparate(0, 770, 771, 770, 771);
            seam.SetBlendEquation(2, 32774);
            seam.SetBlendFuncSeparate(2, 1, 1, 1, 1);
            seam.DrawFullscreenTriangle();

            // The accumulate override must not outlive the draw: a second draw with
            // replace blending on motion keeps the scope and rewrites blue only.
            seam.SetBlendFuncSeparate(2, 1, 0, 1, 0);
            seam.SetDrawBuffers(scene.Framebuffer, 0b0001);
            seam.DrawFullscreenTriangle();

            long maskRestarts = device!.MaskRestartsForTests;
            byte[] colour = device.ReadBackLevel0ForTests(scene.Colour);
            byte[] glow = device.ReadBackLevel0ForTests(scene.Glow);
            byte[] motion = device.ReadBackLevel0ForTests(scene.Motion);
            seam.Present();

            Assert.Equal(0, maskRestarts);
            byte[] seed = Half(0.125f, 0.25f, 0.375f, 0.5f);
            byte[] merged = Half(0.125f, 0.25f, 0.625f, 0.5f);
            for (int p = 0; p < motion.Length; p += 8)
            {
                Assert.Equal(seed.AsSpan(0, 4).ToArray(), motion.AsSpan(p, 4).ToArray());   // red, green
                Assert.Equal(seed.AsSpan(6, 2).ToArray(), motion.AsSpan(p + 6, 2).ToArray()); // alpha
            }
            AssertEveryPixel(motion, 8, merged, "motion after additive merge");
            AssertEveryPixel(glow, 4, new byte[] { 51, 102, 153, 255 }, "glow (not written by the merge)");

            // Colour: two draws of (1,1,1,0.5) over black with SRC_ALPHA blending.
            Assert.InRange(colour[0], 189, 193);
            Assert.Equal(colour[0], colour[1]);

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// The final composition shape: draw buffers select Primary 0 only while the
    /// program samples Primary 1. The sampled slot leaves the draw's pass, colour receives
    /// glow's texels exactly, glow keeps them; selecting glow again lets it rejoin and
    /// a write lands. Validation stays clean. The stated draw declares its pass without the
    /// sampled slot before any scope opens, so nothing splits on either path.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(TiersWithFrameGraph))]
    public void CompositionSamplesAnAttachmentItsDrawBuffersExclude(string tierToken, bool frameGraph)
    {
        ColorWriteTier tier = Tier(tierToken);
        Skip.IfNot(TryCreateDevice(tier, out VulkanDevice? device), "Vulkan or tier " + tier + " unavailable.");
        using (device)
        {
            VulkanDevice seam = device!;
            seam.FrameGraphEnabled = frameGraph;
            int compose = GpuTest.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform sampler2D glowTex;
                in vec2 uv;
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = texture(glowTex, uv); }
                """, "mw-compose");
            int writeGlow = GpuTest.LinkProgram(seam, FullscreenVertex,
                FourOutputs("0.0, 0.0, 0.0, 1.0", "1.0, 0.0, 0.0, 1.0", "0.0, 0.0, 0.0, 0.0", "0.0, 0.0, 0.0, 0.0"),
                "mw-write-glow");
            Scene scene = CreateScene(seam);
            SeedFrame(seam, scene);

            seam.BeginFrame();
            seam.BindFramebuffer(scene.Framebuffer);
            BaseState(seam);
            seam.SetDrawBuffers(scene.Framebuffer, 0b0001);
            seam.ClearColor(0, 0f, 0f, 0f, 1f); // inference: opens the scope with glow in it, masked
            seam.UseProgram(compose);
            seam.SetSamplerUnit(compose, "glowTex", 0);
            seam.BindTexture(0, scene.Glow);
            seam.DrawFullscreenTriangle();
            byte[] composed = device!.ReadBackLevel0ForTests(scene.Colour);
            byte[] glowAfterCompose = device.ReadBackLevel0ForTests(scene.Glow);
            long splitsAfterCompose = device.FeedbackSplitsForTests;

            seam.BindTexture(0, 0);
            seam.BindFramebuffer(scene.Framebuffer);
            seam.SetDrawBuffers(scene.Framebuffer, 0b0010);
            seam.UseProgram(writeGlow);
            seam.DrawFullscreenTriangle();
            long maskRestarts = device.MaskRestartsForTests;
            byte[] glowAfterWrite = device.ReadBackLevel0ForTests(scene.Glow);
            seam.Present();

            _output.WriteLine($"tier={tier} frameGraph={frameGraph} splits_after_compose={splitsAfterCompose} mask_restarts={maskRestarts}");
            Assert.Equal(0, splitsAfterCompose);
            Assert.Equal(0, maskRestarts);
            AssertEveryPixel(composed, 4, new byte[] { 51, 102, 153, 255 }, "colour = sampled glow");
            AssertEveryPixel(glowAfterCompose, 4, new byte[] { 51, 102, 153, 255 }, "glow untouched by composition");
            AssertEveryPixel(glowAfterWrite, 4, new byte[] { 255, 0, 0, 255 }, "glow written after it rejoined");

            GpuTest.AssertClean(seam);
        }
    }
}

/// <summary>GPU contract for the terrain shaders' TAA motion attachment.</summary>
public sealed class TerrainMotionContractTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    [SkippableTheory]
    [InlineData("chunkopaque")]
    [InlineData("chunktopsoil")]
    public void TerrainWritesPreviousMinusCurrentPixelsAndDepth(string shader)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!, shader);

            // Identity projections map one NDC unit to half the render width.
            // Both axes and both signs matter: a magnitude-only check misses
            // flipped or swapped motion vectors.
            Check(scene.Draw(0, 0, 0), 0, 0);
            Check(scene.Draw(0.25f, 0, 0), 8, 0);
            Check(scene.Draw(0, -0.125f, 0), 0, -4);
            Check(scene.Draw(-0.1875f, 0.0625f, 0), -6, 2);

            GpuTest.AssertClean(device!);
        }
    }

    [SkippableTheory]
    [InlineData("chunkopaque")]
    [InlineData("chunktopsoil")]
    public void PreviousWarpMovesTheVectorWithoutWritingUncoveredPixels(string shader)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!, shader);
            float[] motion = scene.Draw(0, 0, 8);

            // Vertexwarp's phase is zero for this face. Compute its displacement
            // independently of the shader output, then convert NDC to pixels.
            double warp = (Math.Sin(0) + Math.Sin(0.5) + Math.Sin(1) / 3) / 30 * 8;
            float expectedX = (float)(warp * Size / 2);
            Assert.True(expectedX > 1);
            Check(motion, expectedX, 0);
            Assert.InRange(Pixel(motion, 2, 2, 3), 0, 0.001f);

            GpuTest.AssertClean(device!);
        }
    }

    private static void Check(float[] pixels, float expectedX, float expectedY)
    {
        Assert.InRange(Pixel(pixels, Size / 2, Size / 2, 0), expectedX - 0.05f, expectedX + 0.05f);
        Assert.InRange(Pixel(pixels, Size / 2, Size / 2, 1), expectedY - 0.05f, expectedY + 0.05f);
        Assert.InRange(Pixel(pixels, Size / 2, Size / 2, 3), 0.49f, 0.51f);
    }

    private static float Pixel(float[] pixels, int x, int y, int channel) =>
        pixels[(y * Size + x) * 4 + channel];

    private sealed class Scene
    {
        private readonly VulkanDevice device;
        private readonly int program;
        private readonly int framebuffer;
        private readonly int motion;
        private readonly int mesh;

        public Scene(VulkanDevice device, string shader)
        {
            this.device = device;
            var variant = ShaderCorpus.Variants().Single(v => v.Name == "taa-no-ssao");
            Assert.Equal(1, variant.TaaMotion);
            Assert.Equal(2, variant.TaaMotionLocation);
            var stages = ShaderCorpus.BuildProgram(shader, ShaderCorpus.LoadShaderFiles(),
                ShaderCorpus.LoadIncludes(), variant);
            program = LinkFromCorpus(device, stages, shader, oit: true);
            Assert.True(device.GetUniformLocation(program, "taaRenderSize") >= 0,
                shader + " has no motion writer");

            int unit = BindEveryDeclaredSampler(device, device, program);
            int atlas = CreateWhiteTexture(device);
            foreach (string sampler in new[] { "terrainTex", "terrainTexLinear" })
            {
                device.SetSamplerUnit(program, sampler, unit);
                device.BindTexture(unit++, atlas);
            }

            MotionTarget target = CreateMotionTarget(device, Size);
            framebuffer = target.Framebuffer;
            motion = target.MotionTexture;
            mesh = CreateFaceMesh(device);
        }

        public float[] Draw(float cameraX, float cameraY, float previousWarp)
        {
            device.BeginFrame();
            device.BindFramebuffer(framebuffer);
            device.ClearColor(0, 0, 0, 0, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);
            device.UseProgram(program);

            SetMatrix(device, program, "projectionMatrix", Identity);
            SetMatrix(device, program, "modelViewMatrix", Identity);
            SetMatrix(device, program, "prevProjectionMatrix", Identity);
            SetMatrix(device, program, "prevModelViewMatrix", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixFar", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixNear", Identity);
            SetFloat3(device, program, "cameraPosDelta", cameraX, cameraY, 0);
            SetFloat2(device, program, "taaRenderSize", Size, Size);
            SetFloat2(device, program, "taaJitterPx", 0, 0);
            SetViewUniforms();
            SetWarpUniforms(previousWarp);

            device.SetViewport(0, 0, Size, Size);
            device.SetDepthTest(true);
            device.SetDepthMask(true);
            device.SetDepthFunc(0x203); // GL_LEQUAL
            device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard);
            device.DrawMesh(mesh);

            float[] pixels = ReadMotion(device, motion, Size);
            device.Present();
            return pixels;
        }

        private void SetViewUniforms()
        {
            SetFloat(device, program, "viewDistance", 1024);
            SetFloat(device, program, "viewDistanceLod0", 1024);
            SetFloat(device, program, "alphaTest", 0.001f);
            SetFloat(device, program, "zNear", 0.1f);
            SetFloat(device, program, "zFar", 1024);
            SetFloat(device, program, "shadowRangeFar", 1024);
            SetFloat(device, program, "shadowRangeNear", 64);
            SetFloat(device, program, "shadowMapWidthInv", 1);
            SetFloat(device, program, "shadowMapHeightInv", 1);
            SetFloat2(device, program, "blockTextureSize", 1, 1);
            SetFloat3(device, program, "rgbaAmbientIn", 1, 1, 1);
            SetFloat2(device, program, "frameSize", Size, Size);
        }

        private void SetWarpUniforms(float previousWarp)
        {
            SetInt(device, program, "perceptionEffectId", 1);
            SetInt(device, program, "prevPerceptionEffectId", 1);
            SetFloat(device, program, "windWaveIntensity", 1);
            SetFloat(device, program, "waterWaveIntensity", 1);
            SetFloat(device, program, "prevWindWaveIntensity", 1);
            SetFloat(device, program, "prevWaterWaveIntensity", 1);
            SetFloat(device, program, "globalWarpIntensity", 0);
            SetFloat(device, program, "prevGlobalWarpIntensity", previousWarp);
        }

    }
}

/// <summary>Previous-object motion written by the actual standard shader.</summary>
public sealed class StandardMotionContractTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    private static float[] Translation(float x, float y, float z) =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        x, y, z, 1,
    ];

    // At view z = -1 the shear moves the raster position by exactly the
    // requested subpixel jitter; the previous projection remains unjittered.
    private static float[] Projection(float jitterX, float jitterY)
    {
        var projection = new float[16];
        projection[0] = projection[5] = 1;
        projection[8] = -2 * jitterX / Size;
        projection[9] = -2 * jitterY / Size;
        projection[10] = projection[11] = projection[14] = -1;
        return projection;
    }

    [SkippableFact]
    public void PreviousModelAndMissingHistoryProduceIndependentPixelVectors()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!);
            Check(scene.Draw(Identity), 0, 0, 0.5f, 0);
            Check(scene.Draw(Translation(0.25f, 0, 0)), 8, 0, 0.5f, 0);
            Check(scene.Draw(Translation(0, -0.125f, 0)), 0, -4, 0.5f, 0);
            Check(scene.Draw(Translation(-0.1875f, 0.0625f, 0)), -6, 2, 0.5f, 0);

            // A stale object transform must not leak into a draw with no history.
            Check(scene.Draw(Translation(-0.5f, 0.5f, 0), history: false,
                cameraX: 0.25f, cameraY: -0.125f), 8, -4, 0.5f, 1);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void WarpOptOutAndBehindCameraKeepTheirMotionContract()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!);
            double offset = (Math.Sin(0) + Math.Sin(0.5) + Math.Sin(1) / 3) / 30 * 8;
            float expected = (float)(offset * Size / 2);
            Assert.True(expected > 1);
            Check(scene.Draw(Identity, previousWarp: 8), expected, 0, 0.5f, 0);
            Check(scene.Draw(Identity, previousWarp: 8, noWarp: true), 0, 0, 0.5f, 0);

            float[] behindProjection =
            [
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -1,
                0, 0, 0, 0,
            ];
            Check(scene.Draw(Identity, previousView: Translation(0, 0, 1),
                previousProjection: behindProjection, reactive: 0.6f),
                0, 0, 0, 0.6f);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void MoverMotionExcludesProjectionJitterAndIgnoresStaleHistory()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!, perspective: true);
            Check(scene.Draw(Identity, noWarp: true, jitterX: 0.37f, jitterY: -0.24f),
                0, 0, 0.5f, 0);
            Check(scene.Draw(Identity, noWarp: true, jitterX: -0.5f, jitterY: 0.5f),
                0, 0, 0.5f, 0);
            Check(scene.Draw(Translation(0.25f, 0, 0), noWarp: true,
                jitterX: 0.5f, jitterY: -0.5f), 8, 0, 0.5f, 0);
            Check(scene.Draw(Translation(0, -0.125f, 0), noWarp: true,
                jitterX: -0.5f, jitterY: 0.5f), 0, -4, 0.5f, 0);

            float[] previous = Translation(-0.1875f, 0.0625f, 0);
            float[] unjittered = scene.Draw(previous, noWarp: true);
            float[] jittered = scene.Draw(previous, noWarp: true,
                jitterX: 0.31f, jitterY: 0.47f);
            Check(jittered, -6, 2, 0.5f, 0);
            int centre = ((Size / 2) * Size + Size / 2) * 4;
            Assert.InRange(jittered[centre], unjittered[centre] - 0.05f,
                unjittered[centre] + 0.05f);
            Assert.InRange(jittered[centre + 1], unjittered[centre + 1] - 0.05f,
                unjittered[centre + 1] + 0.05f);

            Check(scene.Draw(Translation(-0.5f, 0.5f, 0), history: false,
                noWarp: true, cameraX: 0.25f, cameraY: -0.125f,
                jitterX: 0.42f, jitterY: 0.13f), 8, -4, 0.5f, 1);
            GpuTest.AssertClean(device!);
        }
    }

    private static void Check(float[] pixels, float x, float y, float depth, float reactive)
    {
        int centre = ((Size / 2) * Size + Size / 2) * 4;
        Assert.InRange(pixels[centre], x - 0.05f, x + 0.05f);
        Assert.InRange(pixels[centre + 1], y - 0.05f, y + 0.05f);
        Assert.InRange(pixels[centre + 2], reactive - 0.01f, reactive + 0.01f);
        Assert.InRange(pixels[centre + 3], depth - 0.01f, depth + 0.01f);
    }

    private sealed class Scene
    {
        private readonly VulkanDevice device;
        private readonly int program;
        private readonly MotionTarget target;
        private readonly int mesh;
        private readonly bool perspective;

        public Scene(VulkanDevice device, bool perspective = false)
        {
            this.device = device;
            this.perspective = perspective;
            var variant = new ShaderCorpus.ShaderVariant
            {
                Name = "taa-standard",
                TaaMotion = 1,
                TaaMotionLocation = 2,
            };
            var stages = ShaderCorpus.BuildProgram("standard", ShaderCorpus.LoadShaderFiles(),
                ShaderCorpus.LoadIncludes(), variant);
            program = LinkFromCorpus(device, stages, "standard", oit: false);
            foreach (string required in new[] { "taaRenderSize", "taaHistoryValid", "prevModelMatrix" })
                Assert.True(device.GetUniformLocation(program, required) >= 0, required + " is missing");
            BindEveryDeclaredSampler(device, device, program);
            target = CreateMotionTarget(device, Size);
            mesh = CreateFaceMesh(device, perspective ? -1 : 0);
        }

        public float[] Draw(float[] previousModel, bool history = true, bool noWarp = false,
            float cameraX = 0, float cameraY = 0, float previousWarp = 0,
            float[]? previousView = null, float[]? previousProjection = null, float reactive = 0,
            float jitterX = 0, float jitterY = 0)
        {
            device.BeginFrame();
            device.BindFramebuffer(target.Framebuffer);
            device.ClearColor(0, 0, 0, 0, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);
            device.UseProgram(program);

            SetMatrix(device, program, "projectionMatrix",
                perspective ? Projection(jitterX, jitterY) : Identity);
            SetMatrix(device, program, "viewMatrix", Identity);
            SetMatrix(device, program, "modelMatrix", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixFar", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixNear", Identity);
            SetMatrix(device, program, "prevProjectionMatrix",
                previousProjection ?? (perspective ? Projection(0, 0) : Identity));
            SetMatrix(device, program, "prevViewMatrix", previousView ?? Identity);
            SetMatrix(device, program, "prevModelMatrix", previousModel);
            SetInt(device, program, "taaHistoryValid", history ? 1 : 0);
            SetFloat(device, program, "taaReactive", history ? reactive : 1);
            SetFloat3(device, program, "cameraPosDelta", cameraX, cameraY, 0);
            SetFloat2(device, program, "taaRenderSize", Size, Size);
            SetFloat2(device, program, "taaJitterPx", jitterX, jitterY);
            SetInt(device, program, "dontWarpVertices", noWarp ? 1 : 0);
            SetFloat(device, program, "alphaTest", -1);
            SetFloat(device, program, "viewDistance", 1024);
            SetFloat(device, program, "viewDistanceLod0", 1024);
            SetFloat(device, program, "zNear", 0.1f);
            SetFloat(device, program, "zFar", 1024);
            SetFloat(device, program, "shadowRangeFar", 1024);
            SetFloat(device, program, "shadowRangeNear", 64);
            SetFloat(device, program, "shadowMapWidthInv", 1);
            SetFloat(device, program, "shadowMapHeightInv", 1);
            SetFloat3(device, program, "rgbaAmbientIn", 1, 1, 1);
            SetFloat4(device, program, "rgbaLightIn", 1, 1, 1, 1);
            SetFloat4(device, program, "rgbaFogIn", 1, 1, 1, 1);
            SetFloat4(device, program, "rgbaTint", 1, 1, 1, 1);
            SetFloat4(device, program, "averageColor", 1, 1, 1, 1);
            SetFloat2(device, program, "frameSize", Size, Size);
            SetInt(device, program, "perceptionEffectId", 1);
            SetInt(device, program, "prevPerceptionEffectId", 1);
            SetFloat(device, program, "windWaveIntensity", 1);
            SetFloat(device, program, "waterWaveIntensity", 1);
            SetFloat(device, program, "prevWindWaveIntensity", 1);
            SetFloat(device, program, "prevWaterWaveIntensity", 1);
            SetFloat(device, program, "globalWarpIntensity", 0);
            SetFloat(device, program, "prevGlobalWarpIntensity", previousWarp);

            device.SetViewport(0, 0, Size, Size);
            device.SetDepthTest(true);
            device.SetDepthMask(true);
            device.SetDepthFunc(0x203); // GL_LEQUAL
            device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard);
            device.DrawMesh(mesh);
            float[] pixels = ReadMotion(device, target.MotionTexture, Size);
            device.Present();
            return pixels;
        }
    }
}

/// <summary>The instanced shader must read each draw instance's own history.</summary>
public sealed class InstancedMotionContractTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    private static float[] Translation(float x, float y, float z) =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        x, y, z, 1,
    ];

    private readonly record struct Instance(float[] Current, float[] Previous,
        bool History = true, float Reactive = 0);

    [SkippableFact]
    public void OneDrawKeepsPreviousTransformPerInstance()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!);
            Check(scene.Draw([new Instance(Identity, Identity)]), 32, 0, 0, 0.5f, 0);
            Check(scene.Draw([new Instance(Identity, Translation(0.25f, 0, 0))]),
                32, 8, 0, 0.5f, 0);
            Check(scene.Draw([new Instance(Identity, Translation(-0.1875f, 0.0625f, 0))]),
                32, -6, 2, 0.5f, 0);

            // Two transforms in one draw prove the previous matrix is sourced
            // from the instance stream rather than a shared uniform.
            float[] pixels = scene.Draw(
            [
                new Instance(Translation(-0.5f, 0, 0), Translation(-0.25f, 0, 0)),
                new Instance(Translation(0.5f, 0, 0), Translation(0.5f, -0.125f, 0)),
            ]);
            Check(pixels, 16, 8, 0, 0.5f, 0);
            Check(pixels, 48, 0, -4, 0.5f, 0);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void MissingAndBehindCameraHistoryKeepReactiveInformation()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!);
            Check(scene.Draw([new Instance(Identity, Translation(-0.5f, 0.5f, 0),
                History: false, Reactive: 1)], cameraX: 0.25f, cameraY: -0.125f),
                32, 8, -4, 0.5f, 1);

            float[] behindProjection =
            [
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -1,
                0, 0, 0, 0,
            ];
            Check(scene.Draw([new Instance(Identity, Identity, Reactive: 0.6f)],
                previousView: Translation(0, 0, 1), previousProjection: behindProjection),
                32, 0, 0, 0, 0.6f);
            GpuTest.AssertClean(device!);
        }
    }

    private static void Check(float[] pixels, int x, float expectedX, float expectedY,
        float depth, float reactive)
    {
        int pixel = ((Size / 2) * Size + x) * 4;
        Assert.InRange(pixels[pixel], expectedX - 0.05f, expectedX + 0.05f);
        Assert.InRange(pixels[pixel + 1], expectedY - 0.05f, expectedY + 0.05f);
        Assert.InRange(pixels[pixel + 2], reactive - 0.01f, reactive + 0.01f);
        Assert.InRange(pixels[pixel + 3], depth - 0.01f, depth + 0.01f);
    }

    private sealed class Scene
    {
        private readonly VulkanDevice device;
        private readonly int program;
        private readonly MotionTarget target;

        public Scene(VulkanDevice device)
        {
            this.device = device;
            var variant = new ShaderCorpus.ShaderVariant
            {
                Name = "taa-instanced",
                TaaMotion = 1,
                TaaMotionLocation = 2,
            };
            var stages = ShaderCorpus.BuildProgram("instanced", ShaderCorpus.LoadShaderFiles(),
                ShaderCorpus.LoadIncludes(), variant);
            program = LinkFromCorpus(device, stages, "instanced", oit: false);
            foreach (string required in new[] { "taaRenderSize", "prevModelViewMatrix" })
                Assert.True(device.GetUniformLocation(program, required) >= 0, required + " is missing");
            BindEveryDeclaredSampler(device, device, program);
            target = CreateMotionTarget(device, Size);
        }

        public float[] Draw(Instance[] instances, float cameraX = 0, float cameraY = 0,
            float[]? previousView = null, float[]? previousProjection = null)
        {
            int mesh = device.CreateMesh(BuildMesh(instances), staticDraw: false);
            Assert.True(mesh > 0, device.GetError() ?? "instanced face upload failed");
            device.BeginFrame();
            device.BindFramebuffer(target.Framebuffer);
            device.ClearColor(0, 0, 0, 0, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);
            device.UseProgram(program);
            SetMatrix(device, program, "projectionMatrix", Identity);
            SetMatrix(device, program, "modelViewMatrix", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixFar", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixNear", Identity);
            SetMatrix(device, program, "prevProjectionMatrix", previousProjection ?? Identity);
            SetMatrix(device, program, "prevModelViewMatrix", previousView ?? Identity);
            SetFloat3(device, program, "cameraPosDelta", cameraX, cameraY, 0);
            SetFloat2(device, program, "taaRenderSize", Size, Size);
            SetFloat2(device, program, "taaJitterPx", 0, 0);
            SetFloat(device, program, "alphaTest", -1);
            SetFloat(device, program, "viewDistance", 1024);
            SetFloat(device, program, "viewDistanceLod0", 1024);
            SetFloat(device, program, "zNear", 0.1f);
            SetFloat(device, program, "zFar", 1024);
            SetFloat(device, program, "shadowRangeFar", 1024);
            SetFloat(device, program, "shadowRangeNear", 64);
            SetFloat(device, program, "shadowMapWidthInv", 1);
            SetFloat(device, program, "shadowMapHeightInv", 1);
            SetFloat3(device, program, "rgbaAmbientIn", 1, 1, 1);
            SetFloat4(device, program, "rgbaFogIn", 1, 1, 1, 1);
            SetFloat4(device, program, "averageColor", 1, 1, 1, 1);
            SetFloat2(device, program, "frameSize", Size, Size);

            device.SetViewport(0, 0, Size, Size);
            device.SetDepthTest(true);
            device.SetDepthMask(true);
            device.SetDepthFunc(0x203); // GL_LEQUAL
            device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard);
            device.DrawMeshInstanced(mesh, instances.Length);
            float[] pixels = ReadMotion(device, target.MotionTexture, Size);
            device.Present();
            return pixels;
        }

        private static MeshData BuildMesh(Instance[] instances)
        {
            MeshData mesh = CreateFaceData();
            CustomMeshDataPartFloat stream = OptimumInstanceMotion.CreateInstanceFloats(instances.Length);
            for (int i = 0; i < instances.Length; i++)
            {
                int start = i * OptimumInstanceMotion.InstanceFloats;
                for (int channel = 0; channel < 4; channel++)
                    stream.Values[start + OptimumInstanceMotion.LightOffset + channel] = 1;
                Array.Copy(instances[i].Current, 0, stream.Values,
                    start + OptimumInstanceMotion.TransformOffset, 16);
                Array.Copy(instances[i].Previous, 0, stream.Values,
                    start + OptimumInstanceMotion.PrevTransformOffset, 16);
                stream.Values[start + OptimumInstanceMotion.MetaOffset] = instances[i].History ? 1 : 0;
                stream.Values[start + OptimumInstanceMotion.MetaOffset + 1] = instances[i].Reactive;
            }
            stream.Count = instances.Length * OptimumInstanceMotion.InstanceFloats;
            mesh.CustomFloats = stream;
            return mesh;
        }
    }
}

/// <summary>Previous skinning and model motion from the opaque entity shader.</summary>
public sealed class EntityMotionContractTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private const int AnimationUboBytes = 35 * 16 * sizeof(float);
    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    private static float[] Translation(float x, float y, float z) =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        x, y, z, 1,
    ];

    [SkippableFact]
    public void SeparatePreviousBoneAndModelSourcesProduceExpectedPixels()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            Check(new Scene(device!, Identity).Draw(Identity), 0, 0, 0.5f, 0);
            Check(new Scene(device!, Translation(0.25f, 0, 0)).Draw(Identity),
                8, 0, 0.5f, 0);
            Check(new Scene(device!, Translation(0, -0.125f, 0)).Draw(Identity),
                0, -4, 0.5f, 0);
            Check(new Scene(device!, Translation(-0.1875f, 0.0625f, 0)).Draw(Identity),
                -6, 2, 0.5f, 0);
            Check(new Scene(device!, Identity).Draw(Translation(-0.25f, 0.125f, 0)),
                -8, 4, 0.5f, 0);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void InvalidHistoryWarpAndBehindCameraKeepTheirReactiveContract()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            Check(new Scene(device!, Translation(0.75f, 0.75f, 0)).Draw(
                Translation(-0.5f, 0.5f, 0), history: false,
                cameraX: 0.25f, cameraY: -0.125f), 8, -4, 0.5f, 1);

            double offset = (Math.Sin(0) + Math.Sin(0.5) + Math.Sin(1) / 3) / 30 * 8;
            float expectedWarp = (float)(offset * Size / 2);
            Assert.True(expectedWarp > 1);
            Check(new Scene(device!, Identity).Draw(Identity, previousWarp: 8),
                expectedWarp, 0, 0.5f, 0);

            float[] behindProjection =
            [
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -1,
                0, 0, 0, 0,
            ];
            Check(new Scene(device!, Identity).Draw(Identity,
                previousView: Translation(0, 0, 1),
                previousProjection: behindProjection, reactive: 0.6f),
                0, 0, 0, 0.6f);
            GpuTest.AssertClean(device!);
        }
    }

    private static void Check(float[] pixels, float x, float y, float depth, float reactive)
    {
        int centre = ((Size / 2) * Size + Size / 2) * 4;
        Assert.InRange(pixels[centre], x - 0.05f, x + 0.05f);
        Assert.InRange(pixels[centre + 1], y - 0.05f, y + 0.05f);
        Assert.InRange(pixels[centre + 2], reactive - 0.01f, reactive + 0.01f);
        Assert.InRange(pixels[centre + 3], depth - 0.01f, depth + 0.01f);
    }

    private sealed class Scene
    {
        private readonly VulkanDevice device;
        private readonly int program;
        private readonly MotionTarget target;
        private readonly int mesh;

        public Scene(VulkanDevice device, float[] previousBone)
        {
            this.device = device;
            var variant = new ShaderCorpus.ShaderVariant
            {
                Name = "taa-entity-opaque",
                UseOit = 0,
                TaaMotion = 1,
                TaaMotionLocation = 2,
                MaxAnimatedElements = 35,
            };
            var stages = ShaderCorpus.BuildProgram("entityanimated", ShaderCorpus.LoadShaderFiles(),
                ShaderCorpus.LoadIncludes(), variant);
            program = LinkFromCorpus(device, stages, "entityanimated", oit: false);
            foreach (string required in new[] { "taaRenderSize", "taaHistoryValid" })
                Assert.True(device.GetUniformLocation(program, required) >= 0, required + " is missing");
            int unit = BindEveryDeclaredSampler(device, device, program);
            int atlas = CreateWhiteTexture(device);
            device.SetSamplerUnit(program, "entityTex", unit);
            device.BindTexture(unit, atlas);

            int current = device.CreateUniformBuffer(program, 0, "Animation", AnimationUboBytes);
            int previous = device.CreateUniformBuffer(program, 1, "AnimationPrev", AnimationUboBytes);
            WriteBone(device, current, Identity);
            WriteBone(device, previous, previousBone);
            target = CreateMotionTarget(device, Size);
            mesh = device.CreateMesh(SkinnedFace(), staticDraw: true);
            Assert.True(mesh > 0, device.GetError() ?? "entity face upload failed");
        }

        public float[] Draw(float[] previousModel, bool history = true, float cameraX = 0,
            float cameraY = 0, float previousWarp = 0, float[]? previousView = null,
            float[]? previousProjection = null, float reactive = 0)
        {
            device.BeginFrame();
            device.BindFramebuffer(target.Framebuffer);
            device.ClearColor(0, 0, 0, 0, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);
            device.UseProgram(program);
            SetMatrix(device, program, "projectionMatrix", Identity);
            SetMatrix(device, program, "viewMatrix", Identity);
            SetMatrix(device, program, "modelMatrix", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixFar", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixNear", Identity);
            SetMatrix(device, program, "prevProjectionMatrix", previousProjection ?? Identity);
            SetMatrix(device, program, "prevViewMatrix", previousView ?? Identity);
            SetMatrix(device, program, "prevModelMatrix", previousModel);
            SetInt(device, program, "taaHistoryValid", history ? 1 : 0);
            SetFloat(device, program, "taaReactive", history ? reactive : 1);
            SetFloat3(device, program, "cameraPosDelta", cameraX, cameraY, 0);
            SetFloat2(device, program, "taaRenderSize", Size, Size);
            SetFloat2(device, program, "taaJitterPx", 0, 0);
            SetFloat(device, program, "alphaTest", -1);
            SetFloat(device, program, "viewDistance", 1024);
            SetFloat(device, program, "viewDistanceLod0", 1024);
            SetFloat(device, program, "zNear", 0.1f);
            SetFloat(device, program, "zFar", 1024);
            SetFloat(device, program, "shadowRangeFar", 1024);
            SetFloat(device, program, "shadowRangeNear", 64);
            SetFloat(device, program, "shadowMapWidthInv", 1);
            SetFloat(device, program, "shadowMapHeightInv", 1);
            SetInt(device, program, "entityId", 1);
            SetFloat3(device, program, "rgbaAmbientIn", 1, 1, 1);
            SetFloat4(device, program, "rgbaLightIn", 1, 1, 1, 1);
            SetFloat4(device, program, "rgbaFogIn", 1, 1, 1, 1);
            SetFloat4(device, program, "renderColor", 1, 1, 1, 1);
            SetFloat2(device, program, "frameSize", Size, Size);
            SetInt(device, program, "perceptionEffectId", 1);
            SetInt(device, program, "prevPerceptionEffectId", 1);
            SetFloat(device, program, "windWaveIntensity", 1);
            SetFloat(device, program, "waterWaveIntensity", 1);
            SetFloat(device, program, "prevWindWaveIntensity", 1);
            SetFloat(device, program, "prevWaterWaveIntensity", 1);
            SetFloat(device, program, "globalWarpIntensity", 0);
            SetFloat(device, program, "prevGlobalWarpIntensity", previousWarp);

            device.SetViewport(0, 0, Size, Size);
            device.SetDepthTest(true);
            device.SetDepthMask(true);
            device.SetDepthFunc(0x203); // GL_LEQUAL
            device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard);
            device.DrawMesh(mesh);
            float[] pixels = ReadMotion(device, target.MotionTexture, Size);
            device.Present();
            return pixels;
        }

        private static unsafe void WriteBone(VulkanDevice device, int buffer, float[] matrix)
        {
            fixed (float* values = matrix)
                device.UpdateUniformBuffer(buffer, (IntPtr)values, 0, 16 * sizeof(float));
        }

        private static MeshData SkinnedFace()
        {
            MeshData mesh = CreateFaceData();
            mesh.CustomFloats = new CustomMeshDataPartFloat(4)
            {
                Count = 4,
                InterleaveSizes = [1],
                InterleaveOffsets = [0],
                InterleaveStride = 4,
            };
            mesh.CustomInts = new CustomMeshDataPartInt(4)
            {
                Count = 4,
                InterleaveSizes = [1],
                InterleaveOffsets = [0],
                InterleaveStride = 4,
            };
            return mesh;
        }
    }
}
