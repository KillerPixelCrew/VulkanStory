using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

// Retained fullscreen-pass cache and pipeline/pass descriptions from
// VulkanClientPlatform.NativeBlit.cs; baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
internal sealed partial class GameGraphicsAdapter
{
    private sealed class NativeFullscreenPass(string name, string[] uniforms, string[] samplers)
    {
        internal string PassName { get; } = name;
        internal string[] UniformNames { get; } = uniforms;
        internal string[] SamplerNames { get; } = samplers;
        internal NativeUniform[] Uniforms { get; } = new NativeUniform[uniforms.Length];
        internal NativeSamplerSlot[] Samplers { get; } = new NativeSamplerSlot[samplers.Length];
        internal NativePipeline? Pipeline;
        internal RenderTargetFormats? Formats;
        internal bool Reported;

        internal void Adopt(NativePipeline pipeline, RenderTargetFormats formats)
        {
            Pipeline = pipeline; Formats = formats;
            for (int index = 0; index < UniformNames.Length; index++) Uniforms[index] = pipeline.Uniform(UniformNames[index]);
            for (int index = 0; index < SamplerNames.Length; index++) Samplers[index] = pipeline.Sampler(SamplerNames[index]);
        }
    }
    private static AttachmentBlend[] OpaqueColorZero() => [AttachmentBlend.Default];
    private const int NativeDefaultTarget = PassDeclaration.DefaultFramebuffer;

    private NativePipeline? NativePipelineFor(NativeFullscreenPass pass, ShaderProgramBase program,
        int framebuffer, AttachmentBlend[]? blend = null) =>
        NativePipelineFor(pass, program.ProgramId, framebuffer, blend);

    private NativePipeline? NativePipelineFor(NativeFullscreenPass pass, int program,
        int framebuffer, AttachmentBlend[]? blend = null)
    {
        var renderer = RequireDevice();
        RenderTargetFormats? formats = renderer.NativeTargetFormats(framebuffer, 1u);
        if (formats == null) return null;
        if (pass.Pipeline != null && pass.Pipeline.ProgramId == program &&
            formats.Equals(pass.Formats) && renderer.IsNativePipelineLive(pass.Pipeline)) return pass.Pipeline;
        NativePipeline? pipeline = renderer.RequestNativePipeline(new NativePipelineDescription
        {
            ProgramId = program, PassName = pass.PassName,
            Blend = blend ?? OpaqueColorZero(), DepthTest = false, DepthWrite = false,
            Cull = CullModeFlags.None, Topology = PrimitiveTopology.TriangleList, Targets = formats,
        }, out string reason);
        if (pipeline == null)
        {
            if (!pass.Reported)
            {
                pass.Reported = true;
                platform!.Logger.Warning("VulkanStory: no native pipeline for '{0}': {1}", pass.PassName, reason);
            }
            pass.Pipeline = null;
            return null;
        }
        pass.Reported = false;
        pass.Adopt(pipeline, formats);
        return pipeline;
    }
    /// <summary>Opens an owned fullscreen pass with explicit texture-read dependencies, target and viewport dimensions.</summary>
    /// <param name="name">Pass diagnostic name.</param>
    /// <param name="framebuffer">Backend framebuffer or retained default target identifier.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <param name="reads">Texture identifiers sampled by the pass.</param>
    /// <returns>True when the native pass opened; false when the backend declines its description.</returns>
    private bool BeginNativeBlitPass(string name, int framebuffer, int width, int height, int[] reads) =>
        RequireDevice().BeginNativePass(new NativePassDescription
        {
            Name = name, FramebufferId = framebuffer, ColorSlots = 1u,
            Reads = reads, Flags = PassFlags.None, ViewportWidth = width, ViewportHeight = height,
        });
}
