using static Optimum.Render.Vulkan.Tests.MotionFixture;
using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;
using Vintagestory.API.MathTools;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The skinned-entity motion-vector writer (TAA P3), driven through the seam with
/// the real entityanimated program and read back as pixels.
///
/// The thing under test is the double skinning: the same vertex is transformed
/// once by <c>modelMatrix * Animation.values[jointId]</c> and once by
/// <c>prevModelMatrix * AnimationPrev.values[jointId]</c>, and the difference,
/// converted into render pixels, is the motion vector. Two bone sets that differ
/// by a known translation must therefore produce exactly that translation in
/// pixels - which a "motion is non-zero when the entity moved" assertion could
/// not tell apart from a sign flip, an axis swap or the two blocks being fed the
/// same buffer (the failure mode the UBO binding-point fix exists for).
///
/// The still case is the baseline, not the test: identical bone sets and no
/// camera movement must give exactly (0, 0), because a converged entity that
/// wobbles is what a wrong previous transform looks like on screen.
///
/// As in TaaMotionWriterTests the RGBA16F attachment comes back through an RGBA8
/// decode pass, because the seam's readback is fixed at four bytes per pixel from
/// colour attachment 0.
/// </summary>
public class TaaEntityMotionWriterTests
{
    private readonly ITestOutputHelper _output;

    public TaaEntityMotionWriterTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    /// <summary>Pixels per unit in the decode pass: mv/DecodeScale * 0.5 + 0.5 into an RGBA8 channel.</summary>
    private const float DecodeScale = 32f;

    /// <summary>Normal pointing up, no glow, and no wind-mode bits, so no vertex warp runs.</summary>
    private const int UpNormalFlags = 7 << 18;

    /// <summary>MAXANIMATEDELEMENTS as the corpus stamps it; the UBO is that many mat4.</summary>
    private const int MaxAnimatedElements = 35;

    private const int AnimationUboBytes = MaxAnimatedElements * 16 * 4;

    private static readonly float[] Identity =
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    private static float[] Translation(float x, float y, float z) => new[]
    {
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        x,  y,  z,  1f,
    };

    /// <summary>
    /// A previous camera the surface was behind: the previous view puts the quad at
    /// view z = +1 and the previous projection's w row is -z, so the previous clip w
    /// is -1 and the writer takes its behind-the-camera branch.
    /// </summary>
    private static readonly float[] BehindView = Translation(0f, 0f, 1f);

    private static readonly float[] BehindProjection =
    {
        1, 0, 0,  0,
        0, 1, 0,  0,
        0, 0, 1, -1,
        0, 0, 0,  0,
    };

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// A pose that did not change, under a camera that did not move, is zero
    /// motion - and the writer still stamps its own depth, so the resolve accepts
    /// the pixel instead of silently falling back to camera reprojection.
    /// </summary>
    [SkippableFact]
    public void AnUnchangedPoseWritesZeroMotionAndTheFragmentDepth()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Decoded centre = RenderEntityMotion(device!,
                previousBone: Identity,
                previousModelMatrix: Identity,
                historyValid: 1,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f,
                previousGlobalWarp: 0f);

            _output.WriteLine($"still: mv = ({centre.MotionX}, {centre.MotionY}), writerDepth = {centre.WriterDepth}");

            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            // Identity matrices put the quad at NDC z = 0, which is window depth
            // 0.5 on both backends.
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The bone the vertex is weighted to having been somewhere else last frame is
    /// the entity's own motion: with identity camera matrices a bone translation
    /// of d moves the pixel by exactly d * 0.5 * renderSize, and the sign is
    /// "where the pixel was", not "where it went".
    /// </summary>
    [SkippableTheory]
    [InlineData(0.25f, 0f)]
    [InlineData(0f, -0.125f)]
    [InlineData(-0.1875f, 0.0625f)]
    public void APreviousBoneTransformShowsUpAsTheExactPixelDisplacement(float boneX, float boneY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Decoded centre = RenderEntityMotion(device!,
                previousBone: Translation(boneX, boneY, 0f),
                previousModelMatrix: Identity,
                historyValid: 1,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f,
                previousGlobalWarp: 0f);

            float expectedX = boneX * 0.5f * Size;
            float expectedY = boneY * 0.5f * Size;

            _output.WriteLine($"bone ({boneX}, {boneY}): mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The renderer's model matrix having been somewhere else last frame moves the
    /// entity exactly as a bone does. Separate from the bone case because the two
    /// come from different sources - the model matrix through a uniform, the bones
    /// through the second UBO - and a shader that dropped one of the two factors
    /// would still pass the other test.
    /// </summary>
    [SkippableFact]
    public void APreviousModelMatrixShowsUpAsTheExactPixelDisplacement()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float modelX = -0.25f;
            const float modelY = 0.125f;
            Decoded centre = RenderEntityMotion(device!,
                previousBone: Identity,
                previousModelMatrix: Translation(modelX, modelY, 0f),
                historyValid: 1,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f,
                previousGlobalWarp: 0f);

            float expectedX = modelX * 0.5f * Size;
            float expectedY = modelY * 0.5f * Size;

            _output.WriteLine($"model ({modelX}, {modelY}): mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
        }
    }

    /// <summary>
    /// Without usable history - a spawn, a changed animator or mesh, a first/third
    /// person switch, the entity off-screen last frame - the writer must not read
    /// the previous pose at all. It falls back to treating the surface as static
    /// in the world, so only the camera's own movement displaces it; the C# side
    /// raises taaReactive for the same draw so the resolve leans on this frame.
    ///
    /// The previous bone here is deliberately a large translation: if the shader
    /// took the history branch anyway, the vector would be that instead.
    /// </summary>
    [SkippableFact]
    public void WithoutUsableHistoryTheVectorIsCameraMotionOnly()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float cameraDeltaX = 0.25f;
            const float cameraDeltaY = -0.125f;
            Decoded centre = RenderEntityMotion(device!,
                previousBone: Translation(0.75f, 0.75f, 0f),
                previousModelMatrix: Translation(-0.5f, 0.5f, 0f),
                historyValid: 0,
                cameraDeltaX: cameraDeltaX,
                cameraDeltaY: cameraDeltaY,
                previousGlobalWarp: 0f);

            float expectedX = cameraDeltaX * 0.5f * Size;
            float expectedY = cameraDeltaY * 0.5f * Size;

            _output.WriteLine($"no history: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
        }
    }

    /// <summary>
    /// Vertex animation that differed last frame is motion too, for entities as
    /// much as for terrain: the previous warp state runs through the same
    /// WarpState overloads in the vertexwarp include. The values are chosen so
    /// applyGlobalWarping's phase argument saturates at zero over the whole quad,
    /// which turns the warp into a constant offset with a closed-form expectation
    /// instead of a "not zero" assertion.
    /// </summary>
    [SkippableFact]
    public void APreviousWarpStateThatDiffersFromThisFrameProducesItsOwnMotion()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float previousWarp = 8f;
            Decoded centre = RenderEntityMotion(device!,
                previousBone: Identity,
                previousModelMatrix: Identity,
                historyValid: 1,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f,
                previousGlobalWarp: previousWarp);

            double offsetX = (Math.Sin(0.0) + Math.Sin(0.5) + Math.Sin(1.0) / 3.0) / 30.0 * previousWarp;
            float expectedX = (float)(offsetX * 0.5 * Size);

            _output.WriteLine($"warp-only: mv = ({centre.MotionX}, {centre.MotionY}), expected ({expectedX}, 0)");

            Assert.True(Math.Abs(expectedX) > 1f, "the warp displacement chosen is too small to test");
            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
        }
    }

    /// <summary>
    /// A previous position behind the previous camera is not a motion vector, but
    /// the contract (docs/vulkan.md section 3.2) still wants the
    /// reactive value: a writer that bails out of its vector delivers b and zeroes
    /// only rg and a. The entity helper used to return vec4(0.0) there and drop the
    /// taaReactive value with the vector.
    /// </summary>
    [SkippableFact]
    public void APreviousPositionBehindThePreviousCameraStillCarriesTheReactiveValue()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float reactive = 0.6f;
            Decoded centre = RenderEntityMotion(device!,
                previousBone: Identity,
                previousModelMatrix: Identity,
                historyValid: 1,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f,
                previousGlobalWarp: 0f,
                previousView: BehindView,
                previousProjection: BehindProjection,
                reactive: reactive);

            _output.WriteLine($"behind: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"reactive = {centre.Reactive}, writerDepth = {centre.WriterDepth}");

            // No vector, and the zero alpha that routes the pixel to the camera
            // fallback rather than pretending the writer owns it.
            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            Assert.InRange(centre.WriterDepth, 0f, 0.01f);
            // ... but the reactive value is still delivered.
            Assert.InRange(centre.Reactive, reactive - 0.01f, reactive + 0.01f);
        }
    }

    // ---------------------------------------------------------------- harness

    private readonly struct Decoded
    {
        public Decoded(float motionX, float motionY, float reactive, float writerDepth)
        {
            MotionX = motionX;
            MotionY = motionY;
            Reactive = reactive;
            WriterDepth = writerDepth;
        }

        public float MotionX { get; }
        public float MotionY { get; }
        public float Reactive { get; }
        public float WriterDepth { get; }
    }

    /// <summary>
    /// Draws one skinned quad with the real entityanimated program compiled as a
    /// motion writer, then decodes the motion attachment and returns its centre
    /// pixel. The current pose is always the identity, so every expectation is
    /// stated entirely in terms of the previous-frame inputs.
    /// </summary>
    private unsafe Decoded RenderEntityMotion(
        VulkanDevice device,
        float[] previousBone,
        float[] previousModelMatrix,
        int historyValid,
        float cameraDeltaX,
        float cameraDeltaY,
        float previousGlobalWarp,
        float[]? previousView = null,
        float[]? previousProjection = null,
        float? reactive = null)
    {
        VulkanDevice seam = device;

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        // USEOIT 0 is the opaque entity pass - the only one that writes into
        // Primary's motion attachment; the OIT twin fills six outputs on
        // Transparent and never touches it. Built here rather than taken from
        // ShaderCorpus.Variants() because a global USEOIT 0 is not a
        // configuration the client produces for every program.
        var variant = new ShaderCorpus.ShaderVariant
        {
            Name = "taa-entity-opaque",
            UseOit = 0,
            TaaMotion = 1,
            TaaMotionLocation = 2,
            MaxAnimatedElements = MaxAnimatedElements,
        };

        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram("entityanimated", files, includes, variant);
        Assert.NotEmpty(stages);
        int program = LinkFromCorpus(seam, stages, "entityanimated");

        Assert.True(seam.GetUniformLocation(program, "taaRenderSize") >= 0,
            "entityanimated declares no taaRenderSize, so it is not a motion writer");
        Assert.True(seam.GetUniformLocation(program, "taaHistoryValid") >= 0,
            "entityanimated declares no taaHistoryValid, so it cannot reject stale history");

        int nextUnit = BindEveryDeclaredSampler(device, seam, program);
        int atlas = CreateWhiteTexture(seam);
        seam.SetSamplerUnit(program, "entityTex", nextUnit);
        seam.BindTexture(nextUnit, atlas);

        // The two bone blocks. Feeding them from separate buffers is the point:
        // one buffer serving both declarations is exactly what a hard-coded
        // binding point would produce, and it would make every motion vector zero.
        int animation = seam.CreateUniformBuffer(program, 0, "Animation", AnimationUboBytes);
        int animationPrev = seam.CreateUniformBuffer(program, 1, "AnimationPrev", AnimationUboBytes);
        WriteBone(seam, animation, Identity);
        WriteBone(seam, animationPrev, previousBone);

        // Primary stand-in: colour, glow and the motion attachment at index 2.
        int colour = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int glow = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int motion = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba16f,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int depth = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.DepthComponent32,
            EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
        seam.SetTextureParameter(motion, OptimumGlConstants.TextureMinFilter, 9728);
        seam.SetTextureParameter(motion, OptimumGlConstants.TextureMagFilter, 9728);

        int scene = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment0, colour, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment1, glow, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment2, motion, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.DepthAttachment, depth, 0);
        seam.SetDrawBuffers(scene, 0b111);
        Assert.True(seam.CheckFramebufferComplete(scene, out string status), status);

        int mesh = seam.CreateMesh(BuildSkinnedQuad(), staticDraw: true);
        Assert.True(mesh > 0, seam.GetError() ?? "mesh upload failed");

        seam.BeginFrame();
        seam.BindFramebuffer(scene);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.ClearColor(1, 0f, 0f, 0f, 1f);
        seam.ClearColor(2, 0f, 0f, 0f, 0f);
        seam.ClearDepth(1f);

        seam.UseProgram(program);
        SetMatrix(seam, program, "projectionMatrix", Identity);
        SetMatrix(seam, program, "viewMatrix", Identity);
        SetMatrix(seam, program, "modelMatrix", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixFar", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixNear", Identity);
        SetSceneUniforms(seam, program);
        SetWarpUniforms(seam, program, previousGlobalWarp);

        SetMatrix(seam, program, "prevProjectionMatrix", previousProjection ?? Identity);
        SetMatrix(seam, program, "prevViewMatrix", previousView ?? Identity);
        SetMatrix(seam, program, "prevModelMatrix", previousModelMatrix);
        SetInt(seam, program, "taaHistoryValid", historyValid);
        SetFloat(seam, program, "taaReactive", reactive ?? (historyValid != 0 ? 0f : 1f));
        SetFloat3(seam, program, "cameraPosDelta", cameraDeltaX, cameraDeltaY, 0f);
        SetFloat2(seam, program, "taaRenderSize", Size, Size);
        SetFloat2(seam, program, "taaJitterPx", 0f, 0f);

        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(true);
        seam.SetDepthMask(true);
        seam.SetDepthFunc(0x203);   // GL_LEQUAL
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawMesh(mesh);

        byte[] decoded = DecodeMotion(seam, motion);
        seam.Present();

        int offset = ((Size / 2) * Size + Size / 2) * 4;

        AssertClean(seam);

        return new Decoded(
            (decoded[offset] / 255f * 2f - 1f) * DecodeScale,
            (decoded[offset + 1] / 255f * 2f - 1f) * DecodeScale,
            decoded[offset + 2] / 255f,
            decoded[offset + 3] / 255f);
    }

    private static unsafe void WriteBone(VulkanDevice seam, int ubo, float[] matrix)
    {
        // Only joint 0 is referenced by the mesh below; the rest of the block
        // stays zero, which is what a shader that read the wrong joint would show.
        fixed (float* values = matrix)
        {
            seam.UpdateUniformBuffer(ubo, (IntPtr)values, 0, 16 * sizeof(float));
        }
    }

    /// <summary>
    /// Reads the RGBA16F motion attachment through an RGBA8 decode pass, because
    /// the seam's readback is fixed at four bytes per pixel from attachment 0.
    /// </summary>
    private unsafe byte[] DecodeMotion(VulkanDevice seam, int motionTexture)
    {
        const string decodeVertex = @"#version 330 core
layout(location = 0) in vec3 xyz;
void main(void) { gl_Position = vec4(xyz, 1.0); }
";
        const string decodeFragment = @"#version 330 core
uniform sampler2D motionTex;
uniform float decodeScale;
layout(location = 0) out vec4 outColor;
void main(void)
{
	vec4 m = texelFetch(motionTex, ivec2(gl_FragCoord.xy), 0);
	outColor = vec4(
		clamp(m.r / decodeScale * 0.5 + 0.5, 0.0, 1.0),
		clamp(m.g / decodeScale * 0.5 + 0.5, 0.0, 1.0),
		clamp(m.b, 0.0, 1.0),
		clamp(m.a, 0.0, 1.0));
}
";
        int decode = LinkFromCorpus(seam, new List<ShaderStageSource>
        {
            new() { Stage = EnumShaderType.VertexShader, Code = decodeVertex, PrefixCode = "", Filename = "taa-entity-decode.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = decodeFragment, PrefixCode = "", Filename = "taa-entity-decode.fsh" },
        }, "taa-entity-decode");

        var quad = new MeshData(4, 6, withNormals: false, withUv: false, withRgba: false, withFlags: false)
        {
            xyz = new[] { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f },
            VerticesCount = 4,
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
            IndicesCount = 6,
        };
        int quadMesh = seam.CreateMesh(quad, staticDraw: true);
        Assert.True(quadMesh > 0, seam.GetError() ?? "decode mesh upload failed");

        int target = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
        seam.SetDrawBuffers(framebuffer, 0b1);

        seam.BindFramebuffer(framebuffer);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.UseProgram(decode);
        seam.SetSamplerUnit(decode, "motionTex", 15);
        seam.BindTexture(15, motionTexture);
        SetFloat(seam, decode, "decodeScale", DecodeScale);
        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(false);
        seam.SetDepthMask(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawMesh(quadMesh);

        var pixels = new byte[Size * Size * 4];
        fixed (byte* destination = pixels)
        {
            seam.BindFramebuffer(framebuffer);
            seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
        }
        return pixels;
    }

    /// <summary>
    /// A quad in entityanimated's attribute layout: xyz, uv, rgba, flags, then the
    /// custom float (damageEffectIn, location 4) and custom int (jointId,
    /// location 5). Normals are absent, which is what puts uv on location 1 the
    /// way the shader declares it.
    /// </summary>
    private static MeshData BuildSkinnedQuad()
    {
        var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);

        float[] positions =
        {
            -0.5f, -0.5f, 0f,
             0.5f, -0.5f, 0f,
             0.5f,  0.5f, 0f,
            -0.5f,  0.5f, 0f,
        };
        float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };

        for (int i = 0; i < 4; i++)
        {
            mesh.AddVertexWithFlags(
                positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                uvs[i * 2], uvs[i * 2 + 1],
                Vintagestory.API.MathTools.ColorUtil.WhiteArgb,
                flags: UpNormalFlags);
        }

        mesh.CustomFloats = new CustomMeshDataPartFloat(4)
        {
            Count = 4,
            InterleaveSizes = new[] { 1 },
            InterleaveOffsets = new[] { 0 },
            InterleaveStride = 4,
        };
        mesh.CustomInts = new CustomMeshDataPartInt(4)
        {
            Count = 4,
            InterleaveSizes = new[] { 1 },
            InterleaveOffsets = new[] { 0 },
            InterleaveStride = 4,
        };

        foreach (int index in new[] { 0, 1, 2, 0, 2, 3 })
        {
            mesh.AddIndex(index);
        }
        return mesh;
    }

    /// <summary>
    /// Enough of the lighting and fog surface to keep the fragment alive: an
    /// unlit, fully transparent fragment is discarded before it can write a
    /// motion vector, and the test would read the cleared attachment instead.
    /// alphaTest is pushed below zero so nothing can discard at all.
    /// </summary>
    private static void SetSceneUniforms(VulkanDevice seam, int program)
    {
        SetFloat(seam, program, "alphaTest", -1f);
        SetFloat(seam, program, "viewDistance", 1024f);
        SetFloat(seam, program, "viewDistanceLod0", 1024f);
        SetFloat(seam, program, "zNear", 0.1f);
        SetFloat(seam, program, "zFar", 1024f);
        SetFloat(seam, program, "fogMinIn", 0f);
        SetFloat(seam, program, "fogDensityIn", 0f);
        SetFloat(seam, program, "shadowRangeFar", 1024f);
        SetFloat(seam, program, "shadowRangeNear", 64f);
        SetFloat(seam, program, "shadowMapWidthInv", 1f);
        SetFloat(seam, program, "shadowMapHeightInv", 1f);
        SetFloat(seam, program, "shadowIntensity", 0f);
        SetFloat(seam, program, "damageEffect", 0f);
        SetFloat(seam, program, "glitchEffectStrength", 0f);
        SetInt(seam, program, "glitchFlicker", 0);
        SetInt(seam, program, "entityId", 1);
        SetInt(seam, program, "extraGlow", 0);
        SetInt(seam, program, "addRenderFlags", 0);
        SetFloat(seam, program, "frostAlpha", 0f);
        SetFloat3(seam, program, "rgbaAmbientIn", 1f, 1f, 1f);
        SetFloat4(seam, program, "rgbaLightIn", 1f, 1f, 1f, 1f);
        SetFloat4(seam, program, "rgbaFogIn", 1f, 1f, 1f, 1f);
        SetFloat4(seam, program, "renderColor", 1f, 1f, 1f, 1f);
        SetFloat2(seam, program, "frameSize", Size, Size);
    }

    /// <summary>
    /// Both halves of the warp state, pinned so this frame's warp is a no-op and
    /// only the previous one moves anything.
    /// </summary>
    private static void SetWarpUniforms(VulkanDevice seam, int program, float previousGlobalWarp)
    {
        SetFloat(seam, program, "timeCounter", 0f);
        SetFloat(seam, program, "windWaveCounter", 0f);
        SetFloat(seam, program, "windWaveCounterHighFreq", 0f);
        SetFloat(seam, program, "waterWaveCounter", 0f);
        SetFloat(seam, program, "windSpeed", 0f);
        SetFloat(seam, program, "globalWarpIntensity", 0f);
        SetFloat(seam, program, "glitchWaviness", 0f);
        SetFloat(seam, program, "windWaveIntensity", 1f);
        SetFloat(seam, program, "waterWaveIntensity", 1f);
        SetInt(seam, program, "perceptionEffectId", 1);
        SetFloat(seam, program, "perceptionEffectIntensity", 0f);
        SetFloat3(seam, program, "playerpos", 0f, 0f, 0f);

        SetFloat(seam, program, "prevTimeCounter", 0f);
        SetFloat(seam, program, "prevWindWaveCounter", 0f);
        SetFloat(seam, program, "prevWindWaveCounterHighFreq", 0f);
        SetFloat(seam, program, "prevWaterWaveCounter", 0f);
        SetFloat(seam, program, "prevWindSpeed", 0f);
        SetFloat(seam, program, "prevGlobalWarpIntensity", previousGlobalWarp);
        SetFloat(seam, program, "prevGlitchWaviness", 0f);
        SetFloat(seam, program, "prevWindWaveIntensity", 1f);
        SetFloat(seam, program, "prevWaterWaveIntensity", 1f);
        SetInt(seam, program, "prevPerceptionEffectId", 1);
        SetFloat(seam, program, "prevPerceptionEffectIntensity", 0f);
        SetFloat3(seam, program, "prevPlayerpos", 0f, 0f, 0f);
    }

    private static int LinkFromCorpus(
        VulkanDevice seam, List<ShaderStageSource> stages, string name) =>
        MotionFixture.LinkFromCorpus(seam, stages, name, oit: false);

    private static bool TryCreateDevice(ITestOutputHelper output, out VulkanDevice? device) =>
        GpuTest.TryCreateDevice(output, out device);

    private static void AssertClean(VulkanDevice seam) => GpuTest.AssertClean(seam);

}

/// <summary>
/// The instanced motion-vector writer (TAA P3), driven through the seam with the
/// real instanced program and read back as pixels. This is the writer every
/// mechanical-power block goes through - axles, gears, clutches, transmissions,
/// creative rotors, pulverizers - all of them drawn as instances of one mesh.
///
/// What it has to get right, and what a "the gear moved, so motion is non-zero"
/// assertion could not tell apart from a sign flip, an axis swap or a shared
/// uniform standing in for a per-instance attribute:
/// - a previous instance transform turns into exactly that displacement in render
///   pixels, with the sign "where the pixel was";
/// - a gear that did not turn under a camera that did not move is exactly (0, 0);
/// - two instances in ONE draw get their OWN previous transforms - the failure
///   mode of the whole design is every instance reading one gear's matrix;
/// - an instance whose history the C# side could not match (a new device, a
///   reordered buffer) falls back to camera-only motion and carries reactive 1,
///   instead of reprojecting by whatever matrix landed in its slot.
///
/// As in TaaStandardMotionWriterTests the RGBA16F attachment comes back through
/// an RGBA8 decode pass, because the seam's readback is fixed at four bytes per
/// pixel from colour attachment 0. Decode quantisation is 2*DecodeScale/255 px,
/// so the tolerances stay above it.
/// </summary>
public class TaaInstancedMotionWriterTests
{
    private readonly ITestOutputHelper _output;

    public TaaInstancedMotionWriterTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    /// <summary>Pixels per unit in the decode pass: mv/DecodeScale * 0.5 + 0.5 into an RGBA8 channel.</summary>
    private const float DecodeScale = 32f;

    /// <summary>Normal pointing up, no glow.</summary>
    private const int UpNormalFlags = 7 << 18;

    private static readonly float[] Identity =
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    private static float[] Translation(float x, float y, float z) => new[]
    {
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        x,  y,  z,  1f,
    };

    /// <summary>
    /// A previous camera the block was behind: the previous view puts the quad at
    /// view z = +1 and the previous projection's w row is -z, so the previous clip w
    /// is -1 and the writer takes its behind-the-camera branch.
    /// </summary>
    private static readonly float[] BehindView = Translation(0f, 0f, 1f);

    private static readonly float[] BehindProjection =
    {
        1, 0, 0,  0,
        0, 1, 0,  0,
        0, 0, 1, -1,
        0, 0, 0,  0,
    };

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// A gear that did not turn, under a camera that did not move, is zero motion -
    /// and the writer still stamps its own depth, so the resolve accepts the pixel
    /// instead of silently falling back to camera reprojection.
    /// </summary>
    [SkippableFact]
    public void AnUnmovedInstanceWritesZeroMotionAndTheFragmentDepth()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            var instance = new Instance(Identity, Identity, historyValid: true);
            Decoded centre = RenderInstancedMotion(device!, new[] { instance }, 0f, 0f)[Size / 2];

            _output.WriteLine($"still: mv = ({centre.MotionX}, {centre.MotionY}), writerDepth = {centre.WriterDepth}");

            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            // Identity matrices put the quad at NDC z = 0, which is window depth
            // 0.5 on both backends.
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The transform this instance was drawn with last frame is its motion: with
    /// identity camera matrices a translation of d moves the pixel by exactly
    /// d * 0.5 * renderSize, and the sign is "where the pixel was", not "where it
    /// went".
    /// </summary>
    [SkippableTheory]
    [InlineData(0.25f, 0f)]
    [InlineData(0f, -0.125f)]
    [InlineData(-0.1875f, 0.0625f)]
    public void APreviousInstanceTransformShowsUpAsTheExactPixelDisplacement(float prevX, float prevY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            var instance = new Instance(Identity, Translation(prevX, prevY, 0f), historyValid: true);
            Decoded centre = RenderInstancedMotion(device!, new[] { instance }, 0f, 0f)[Size / 2];

            float expectedX = prevX * 0.5f * Size;
            float expectedY = prevY * 0.5f * Size;

            _output.WriteLine($"prev ({prevX}, {prevY}): mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The point of putting the previous transform in the instance stream rather
    /// than in a uniform: two devices drawn by ONE instanced draw call each get
    /// their own previous transform. A writer that took it from a uniform - or
    /// that keyed history on the buffer slot instead of the device - would give
    /// both halves of this image the same vector, which is what a whole gear
    /// network smearing into one direction looks like on screen.
    /// </summary>
    [SkippableFact]
    public void EachInstanceInOneDrawGetsItsOwnPreviousTransform()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float leftPrevX = 0.25f;
            const float rightPrevY = -0.125f;

            // The current transforms put instance 0 in the left half of the image
            // and instance 1 in the right half; the quad is one unit across, so the
            // halves do not overlap.
            var left = new Instance(
                Translation(-0.5f, 0f, 0f),
                MultiplyTranslations(-0.5f + leftPrevX, 0f),
                historyValid: true);
            var right = new Instance(
                Translation(0.5f, 0f, 0f),
                MultiplyTranslations(0.5f, rightPrevY),
                historyValid: true);

            Decoded[] row = RenderInstancedMotion(device!, new[] { left, right }, 0f, 0f);
            Decoded leftPixel = row[Size / 4];
            Decoded rightPixel = row[Size * 3 / 4];

            float expectedLeftX = leftPrevX * 0.5f * Size;
            float expectedRightY = rightPrevY * 0.5f * Size;

            _output.WriteLine($"left: mv = ({leftPixel.MotionX}, {leftPixel.MotionY}), expected ({expectedLeftX}, 0)");
            _output.WriteLine($"right: mv = ({rightPixel.MotionX}, {rightPixel.MotionY}), expected (0, {expectedRightY})");

            Assert.InRange(leftPixel.MotionX, expectedLeftX - 0.3f, expectedLeftX + 0.3f);
            Assert.InRange(leftPixel.MotionY, -0.3f, 0.3f);
            Assert.InRange(rightPixel.MotionX, -0.3f, 0.3f);
            Assert.InRange(rightPixel.MotionY, expectedRightY - 0.3f, expectedRightY + 0.3f);
        }
    }

    /// <summary>
    /// Without usable history - a device placed this frame, a chunk that streamed
    /// in, a buffer whose instances were reordered - the writer must not read the
    /// previous transform at all. It falls back to treating the block as static in
    /// the world, so only the camera's own movement displaces it, and the reactive
    /// channel the C# side stamped comes through so the resolve leans on this frame.
    ///
    /// The previous transform here is deliberately a large translation: if the
    /// shader took the history branch anyway, the vector would be that instead.
    /// </summary>
    [SkippableFact]
    public void WithoutUsableHistoryTheVectorIsCameraMotionOnlyAndReactive()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float cameraDeltaX = 0.25f;
            const float cameraDeltaY = -0.125f;

            var instance = new Instance(Identity, Translation(-0.5f, 0.5f, 0f), historyValid: false);
            Decoded centre = RenderInstancedMotion(device!, new[] { instance }, cameraDeltaX, cameraDeltaY)[Size / 2];

            float expectedX = cameraDeltaX * 0.5f * Size;
            float expectedY = cameraDeltaY * 0.5f * Size;

            _output.WriteLine($"no history: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY}), reactive = {centre.Reactive}");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.Reactive, 0.9f, 1.1f);
        }
    }

    /// <summary>
    /// A previous position behind the previous camera is not a motion vector, but
    /// the contract (docs/vulkan.md section 3.2) still wants the
    /// reactive value: a writer that bails out of its vector delivers b and zeroes
    /// only rg and a. The instanced helper used to return vec4(0.0) there and drop
    /// the per-instance reactive value with the vector.
    /// </summary>
    [SkippableFact]
    public void APreviousPositionBehindThePreviousCameraStillCarriesTheReactiveValue()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float reactive = 0.6f;
            var instance = new Instance(Identity, Identity, historyValid: true, reactive: reactive);
            Decoded centre = RenderInstancedMotion(device!, new[] { instance }, 0f, 0f,
                previousView: BehindView, previousProjection: BehindProjection)[Size / 2];

            _output.WriteLine($"behind: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"reactive = {centre.Reactive}, writerDepth = {centre.WriterDepth}");

            // No vector, and the zero alpha that routes the pixel to the camera
            // fallback rather than pretending the writer owns it.
            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            Assert.InRange(centre.WriterDepth, 0f, 0.01f);
            // ... but the reactive value is still delivered.
            Assert.InRange(centre.Reactive, reactive - 0.01f, reactive + 0.01f);
        }
    }

    // ---------------------------------------------------------------- harness

    private readonly struct Instance
    {
        public Instance(float[] transform, float[] previousTransform, bool historyValid, float? reactive = null)
        {
            Transform = transform;
            PreviousTransform = previousTransform;
            HistoryValid = historyValid;
            Reactive = reactive ?? (historyValid ? 0f : 1f);
        }

        public float[] Transform { get; }
        public float[] PreviousTransform { get; }
        public bool HistoryValid { get; }
        /// <summary>The metadata's reactive channel; by default what the C# side stamps for the history state.</summary>
        public float Reactive { get; }
    }

    private readonly struct Decoded
    {
        public Decoded(float motionX, float motionY, float reactive, float writerDepth)
        {
            MotionX = motionX;
            MotionY = motionY;
            Reactive = reactive;
            WriterDepth = writerDepth;
        }

        public float MotionX { get; }
        public float MotionY { get; }
        public float Reactive { get; }
        public float WriterDepth { get; }
    }

    /// <summary>Two translations composed: the current placement plus the previous offset.</summary>
    private static float[] MultiplyTranslations(float x, float y) => Translation(x, y, 0f);

    /// <summary>
    /// Draws the given instances with the real instanced program compiled as a
    /// motion writer, then decodes the motion attachment and returns the middle
    /// scanline. Every expectation is stated in terms of the previous-frame inputs:
    /// both camera matrices are the identity and no jitter is applied.
    /// </summary>
    private unsafe Decoded[] RenderInstancedMotion(
        VulkanDevice device, Instance[] instances, float cameraDeltaX, float cameraDeltaY,
        float[]? previousView = null, float[]? previousProjection = null)
    {
        VulkanDevice seam = device;

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        var variant = new ShaderCorpus.ShaderVariant
        {
            Name = "taa-instanced",
            TaaMotion = 1,
            TaaMotionLocation = 2,
        };

        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram("instanced", files, includes, variant);
        Assert.NotEmpty(stages);
        int program = LinkFromCorpus(seam, stages, "instanced");

        Assert.True(seam.GetUniformLocation(program, "taaRenderSize") >= 0,
            "instanced declares no taaRenderSize, so it is not a motion writer");
        Assert.True(seam.GetUniformLocation(program, "prevModelViewMatrix") >= 0,
            "instanced declares no prevModelViewMatrix, so it has no previous camera");

        BindEveryDeclaredSampler(device, seam, program);

        // Primary stand-in: colour, glow and the motion attachment at index 2.
        int colour = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int glow = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int motion = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba16f,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int depth = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.DepthComponent32,
            EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
        seam.SetTextureParameter(motion, OptimumGlConstants.TextureMinFilter, 9728);
        seam.SetTextureParameter(motion, OptimumGlConstants.TextureMagFilter, 9728);

        int scene = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment0, colour, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment1, glow, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment2, motion, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.DepthAttachment, depth, 0);
        seam.SetDrawBuffers(scene, 0b111);
        Assert.True(seam.CheckFramebufferComplete(scene, out string status), status);

        int mesh = seam.CreateMesh(BuildInstancedQuad(instances), staticDraw: false);
        Assert.True(mesh > 0, seam.GetError() ?? "mesh upload failed");

        seam.BeginFrame();
        seam.BindFramebuffer(scene);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.ClearColor(1, 0f, 0f, 0f, 1f);
        seam.ClearColor(2, 0f, 0f, 0f, 0f);
        seam.ClearDepth(1f);

        seam.UseProgram(program);
        SetMatrix(seam, program, "projectionMatrix", Identity);
        SetMatrix(seam, program, "modelViewMatrix", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixFar", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixNear", Identity);
        SetSceneUniforms(seam, program);

        SetMatrix(seam, program, "prevProjectionMatrix", previousProjection ?? Identity);
        SetMatrix(seam, program, "prevModelViewMatrix", previousView ?? Identity);
        SetFloat3(seam, program, "cameraPosDelta", cameraDeltaX, cameraDeltaY, 0f);
        SetFloat2(seam, program, "taaRenderSize", Size, Size);
        SetFloat2(seam, program, "taaJitterPx", 0f, 0f);

        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(true);
        seam.SetDepthMask(true);
        seam.SetDepthFunc(0x203);   // GL_LEQUAL
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawMeshInstanced(mesh, instances.Length);

        byte[] decoded = DecodeMotion(seam, motion);
        seam.Present();

        AssertClean(seam);

        var row = new Decoded[Size];
        for (int x = 0; x < Size; x++)
        {
            int offset = ((Size / 2) * Size + x) * 4;
            row[x] = new Decoded(
                (decoded[offset] / 255f * 2f - 1f) * DecodeScale,
                (decoded[offset + 1] / 255f * 2f - 1f) * DecodeScale,
                decoded[offset + 2] / 255f,
                decoded[offset + 3] / 255f);
        }
        return row;
    }

    /// <summary>
    /// Reads the RGBA16F motion attachment through an RGBA8 decode pass, because
    /// the seam's readback is fixed at four bytes per pixel from attachment 0.
    /// Unlike the other writers' harnesses this one carries the reactive channel
    /// too, because "no history" and "reactive" are one decision here.
    /// </summary>
    private unsafe byte[] DecodeMotion(VulkanDevice seam, int motionTexture)
    {
        const string decodeVertex = @"#version 330 core
layout(location = 0) in vec3 xyz;
void main(void) { gl_Position = vec4(xyz, 1.0); }
";
        const string decodeFragment = @"#version 330 core
uniform sampler2D motionTex;
uniform float decodeScale;
layout(location = 0) out vec4 outColor;
void main(void)
{
	vec4 m = texelFetch(motionTex, ivec2(gl_FragCoord.xy), 0);
	outColor = vec4(
		clamp(m.r / decodeScale * 0.5 + 0.5, 0.0, 1.0),
		clamp(m.g / decodeScale * 0.5 + 0.5, 0.0, 1.0),
		clamp(m.b, 0.0, 1.0),
		clamp(m.a, 0.0, 1.0));
}
";
        int decode = LinkFromCorpus(seam, new List<ShaderStageSource>
        {
            new() { Stage = EnumShaderType.VertexShader, Code = decodeVertex, PrefixCode = "", Filename = "taa-instanced-decode.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = decodeFragment, PrefixCode = "", Filename = "taa-instanced-decode.fsh" },
        }, "taa-instanced-decode");

        var quad = new MeshData(4, 6, withNormals: false, withUv: false, withRgba: false, withFlags: false)
        {
            xyz = new[] { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f },
            VerticesCount = 4,
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
            IndicesCount = 6,
        };
        int quadMesh = seam.CreateMesh(quad, staticDraw: true);
        Assert.True(quadMesh > 0, seam.GetError() ?? "decode mesh upload failed");

        int target = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
        seam.SetDrawBuffers(framebuffer, 0b1);

        seam.BindFramebuffer(framebuffer);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.UseProgram(decode);
        seam.SetSamplerUnit(decode, "motionTex", 15);
        seam.BindTexture(15, motionTexture);
        SetFloat(seam, decode, "decodeScale", DecodeScale);
        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(false);
        seam.SetDepthMask(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawMesh(quadMesh);

        var pixels = new byte[Size * Size * 4];
        fixed (byte* destination = pixels)
        {
            seam.BindFramebuffer(framebuffer);
            seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
        }
        return pixels;
    }

    /// <summary>
    /// A quad in instanced.vsh's attribute layout - xyz, uv, rgbaBlockIn, flags,
    /// with no normals, which is what puts uv on location 1 - plus the instance
    /// stream in OptimumInstanceMotion's layout: light rgba at 4, transform at
    /// 5..8, previous transform at 9..12 and the TAA metadata at 13. The instance
    /// values are written by hand rather than through
    /// OptimumInstanceMotion.WriteInstance so the shader contract is tested
    /// independently of the history bookkeeping (which
    /// Optimum.Tests/taa-instanced-motion-tests.cs drives directly).
    /// </summary>
    private static MeshData BuildInstancedQuad(Instance[] instances)
    {
        var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);

        float[] positions =
        {
            -0.5f, -0.5f, 0f,
             0.5f, -0.5f, 0f,
             0.5f,  0.5f, 0f,
            -0.5f,  0.5f, 0f,
        };
        float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };

        for (int i = 0; i < 4; i++)
        {
            mesh.AddVertexWithFlags(
                positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                uvs[i * 2], uvs[i * 2 + 1],
                ColorUtil.WhiteArgb,
                flags: UpNormalFlags);
        }

        foreach (int index in new[] { 0, 1, 2, 0, 2, 3 })
        {
            mesh.AddIndex(index);
        }

        CustomMeshDataPartFloat part = OptimumInstanceMotion.CreateInstanceFloats(instances.Length);
        for (int i = 0; i < instances.Length; i++)
        {
            int j = i * OptimumInstanceMotion.InstanceFloats;
            part.Values[j + OptimumInstanceMotion.LightOffset] = 1f;
            part.Values[j + OptimumInstanceMotion.LightOffset + 1] = 1f;
            part.Values[j + OptimumInstanceMotion.LightOffset + 2] = 1f;
            part.Values[j + OptimumInstanceMotion.LightOffset + 3] = 1f;
            Array.Copy(instances[i].Transform, 0, part.Values, j + OptimumInstanceMotion.TransformOffset, 16);
            Array.Copy(instances[i].PreviousTransform, 0, part.Values, j + OptimumInstanceMotion.PrevTransformOffset, 16);
            part.Values[j + OptimumInstanceMotion.MetaOffset] = instances[i].HistoryValid ? 1f : 0f;
            part.Values[j + OptimumInstanceMotion.MetaOffset + 1] = instances[i].Reactive;
        }
        part.Count = instances.Length * OptimumInstanceMotion.InstanceFloats;
        mesh.CustomFloats = part;
        return mesh;
    }

    /// <summary>
    /// Enough of the lighting, fog and shadow surface to keep the fragment alive:
    /// a fragment below alphaTest is discarded before it can write a motion
    /// vector, and the test would read the cleared attachment instead.
    /// </summary>
    private static void SetSceneUniforms(VulkanDevice seam, int program)
    {
        SetFloat(seam, program, "alphaTest", -1f);
        SetFloat(seam, program, "viewDistance", 1024f);
        SetFloat(seam, program, "viewDistanceLod0", 1024f);
        SetFloat(seam, program, "zNear", 0.1f);
        SetFloat(seam, program, "zFar", 1024f);
        SetFloat(seam, program, "fogMinIn", 0f);
        SetFloat(seam, program, "fogDensityIn", 0f);
        SetFloat(seam, program, "shadowRangeFar", 1024f);
        SetFloat(seam, program, "shadowRangeNear", 64f);
        SetFloat(seam, program, "shadowMapWidthInv", 1f);
        SetFloat(seam, program, "shadowMapHeightInv", 1f);
        SetFloat(seam, program, "shadowIntensity", 0f);
        SetFloat(seam, program, "extraGodray", 0f);
        SetFloat(seam, program, "ssaoAttn", 0f);
        SetInt(seam, program, "applySsao", 0);
        SetInt(seam, program, "normalShaded", 0);
        SetInt(seam, program, "skyShaded", 0);
        SetFloat3(seam, program, "rgbaAmbientIn", 1f, 1f, 1f);
        SetFloat4(seam, program, "rgbaFogIn", 1f, 1f, 1f, 1f);
        SetFloat4(seam, program, "averageColor", 1f, 1f, 1f, 1f);
        SetFloat2(seam, program, "frameSize", Size, Size);
        SetFloat3(seam, program, "playerpos", 0f, 0f, 0f);
        SetFloat(seam, program, "timeCounter", 0f);
        SetFloat(seam, program, "windWaveCounter", 0f);
        SetFloat(seam, program, "windWaveCounterHighFreq", 0f);
        SetFloat(seam, program, "waterWaveCounter", 0f);
        SetFloat(seam, program, "windSpeed", 0f);
        SetFloat(seam, program, "globalWarpIntensity", 0f);
        SetFloat(seam, program, "glitchWaviness", 0f);
    }

    private static int LinkFromCorpus(
        VulkanDevice seam, List<ShaderStageSource> stages, string name) =>
        MotionFixture.LinkFromCorpus(seam, stages, name, oit: false);

    private static bool TryCreateDevice(ITestOutputHelper output, out VulkanDevice? device) =>
        GpuTest.TryCreateDevice(output, out device);

    private static void AssertClean(VulkanDevice seam) => GpuTest.AssertClean(seam);

}

/// <summary>
/// The standard-shader motion writer as the TAA P4 "movers" drive it: a
/// block-entity model - the helve hammer's head, the resonator's disc, the pot
/// lid, a falling block - drawn with <c>dontWarpVertices</c> set to "no warp" and
/// a previous model matrix from <c>OptimumStandardMotion</c>, <b>under a jittered
/// projection</b>.
///
/// The jitter is the point. Every P3 GPU test ran with <c>taaJitterPx = 0</c>, and
/// with the identity projection those tests use the NDC shear
/// <c>P[8] -= 2*jx/W</c> is a no-op on a quad at z = 0, so a jittered case there
/// would have proved nothing. This file uses the perspective-SHAPED matrix from
/// TaaLiquidMotionTests (clip.w = -z_view, quad at z = -1), where the shear
/// displaces the raster position by exactly jx pixels - so if the writer forgot
/// either half of the convention (subtract the jitter from the current pixel,
/// reproject the previous one through an UNJITTERED matrix) the vector comes back
/// off by the jitter and these assertions fail.
///
/// What is being proved:
/// - a mover's previous model matrix is exactly its pixel displacement, and the
///   answer does not change when the frame is jittered;
/// - a mover that did not move is exactly zero even under jitter, because a
///   converged block entity that shimmers is what a leaked jitter looks like;
/// - without usable history the vector is camera motion only, never the stale
///   previous matrix - the case every one of these renderers hits on its first
///   frame, on a mesh swap, and after a reset.
///
/// As in TaaStandardMotionWriterTests the RGBA16F attachment comes back through
/// an RGBA8 decode pass, because the seam's readback is fixed at four bytes per
/// pixel from colour attachment 0.
/// </summary>
public class TaaMoverMotionTests
{
    private readonly ITestOutputHelper _output;

    public TaaMoverMotionTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    /// <summary>Pixels per unit in the decode pass: mv/DecodeScale * 0.5 + 0.5 into an RGBA8 channel.</summary>
    private const float DecodeScale = 32f;

    /// <summary>Normal pointing up, no glow, no wind-mode bits, so no vertex warp runs.</summary>
    private const int UpNormalFlags = 7 << 18;

    /// <summary>
    /// standard.vsh: 0 = full warp, 2 = the held item's quarter warp, anything else
    /// = none. Block-entity models pass "none", which is what every renderer in
    /// this phase does.
    /// </summary>
    private const int WarpNone = 1;

    /// <summary>The view-space z the test quad sits at; clip.w = -z = 1 there.</summary>
    private const float QuadViewZ = -1f;

    private static readonly float[] Identity =
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    private static float[] Translation(float x, float y, float z) => new[]
    {
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        x,  y,  z,  1f,
    };

    /// <summary>
    /// The perspective-SHAPED projection, column-major, plus the NDC jitter shear
    /// the frame contract applies (`P[8] -= 2*jx/W; P[9] -= 2*jy/H`).
    ///
    /// clip = (x + m02*z, y + m12*z, -z - 1, -z). At z = -1 that is clip.w = 1, so
    /// ndc.xy = (x + 2*jx/W, y + 2*jy/H) and the raster position moves by exactly
    /// (jx, jy) pixels - the property that makes a jittered assertion mean
    /// something. ndc.z = 0, so window depth is 0.5.
    /// </summary>
    private static float[] Projection(float jitterX, float jitterY)
    {
        var p = new float[16];
        p[0] = 1f;                          // m00
        p[5] = 1f;                          // m11
        p[8] = -2f * jitterX / Size;        // m02, the jitter shear
        p[9] = -2f * jitterY / Size;        // m12
        p[10] = -1f;                        // m22
        p[11] = -1f;                        // m32: clip.w = -z_view
        p[14] = -1f;                        // m23
        p[15] = 0f;                         // m33
        return p;
    }

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// A block entity that is not moving, under a still camera, in a jittered
    /// frame, is exactly zero motion - and still stamps its own depth, so the
    /// resolve accepts the pixel instead of camera-reprojecting it.
    ///
    /// A writer that forgot `gl_FragCoord.xy - taaJitterPx` would report the jitter
    /// itself here, which is a sub-pixel wobble on every still block entity in the
    /// world and precisely the artefact TAA is supposed to remove.
    /// </summary>
    [SkippableTheory]
    [InlineData(0f, 0f)]
    [InlineData(0.37f, -0.24f)]
    [InlineData(-0.5f, 0.5f)]
    public void AStillMoverIsZeroMotionEvenUnderJitter(float jitterX, float jitterY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Decoded centre = RenderMoverMotion(device!,
                previousModelMatrix: Identity,
                historyValid: 1,
                jitterX: jitterX,
                jitterY: jitterY,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f);

            _output.WriteLine($"jitter ({jitterX}, {jitterY}): mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"writerDepth = {centre.WriterDepth}");

            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The model matrix the mover was drawn with last frame is its motion: with
    /// identity view matrices a translation of d displaces the pixel by exactly
    /// d * 0.5 * renderSize, and the sign is "where the pixel was", not "where it
    /// went".
    ///
    /// The jitter is deliberately large and asymmetric here: it enters the current
    /// pixel through the shear and must leave again through `- taaJitterPx`,
    /// leaving the same answer as the unjittered frame. The companion test below
    /// asserts that equality directly.
    /// </summary>
    [SkippableTheory]
    [InlineData(0.25f, 0f, 0.5f, -0.5f)]
    [InlineData(0f, -0.125f, -0.5f, 0.5f)]
    [InlineData(-0.1875f, 0.0625f, 0.31f, 0.47f)]
    public void APreviousModelMatrixIsTheExactPixelDisplacementUnderJitter(
        float modelX, float modelY, float jitterX, float jitterY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Decoded centre = RenderMoverMotion(device!,
                previousModelMatrix: Translation(modelX, modelY, 0f),
                historyValid: 1,
                jitterX: jitterX,
                jitterY: jitterY,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f);

            float expectedX = modelX * 0.5f * Size;
            float expectedY = modelY * 0.5f * Size;

            _output.WriteLine($"model ({modelX}, {modelY}) jitter ({jitterX}, {jitterY}): " +
                              $"mv = ({centre.MotionX}, {centre.MotionY}), expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The convention says the vector excludes the jitter. That is only testable as
    /// an equality between two frames of the same scene that differ by nothing but
    /// the jitter phase, which is what this is: same previous matrix, same camera,
    /// two different Halton-sized offsets, one answer.
    /// </summary>
    [SkippableFact]
    public void TheVectorIsTheSameWhicheverJitterPhaseTheFrameIsOn()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            float[] previous = Translation(0.25f, -0.125f, 0f);

            Decoded unjittered = RenderMoverMotion(device!, previous, 1, 0f, 0f, 0f, 0f);
            Decoded jittered = RenderMoverMotion(device!, previous, 1, 0.5f, -0.5f, 0f, 0f);

            _output.WriteLine($"unjittered = ({unjittered.MotionX}, {unjittered.MotionY}), " +
                              $"jittered = ({jittered.MotionX}, {jittered.MotionY})");

            Assert.InRange(jittered.MotionX, unjittered.MotionX - 0.3f, unjittered.MotionX + 0.3f);
            Assert.InRange(jittered.MotionY, unjittered.MotionY - 0.3f, unjittered.MotionY + 0.3f);
        }
    }

    /// <summary>
    /// The case every one of these renderers hits on its first frame, whenever its
    /// mesh is re-uploaded, and after a reset: <c>OptimumStandardMotion.Apply</c>
    /// reports no usable history, so the writer must not touch the previous model
    /// matrix at all. It treats the surface as static in the world, so only the
    /// camera's own movement displaces it, and C# raises <c>taaReactive</c> for the
    /// same draw.
    ///
    /// The previous model matrix here is a large translation on purpose: if the
    /// shader took the history branch anyway, the vector would be that instead of
    /// the camera delta, and the test would say so rather than merely noticing
    /// "some motion".
    /// </summary>
    [SkippableFact]
    public void WithoutUsableHistoryTheMoverFallsBackToCameraMotionOnly()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float cameraDeltaX = 0.25f;
            const float cameraDeltaY = -0.125f;

            Decoded centre = RenderMoverMotion(device!,
                previousModelMatrix: Translation(-0.5f, 0.5f, 0f),
                historyValid: 0,
                jitterX: 0.42f,
                jitterY: 0.13f,
                cameraDeltaX: cameraDeltaX,
                cameraDeltaY: cameraDeltaY);

            float expectedX = cameraDeltaX * 0.5f * Size;
            float expectedY = cameraDeltaY * 0.5f * Size;

            _output.WriteLine($"no history: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
        }
    }

    // ---------------------------------------------------------------- harness

    private readonly struct Decoded
    {
        public Decoded(float motionX, float motionY, float writerDepth)
        {
            MotionX = motionX;
            MotionY = motionY;
            WriterDepth = writerDepth;
        }

        public float MotionX { get; }
        public float MotionY { get; }
        public float WriterDepth { get; }
    }

    /// <summary>
    /// Draws one block-entity-shaped quad with the real standard program compiled
    /// as a motion writer, then decodes the motion attachment and returns its
    /// centre pixel. The current model matrix is always the identity and this
    /// frame's warp state is pinned to a no-op, so every expectation is stated
    /// entirely in terms of the previous-frame inputs and the jitter.
    /// </summary>
    private unsafe Decoded RenderMoverMotion(
        VulkanDevice device,
        float[] previousModelMatrix,
        int historyValid,
        float jitterX,
        float jitterY,
        float cameraDeltaX,
        float cameraDeltaY)
    {
        VulkanDevice seam = device;

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        var variant = new ShaderCorpus.ShaderVariant
        {
            Name = "taa-mover",
            TaaMotion = 1,
            TaaMotionLocation = 2,
        };

        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram("standard", files, includes, variant);
        Assert.NotEmpty(stages);
        int program = LinkFromCorpus(seam, stages, "standard");

        Assert.True(seam.GetUniformLocation(program, "taaJitterPx") >= 0,
            "standard declares no taaJitterPx, so it cannot exclude the jitter");
        Assert.True(seam.GetUniformLocation(program, "prevModelMatrix") >= 0,
            "standard declares no prevModelMatrix, so a mover has no previous transform to use");

        BindEveryDeclaredSampler(device, seam, program);

        // Primary stand-in: colour, glow and the motion attachment at index 2.
        int colour = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int glow = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int motion = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba16f,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int depth = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.DepthComponent32,
            EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
        seam.SetTextureParameter(motion, OptimumGlConstants.TextureMinFilter, 9728);
        seam.SetTextureParameter(motion, OptimumGlConstants.TextureMagFilter, 9728);

        int scene = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment0, colour, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment1, glow, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment2, motion, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.DepthAttachment, depth, 0);
        seam.SetDrawBuffers(scene, 0b111);
        Assert.True(seam.CheckFramebufferComplete(scene, out string status), status);

        int mesh = seam.CreateMesh(BuildQuad(), staticDraw: true);
        Assert.True(mesh > 0, seam.GetError() ?? "mesh upload failed");

        seam.BeginFrame();
        seam.BindFramebuffer(scene);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.ClearColor(1, 0f, 0f, 0f, 1f);
        seam.ClearColor(2, 0f, 0f, 0f, 0f);
        // The previous call's decode pass left depth writes off, and a depth clear honours that.
        seam.SetDepthMask(true);
        seam.ClearDepth(1f);

        seam.UseProgram(program);

        // This frame is jittered; the previous projection never is. That pair is
        // the whole convention under test.
        SetMatrix(seam, program, "projectionMatrix", Projection(jitterX, jitterY));
        SetMatrix(seam, program, "prevProjectionMatrix", Projection(0f, 0f));
        SetMatrix(seam, program, "viewMatrix", Identity);
        SetMatrix(seam, program, "prevViewMatrix", Identity);
        SetMatrix(seam, program, "modelMatrix", Identity);
        SetMatrix(seam, program, "prevModelMatrix", previousModelMatrix);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixFar", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixNear", Identity);

        SetSceneUniforms(seam, program);
        SetWarpUniforms(seam, program);

        SetInt(seam, program, "taaHistoryValid", historyValid);
        SetFloat(seam, program, "taaReactive", historyValid != 0 ? 0f : 1f);
        SetFloat3(seam, program, "cameraPosDelta", cameraDeltaX, cameraDeltaY, 0f);
        SetFloat2(seam, program, "taaRenderSize", Size, Size);
        SetFloat2(seam, program, "taaJitterPx", jitterX, jitterY);

        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(true);
        seam.SetDepthMask(true);
        seam.SetDepthFunc(0x203);   // GL_LEQUAL
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawMesh(mesh);

        byte[] decoded = DecodeMotion(seam, motion);
        seam.Present();

        int offset = ((Size / 2) * Size + Size / 2) * 4;

        AssertClean(seam);

        return new Decoded(
            (decoded[offset] / 255f * 2f - 1f) * DecodeScale,
            (decoded[offset + 1] / 255f * 2f - 1f) * DecodeScale,
            decoded[offset + 2] / 255f);
    }

    /// <summary>
    /// Reads the RGBA16F motion attachment through an RGBA8 decode pass, because
    /// the seam's readback is fixed at four bytes per pixel from attachment 0, and
    /// reads it back inside the same frame.
    /// </summary>
    private unsafe byte[] DecodeMotion(VulkanDevice seam, int motionTexture)
    {
        const string decodeVertex = @"#version 330 core
layout(location = 0) in vec3 xyz;
void main(void) { gl_Position = vec4(xyz, 1.0); }
";
        const string decodeFragment = @"#version 330 core
uniform sampler2D motionTex;
uniform float decodeScale;
layout(location = 0) out vec4 outColor;
void main(void)
{
	vec4 m = texelFetch(motionTex, ivec2(gl_FragCoord.xy), 0);
	outColor = vec4(
		clamp(m.r / decodeScale * 0.5 + 0.5, 0.0, 1.0),
		clamp(m.g / decodeScale * 0.5 + 0.5, 0.0, 1.0),
		clamp(m.a, 0.0, 1.0),
		1.0);
}
";
        int decode = LinkFromCorpus(seam, new List<ShaderStageSource>
        {
            new() { Stage = EnumShaderType.VertexShader, Code = decodeVertex, PrefixCode = "", Filename = "taa-mover-decode.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = decodeFragment, PrefixCode = "", Filename = "taa-mover-decode.fsh" },
        }, "taa-mover-decode");

        var quad = new MeshData(4, 6, withNormals: false, withUv: false, withRgba: false, withFlags: false)
        {
            xyz = new[] { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f },
            VerticesCount = 4,
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
            IndicesCount = 6,
        };
        int quadMesh = seam.CreateMesh(quad, staticDraw: true);
        Assert.True(quadMesh > 0, seam.GetError() ?? "decode mesh upload failed");

        int target = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
        seam.SetDrawBuffers(framebuffer, 0b1);

        seam.BindFramebuffer(framebuffer);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.UseProgram(decode);
        seam.SetSamplerUnit(decode, "motionTex", 15);
        seam.BindTexture(15, motionTexture);
        SetFloat(seam, decode, "decodeScale", DecodeScale);
        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(false);
        seam.SetDepthMask(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawMesh(quadMesh);

        var pixels = new byte[Size * Size * 4];
        fixed (byte* destination = pixels)
        {
            seam.BindFramebuffer(framebuffer);
            seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
        }
        return pixels;
    }

    /// <summary>
    /// A quad in standard.vsh's attribute layout (xyz, uv, rgba, flags), placed at
    /// view-space z = -1 so the perspective-shaped projection gives it clip.w = 1
    /// and the jitter shear a real pixel displacement. It is deliberately smaller
    /// than the viewport so the centre pixel stays covered under every jitter.
    /// </summary>
    private static MeshData BuildQuad()
    {
        var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);

        float[] positions =
        {
            -0.5f, -0.5f, QuadViewZ,
             0.5f, -0.5f, QuadViewZ,
             0.5f,  0.5f, QuadViewZ,
            -0.5f,  0.5f, QuadViewZ,
        };
        float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };

        for (int i = 0; i < 4; i++)
        {
            mesh.AddVertexWithFlags(
                positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                uvs[i * 2], uvs[i * 2 + 1],
                Vintagestory.API.MathTools.ColorUtil.WhiteArgb,
                flags: UpNormalFlags);
        }

        foreach (int index in new[] { 0, 1, 2, 0, 2, 3 })
        {
            mesh.AddIndex(index);
        }
        return mesh;
    }

    /// <summary>
    /// Enough of the lighting, fog and overlay surface to keep the fragment alive:
    /// a discarded fragment writes no motion vector and the test would read the
    /// cleared attachment instead. alphaTest is pushed below zero so nothing can
    /// discard at all, and dontWarpVertices is the block-entity value.
    /// </summary>
    private static void SetSceneUniforms(VulkanDevice seam, int program)
    {
        SetInt(seam, program, "dontWarpVertices", WarpNone);
        SetInt(seam, program, "fadeFromSpheresFog", 0);
        SetInt(seam, program, "addRenderFlags", 0);
        SetInt(seam, program, "extraGlow", 0);
        SetFloat(seam, program, "extraZOffset", 0f);

        SetFloat(seam, program, "alphaTest", -1f);
        SetFloat(seam, program, "viewDistance", 1024f);
        SetFloat(seam, program, "viewDistanceLod0", 1024f);
        SetFloat(seam, program, "zNear", 0.1f);
        SetFloat(seam, program, "zFar", 1024f);
        SetFloat(seam, program, "fogMinIn", 0f);
        SetFloat(seam, program, "fogDensityIn", 0f);
        SetFloat(seam, program, "shadowRangeFar", 1024f);
        SetFloat(seam, program, "shadowRangeNear", 64f);
        SetFloat(seam, program, "shadowMapWidthInv", 1f);
        SetFloat(seam, program, "shadowMapHeightInv", 1f);
        SetFloat(seam, program, "shadowIntensity", 0f);
        SetFloat(seam, program, "damageEffect", 0f);
        SetFloat(seam, program, "overlayOpacity", 0f);
        SetFloat(seam, program, "extraGodray", 0f);
        SetFloat(seam, program, "ssaoAttn", 0f);
        SetInt(seam, program, "applySsao", 0);
        SetInt(seam, program, "tempGlowMode", 0);
        SetInt(seam, program, "normalShaded", 0);
        SetInt(seam, program, "skyShaded", 0);
        SetFloat3(seam, program, "rgbaAmbientIn", 1f, 1f, 1f);
        SetFloat4(seam, program, "rgbaLightIn", 1f, 1f, 1f, 1f);
        SetFloat4(seam, program, "rgbaFogIn", 1f, 1f, 1f, 1f);
        SetFloat4(seam, program, "rgbaGlowIn", 0f, 0f, 0f, 0f);
        SetFloat4(seam, program, "rgbaTint", 1f, 1f, 1f, 1f);
        SetFloat4(seam, program, "averageColor", 1f, 1f, 1f, 1f);
        SetFloat2(seam, program, "frameSize", Size, Size);
    }

    /// <summary>
    /// Both halves of the warp state pinned to the same no-op, so nothing in this
    /// file's expectations comes from vertex animation - a block-entity model
    /// passes "no warp" anyway, and P3 already covers the warp branches.
    /// </summary>
    private static void SetWarpUniforms(VulkanDevice seam, int program)
    {
        foreach (string prefix in new[] { "", "prev" })
        {
            SetFloat(seam, program, Name(prefix, "timeCounter"), 0f);
            SetFloat(seam, program, Name(prefix, "windWaveCounter"), 0f);
            SetFloat(seam, program, Name(prefix, "windWaveCounterHighFreq"), 0f);
            SetFloat(seam, program, Name(prefix, "waterWaveCounter"), 0f);
            SetFloat(seam, program, Name(prefix, "windSpeed"), 0f);
            SetFloat(seam, program, Name(prefix, "globalWarpIntensity"), 0f);
            SetFloat(seam, program, Name(prefix, "glitchWaviness"), 0f);
            SetFloat(seam, program, Name(prefix, "windWaveIntensity"), 1f);
            SetFloat(seam, program, Name(prefix, "waterWaveIntensity"), 1f);
            SetInt(seam, program, Name(prefix, "perceptionEffectId"), 1);
            SetFloat(seam, program, Name(prefix, "perceptionEffectIntensity"), 0f);
            SetFloat3(seam, program, Name(prefix, "playerpos"), 0f, 0f, 0f);
        }
    }

    private static string Name(string prefix, string uniform)
    {
        if (prefix.Length == 0) return uniform;
        return prefix + char.ToUpperInvariant(uniform[0]) + uniform.Substring(1);
    }

    private static int LinkFromCorpus(
        VulkanDevice seam, List<ShaderStageSource> stages, string name) =>
        MotionFixture.LinkFromCorpus(seam, stages, name, oit: false);

    private static bool TryCreateDevice(ITestOutputHelper output, out VulkanDevice? device) =>
        GpuTest.TryCreateDevice(output, out device);

    private static void AssertClean(VulkanDevice seam) => GpuTest.AssertClean(seam);

}
