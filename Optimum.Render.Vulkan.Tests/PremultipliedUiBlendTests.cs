using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// DLSS frame generation, design step 3: the blend seam of the HUD-less frame, on the
/// GPU rather than by inspection.
///
/// The HUD stops drawing onto the world and draws into its own RGBA8 target cleared to
/// (0,0,0,0), which is composed over the display image afterwards with (ONE,
/// ONE_MINUS_SRC_ALPHA). gui.fsh writes STRAIGHT alpha and the Ortho stage draws under
/// EnumBlendMode.Standard, whose vanilla factors are non-separate - SRC_ALPHA is
/// applied to the alpha channel as well. Onto the opaque world nothing ever reads that
/// alpha, so it never mattered; accumulated into a transparent target it lands as
/// out_a = src_a*src_a + dst_a*(1-src_a) instead of the over-operator's
/// out_a = src_a + dst_a*(1-src_a) - roughly alpha squared per layer - and the compose
/// then shows the world through the HUD.
///
/// ClientPlatformWindows.OptimumUiTargetBound scopes the fix to exactly the frames in
/// which that target is bound. These tests draw the two layers on the device and read
/// the bytes back: the scoped factors reproduce the over-operator, the unscoped ones
/// reproduce the squared alpha (that difference is the evidence the branch does
/// something, and a refactor that loses the scope fails here), and the composed result
/// matches the same two layers drawn straight onto the background.
/// </summary>
public class PremultipliedUiBlendTests
{
    private const uint Size = 8;

    // Two straight-alpha layers, the shape a GUI dialog over a HUD element has.
    private const float LayerAr = 0.8f, LayerAg = 0.4f, LayerAb = 0.2f, LayerAa = 0.4f;
    private const float LayerBr = 0.2f, LayerBg = 0.6f, LayerBb = 1.0f, LayerBa = 0.6f;

    // The background the HUD used to be drawn straight onto: an opaque grey.
    private const byte BackgroundByte = 0x40;

    private readonly ITestOutputHelper _output;

    public PremultipliedUiBlendTests(ITestOutputHelper output) => _output = output;

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    /// <summary>Layer A: a quad over columns 0..5, straight alpha out of the shader.</summary>
    private const string LayerAFragment = """
        #version 330 core
        layout(location = 0) out vec4 outColor;
        void main(void)
        {
            if (gl_FragCoord.x >= 6.0) discard;
            outColor = vec4(0.8, 0.4, 0.2, 0.4);
        }
        """;

    /// <summary>Layer B: a quad over columns 2..7, overlapping A on columns 2..5.</summary>
    private const string LayerBFragment = """
        #version 330 core
        layout(location = 0) out vec4 outColor;
        void main(void)
        {
            if (gl_FragCoord.x < 2.0) discard;
            outColor = vec4(0.2, 0.6, 1.0, 0.6);
        }
        """;

    /// <summary>The compose: the UI target sampled 1:1 and blended premultiplied.</summary>
    private const string ComposeFragment = """
        #version 330 core
        uniform sampler2D uiTex;
        layout(location = 0) out vec4 outColor;
        void main(void)
        {
            outColor = texture(uiTex, gl_FragCoord.xy / 8.0);
        }
        """;

    /// <summary>
    /// Both layers into a target cleared to (0,0,0,0), once with the UI scope open and
    /// once with today's factors. Scoped: every channel, alpha included, equals the
    /// over-operator to within 1/255. Unscoped: the colour is the same but the alpha is
    /// the squared form, which is off by a fifth of full scale where the two layers
    /// overlap - that gap is what the scope buys.
    /// </summary>
    [SkippableFact]
    public unsafe void TheScopedStandardModeAccumulatesTheOverOperator()
    {
        var messages = new List<string>();
        Skip.IfNot(GpuTest.TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            byte[] scoped = DrawTheTwoLayers(context!, commands, textures, state, targets, pipelines, compiler,
                uiTargetBound: true, clearByte: 0x00, programId: 1);
            byte[] vanilla = DrawTheTwoLayers(context!, commands, textures, state, targets, pipelines, compiler,
                uiTargetBound: false, clearByte: 0x00, programId: 3);

            int worstScoped = 0;
            int worstColorGap = 0;
            int worstAlphaGap = 0;
            for (int x = 0; x < (int)Size; x++)
            {
                (float r, float g, float b, float over, float squared) = ExpectedLayers(x);

                int texel = x * 4; // row 0 is representative: the layers vary in x only.
                worstScoped = Math.Max(worstScoped, Math.Abs(scoped[texel] - Quantize(r)));
                worstScoped = Math.Max(worstScoped, Math.Abs(scoped[texel + 1] - Quantize(g)));
                worstScoped = Math.Max(worstScoped, Math.Abs(scoped[texel + 2] - Quantize(b)));
                worstScoped = Math.Max(worstScoped, Math.Abs(scoped[texel + 3] - Quantize(over)));

                // The rgb factors are untouched by the scope: src_rgb*src_a is already
                // the premultiplied colour, so both runs must agree on colour.
                for (int channel = 0; channel < 3; channel++)
                {
                    worstColorGap = Math.Max(worstColorGap, Math.Abs(scoped[texel + channel] - vanilla[texel + channel]));
                }

                // The unscoped run stores the squared alpha, and that is the defect.
                Assert.InRange(vanilla[texel + 3], Quantize(squared) - 1, Quantize(squared) + 1);
                worstAlphaGap = Math.Max(worstAlphaGap, Math.Abs(scoped[texel + 3] - vanilla[texel + 3]));
            }

            _output.WriteLine("scoped vs over-operator, worst channel: " + worstScoped + "/255");
            _output.WriteLine("scoped vs unscoped colour, worst channel: " + worstColorGap + "/255");
            _output.WriteLine("scoped vs unscoped alpha, worst: " + worstAlphaGap + "/255");

            Assert.True(worstScoped <= 1, "scoped Standard must reproduce the over-operator; worst " + worstScoped + "/255");
            Assert.True(worstColorGap <= 1, "the scope must not move the colour channels; worst " + worstColorGap + "/255");
            // Overlap: over-operator alpha 0.76, squared 0.424 - 86/255 apart.
            Assert.True(worstAlphaGap > 60, "the unscoped factors must still produce the squared alpha; gap " + worstAlphaGap);

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// The claim the whole design rests on: a HUD accumulated into the UI target under
    /// the scope and composed afterwards with (ONE, ONE_MINUS_SRC_ALPHA) looks like the
    /// same HUD drawn straight onto the image, which is what the game does today. Only
    /// the colour is compared - the display image's alpha is never read by the blit or
    /// by any capture path - and the tolerance covers the one extra 8-bit rounding the
    /// intermediate RGBA8 target adds.
    /// </summary>
    [SkippableFact]
    public unsafe void ComposingTheUiTargetMatchesDrawingTheHudDirectly()
    {
        var messages = new List<string>();
        Skip.IfNot(GpuTest.TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();
            using var descriptors = new DescriptorCache(context!);

            // Today's frame: the two layers straight onto the opaque image, vanilla factors.
            byte[] direct = DrawTheTwoLayers(context!, commands, textures, state, targets, pipelines, compiler,
                uiTargetBound: false, clearByte: BackgroundByte, programId: 5);

            // The HUD-less frame: the same two layers into the transparent UI target
            // under the scope, then one premultiplied fullscreen compose over the image.
            int uiTexture = textures.Create(Size, Size, Format.R8G8B8A8Unorm);
            FillTexture(textures, uiTexture, 0x00);
            int uiTarget = targets.Create(Size, Size);
            targets.Attach(uiTarget, 0, uiTexture);
            targets.SetDrawBuffers(uiTarget, 0b1);

            state.SetBlend(true, EnumBlendMode.Standard, uiTargetBound: true);
            DrawLayer(context!, commands, targets, pipelines, state, compiler, uiTarget, LayerAFragment, 7);
            DrawLayer(context!, commands, targets, pipelines, state, compiler, uiTarget, LayerBFragment, 8);

            int composedTexture = textures.Create(Size, Size, Format.R8G8B8A8Unorm);
            FillTexture(textures, composedTexture, BackgroundByte);
            int composedTarget = targets.Create(Size, Size);
            targets.Attach(composedTarget, 0, composedTexture);
            targets.SetDrawBuffers(composedTarget, 0b1);

            // D5: the compose is the over-operator on already-premultiplied source.
            state.SetBlend(true, EnumBlendMode.PremultipliedAlpha);
            TranslatedProgram translated = Translate(compiler, FullscreenVertex, ComposeFragment);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));
            using var compose = new ShaderProgramResources(context!, 9, translated);
            state.SetProgram(9);
            RenderFullscreenSampling(context!, commands, textures, targets, pipelines, state, descriptors,
                compose, composedTarget, uiTexture);

            byte[] composed = ReadTexture(context!, commands, textures, composedTexture);

            int worst = 0;
            for (int texel = 0; texel < (int)(Size * Size); texel++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    worst = Math.Max(worst, Math.Abs(composed[texel * 4 + channel] - direct[texel * 4 + channel]));
                }
            }

            // Measured 1/255 on the reference device (RGBA8 intermediate, so one extra
            // rounding step is the whole budget).
            _output.WriteLine("composed vs drawn directly, worst colour channel: " + worst + "/255");
            Assert.True(worst <= 1,
                "the composed HUD must match the directly drawn one; worst " + worst + "/255");

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// The scope is exactly the UI target. With nothing bound - the default - and with
    /// the flag explicitly false, Standard resolves to vanilla's non-separate
    /// SRC_ALPHA / ONE_MINUS_SRC_ALPHA on both channels, byte for byte what
    /// ClientPlatformWindows.GlToggleBlend emits as GL.BlendFunc(770, 771). Only the
    /// alpha pair moves when the flag is true, and no other named mode moves at all.
    /// </summary>
    [Fact]
    public void WithoutTheScopeTheStandardFactorsAreTodaysFactors()
    {
        var tracker = new GlStateTracker();

        tracker.SetBlend(true, EnumBlendMode.Standard);
        AssertFactors(tracker.BlendFor(0), BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,
            BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha);

        tracker.SetBlend(true, EnumBlendMode.Standard, uiTargetBound: false);
        AssertFactors(tracker.BlendFor(0), BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,
            BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha);

        tracker.SetBlend(true, EnumBlendMode.Standard, uiTargetBound: true);
        AssertFactors(tracker.BlendFor(0), BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,
            BlendFactor.One, BlendFactor.OneMinusSrcAlpha);

        // Back out of the scope and the engine is where it was.
        tracker.SetBlend(true, EnumBlendMode.Standard);
        AssertFactors(tracker.BlendFor(0), BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,
            BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha);
    }

    /// <summary>
    /// The scope touches Standard alone: the modes the 3D passes use before the blit -
    /// and PremultipliedAlpha, which the compose itself needs - keep their vanilla
    /// factor pairs whether it is open or not.
    /// </summary>
    [Theory]
    [InlineData(EnumBlendMode.Brighten)]
    [InlineData(EnumBlendMode.Multiply)]
    [InlineData(EnumBlendMode.PremultipliedAlpha)]
    [InlineData(EnumBlendMode.Glow)]
    [InlineData(EnumBlendMode.Overlay)]
    public void TheScopeLeavesEveryOtherNamedModeAlone(EnumBlendMode mode)
    {
        var unscoped = new GlStateTracker();
        unscoped.SetBlend(true, mode);
        var scopedTracker = new GlStateTracker();
        scopedTracker.SetBlend(true, mode, uiTargetBound: true);

        AttachmentBlend expected = unscoped.BlendFor(0);
        AssertFactors(scopedTracker.BlendFor(0), expected.SrcColor, expected.DstColor,
            expected.SrcAlpha, expected.DstAlpha);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertFactors(AttachmentBlend blend,
        BlendFactor srcColor, BlendFactor dstColor, BlendFactor srcAlpha, BlendFactor dstAlpha)
    {
        Assert.Equal(srcColor, blend.SrcColor);
        Assert.Equal(dstColor, blend.DstColor);
        Assert.Equal(srcAlpha, blend.SrcAlpha);
        Assert.Equal(dstAlpha, blend.DstAlpha);
    }

    /// <summary>
    /// The two layers over a destination that starts at <paramref name="clearByte" /> in
    /// every channel, expressed as the CPU does the blend: colour is always
    /// src_rgb*src_a + dst_rgb*(1-src_a), alpha is the over-operator under the scope and
    /// src_a*src_a + dst_a*(1-src_a) without it. Columns 0-1 carry layer A only, 2-5
    /// both, 6-7 layer B only.
    /// </summary>
    private static (float r, float g, float b, float over, float squared) ExpectedLayers(int x)
    {
        float start = BackgroundStart;
        float r = start, g = start, b = start, over = start, squared = start;

        if (x < 6)
        {
            r = LayerAr * LayerAa + r * (1f - LayerAa);
            g = LayerAg * LayerAa + g * (1f - LayerAa);
            b = LayerAb * LayerAa + b * (1f - LayerAa);
            over = LayerAa + over * (1f - LayerAa);
            squared = LayerAa * LayerAa + squared * (1f - LayerAa);
        }
        if (x >= 2)
        {
            r = LayerBr * LayerBa + r * (1f - LayerBa);
            g = LayerBg * LayerBa + g * (1f - LayerBa);
            b = LayerBb * LayerBa + b * (1f - LayerBa);
            over = LayerBa + over * (1f - LayerBa);
            squared = LayerBa * LayerBa + squared * (1f - LayerBa);
        }
        return (r, g, b, over, squared);
    }

    /// <summary>The UI target starts fully transparent; only ExpectedLayers reads this.</summary>
    private const float BackgroundStart = 0f;

    private static int Quantize(float value) => (int)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);

    private static unsafe byte[] DrawTheTwoLayers(
        VulkanContext context, SetupQueue commands, TextureManager textures, GlStateTracker state,
        RenderTargetManager targets, GraphicsPipelineCache pipelines, ShaderCompiler compiler,
        bool uiTargetBound, byte clearByte, int programId)
    {
        int texture = textures.Create(Size, Size, Format.R8G8B8A8Unorm);
        FillTexture(textures, texture, clearByte);
        int framebuffer = targets.Create(Size, Size);
        targets.Attach(framebuffer, 0, texture);
        targets.SetDrawBuffers(framebuffer, 0b1);

        state.SetBlend(true, EnumBlendMode.Standard, uiTargetBound);
        DrawLayer(context, commands, targets, pipelines, state, compiler, framebuffer, LayerAFragment, programId);
        DrawLayer(context, commands, targets, pipelines, state, compiler, framebuffer, LayerBFragment, programId + 1);

        return ReadTexture(context, commands, textures, texture);
    }

    private static unsafe void DrawLayer(
        VulkanContext context, SetupQueue commands, RenderTargetManager targets,
        GraphicsPipelineCache pipelines, GlStateTracker state, ShaderCompiler compiler,
        int framebuffer, string fragment, int programId)
    {
        TranslatedProgram translated = Translate(compiler, FullscreenVertex, fragment);
        Assert.True(translated.Success, string.Join("; ", translated.Errors));
        using var program = new ShaderProgramResources(context, programId, translated);
        state.SetProgram(programId);
        RenderFullscreen(context, commands, targets, pipelines, state, program, framebuffer);
    }

    private static TranslatedProgram Translate(ShaderCompiler compiler, string vertex, string fragment) =>
        ShaderTranslator.Translate(new[]
        {
            new ShaderStageSource { Stage = EnumShaderType.VertexShader, Code = vertex, Filename = "t.vsh" },
            new ShaderStageSource { Stage = EnumShaderType.FragmentShader, Code = fragment, Filename = "t.fsh" },
        }, compiler);

    private static Pipeline PipelineFor(
        RenderTargetManager targets, GraphicsPipelineCache pipelines, GlStateTracker state,
        ShaderProgramResources program, int framebuffer)
    {
        VulkanFramebuffer bound = targets.Get(framebuffer)!;
        int formatsId = targets.FormatsIdOf(bound);
        RenderTargetFormats formats = state.TargetFormats(formatsId);
        int attachmentCount = targets.EnabledAttachmentCount(bound);

        var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        for (int i = 0; i < blend.Length; i++) blend[i] = state.BlendFor(i);

        return pipelines.Get(
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
    }

    private static unsafe void SetFixedState(Vk api, CommandBuffer commandBuffer)
    {
        var viewport = new Viewport(0, 0, Size, Size, 0, 1);
        api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(Size, Size));
        api.CmdSetScissor(commandBuffer, 0, 1, &scissor);

        api.CmdSetCullMode(commandBuffer, CullModeFlags.None);
        api.CmdSetFrontFace(commandBuffer, GlStateTracker.FrontFace);
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

    private static unsafe void RenderFullscreen(
        VulkanContext context, SetupQueue commands, RenderTargetManager targets,
        GraphicsPipelineCache pipelines, GlStateTracker state, ShaderProgramResources program,
        int framebuffer)
    {
        Pipeline pipeline = PipelineFor(targets, pipelines, state, program, framebuffer);

        commands.SubmitAndWait(commandBuffer =>
        {
            targets.Bind(commandBuffer, framebuffer);
            targets.EnsureRendering(commandBuffer);

            Vk api = context.Api;
            api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
            SetFixedState(api, commandBuffer);
            api.CmdDraw(commandBuffer, 3, 1, 0, 0);
            targets.EndRendering(commandBuffer);
        });
    }

    /// <summary>
    /// One fullscreen pass sampling <paramref name="sampledTextureId" /> - the compose.
    /// The sampled texture is still in ColorAttachmentOptimal from the pass that wrote
    /// it, so it is transitioned before the rendering scope opens.
    /// </summary>
    private static unsafe void RenderFullscreenSampling(
        VulkanContext context, SetupQueue commands, TextureManager textures, RenderTargetManager targets,
        GraphicsPipelineCache pipelines, GlStateTracker state, DescriptorCache descriptors,
        ShaderProgramResources program, int framebuffer, int sampledTextureId)
    {
        Pipeline pipeline = PipelineFor(targets, pipelines, state, program, framebuffer);

        commands.SubmitAndWait(commandBuffer =>
        {
            Vk api = context.Api;
            VulkanTexture sampled = textures.Get(sampledTextureId)!;
            textures.TransitionTexture(commandBuffer, sampled, ImageLayout.ShaderReadOnlyOptimal);

            targets.Bind(commandBuffer, framebuffer);
            targets.EnsureRendering(commandBuffer);

            var binding = new SamplerBindingValue(
                (uint)program.Interface.Samplers[0].Binding,
                sampled.View,
                textures.Samplers.Get(sampled.State),
                sampled.Id,
                ImageLayout.ShaderReadOnlyOptimal);
            DescriptorSet samplerSet = descriptors.Get(
                new DescriptorSetContents(program.ProgramId, ProgramInterfaceLayout.SamplerSet,
                    new[] { binding }, Array.Empty<BufferBindingValue>()),
                program.SetLayouts[ProgramInterfaceLayout.SamplerSet]);

            api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
            api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, program.PipelineLayout,
                ProgramInterfaceLayout.SamplerSet, 1, &samplerSet, 0, null);
            SetFixedState(api, commandBuffer);
            api.CmdDraw(commandBuffer, 3, 1, 0, 0);
            targets.EndRendering(commandBuffer);
        });
    }

    private static unsafe void FillTexture(TextureManager textures, int textureId, byte value)
    {
        var pixels = new byte[Size * Size * 4];
        Array.Fill(pixels, value);
        fixed (byte* data = pixels)
        {
            textures.Upload(textureId, 0, 0, 0, Size, Size, (IntPtr)data, 4);
        }
    }

    private static unsafe byte[] ReadTexture(
        VulkanContext context, SetupQueue commands, TextureManager textures, int textureId)
    {
        VulkanTexture texture = textures.Get(textureId)!;
        ulong bytes = (ulong)Size * Size * 4;

        using var readback = new VulkanBuffer(context, bytes,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        commands.SubmitAndWait(commandBuffer =>
        {
            textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(Size, Size, 1),
            };
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        var result = new byte[(int)bytes];
        Marshal.Copy(readback.Mapped, result, 0, result.Length);
        return result;
    }
}
