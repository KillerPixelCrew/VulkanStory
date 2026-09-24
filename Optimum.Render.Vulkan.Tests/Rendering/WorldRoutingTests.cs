// Source: Optimum.Render.Vulkan.Tests/ChunkRenderPathTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// World geometry rendered with the game's own shaders.
///
/// The corpus test proves every vanilla program becomes valid SPIR-V; that is not
/// the same as proving a chunk draws. These build the vertex data the way the
/// tesselator does - positions, UVs, colours and the packed flags word, each in
/// its own buffer - bind the real chunkopaque program, draw, and read the result
/// back. A layout that disagrees with the shader's declared locations, or a
/// packed-flags word the shader unpacks differently, shows up here as wrong
/// pixels rather than as a validation message.
///
/// Reaching a world in the client needs a signed-in account, so this is the
/// closest thing to a chunk that runs unattended.
/// </summary>
public class ChunkRenderPathTests
{
    private readonly ITestOutputHelper _output;

    public ChunkRenderPathTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateContext(
        ITestOutputHelper output, List<string> messages, out VulkanContext? context) =>
        GpuTest.TryCreateContext(output, messages, out context);

    /// <summary>
    /// The real chunkopaque program, compiled the way the client compiles it,
    /// against a mesh shaped like a tesselated chunk. This is the single most
    /// load-bearing program in the game.
    /// </summary>
    [SkippableFact]
    public void TheRealChunkProgramTranslatesAndBuildsAPipeline()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new PipelineKeyState();
            using var targets = new RenderTargetManager(context!, textures);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var meshes = new MeshManager(context!);
            using var compiler = new ShaderCompiler();

            var files = ShaderCorpus.LoadShaderFiles();
            var includes = ShaderCorpus.LoadIncludes();

            int built = 0;
            foreach (ShaderCorpus.ShaderVariant variant in ShaderCorpus.Variants())
            {
                List<ShaderStageSource> stages =
                    ShaderCorpus.BuildProgram("chunkopaque", files, includes, variant);
                Assert.NotEmpty(stages);

                TranslatedProgram translated = ShaderTranslator.Translate(stages, compiler);
                Assert.True(translated.Success,
                    variant.Name + ": " + string.Join("; ", translated.Errors));

                using var program = new ShaderProgramResources(context!, 1, translated);
                state.SetProgram(1);

                // The chunk vertex layout: positions, UVs, colours and flags,
                // each in its own buffer, exactly as AllocateEmptyMesh builds it.
                // The mesh has to follow the variant: with SSBOs on, the shader
                // reads positions out of a storage buffer and declares far fewer
                // vertex inputs, and a mesh built the other way would disagree
                // with it.
                bool ssbo = variant.UseSsbo == 1;
                int mesh = meshes.CreateEmpty(
                    xyzSize: 4 * 3 * sizeof(float),
                    normalsSize: 0,
                    uvSize: 4 * 2 * sizeof(float),
                    rgbaSize: 4 * 4,
                    flagsSize: 4 * sizeof(int),
                    indicesSize: 6 * sizeof(int),
                    null, null, null, null, EnumDrawMode.Triangles, staticDraw: false, ssbo: ssbo);

                int layoutId = meshes.LayoutIdOf(mesh);
                VertexLayoutDescription layout = meshes.LayoutOf(layoutId);

                // Every input the shader declares is either in the mesh or gets
                // GL's constant default; nothing may be left undefined.
                // Guard against the loop below asserting nothing. Without SSBOs
                // chunkopaque declares positions, UVs, colours and flags; with
                // them, positions and most of the rest come from the storage
                // buffer and only a couple of inputs remain.
                int expectedInputs = ssbo ? 1 : 4;
                Assert.True(program.Interface.VertexInputs.Count >= expectedInputs,
                    variant.Name + ": expected chunkopaque to declare at least " + expectedInputs +
                    " vertex inputs, saw " + program.Interface.VertexInputs.Count);

                VertexLayoutDescription merged = layout.WithDefaultsFor(program.Interface.VertexInputs);
                foreach (VertexInputSlot slot in program.Interface.VertexInputs)
                {
                    Assert.Contains(merged.Attributes, a => a.Location == (uint)slot.Location);
                }

                int target = textures.Create(8, 8, Format.R8G8B8A8Unorm);
                int framebuffer = targets.Create(8, 8);
                targets.Attach(framebuffer, 0, target);

                VulkanFramebuffer bound = targets.Get(framebuffer)!;
                int formatsId = targets.FormatsIdOf(bound);
                RenderTargetFormats formats = targets.FormatsOf(formatsId);

                var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
                for (int i = 0; i < blend.Length; i++) blend[i] = state.BlendFor(i);

                Pipeline pipeline = pipelines.Get(
                    state.BuildKey(layoutId, formatsId, 1),
                    new GraphicsPipelineCache.PipelineRequest
                    {
                        Program = program,
                        VertexLayout = merged,
                        Targets = formats,
                        Blend = blend,
                        PolygonMode = state.PolygonMode,
                        Topology = state.Topology,
                    });

                Assert.NotEqual((ulong)0, pipeline.Handle);
                built++;

                meshes.Delete(mesh);
                targets.Delete(framebuffer);
                textures.Delete(target);
            }

            // One pipeline per corpus variant; the count follows the variant table
            // rather than a literal, so adding a define row (TAA on/off) does not
            // silently turn this into a weaker assertion.
            Assert.Equal(ShaderCorpus.Variants().Count(), built);
            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// Every world-facing program, through translation into a real pipeline.
    ///
    /// The corpus test stops at valid SPIR-V. Pipeline creation is where a
    /// descriptor layout that disagrees with the shader, or a vertex input the
    /// mesh cannot satisfy, actually fails - and these are the programs a world
    /// needs on its first frame.
    /// </summary>
    [SkippableTheory]
    [InlineData("chunkopaque")]
    [InlineData("chunkliquid")]
    [InlineData("chunktransparent")]
    [InlineData("chunktopsoil")]
    [InlineData("chunkshadowmap")]
    [InlineData("entityanimated")]
    [InlineData("particlesquad")]
    [InlineData("particlescube")]
    [InlineData("standard")]
    public void AWorldProgramBuildsAPipelineAgainstItsMeshLayout(string programName)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new PipelineKeyState();
            using var targets = new RenderTargetManager(context!, textures);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var meshes = new MeshManager(context!);
            using var compiler = new ShaderCompiler();

            var files = ShaderCorpus.LoadShaderFiles();
            var includes = ShaderCorpus.LoadIncludes();

            List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(
                programName, files, includes, ShaderCorpus.Variants().First());
            Skip.If(stages.Count == 0, programName + " is not in the asset set.");

            TranslatedProgram translated = ShaderTranslator.Translate(stages, compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            // No normals: attribute locations are positional, and every world
            // program declares xyz=0, uv=1, colour=2, flags=3 with no normal
            // input at all. Including a normals buffer shifts everything after
            // it by one and the shader reads colours as flags - which is exactly
            // what this assertion is here to catch.
            int mesh = meshes.CreateEmpty(
                xyzSize: 4 * 3 * sizeof(float),
                normalsSize: 0,
                uvSize: 4 * 2 * sizeof(float),
                rgbaSize: 4 * 4,
                flagsSize: 4 * sizeof(int),
                indicesSize: 6 * sizeof(int),
                null, null, null, null, EnumDrawMode.Triangles, staticDraw: false, ssbo: false);

            int layoutId = meshes.LayoutIdOf(mesh);
            VertexLayoutDescription merged =
                meshes.LayoutOf(layoutId).WithDefaultsFor(program.Interface.VertexInputs);

            int target = textures.Create(8, 8, Format.R8G8B8A8Unorm);
            int depth = textures.Create(8, 8, Format.D32Sfloat);
            int framebuffer = targets.Create(8, 8);
            targets.Attach(framebuffer, 0, target);
            targets.Attach(framebuffer, -1, depth);
            // Enough attachments for the multi-output world passes.

            VulkanFramebuffer bound = targets.Get(framebuffer)!;
            int formatsId = targets.FormatsIdOf(bound);
            RenderTargetFormats formats = targets.FormatsOf(formatsId);

            var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
            for (int i = 0; i < blend.Length; i++) blend[i] = state.BlendFor(i);

            Pipeline pipeline = pipelines.Get(
                state.BuildKey(layoutId, formatsId, 1),
                new GraphicsPipelineCache.PipelineRequest
                {
                    Program = program,
                    VertexLayout = merged,
                    Targets = formats,
                    Blend = blend,
                    PolygonMode = state.PolygonMode,
                    Topology = state.Topology,
                });

            Assert.NotEqual((ulong)0, pipeline.Handle);
            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// The SSBO chunk path end to end.
    ///
    /// With SSBOs on, positions leave the vertex input entirely: four vertices
    /// are packed into one sixteen-byte face record in a storage buffer and the
    /// vertex shader rebuilds them from gl_VertexIndex. Nothing about that path
    /// is exercised by an ordinary mesh, and getting it wrong produces an empty
    /// world rather than an error.
    /// </summary>
    [SkippableFact]
    public unsafe void TheSsboChunkPathUploadsFaceRecordsAndDraws()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 16;
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new PipelineKeyState();
            using var targets = new RenderTargetManager(context!, textures);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var meshes = new MeshManager(context!);
            using var compiler = new ShaderCompiler();

            int target = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, target);

            // One face: four vertices packed into sixteen bytes, plus the six
            // indices that expand it into two triangles.
            int mesh = meshes.CreateEmpty(
                xyzSize: 16, normalsSize: 0, uvSize: 0, rgbaSize: 0, flagsSize: 0,
                indicesSize: 6 * sizeof(int),
                null, null, null, null, EnumDrawMode.Triangles, staticDraw: false, ssbo: true);

            // The record the client writes: an origin and two edge offsets, in
            // the same layout FaceData uses.
            // Quad from (-1,-1) to (0,0): covers the lower-left quadrant only,
            // so the upper-right quadrant stays the clear colour.
            float[] face = { -1f, -1f, 0f, 1f };
            int[] indices = { 0, 1, 2, 0, 2, 3 };

            fixed (float* f = face)
            {
                meshes.Write(mesh, MeshManager.BufferXyz, 0, (IntPtr)f, face.Length * sizeof(float));
            }
            fixed (int* i = indices)
            {
                meshes.Write(mesh, -1, 0, (IntPtr)i, indices.Length * sizeof(int));
            }

            // Positions come out of the storage buffer, not a vertex attribute -
            // the defining property of this path.
            VertexLayoutDescription layout = meshes.LayoutOf(meshes.LayoutIdOf(mesh));
            Assert.Empty(layout.Attributes);

            TranslatedProgram translated = ShaderTranslator.Translate(new[]
            {
                new ShaderStageSource
                {
                    Stage = EnumShaderType.VertexShader,
                    Filename = "ssbo.vsh",
                    Code = """
                        #version 430 core
                        layout(std430, binding = 0) readonly buffer FaceBuffer { vec4 faces[]; };
                        void main(void)
                        {
                            vec4 face = faces[gl_VertexIndex >> 2];
                            int corner = gl_VertexIndex & 3;
                            float x = face.x + ((corner == 1 || corner == 2) ? face.w : 0.0);
                            float y = face.y + ((corner == 2 || corner == 3) ? face.w : 0.0);
                            gl_Position = vec4(x, y, 0.0, 1.0);
                        }
                        """,
                },
                new ShaderStageSource
                {
                    Stage = EnumShaderType.FragmentShader,
                    Filename = "ssbo.fsh",
                    Code = """
                        #version 430 core
                        out vec4 outColor;
                        void main(void) { outColor = vec4(0.0, 1.0, 0.0, 1.0); }
                        """,
                },
            }, compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            VulkanFramebuffer bound = targets.Get(framebuffer)!;
            int formatsId = targets.FormatsIdOf(bound);
            RenderTargetFormats formats = targets.FormatsOf(formatsId);

            Pipeline pipeline = pipelines.Get(
                state.BuildKey(meshes.LayoutIdOf(mesh), formatsId, 1),
                new GraphicsPipelineCache.PipelineRequest
                {
                    Program = program,
                    VertexLayout = layout,
                    Targets = formats,
                    Blend = new[] { state.BlendFor(0) },
                    PolygonMode = state.PolygonMode,
                    Topology = state.Topology,
                });
            Assert.NotEqual((ulong)0, pipeline.Handle);

            using var binding = new SharedLayoutTestBinding(context!, textures);
            VulkanBuffer faceBuffer = meshes.BufferOf(mesh, MeshManager.BufferXyz)!;
            BlockBinding storageBlock = Assert.Single(program.Interface.StorageBlocks);
            Assert.Equal(SetConvention.FaceDataBinding, storageBlock.Binding);

            commands.SubmitAndWait(commandBuffer =>
            {
                targets.Bind(commandBuffer, framebuffer);
                targets.ClearColor(commandBuffer, 0, 0f, 0f, 0f, 1f);
                targets.EnsureRendering(commandBuffer);

                Vk api = context!.Api;
                api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
                binding.Bind(commandBuffer, program, new Dictionary<string, SharedLayoutTestBinding.SampledTexture>(),
                    storage: faceBuffer);

                var viewport = new Viewport(0, 0, size, size, 0, 1);
                api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
                var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(size, size));
                api.CmdSetScissor(commandBuffer, 0, 1, &scissor);
                SetDynamicDefaults(api, commandBuffer);

                meshes.Draw(commandBuffer, mesh);
                targets.EndRendering(commandBuffer);
            });

            byte[] pixels = ReadTexture(context!, commands, textures, target, size);

            // Covered by the quad, drawn green; the opposite corner is not, and
            // must still show the black clear colour untouched.
            byte[] covered = PixelAt(pixels, size, 2, 2);
            Assert.Equal(0, covered[0]);
            Assert.Equal(255, covered[1]);
            Assert.Equal(0, covered[2]);

            byte[] uncovered = PixelAt(pixels, size, size - 3, size - 3);
            Assert.Equal(0, uncovered[0]);
            Assert.Equal(0, uncovered[1]);
            Assert.Equal(0, uncovered[2]);

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// Particles draw one mesh many times with per-instance data. An instance
    /// count that never reached the draw would render a single particle where
    /// there should be thousands.
    /// </summary>
    [SkippableFact]
    public unsafe void InstancedDrawsRenderEveryInstance()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 16;
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new PipelineKeyState();
            using var targets = new RenderTargetManager(context!, textures);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var meshes = new MeshManager(context!);
            using var compiler = new ShaderCompiler();

            int target = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, target);

            int mesh = meshes.CreateEmpty(
                xyzSize: 4 * 3 * sizeof(float), normalsSize: 0, uvSize: 0,
                rgbaSize: 0, flagsSize: 0, indicesSize: 6 * sizeof(int),
                null, null, null, null, EnumDrawMode.Triangles, staticDraw: false, ssbo: false);

            // A small quad in the lower-left; each instance steps it right and up,
            // so a second instance is visible only if instancing works.
            float[] positions =
            {
                -1f,   -1f,   0f,
                -0.5f, -1f,   0f,
                -0.5f, -0.5f, 0f,
                -1f,   -0.5f, 0f,
            };
            int[] indices = { 0, 1, 2, 0, 2, 3 };

            fixed (float* p = positions)
            {
                meshes.Write(mesh, MeshManager.BufferXyz, 0, (IntPtr)p, positions.Length * sizeof(float));
            }
            fixed (int* i = indices)
            {
                meshes.Write(mesh, -1, 0, (IntPtr)i, indices.Length * sizeof(int));
            }

            TranslatedProgram translated = ShaderTranslator.Translate(new[]
            {
                new ShaderStageSource
                {
                    Stage = EnumShaderType.VertexShader,
                    Filename = "inst.vsh",
                    Code = """
                        #version 330 core
                        layout(location = 0) in vec3 position;
                        void main(void)
                        {
                            float step = float(gl_InstanceID) * 0.75;
                            gl_Position = vec4(position.x + step, position.y + step, 0.0, 1.0);
                        }
                        """,
                },
                new ShaderStageSource
                {
                    Stage = EnumShaderType.FragmentShader,
                    Filename = "inst.fsh",
                    Code = """
                        #version 330 core
                        out vec4 outColor;
                        void main(void) { outColor = vec4(1.0, 0.0, 1.0, 1.0); }
                        """,
                },
            }, compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            VulkanFramebuffer bound = targets.Get(framebuffer)!;
            int formatsId = targets.FormatsIdOf(bound);
            RenderTargetFormats formats = targets.FormatsOf(formatsId);

            Pipeline pipeline = pipelines.Get(
                state.BuildKey(meshes.LayoutIdOf(mesh), formatsId, 1),
                new GraphicsPipelineCache.PipelineRequest
                {
                    Program = program,
                    VertexLayout = meshes.LayoutOf(meshes.LayoutIdOf(mesh)),
                    Targets = formats,
                    Blend = new[] { state.BlendFor(0) },
                    PolygonMode = state.PolygonMode,
                    Topology = state.Topology,
                });

            commands.SubmitAndWait(commandBuffer =>
            {
                targets.Bind(commandBuffer, framebuffer);
                targets.EnsureRendering(commandBuffer);

                Vk api = context!.Api;
                api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);

                var viewport = new Viewport(0, 0, size, size, 0, 1);
                api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
                var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(size, size));
                api.CmdSetScissor(commandBuffer, 0, 1, &scissor);
                SetDynamicDefaults(api, commandBuffer);

                meshes.Draw(commandBuffer, mesh, instanceCount: 2);
                targets.EndRendering(commandBuffer);
            });

            byte[] pixels = ReadTexture(context!, commands, textures, target, size);

            // Instance 0 covers the lower-left, instance 1 the middle. Both being
            // magenta is what distinguishes a real instanced draw from one.
            Assert.Equal(255, PixelAt(pixels, size, 2, 2)[0]);
            Assert.Equal(255, PixelAt(pixels, size, 2, 2)[2]);
            Assert.Equal(255, PixelAt(pixels, size, 9, 9)[0]);
            Assert.Equal(255, PixelAt(pixels, size, 9, 9)[2]);

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] PixelAt(byte[] pixels, uint size, uint x, uint y) =>
        pixels.Skip((int)((y * size + x) * 4)).Take(4).ToArray();

    private static void SetDynamicDefaults(Vk api, CommandBuffer commandBuffer)
    {
        api.CmdSetCullMode(commandBuffer, CullModeFlags.None);
        api.CmdSetFrontFace(commandBuffer, PipelineKeyState.FrontFace);
        api.CmdSetPrimitiveTopology(commandBuffer, PrimitiveTopology.TriangleList);
        api.CmdSetDepthTestEnable(commandBuffer, false);
        api.CmdSetDepthWriteEnable(commandBuffer, false);
        api.CmdSetDepthCompareOp(commandBuffer, CompareOp.Always);
        api.CmdSetStencilTestEnable(commandBuffer, false);
        api.CmdSetStencilOp(commandBuffer, StencilFaceFlags.FaceFrontAndBack,
            StencilOp.Keep, StencilOp.Keep, StencilOp.Keep, CompareOp.Always);
        api.CmdSetStencilCompareMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xFF);
        api.CmdSetStencilWriteMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xFF);
        api.CmdSetStencilReference(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0);
        api.CmdSetLineWidth(commandBuffer, 1.0f);
    }

    private static unsafe byte[] ReadTexture(
        VulkanContext context, SetupQueue commands, TextureManager textures, int textureId, uint size)
    {
        VulkanTexture texture = textures.Get(textureId)!;
        ulong bytes = (ulong)size * size * 4;

        using var readback = new VulkanBuffer(context, bytes,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        commands.SubmitAndWait(commandBuffer =>
        {
            textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(size, size, 1),
            };
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        var result = new byte[(int)bytes];
        Marshal.Copy(readback.Mapped, result, 0, result.Length);
        return result;
    }

}
}

// Source: Optimum.Render.Vulkan.Tests/WorldRenderPathTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The device features the world passes need, which the menu never touches.
///
/// Reaching a world in the real client needs a signed-in account, so these drive
/// the same device paths directly instead: the layered accumulation target and
/// six-attachment blending that weighted-blended OIT sets up, the depth-only
/// shadow map, and the occlusion query the sun uses to size its glare. Each runs
/// with validation on and asserts a clean message log, because the failures these
/// guard against are silent - a legal frame that draws the wrong thing.
/// </summary>
public class WorldRenderPathTests
{
    private readonly ITestOutputHelper _output;

    public WorldRenderPathTests(ITestOutputHelper output) => _output = output;

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    private static bool TryCreateContext(
        ITestOutputHelper output, List<string> messages, out VulkanContext? context) =>
        GpuTest.TryCreateContext(output, messages, out context);

    /// <summary>
    /// OIT accumulates into three layers of one 2D array texture, attached a
    /// layer at a time to colour attachments 3, 4 and 5. Each attachment has to
    /// reach its own layer: a layer index dropped somewhere in the attach path
    /// would have all three writing over each other, which still renders and
    /// still validates.
    /// </summary>
    [SkippableFact]
    public unsafe void EachOitAccumulationLayerIsWrittenSeparately()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 8;
            const uint layers = 3;
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new PipelineKeyState();
            using var targets = new RenderTargetManager(context!, textures);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int accumulation = textures.Create(size, size, Format.R8G8B8A8Unorm, layers);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, accumulation, 0);
            targets.Attach(framebuffer, 1, accumulation, 1);
            targets.Attach(framebuffer, 2, accumulation, 2);

            TranslatedProgram translated = Translate(compiler, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outA;
                layout(location = 1) out vec4 outB;
                layout(location = 2) out vec4 outC;
                void main(void)
                {
                    outA = vec4(1.0, 0.0, 0.0, 1.0);
                    outB = vec4(0.0, 1.0, 0.0, 1.0);
                    outC = vec4(0.0, 0.0, 1.0, 1.0);
                }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            RenderFullscreen(context!, commands, targets, pipelines, state, program, framebuffer, size);

            // Red into layer 0, green into layer 1, blue into layer 2.
            Assert.Equal(new byte[] { 255, 0, 0 }, FirstPixel(context!, commands, textures, accumulation, size, 0));
            Assert.Equal(new byte[] { 0, 255, 0 }, FirstPixel(context!, commands, textures, accumulation, size, 1));
            Assert.Equal(new byte[] { 0, 0, 255 }, FirstPixel(context!, commands, textures, accumulation, size, 2));

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// The OIT pass gives each attachment its own blend factors in one draw:
    /// revealage multiplies down from one while accumulation adds up from zero.
    /// A per-attachment blend state collapsed into a single shared one would
    /// produce a plausible-looking but wrong composite.
    /// </summary>
    [SkippableFact]
    public unsafe void AttachmentsKeepIndependentBlendFactors()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 8;
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new PipelineKeyState();
            using var targets = new RenderTargetManager(context!, textures);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int reveal = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int accum = textures.Create(size, size, Format.R8G8B8A8Unorm);

            // Revealage starts at one, accumulation at zero.
            FillTexture(textures, reveal, size, 255);
            FillTexture(textures, accum, size, 0);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, reveal);
            targets.Attach(framebuffer, 1, accum);

            // Attachment 0: dst * src (GL_ZERO, GL_SRC_COLOR reversed as the OIT
            // pass writes it - factor pair 774/0 is DST_COLOR, ZERO).
            state.SetBlend(true, EnumBlendMode.Standard);
            state.SetAttachmentBlendFunc(0, 774, 0, 774, 0);
            // Attachment 1: additive.
            state.SetAttachmentBlendFunc(1, 1, 1, 1, 1);

            TranslatedProgram translated = Translate(compiler, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outReveal;
                layout(location = 1) out vec4 outAccum;
                void main(void)
                {
                    outReveal = vec4(0.5, 0.5, 0.5, 1.0);
                    outAccum  = vec4(0.25, 0.25, 0.25, 1.0);
                }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            RenderFullscreen(context!, commands, targets, pipelines, state, program, framebuffer, size);

            byte[] revealPixels = ReadTexture(context!, commands, textures, reveal, size);
            byte[] accumPixels = ReadTexture(context!, commands, textures, accum, size);

            // dst(1.0) * src(0.5) = 0.5, so revealage came down rather than
            // being replaced.
            Assert.InRange(revealPixels[0], 120, 136);
            // 0 + 0.25 = 0.25, so accumulation added rather than multiplying.
            Assert.InRange(accumPixels[0], 56, 72);

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// The shadow passes render depth with no colour attachment at all. A target
    /// that quietly requires one would fail to build a pipeline, and a depth
    /// attachment that never got stored would leave every shadow lookup reading
    /// the clear value.
    /// </summary>
    [SkippableFact]
    public unsafe void ADepthOnlyTargetStoresWhatWasDrawn()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 8;
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new PipelineKeyState();
            using var targets = new RenderTargetManager(context!, textures);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int depth = textures.Create(size, size, Format.D32Sfloat);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, -1, depth);

            // Draws at a fixed clip depth; after the Vulkan remap that is 0.75.
            TranslatedProgram translated = Translate(compiler, """
                #version 330 core
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.5, 1.0);
                }
                """, """
                #version 330 core
                void main(void) { }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);
            state.SetDepthTest(true);
            state.SetDepthWrite(true);
            state.SetDepthFunc(0x203);   // GL_LEQUAL

            // The shadow pass clears depth to one before drawing; without that
            // the comparison runs against undefined contents and rejects
            // everything, which is a property of the test rather than the device.
            commands.SubmitAndWait(commandBuffer =>
            {
                targets.Bind(commandBuffer, framebuffer);
                targets.ClearDepth(commandBuffer, 1f);
                targets.EndRendering(commandBuffer);
            });

            RenderFullscreen(context!, commands, targets, pipelines, state, program, framebuffer, size,
                depthTest: true);

            float stored = ReadDepth(context!, commands, textures, depth, size);

            // (0.5 + 1.0) * 0.5 = 0.75 - the GL-to-Vulkan depth remap, measured
            // rather than assumed.
            Assert.InRange(stored, 0.74f, 0.76f);

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// The sun's glare is sized by an occlusion query counting the samples its
    /// quad passed. A query that never produced a result would leave the glare
    /// pinned at its last value forever, which looks like a lighting bug rather
    /// than a query one.
    /// </summary>
    [SkippableFact]
    public unsafe void AnOcclusionQueryCountsTheSamplesThatPassed()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 8;
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new PipelineKeyState();
            using var targets = new RenderTargetManager(context!, textures);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int color = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, color);

            TranslatedProgram translated = Translate(compiler, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = vec4(1.0); }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            var poolInfo = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Occlusion,
                QueryCount = 1,
            };
            Assert.Equal(Result.Success,
                context!.Api.CreateQueryPool(context.Device, &poolInfo, null, out QueryPool pool));

            ulong passed;
            try
            {
                RenderFullscreen(context, commands, targets, pipelines, state, program, framebuffer, size,
                    depthTest: false, queryPool: pool);

                Assert.Equal(Result.Success, context.Api.GetQueryPoolResults(
                    context.Device, pool, 0, 1, (nuint)sizeof(ulong), &passed, sizeof(ulong),
                    QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit));
            }
            finally
            {
                context.Api.DestroyQueryPool(context.Device, pool, null);
            }

            // The triangle covers the whole 8x8 target.
            Assert.Equal((ulong)(size * size), passed);

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static TranslatedProgram Translate(ShaderCompiler compiler, string vertex, string fragment) =>
        ShaderTranslator.Translate(new[]
        {
            new ShaderStageSource { Stage = EnumShaderType.VertexShader, Code = vertex, Filename = "t.vsh" },
            new ShaderStageSource { Stage = EnumShaderType.FragmentShader, Code = fragment, Filename = "t.fsh" },
        }, compiler);

    private static unsafe void RenderFullscreen(
        VulkanContext context, SetupQueue commands, RenderTargetManager targets,
        GraphicsPipelineCache pipelines, PipelineKeyState state, ShaderProgramResources program,
        int framebuffer, uint size, bool depthTest = false, QueryPool queryPool = default)
    {
        VulkanFramebuffer bound = targets.Get(framebuffer)!;
        int formatsId = targets.FormatsIdOf(bound);
        RenderTargetFormats formats = targets.FormatsOf(formatsId);
        int attachmentCount = targets.EnabledAttachmentCount(bound);

        var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        for (int i = 0; i < blend.Length; i++) blend[i] = state.BlendFor(i);

        Pipeline pipeline = pipelines.Get(
            state.BuildKey(0, formatsId, attachmentCount),
            new GraphicsPipelineCache.PipelineRequest
            {
                Program = program,
                VertexLayout = VertexLayoutDescription.Empty,
                Targets = formats,
                Blend = blend,
                PolygonMode = state.PolygonMode,
                Topology = state.Topology,
            });

        commands.SubmitAndWait(commandBuffer =>
        {
            Vk api = context.Api;
            if (queryPool.Handle != 0)
            {
                api.CmdResetQueryPool(commandBuffer, queryPool, 0, 1);
            }

            targets.Bind(commandBuffer, framebuffer);
            targets.EnsureRendering(commandBuffer);

            api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);

            var viewport = new Viewport(0, 0, size, size, 0, 1);
            api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(size, size));
            api.CmdSetScissor(commandBuffer, 0, 1, &scissor);

            api.CmdSetCullMode(commandBuffer, CullModeFlags.None);
            api.CmdSetFrontFace(commandBuffer, PipelineKeyState.FrontFace);
            api.CmdSetPrimitiveTopology(commandBuffer, PrimitiveTopology.TriangleList);
            api.CmdSetDepthTestEnable(commandBuffer, depthTest);
            api.CmdSetDepthWriteEnable(commandBuffer, depthTest);
            api.CmdSetDepthCompareOp(commandBuffer, depthTest ? CompareOp.LessOrEqual : CompareOp.Always);
            api.CmdSetStencilTestEnable(commandBuffer, false);
            api.CmdSetStencilOp(commandBuffer, StencilFaceFlags.FaceFrontAndBack,
                StencilOp.Keep, StencilOp.Keep, StencilOp.Keep, CompareOp.Always);
            api.CmdSetStencilCompareMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xFF);
            api.CmdSetStencilWriteMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xFF);
            api.CmdSetStencilReference(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0);
            api.CmdSetLineWidth(commandBuffer, 1.0f);

            if (queryPool.Handle != 0)
            {
                api.CmdBeginQuery(commandBuffer, queryPool, 0, 0);
            }
            api.CmdDraw(commandBuffer, 3, 1, 0, 0);
            if (queryPool.Handle != 0)
            {
                api.CmdEndQuery(commandBuffer, queryPool, 0);
            }

            targets.EndRendering(commandBuffer);
        });
    }

    private static unsafe void FillTexture(TextureManager textures, int textureId, uint size, byte value)
    {
        var pixels = new byte[size * size * 4];
        Array.Fill(pixels, value);
        fixed (byte* data = pixels)
        {
            textures.Upload(textureId, 0, 0, 0, size, size, (IntPtr)data, 4);
        }
    }

    private static unsafe byte[] ReadTexture(
        VulkanContext context, SetupQueue commands, TextureManager textures,
        int textureId, uint size, uint layer = 0)
    {
        VulkanTexture texture = textures.Get(textureId)!;
        ulong bytes = (ulong)size * size * 4;

        using var readback = new VulkanBuffer(context, bytes,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        commands.SubmitAndWait(commandBuffer =>
        {
            textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, layer, 1),
                ImageExtent = new Extent3D(size, size, 1),
            };
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        var result = new byte[(int)bytes];
        Marshal.Copy(readback.Mapped, result, 0, result.Length);
        return result;
    }

    private static byte[] FirstPixel(
        VulkanContext context, SetupQueue commands, TextureManager textures,
        int textureId, uint size, uint layer) =>
        ReadTexture(context, commands, textures, textureId, size, layer).Take(3).ToArray();

    private static unsafe float ReadDepth(
        VulkanContext context, SetupQueue commands, TextureManager textures, int textureId, uint size)
    {
        VulkanTexture texture = textures.Get(textureId)!;
        ulong bytes = (ulong)size * size * sizeof(float);

        using var readback = new VulkanBuffer(context, bytes,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        commands.SubmitAndWait(commandBuffer =>
        {
            textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, 0, 1),
                ImageExtent = new Extent3D(size, size, 1),
            };
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        var result = new float[size * size];
        fixed (float* destination = result)
        {
            System.Buffer.MemoryCopy((void*)readback.Mapped, destination, (long)bytes, (long)bytes);
        }
        return result[0];
    }

}
}
