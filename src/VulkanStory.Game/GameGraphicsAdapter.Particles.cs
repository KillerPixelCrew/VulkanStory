using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

// Retained cube/quad particle native draws, baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeMeshPass nativeParticlesCube = new("particlescube", [], []);
    private readonly NativeMeshPass nativeParticlesQuad = new("particlesquad", [], []);
    /// <summary>Draws particle instances through the compatible native particle pass, otherwise retaining ordinary adapter drawing.</summary>
    /// <param name="mesh">Owned particle instance mesh.</param>
    /// <param name="quantity">Requested particle instance count.</param>
    /// <param name="frame">Current temporal frame used to select the previous rendered spawn state.</param>
    internal void RenderParticles(MeshRef mesh, int quantity, TemporalFrameState frame)
    {
        var renderer = RequireDevice(); renderer.GpuMark("particles");
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        bool quad = ReferenceEquals(program, ShaderPrograms.Particlesquad);
        if (quantity <= 0 || program == null || (!quad && !ReferenceEquals(program, ShaderPrograms.Particlescube)) ||
            currentFramebuffer == null || mesh is not VAO vao || vao.VaoId == 0 || vao.Disposed)
        { RenderMeshInstanced(mesh, quantity); return; }
        if (quad && !ReferenceEquals(currentFramebuffer, platform!.FrameBuffers[1]))
        { RenderMeshInstanced(mesh, quantity); return; }
        int handle = MeshHandle(vao);
        if (!quad) renderer.UpdateParticleHistory(handle, ParticleMotionHistory.Prepare(mesh, quantity, frame));
        int layout = renderer.NativeMeshLayoutId(handle);
        RenderTargetFormats? all = renderer.NativeTargetFormats(CurrentTargetId, uint.MaxValue);
        if (layout < 0 || all == null) { RenderMeshInstanced(mesh, quantity); return; }
        int count = all.ColorFormats.Length;
        uint slots = count >= 32 ? uint.MaxValue : (1u << count) - 1u;
        int motion = FrameState.MotionAttachment;
        if (quad) slots &= Stated.DrawBuffers(CurrentTargetId);
        else if (ReferenceEquals(currentFramebuffer, platform!.FrameBuffers[0]) && motion is >= 0 and < 32)
            slots &= FrameState.MotionWriteActive ? motion == 31 ? uint.MaxValue : (1u << (motion + 1)) - 1u : (1u << motion) - 1u;
        RenderTargetFormats? formats = renderer.NativeTargetFormats(CurrentTargetId, slots);
        if (formats == null || slots == 0) { RenderMeshInstanced(mesh, quantity); return; }
        AttachmentBlend[] blend;
        if (quad) blend = Stated.BlendFor(CurrentTargetId, Math.Max(formats.ColorFormats.Length, 1));
        else
        {
            blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
            for (int index = 0; index < blend.Length; index++)
            {
                blend[index] = AttachmentBlend.Default; blend[index].Enabled = true;
                if (FrameState.MotionWriteActive && index == motion)
                {
                    blend[index].SrcColor = blend[index].SrcAlpha = BlendFactor.One;
                    blend[index].DstColor = blend[index].DstAlpha = BlendFactor.Zero;
                }
            }
        }
        NativeMeshPass pass = quad ? nativeParticlesQuad : nativeParticlesCube;
        NativePipeline? pipeline = NativeMeshPipelineFor(pass, program, formats, layout,
            new NativePipelineDescription
            {
                Blend = blend, DepthTest = true, DepthWrite = !quad,
                DepthCompare = CompareOp.Less, Cull = CullModeFlags.None, Topology = PrimitiveTopology.TriangleList,
            });
        if (pipeline == null) { RenderMeshInstanced(mesh, quantity); return; }
        string[] names = quad ? pipeline.SamplerNames : [];
        var textures = new NativeTexture[names.Length]; var reads = new List<int>();
        for (int index = 0; index < names.Length; index++)
        {
            int texture = programTextures.GetValueOrDefault((program.ProgramId, names[index]));
            textures[index] = new NativeTexture(pipeline.Sampler(names[index]), texture);
            if (texture > 0 && !reads.Contains(texture)) reads.Add(texture);
        }
        try
        {
            if (renderer.BeginNativePass(StatedViewportPass((quad ? "ParticlesOit/" : "Particles/") + CurrentTargetId,
                    CurrentTargetId, slots, reads.ToArray(), PassFlags.AllowSplit)) &&
                renderer.DrawNativeMeshInstanced(pipeline, handle, quantity, textures)) RuntimeStats.drawCallsCount++;
            else RejectSceneDraw("particle native pass/instanced draw rejected");
        }
        finally { renderer.EndNativePass(); }
    }
}
