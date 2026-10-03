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
    private readonly NativeMeshPass nativeSky = new("sky", ["modelViewMatrix"], ["sky", "glow"]);
    internal void RenderSkyDome(MeshRef mesh, int sky, int glow, float[] modelView)
    {
        var renderer = RequireDevice();
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (currentFramebuffer == null || program == null || mesh is not VAO vao || vao.Disposed || vao.VaoId == 0)
        { RenderMesh(mesh); return; }
        int handle = MeshHandle(vao), layout = renderer.NativeMeshLayoutId(handle);
        if (layout < 0) { RenderMesh(mesh); return; }
        RenderTargetFormats? formats = renderer.NativeTargetFormats(CurrentTargetId, uint.MaxValue);
        if (formats == null) { RenderMesh(mesh); return; }
        int count = formats.ColorFormats.Length;
        uint slots = count >= 32 ? uint.MaxValue : (1u << count) - 1u;
        var blend = new AttachmentBlend[Math.Max(count, 1)];
        for (int index = 0; index < blend.Length; index++) blend[index] = AttachmentBlend.Default;
        NativePipeline? pipeline = NativeMeshPipelineFor(nativeSky, program, CurrentTargetId, slots, layout,
            new NativePipelineDescription
            {
                Blend = blend, DepthTest = false, DepthWrite = false,
                Cull = CullModeFlags.None, Topology = PrimitiveTopology.TriangleList,
            });
        if (pipeline == null) { RenderMesh(mesh); return; }
        Rect2D viewport = Stated.Viewport;
        try
        {
            if (renderer.BeginNativePass(new NativePassDescription
            {
                Name = "Sky/" + CurrentTargetId, FramebufferId = CurrentTargetId, ColorSlots = slots,
                Reads = [sky, glow], Flags = PassFlags.AllowSplit,
                ViewportX = viewport.Offset.X, ViewportY = viewport.Offset.Y,
                ViewportWidth = (int)viewport.Extent.Width, ViewportHeight = (int)viewport.Extent.Height,
            }))
            {
                if (modelView is { Length: >= 16 }) renderer.WriteNative(pipeline, nativeSky.Uniforms[0], modelView.AsSpan(0, 16));
                if (renderer.DrawNativeMesh(pipeline, handle,
                    [new NativeTexture(nativeSky.Samplers[0], sky), new NativeTexture(nativeSky.Samplers[1], glow)]))
                    RuntimeStats.drawCallsCount++;
            }
        }
        finally { renderer.EndNativePass(); }
    }
}
