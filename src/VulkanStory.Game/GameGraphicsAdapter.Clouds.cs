using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeMeshPass nativeCloudVolumetric = new("cloudvolumetric", [], []);
    /// <summary>Draws the cloud volume using the active original program and compatible native target/layout when available.</summary>
    /// <param name="mesh">Original cloud-volume mesh associated with this adapter.</param>
    internal void RenderCloudVolumetric(MeshRef mesh)
    {
        var renderer = RequireDevice();
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        // The original cloud renderer builds iMvpMatrix from PerspectiveProjectionMat,
        // which intentionally stays unjittered for culling. Its fullscreen ray and
        // depth reconstruction must instead use the projection that rasterized Primary.
        // Set the original program before native selection so the retained path agrees.
        if (program is { PassName: "cloudvolumetric" } && aoTemporal is { InScene: true } temporal &&
            temporal.State.JitterActive && temporal.State.IsViewCaptured(EnumTemporalView.World) &&
            platform!.FrameBuffers is { Count: > 0 } targets &&
            targets[0] is { Width: > 0, Height: > 0 } primary)
        {
            (float[]? inverse, _) = JitteredReprojection(temporal.State, primary.Width, primary.Height);
            if (inverse != null) program.UniformMatrix("iMvpMatrix", inverse);
        }
        if (currentFramebuffer == null || !ReferenceEquals(currentFramebuffer, platform!.FrameBuffers[1]) ||
            Stated.DepthTest || program == null || program.PassName != "cloudvolumetric" ||
            !ReferenceEquals(program, ShaderRegistry.getProgramByName("cloudvolumetric")) ||
            mesh is not VAO vao || vao.VaoId == 0 || vao.Disposed)
        { RenderMesh(mesh); return; }
        int handle = MeshHandle(vao), layout = renderer.NativeMeshLayoutId(handle);
        uint slots = Stated.DrawBuffers(CurrentTargetId);
        RenderTargetFormats? formats = renderer.NativeTargetFormats(CurrentTargetId, slots);
        if (layout < 0 || formats == null || slots == 0) { RenderMesh(mesh); return; }
        NativePipeline? pipeline = NativeMeshPipelineFor(nativeCloudVolumetric, program, formats, layout,
            new NativePipelineDescription
            {
                Blend = Stated.BlendFor(CurrentTargetId, Math.Max(formats.ColorFormats.Length, 1)),
                DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None,
                Topology = renderer.NativeMeshTopology(handle), SamplesBoundDepth = true,
            });
        if (pipeline == null) { RenderMesh(mesh); return; }
        string[] names = pipeline.SamplerNames;
        var textures = new NativeTexture[names.Length]; var reads = new int[names.Length];
        for (int index = 0; index < names.Length; index++)
        {
            int texture = programTextures.GetValueOrDefault((program.ProgramId, names[index]));
            if (texture == 0 && names[index] == "liquidDepth")
            {
                var buffers = platform!.FrameBuffers;
                if (buffers.Count <= (int)EnumFrameBuffer.LiquidDepth || buffers[(int)EnumFrameBuffer.LiquidDepth] == null)
                { RenderMesh(mesh); return; }
                texture = buffers[(int)EnumFrameBuffer.LiquidDepth].DepthTextureId;
            }
            textures[index] = new NativeTexture(pipeline.Sampler(names[index]), texture); reads[index] = texture;
        }
        try
        {
            if (renderer.BeginNativePass(StatedViewportPass("CloudVolumetric/" + CurrentTargetId, CurrentTargetId, slots,
                    reads, PassFlags.AllowSplit)) &&
                renderer.DrawNativeMesh(pipeline, handle, textures)) RuntimeStats.drawCallsCount++;
        }
        finally { renderer.EndNativePass(); }
    }
}
