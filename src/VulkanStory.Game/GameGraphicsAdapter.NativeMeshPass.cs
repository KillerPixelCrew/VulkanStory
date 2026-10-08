using Silk.NET.Vulkan;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

// Retained native mesh cache/fixed-state comparisons; baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
internal sealed partial class GameGraphicsAdapter
{
    private sealed class NativeMeshPass
    {
        public NativeMeshPass(string passName, string[] uniforms, string[] samplers)
        {
            PassName = passName;
            UniformNames = uniforms;
            SamplerNames = samplers;
            Uniforms = new NativeUniform[uniforms.Length];
            Samplers = new NativeSamplerSlot[samplers.Length];
        }

        public string PassName { get; }
        public string[] UniformNames { get; }
        public string[] SamplerNames { get; }

        /// <summary>Resolved once per pipeline, never by name per draw.</summary>
        public NativeUniform[] Uniforms;
        public NativeSamplerSlot[] Samplers;

        public NativePipeline? Pipeline;
        public RenderTargetFormats? Formats;
        public int LayoutId = -1;
        public bool Reported;

        public void Adopt(NativePipeline pipeline, RenderTargetFormats formats, int layoutId)
        {
            Pipeline = pipeline;
            Formats = formats;
            LayoutId = layoutId;
            for (int i = 0; i < UniformNames.Length; i++) Uniforms[i] = pipeline.Uniform(UniformNames[i]);
            for (int i = 0; i < SamplerNames.Length; i++) Samplers[i] = pipeline.Sampler(SamplerNames[i]);
        }
    }

    /// <summary>
    /// The pipeline for one mesh program against one target and one mesh shape, rebuilt only
    /// when the program was relinked, the target's formats changed or the mesh's layout did.
    /// </summary>
    /// <param name="pass">Per-pass cache and pre-resolved uniform/sampler slots.</param>
    /// <param name="program">Original shader program whose backend identifier selects code.</param>
    /// <param name="framebufferId">Backend target identifier.</param>
    /// <param name="colorSlots">Selected target color-write mask.</param>
    /// <param name="layoutId">Retained vertex layout identifier.</param>
    /// <param name="description">Requested fixed draw state, completed with program/layout/target formats.</param>
    /// <returns>Matching live pipeline, or null after target or pipeline refusal.</returns>
    private NativePipeline? NativeMeshPipelineFor(NativeMeshPass pass, ShaderProgramBase program, int framebufferId,
        uint colorSlots, int layoutId, NativePipelineDescription description)
    {
        RenderTargetFormats? formats = RequireDevice().NativeTargetFormats(framebufferId, colorSlots);
        if (formats == null) return null;

        if (pass.Pipeline != null && pass.Pipeline.ProgramId == program.ProgramId &&
            formats.Equals(pass.Formats) && pass.LayoutId == layoutId &&
            SameFixedState(pass.Pipeline.Description, description) && RequireDevice().IsNativePipelineLive(pass.Pipeline))
        {
            return pass.Pipeline;
        }

        description.ProgramId = program.ProgramId;
        description.PassName = pass.PassName;
        description.VertexLayoutId = layoutId;
        description.Targets = formats;

        NativePipeline? pipeline = RequireDevice().RequestNativePipeline(description, out string error);
        if (pipeline == null)
        {
            if (!pass.Reported)
            {
                pass.Reported = true;
                platform!.Logger.Warning("VulkanStory: no native pipeline for '{0}': {1}", pass.PassName, error);
            }
            pass.Pipeline = null;
            return null;
        }

        pass.Reported = false;
        pass.Adopt(pipeline, formats, layoutId);
        return pipeline;
    }

    /// <summary>
    /// Whether two descriptions ask for the same fixed state, which is what makes the cached
    /// pipeline of a <see cref="NativeMeshPass" /> usable for the next draw through it.
    ///
    /// Without this the one-entry cache answered any request for the same program, target and
    /// mesh shape with the pipeline it happened to build first: the aiming reticle's 0.5 and
    /// 1.0 line widths would then both rasterize at whichever came first, and a system that
    /// turns blending on and off between draws would blend both or neither. The device's own
    /// table keys on all of it (NativePipelineCacheKey), so falling through to
    /// <see cref="VulkanDevice.RequestNativePipeline" /> costs a dictionary lookup, not a
    /// pipeline.
    /// </summary>
    private static bool SameFixedState(NativePipelineDescription cached, NativePipelineDescription wanted)
    {
        if (cached.DepthTest != wanted.DepthTest || cached.DepthWrite != wanted.DepthWrite ||
            cached.DepthCompare != wanted.DepthCompare || cached.Cull != wanted.Cull ||
            cached.FrontFace != wanted.FrontFace || cached.Topology != wanted.Topology ||
            cached.PolygonMode != wanted.PolygonMode || cached.SamplesBoundDepth != wanted.SamplesBoundDepth ||
            !cached.LineWidth.Equals(wanted.LineWidth) || cached.Blend.Length != wanted.Blend.Length)
        {
            return false;
        }
        for (int i = 0; i < cached.Blend.Length; i++)
        {
            if (!cached.Blend[i].Equals(wanted.Blend[i])) return false;
        }
        return true;
    }

}
