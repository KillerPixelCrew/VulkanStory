using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

// Retained night/celestial/sun native mesh state and sampler declarations.
internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeMeshPass nativeNightSky = new("nightsky", [], ["ctex"]);
    private readonly NativeMeshPass nativeCelestial = new("celestialobject", [], ["tex", "sky", "glow"]);
    private readonly NativeMeshPass nativeSun = new("standard", [], []);
    private readonly Dictionary<int, string[]> worldSamplerNames = new();
    internal void RenderNightSky(MeshRef mesh) => RenderCelestialMesh(mesh, nativeNightSky, "NightSky", false);
    internal void RenderCelestialBody(MeshRef mesh)
    {
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (ReferenceEquals(program, ShaderPrograms.Standard)) RenderCelestialMesh(mesh, nativeSun, "Sun", true);
        else if (ReferenceEquals(program, ShaderPrograms.Celestialobject)) RenderCelestialMesh(mesh, nativeCelestial, "Celestial", true);
        else RenderMesh(mesh);
    }
    private void RenderCelestialMesh(MeshRef mesh, NativeMeshPass pass, string name, bool blending)
    {
        var renderer = RequireDevice();
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (currentFramebuffer == null || program == null || program.PassName != pass.PassName || mesh is not VAO vao || vao.Disposed || vao.VaoId == 0)
        { RenderMesh(mesh); return; }
        int handle = MeshHandle(vao), layout = renderer.NativeMeshLayoutId(handle);
        RenderTargetFormats? all = renderer.NativeTargetFormats(CurrentTargetId, uint.MaxValue);
        if (layout < 0 || all == null) { RenderMesh(mesh); return; }
        int count = all.ColorFormats.Length;
        uint slots = count >= 32 ? uint.MaxValue : (1u << count) - 1u;
        int motion = FrameState.MotionAttachment;
        if (ReferenceEquals(currentFramebuffer, platform!.FrameBuffers[0]) && motion is >= 0 and < 32)
            slots &= FrameState.MotionWriteActive ? motion == 31 ? uint.MaxValue : (1u << (motion + 1)) - 1u : (1u << motion) - 1u;
        RenderTargetFormats? formats = renderer.NativeTargetFormats(CurrentTargetId, slots);
        if (formats == null || slots == 0) { RenderMesh(mesh); return; }
        var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        for (int index = 0; index < blend.Length; index++)
        {
            blend[index] = AttachmentBlend.Default; blend[index].Enabled = blending;
            if (FrameState.MotionWriteActive && index == motion)
            {
                blend[index].SrcColor = blend[index].SrcAlpha = BlendFactor.One;
                blend[index].DstColor = blend[index].DstAlpha = BlendFactor.Zero;
            }
        }
        NativePipeline? pipeline = NativeMeshPipelineFor(pass, program, CurrentTargetId, slots, layout,
            new NativePipelineDescription
            {
                Blend = blend, DepthTest = false, DepthWrite = false,
                Cull = CullModeFlags.None, Topology = PrimitiveTopology.TriangleList,
            });
        if (pipeline == null) { RenderMesh(mesh); return; }
        string[] names = pass.SamplerNames;
        if (names.Length == 0 && !worldSamplerNames.TryGetValue(program.ProgramId, out names!))
            worldSamplerNames.Add(program.ProgramId, names = renderer.SamplerNamesOf(program.ProgramId));
        var textures = new NativeTexture[names.Length]; var reads = new List<int>();
        for (int index = 0; index < names.Length; index++)
        {
            int texture = programTextures.GetValueOrDefault((program.ProgramId, names[index]));
            textures[index] = new NativeTexture(pipeline.Sampler(names[index]), texture);
            if (texture > 0 && !reads.Contains(texture)) reads.Add(texture);
        }
        Rect2D viewport = Stated.Viewport;
        try
        {
            if (renderer.BeginNativePass(new NativePassDescription
            {
                Name = name + "/" + CurrentTargetId, FramebufferId = CurrentTargetId, ColorSlots = slots,
                Reads = reads.ToArray(), Flags = PassFlags.AllowSplit,
                ViewportX = viewport.Offset.X, ViewportY = viewport.Offset.Y,
                ViewportWidth = (int)viewport.Extent.Width, ViewportHeight = (int)viewport.Extent.Height,
            }) && renderer.DrawNativeMesh(pipeline, handle, textures)) RuntimeStats.drawCallsCount++;
        }
        finally { renderer.EndNativePass(); }
    }
}
