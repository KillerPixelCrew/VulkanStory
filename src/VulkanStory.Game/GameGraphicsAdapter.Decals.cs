using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

// Retained scoped decal multi-draw; the original mesh pool still culls/selects ranges.
internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeMeshPass nativeDecals = new("decals", [], ["decalTexture", "blockTexture"]);
    private bool decalScopeActive;
    private int decalAtlas, decalBlockAtlas;
    internal void BeginDecalPass()
    {
        RequireDevice();
        if (decalScopeActive) throw new InvalidOperationException("Recursive decal pool draw.");
        int program = ShaderProgramBase.CurrentShaderProgram?.ProgramId ?? 0;
        decalAtlas = programTextures.GetValueOrDefault((program, "decalTexture"));
        decalBlockAtlas = programTextures.GetValueOrDefault((program, "blockTexture"));
        decalScopeActive = true;
    }
    internal void EndDecalPass()
    { decalScopeActive = false; decalAtlas = decalBlockAtlas = 0; }
    private bool TryDrawDecalPoolNative(MeshRef mesh, int[] starts, int[] sizes, int groups)
    {
        var renderer = RequireDevice();
        if (!decalScopeActive || groups <= 0 || currentFramebuffer == null ||
            mesh is not VAO vao || vao.Disposed || vao.VaoId == 0 ||
            !ReferenceEquals(ShaderProgramBase.CurrentShaderProgram, ShaderPrograms.Decals)) return false;
        renderer.GpuMark("decals");
        int handle = MeshHandle(vao), layout = renderer.NativeMeshLayoutId(handle);
        RenderTargetFormats? all = renderer.NativeTargetFormats(CurrentTargetId, uint.MaxValue);
        if (layout < 0 || all == null) return false;
        int count = all.ColorFormats.Length;
        uint slots = count >= 32 ? uint.MaxValue : (1u << count) - 1u;
        int motion = FrameState.MotionAttachment;
        if (ReferenceEquals(currentFramebuffer, platform!.FrameBuffers[0]) && motion is >= 0 and < 32)
            slots &= FrameState.MotionWriteActive ? motion == 31 ? uint.MaxValue : (1u << (motion + 1)) - 1u : (1u << motion) - 1u;
        RenderTargetFormats? formats = renderer.NativeTargetFormats(CurrentTargetId, slots);
        if (formats == null || slots == 0) return false;
        var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        for (int index = 0; index < blend.Length; index++)
        {
            blend[index] = AttachmentBlend.Default; blend[index].Enabled = true;
            if (FrameState.MotionWriteActive && index == motion)
            {
                blend[index].SrcColor = blend[index].SrcAlpha = BlendFactor.One;
                blend[index].DstColor = blend[index].DstAlpha = BlendFactor.Zero;
            }
        }
        NativePipeline? pipeline = NativeMeshPipelineFor(nativeDecals, ShaderPrograms.Decals, CurrentTargetId, slots, layout,
            new NativePipelineDescription
            {
                Blend = blend, DepthTest = true, DepthWrite = true, DepthCompare = CompareOp.Less,
                Cull = CullModeFlags.None, Topology = PrimitiveTopology.TriangleList,
            });
        if (pipeline == null) return false;
        Rect2D viewport = Stated.Viewport;
        bool drawn = false;
        try
        {
            if (renderer.BeginNativePass(new NativePassDescription
            {
                Name = "Decals/" + CurrentTargetId, FramebufferId = CurrentTargetId, ColorSlots = slots,
                Reads = [decalAtlas, decalBlockAtlas], Flags = PassFlags.AllowSplit,
                ViewportX = viewport.Offset.X, ViewportY = viewport.Offset.Y,
                ViewportWidth = (int)viewport.Extent.Width, ViewportHeight = (int)viewport.Extent.Height,
            })) drawn = renderer.DrawNativeMeshMulti(pipeline, handle, starts, sizes, groups,
                [new NativeTexture(nativeDecals.Samplers[0], decalAtlas), new NativeTexture(nativeDecals.Samplers[1], decalBlockAtlas)]);
        }
        finally { renderer.EndNativePass(); }
        if (drawn) RuntimeStats.drawCallsCount++;
        return drawn;
    }
}
