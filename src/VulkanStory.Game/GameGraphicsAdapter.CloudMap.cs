using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

// Exact cloud-map resource operations formerly supplied by the fork graphics seam.
internal sealed partial class GameGraphicsAdapter
{
    private readonly Dictionary<int, FrameBufferRef> cloudFrames = new();
    private FrameBufferRef? cloudSavedFramebuffer;
    private int cloudSavedFramebufferId, cloudBoundTexture;
    private readonly NativeMeshPass nativeCloudMap = new("cloudmap", [], []);
    internal int CreateCloudTexture(int width, int format)
    {
        var renderer = RequireDevice();
        int texture = renderer.CreateTexture2DRaw(width, width, format, IntPtr.Zero, 0);
        SetupTextureSampler(texture, 9728, 33071);
        return texture;
    }
    internal int CreateCloudFramebuffer(int width)
    {
        int handle = RequireDevice().CreateFramebuffer(width, width);
        var frame = RegisterFramebuffer(new FrameBufferRef { FboId = handle, Width = width, Height = width, ColorTextureIds = [] });
        cloudFrames.Add(handle, frame); return handle;
    }
    internal int SaveCloudFramebuffer()
    {
        RequireDevice(); cloudSavedFramebuffer = currentFramebuffer;
        cloudSavedFramebufferId = CurrentTargetId < 0 ? 0 : CurrentTargetId;
        return cloudSavedFramebufferId;
    }
    internal void BindCloudFramebuffer(int handle)
    {
        RequireDevice();
        if (handle == cloudSavedFramebufferId) { SetFramebuffer(cloudSavedFramebuffer, keepViewport: true); return; }
        if (handle == 0) { SetFramebuffer(null, keepViewport: true); return; }
        if (!cloudFrames.TryGetValue(handle, out var frame)) throw new InvalidOperationException("Cloud framebuffer has no renderer owner.");
        SetFramebuffer(frame, keepViewport: true);
    }
    internal void AttachCloudTexture(int attachment, int texture)
    {
        var renderer = RequireDevice();
        if (!cloudFrames.TryGetValue(CurrentTargetId, out var frame) || attachment is < 36064 or > 36065)
            throw new InvalidOperationException("Cloud attachment target changed.");
        int slot = attachment - 36064;
        int[] colors = frame.ColorTextureIds;
        if (colors.Length <= slot) Array.Resize(ref colors, slot + 1);
        colors[slot] = texture; frame.ColorTextureIds = colors;
        renderer.AttachTexture(CurrentTargetId, (FramebufferAttachment)attachment, texture, 0);
    }
    internal void DeleteCloudFramebuffer(int handle)
    {
        RequireDevice();
        if (cloudFrames.Remove(handle, out var frame)) DisposeFramebuffer(frame, false);
    }
    internal void DeleteCloudTexture(int texture) { RequireDevice().DeleteTexture(texture); }
    internal void BindCloudTexture(int texture) { RequireDevice(); cloudBoundTexture = texture; }
    internal void UploadCloudShorts(int level, int x, int y, int width, int height, short[] values) =>
        RequireDevice().UploadTexture2DNormalizedShorts(cloudBoundTexture, level, x, y, width, height, values);
    internal int CloudUniformLocation(int program, string name) => RequireDevice().GetUniformLocation(program, name);
    internal void CloudUniform3(int location, int count, float[] values) => RequireDevice().SetUniformArray3(StatedProgram, location, count, values);
    internal void CloudViewport(int x, int y, int width, int height)
    {
        RequireDevice(); Stated.Viewport = new Rect2D(new Offset2D(x, y), new Extent2D((uint)width, (uint)height));
    }
    internal void GetCloudViewport(int[] values)
    {
        RequireDevice(); if (values.Length < 4) throw new ArgumentException("Cloud viewport destination is too small.");
        var view = Stated.Viewport; values[0] = view.Offset.X; values[1] = view.Offset.Y;
        values[2] = (int)view.Extent.Width; values[3] = (int)view.Extent.Height;
    }
    internal void RenderCloudMap(MeshRef mesh)
    {
        var renderer = RequireDevice();
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (!cloudFrames.ContainsKey(CurrentTargetId) || program == null || program.PassName != "cloudmap" ||
            !ReferenceEquals(program, ShaderRegistry.getProgramByName("cloudmap")) || mesh is not VAO vao || vao.Disposed || vao.VaoId == 0)
        { RenderMesh(mesh); return; }
        int handle = MeshHandle(vao), layout = renderer.NativeMeshLayoutId(handle);
        RenderTargetFormats? formats = renderer.NativeTargetFormats(CurrentTargetId, uint.MaxValue);
        if (layout < 0 || formats == null || formats.ColorFormats.Length == 0) { RenderMesh(mesh); return; }
        var blend = new AttachmentBlend[formats.ColorFormats.Length];
        for (int index = 0; index < blend.Length; index++) blend[index] = AttachmentBlend.Default;
        uint slots = formats.ColorFormats.Length >= 32 ? uint.MaxValue : (1u << formats.ColorFormats.Length) - 1u;
        NativePipeline? pipeline = NativeMeshPipelineFor(nativeCloudMap, program, CurrentTargetId, slots, layout,
            new NativePipelineDescription
            {
                Blend = blend, DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None,
                Topology = renderer.NativeMeshTopology(handle),
            });
        if (pipeline == null) { RenderMesh(mesh); return; }
        string[] names = pipeline.SamplerNames;
        var textures = new NativeTexture[names.Length]; var reads = new int[names.Length];
        for (int index = 0; index < names.Length; index++)
        {
            int texture = programTextures.GetValueOrDefault((program.ProgramId, names[index]));
            textures[index] = new NativeTexture(pipeline.Sampler(names[index]), texture); reads[index] = texture;
        }
        var viewport = Stated.Viewport;
        try
        {
            if (renderer.BeginNativePass(new NativePassDescription
            {
                Name = "CloudMap/" + CurrentTargetId, FramebufferId = CurrentTargetId, ColorSlots = slots,
                Reads = reads, Flags = PassFlags.AllowSplit, ViewportX = viewport.Offset.X, ViewportY = viewport.Offset.Y,
                ViewportWidth = (int)viewport.Extent.Width, ViewportHeight = (int)viewport.Extent.Height,
            }) && renderer.DrawNativeMesh(pipeline, handle, textures)) RuntimeStats.drawCallsCount++;
        }
        finally { renderer.EndNativePass(); }
    }
}
