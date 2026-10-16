using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private int glColorClearProgram, glDepthClearProgram;
    private const ColorComponentFlags AllGlChannels = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;

    internal void ClearGlColor(int framebuffer, int output, ColorComponentFlags mask, float[] color, Rect2D? scissor, bool framebufferSrgb = true)
    {
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(framebuffer));
        if (target is null || (uint)output >= (uint)target.Color.Length || mask == 0) return;
        AttachmentSlot slot = target.DrawColor(output);
        ClearGlImage(target, slot.TextureId, slot.MipLevel, slot.Layer, mask, color, 1, scissor, false, !framebufferSrgb);
    }
    internal void ClearGlDepth(int framebuffer, float value, Rect2D? scissor)
    {
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(framebuffer));
        if (target is not null) ClearGlImage(target, target.DepthTextureId, target.DepthMipLevel, target.DepthLayer, AllGlChannels, [], value, scissor, true);
    }
    private void ClearGlImage(VulkanFramebuffer framebuffer, int name, uint level, uint layer, ColorComponentFlags mask, float[] color, float depth, Rect2D? scissor, bool depthOnly, bool linearColor = false)
    {
        VulkanTexture? image = _textures.Get(name);
        if (image is null) return;
        uint imageWidth = Math.Max(1u, image.Width >> (int)level), imageHeight = Math.Max(1u, image.Height >> (int)level);
        long left = 0, bottom = 0, right = Math.Min(framebuffer.Width, imageWidth), top = Math.Min(framebuffer.Height, imageHeight);
        if (scissor is { } clip)
        {
            left = Math.Max(left, clip.Offset.X); bottom = Math.Max(bottom, clip.Offset.Y);
            right = Math.Min(right, (long)clip.Offset.X + clip.Extent.Width); top = Math.Min(top, (long)clip.Offset.Y + clip.Extent.Height);
        }
        if (left >= right || bottom >= top) return;
        EndNativePass();
        if (_frameActive) { _targets.FlushAllPendingClears(Commands); _targets.EndRendering(Commands); }
        if (left == 0 && bottom == 0 && right == imageWidth && top == imageHeight && (depthOnly || mask == AllGlChannels))
        {
            CommandBuffer command = _frameActive ? Commands : _uploads.BeginRecording(inlineInFrame: false);
            try
            {
                if (!depthOnly && linearColor && VulkanTexture.LinearColorFormat(image.Format) != image.Format)
                {
                    // Transfer clears use the image's base format. A rendering
                    // clear uses the selected UNORM view and preserves raw GL
                    // clear values without an sRGB encode or CPU round trip.
                    _textures.Require(_barriers, command, image, ResourceUsage.ColorBlend);
                    _barriers.Flush(command);
                    var attachment = new RenderingAttachmentInfo { SType = StructureType.RenderingAttachmentInfo,
                        ImageView = image.ViewOfLayer(layer, level, true), ImageLayout = ImageLayout.ColorAttachmentOptimal,
                        LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
                        ClearValue = new ClearValue(new ClearColorValue(color[0], color[1], color[2], color[3])) };
                    var rendering = new RenderingInfo { SType = StructureType.RenderingInfo,
                        RenderArea = new Rect2D(new Offset2D(), new Extent2D(imageWidth, imageHeight)), LayerCount = 1,
                        ColorAttachmentCount = 1, PColorAttachments = &attachment };
                    _context.Api.CmdBeginRendering(command, &rendering);
                    _context.Api.CmdEndRendering(command);
                }
                else
                {
                    _textures.Require(_barriers, command, image, ResourceUsage.TransferDst);
                    _barriers.Flush(command);
                    var range = new ImageSubresourceRange(depthOnly ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit, level, 1, layer, 1);
                    if (depthOnly)
                    {
                        var value = new ClearDepthStencilValue(depth, 0);
                        _context.Api.CmdClearDepthStencilImage(command, image.Image, ImageLayout.TransferDstOptimal, &value, 1, &range);
                    }
                    else
                    {
                        var value = new ClearColorValue(color[0], color[1], color[2], color[3]);
                        _context.Api.CmdClearColorImage(command, image.Image, ImageLayout.TransferDstOptimal, &value, 1, &range);
                    }
                }
            }
            finally { if (!_frameActive) _uploads.EndRecording(); }
            if (!_frameActive) _uploads.SubmitStandalone();
            return;
        }
        DrawGlClear(name, (int)level, (int)layer, imageWidth, imageHeight, mask, color, depth,
            new Rect2D(new Offset2D((int)left, (int)bottom), new Extent2D((uint)(right - left), (uint)(top - bottom))), depthOnly, linearColor);
    }

    private int GlClearProgram(bool depth)
    {
        int existing = depth ? glDepthClearProgram : glColorClearProgram;
        if (existing != 0 && IsProgram(existing)) return existing;
        var vertex = new ShaderStageDefinition { Type = ShaderStageType.VertexShader, Code = """
            #version 330 core
            void main() {
                vec2 p = vec2(float((gl_VertexID & 1) << 2), float((gl_VertexID & 2) << 1)) - vec2(1.0);
                gl_Position = vec4(p, 0.0, 1.0);
            }
            """ };
        var fragment = new ShaderStageDefinition { Type = ShaderStageType.FragmentShader, Code = depth ? """
            #version 330 core
            uniform float clearDepth;
            void main() { gl_FragDepth = clearDepth; }
            """ : """
            #version 330 core
            uniform vec4 clearValue;
            layout(location=0) out vec4 outColor;
            void main() { outColor = clearValue; }
            """ };
        if (!CompileShader(vertex) || !CompileShader(fragment)) throw new InvalidOperationException("GL clear shader staging failed.");
        int program = LinkProgram(new ShaderProgramDefinition(depth ? "gl-compat-depth-clear" : "gl-compat-color-clear", vertex, fragment, null, null));
        if (program == 0) throw new InvalidOperationException("GL clear shader link failed: " + GetError());
        if (depth) glDepthClearProgram = program; else glColorClearProgram = program;
        return program;
    }
    private void DrawGlClear(int image, int level, int layer, uint width, uint height, ColorComponentFlags mask, float[] color, float depth, Rect2D clip, bool depthOnly, bool linearColor)
    {
        if (!_frameActive)
        {
            RecordGlGraphics(() => DrawGlClear(image, level, layer, width, height, mask, color, depth, clip, depthOnly, linearColor));
            return;
        }
        EndNativePass(); _targets.EndPass(Commands); _targets.EndRendering(Commands);
        Action<CommandBuffer>? resume = _targets.ScopeOpened;
        _targets.ScopeOpened = null;
        int clipMask = DisableGlClipping();
        int target = 0;
        try
        {
            target = CreateFramebuffer((int)width, (int)height);
            AttachFramebufferTextureLevel(target, depthOnly ? 0x8D00 : 0x8CE0, image, level, layer);
            int program = GlClearProgram(depthOnly);
            AttachmentBlend blend = AttachmentBlend.Default; blend.WriteMask = mask;
            NativePipeline pipeline = RequestNativePipeline(new NativePipelineDescription { ProgramId = program,
                Blend = depthOnly ? [] : [blend], DepthTest = depthOnly, DepthWrite = depthOnly, DepthCompare = CompareOp.Always,
                Targets = NativeTargetFormats(target, depthOnly ? 0u : 1u, linearColor)! }, out string reason) ?? throw new InvalidOperationException(reason);
            if (depthOnly) SetUniform(program, GetUniformLocation(program, "clearDepth"), depth);
            else SetUniform(program, GetUniformLocation(program, "clearValue"), color[0], color[1], color[2], color[3]);
            if (!BeginNativePass(new NativePassDescription { Name = "GL/Clear", FramebufferId = target, ColorSlots = depthOnly ? 0u : 1u,
                LinearColorAttachments = linearColor,
                Scissor = clip, ViewportWidth = (int)width, ViewportHeight = (int)height }) || !DrawNativeFullscreen(pipeline, [], requirePipeline: true))
                throw new InvalidOperationException("GL clear draw failed: " + NativeDrawRefusal);
        }
        finally { try { EndNativePass(); if (target != 0) DeleteFramebuffer(target); } finally { SetGlClipMask(clipMask); _targets.ScopeOpened = resume; } }
    }
}
