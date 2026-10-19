using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeMeshPass nativeQuadMotion = new("particlesquad-motion", [], []);
    private readonly NativeFullscreenPass nativeQuadMotionCompose = new("particle-motion-compose", [], ["particleMotion", "particleDepth", "sceneMotion"]);
    private ShaderProgram? quadMotionShader;
    private int quadMotionSourceProgram, quadMotionComposeProgram, quadMotionComposeLocation = -1;
    private bool quadMotionShaderFailed, quadMotionComposeFailed, quadMotionFrameFailed;
    private ulong quadMotionFrame = ulong.MaxValue;
    private int quadMotionEye = -1;
    internal long QuadMotionDrawsRecorded { get; private set; }
    internal long QuadMotionCompositesRecorded { get; private set; }
    internal long QuadMotionValidInstances { get; private set; }

    private FrameBufferRef CreateQuadMotionTarget(int width, int height)
    {
        var renderer = RequireDevice();
        var target = new FrameBufferRef { Width = width, Height = height,
            FboId = renderer.CreateFramebuffer(width, height), ColorTextureIds = new int[1] };
        try
        {
            target.ColorTextureIds[0] = renderer.CreateTexture2D(width, height, TextureInternalFormat.Rgba16f, TexturePixelFormat.Rgba, IntPtr.Zero, false);
            target.DepthTextureId = renderer.CreateTexture2D(width, height, TextureInternalFormat.DepthComponent32, TexturePixelFormat.DepthComponent, IntPtr.Zero, false);
            SetupTextureSampler(target.ColorTextureIds[0], 9728, 33071); SetupTextureSampler(target.DepthTextureId, 9728, 33071);
            renderer.AttachTexture(target.FboId, FramebufferAttachment.ColorAttachment0, target.ColorTextureIds[0], 0);
            renderer.AttachTexture(target.FboId, FramebufferAttachment.DepthAttachment, target.DepthTextureId, 0);
            StateDrawBuffers(target.FboId, 1);
            if (!renderer.CheckFramebufferComplete(target.FboId, out string status)) throw new InvalidOperationException(status);
            return RegisterFramebuffer(target);
        }
        catch { ReleaseUnpublishedFramebuffer(target); throw; }
    }

    private ShaderProgram? QuadMotionProgram(ShaderProgramBase source)
    {
        if (quadMotionSourceProgram != source.ProgramId)
        {
            ReleaseQuadMotionProgram(); quadMotionSourceProgram = source.ProgramId;
        }
        if (quadMotionShader != null || quadMotionShaderFailed) return quadMotionShader;
        var renderer = RequireDevice(); int id = 0;
        try
        {
            var includes = new HashSet<string>(StringComparer.Ordinal);
            string vertexCode = ExpandOwnedShaderIncludes(EmbeddedShaderSource("particlesquad.vsh")!, includes);
            string fragmentCode = ExpandOwnedShaderIncludes(EmbeddedShaderSource("particlesquad-motion.fsh")!, includes);
            string prefix = ((IShaderProgram)source).VertexShader?.PrefixCode + "\n#define VULKANSTORY_QUAD_MOTION 1\n";
            var vertex = new ShaderStageDefinition { Type = ShaderStageType.VertexShader, Code = vertexCode, PrefixCode = prefix };
            var fragment = new ShaderStageDefinition { Type = ShaderStageType.FragmentShader, Code = fragmentCode, PrefixCode = prefix };
            if (!renderer.CompileShader(vertex) || !renderer.CompileShader(fragment)) throw new InvalidOperationException(renderer.GetError());
            id = renderer.LinkProgram(new ShaderProgramDefinition("particlesquad-motion", vertex, fragment, null, includes));
            if (id <= 0 || !renderer.NativeProgramWritesOutput(id, 0)) throw new InvalidOperationException(renderer.GetError());
            quadMotionShader = new ShaderProgram { ProgramId = id, PassName = "particlesquad-motion", LoadFromFile = false };
        }
        catch (Exception error)
        {
            if (id > 0) renderer.DeleteProgram(id);
            quadMotionShaderFailed = true; platform!.Logger.Error("VulkanStory: quad particle motion shader failed: {0}", error.Message);
        }
        return quadMotionShader;
    }

    private void ResetQuadMotionPublication()
    { quadMotionFrame = ulong.MaxValue; quadMotionEye = -1; quadMotionFrameFailed = false; }

    private void RenderQuadParticleMotion(MeshRef mesh, int count, ShaderProgramBase source, TemporalFrameState frame)
    {
        var renderer = RequireDevice();
        if (!AoSettings.EffectiveTemporalPipeline || !frame.JitterActive || FrameState.MotionAttachment < 0 ||
            !renderer.ProgramComputeWarpCompatible(source.ProgramId) || ShaderOverrides.IsOverridden("particlesquad")) return;
        var program = QuadMotionProgram(source);
        if (quadMotionFrame != renderer.LatencyFrameId || quadMotionEye != renderer.TerrainEyeIndex)
        {
            quadMotionFrame = renderer.LatencyFrameId; quadMotionEye = renderer.TerrainEyeIndex; quadMotionFrameFailed = false;
            if (NativePostTarget(platform!.FrameBuffers, QuadMotionIndex) is { } fresh)
            { renderer.ClearNativeColor(fresh.FboId, 0, 0, 0, 0, 0); renderer.ClearNativeDepth(fresh.FboId, 1); }
        }
        var target = NativePostTarget(platform!.FrameBuffers, QuadMotionIndex);
        if (target == null || program == null || mesh is not VAO vao || vao.Disposed)
        { quadMotionFrameFailed = true; return; }
        int handle = MeshHandle(vao), layout = renderer.NativeMeshLayoutId(handle);
        var formats = renderer.NativeTargetFormats(target.FboId, 1u);
        if (layout < 0 || formats == null) { quadMotionFrameFailed = true; return; }
        renderer.CopyQuadMotionUniforms(source.ProgramId, program.ProgramId);
        renderer.SetUniformMatrix(program.ProgramId, renderer.GetUniformLocation(program.ProgramId, "prevProjectionMatrix"), frame.GetPrevProjection(EnumTemporalView.World));
        renderer.SetUniform(program.ProgramId, renderer.GetUniformLocation(program.ProgramId, "taaRenderSize"), (float)frame.RenderWidth, frame.RenderHeight);
        renderer.SetUniform(program.ProgramId, renderer.GetUniformLocation(program.ProgramId, "taaJitterPx"), frame.JitterPx.X, frame.JitterPx.Y);
        var pipeline = NativeMeshPipelineFor(nativeQuadMotion, program, formats, layout, new NativePipelineDescription
        { Blend = NativeOpaqueBlend(1u), DepthTest = true, DepthWrite = true, DepthCompare = CompareOp.Less, Cull = CullModeFlags.None, Topology = PrimitiveTopology.TriangleList });
        if (pipeline == null) { quadMotionFrameFailed = true; return; }
        try
        {
            if (!BeginNativeTargetPass("QuadParticleMotion/" + target.FboId, target.FboId, 1u, target.Width, target.Height, []) ||
                !renderer.DrawNativeMeshInstanced(pipeline, handle, count, []))
            { quadMotionFrameFailed = true; return; }
            QuadMotionDrawsRecorded++; QuadMotionValidInstances += ParticleMotionHistory.ValidInstances(mesh);
        }
        finally { renderer.EndNativePass(); }
    }

    internal bool ComposeQuadParticleMotion(GameTemporalOwner temporal)
    {
        var renderer = RequireDevice();
        if (quadMotionFrame != renderer.LatencyFrameId || quadMotionEye != renderer.TerrainEyeIndex) return true;
        if (quadMotionFrameFailed) return false;
        var source = NativePostTarget(platform!.FrameBuffers, QuadMotionIndex);
        var primary = NativePostTarget(platform.FrameBuffers, PrimaryIndex);
        int motion = FrameState.MotionAttachment;
        if (source == null || primary == null || motion < 0 || !temporal.HasCurrentSceneSample) return false;
        if (quadMotionComposeLocation != motion)
        {
            if (quadMotionComposeProgram > 0) renderer.DeleteProgram(quadMotionComposeProgram);
            quadMotionComposeProgram = 0; quadMotionComposeFailed = false; quadMotionComposeLocation = motion;
        }
        int program = OwnedProgram("particle-motion-compose", ref quadMotionComposeProgram, ref quadMotionComposeFailed, MotionProgramPrefix(motion));
        if (program <= 0) return false;
        uint slots = 1u << motion;
        var pipeline = NativePostPipeline(nativeQuadMotionCompose, program, primary.FboId, slots, NativeOpaqueBlend(slots), true, true, CompareOp.Less);
        if (pipeline == null || !pipeline.Program.Interface.WrittenFragmentOutputs.Contains(motion)) return false;
        int[] inputs = [source.ColorTextureIds[0], source.DepthTextureId, primary.ColorTextureIds[motion]];
        try
        {
            if (!BeginNativeTargetPass("QuadMotionCompose/" + primary.FboId, primary.FboId, slots, primary.Width, primary.Height, inputs)) return false;
            bool drawn = renderer.DrawNativeFullscreen(pipeline,
                [new NativeTexture(nativeQuadMotionCompose.Samplers[0], inputs[0]), new NativeTexture(nativeQuadMotionCompose.Samplers[1], inputs[1]),
                    new NativeTexture(nativeQuadMotionCompose.Samplers[2], inputs[2])], requirePipeline: true);
            if (drawn) QuadMotionCompositesRecorded++;
            return drawn;
        }
        finally { renderer.EndNativePass(); }
    }

    internal void ReleaseQuadMotionProgram()
    {
        var renderer = RequireLifecycleDevice();
        if (quadMotionShader != null) renderer.DeleteProgram(quadMotionShader.ProgramId);
        if (quadMotionComposeProgram > 0) renderer.DeleteProgram(quadMotionComposeProgram);
        quadMotionShader = null; quadMotionComposeProgram = 0; quadMotionSourceProgram = 0;
        quadMotionShaderFailed = quadMotionComposeFailed = false; quadMotionComposeLocation = -1;
        ResetQuadMotionPublication();
    }
}
