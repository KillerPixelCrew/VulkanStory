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
        if (currentFramebuffer == null || !ReferenceEquals(currentFramebuffer, platform!.FrameBuffers[1]) ||
            Stated.DepthTest || program == null || program.PassName != "cloudvolumetric" ||
            !ReferenceEquals(program, ShaderRegistry.getProgramByName("cloudvolumetric")) ||
            mesh is not VAO vao || vao.VaoId == 0 || vao.Disposed)
        { RenderMesh(mesh); return; }
        int handle = MeshHandle(vao), layout = renderer.NativeMeshLayoutId(handle);
        uint slots = Stated.DrawBuffers(CurrentTargetId);
        RenderTargetFormats? formats = renderer.NativeTargetFormats(CurrentTargetId, slots);
        if (layout < 0 || formats == null || slots == 0) { RenderMesh(mesh); return; }
        NativePipeline? pipeline = NativeMeshPipelineFor(nativeCloudVolumetric, program, CurrentTargetId, slots, layout,
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
        Rect2D viewport = Stated.Viewport;
        try
        {
            if (renderer.BeginNativePass(new NativePassDescription
            {
                Name = "CloudVolumetric/" + CurrentTargetId, FramebufferId = CurrentTargetId, ColorSlots = slots,
                Reads = reads, Flags = PassFlags.AllowSplit,
                ViewportX = viewport.Offset.X, ViewportY = viewport.Offset.Y,
                ViewportWidth = (int)viewport.Extent.Width, ViewportHeight = (int)viewport.Extent.Height,
            }) && renderer.DrawNativeMesh(pipeline, handle, textures)) RuntimeStats.drawCallsCount++;
        }
        finally { renderer.EndNativePass(); }
    }
}
