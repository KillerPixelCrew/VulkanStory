using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

internal readonly record struct TextureBlitRectangle(int X0, int Y0, int X1, int Y1);

public sealed unsafe partial class VulkanDevice
{
    private int glColorBlitProgram, glDepthBlitProgram;

    private void TransferImages(VulkanTexture source, uint sourceLevel, uint sourceLayer, VulkanTexture destination, uint destinationLevel, uint destinationLayer,
        int sx, int sy, int dx, int dy, int width, int height, ImageAspectFlags aspect = 0)
    {
        if (aspect == 0) aspect = (source.Aspect & ImageAspectFlags.DepthBit) != 0 ? ImageAspectFlags.DepthBit : source.Aspect;
        EndNativePass();
        CommandBuffer command = _frameActive ? Commands : _uploads.BeginRecording(inlineInFrame: false);
        try
        {
            if (_frameActive) { _targets.FlushAllPendingClears(command); _targets.EndRendering(command); }
            _textures.Require(_barriers, command, source, ResourceUsage.TransferSrc);
            _textures.Require(_barriers, command, destination, ResourceUsage.TransferDst);
            _barriers.Flush(command);
            if (source.Samples != destination.Samples)
            {
                if (source.Samples == SampleCountFlags.Count1Bit || destination.Samples != SampleCountFlags.Count1Bit ||
                    aspect != ImageAspectFlags.ColorBit || source.Format != destination.Format)
                    throw new NotSupportedException("This image transfer requires a matching color multisample resolve.");
                var resolve = new ImageResolve
                {
                    SrcSubresource = new ImageSubresourceLayers(aspect, sourceLevel, sourceLayer, 1),
                    DstSubresource = new ImageSubresourceLayers(aspect, destinationLevel, destinationLayer, 1),
                    SrcOffset = new Offset3D(sx, sy, 0), DstOffset = new Offset3D(dx, dy, 0),
                    Extent = new Extent3D((uint)width, (uint)height, 1),
                };
                _context.Api.CmdResolveImage(command, source.Image, ImageLayout.TransferSrcOptimal,
                    destination.Image, ImageLayout.TransferDstOptimal, 1, &resolve);
            }
            else
            {
                var copy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(aspect, sourceLevel, sourceLayer, 1),
                    DstSubresource = new ImageSubresourceLayers(aspect, destinationLevel, destinationLayer, 1),
                    SrcOffset = new Offset3D(sx, sy, 0), DstOffset = new Offset3D(dx, dy, 0),
                    Extent = new Extent3D((uint)width, (uint)height, 1),
                };
                _context.Api.CmdCopyImage(command, source.Image, ImageLayout.TransferSrcOptimal,
                    destination.Image, ImageLayout.TransferDstOptimal, 1, &copy);
            }
        }
        finally { if (!_frameActive) _uploads.EndRecording(); }
        if (!_frameActive) _uploads.SubmitStandalone();
    }

    /// <summary>Copies/scales a GL rectangle without changing its affine mapping when framebuffer/scissor bounds clip it.</summary>
    internal void BlitTextureRectangle(int sourceId, int sourceLevel, int sourceLayer, int destinationId, int destinationLevel, int destinationLayer,
        TextureBlitRectangle sourceRect, TextureBlitRectangle destinationRect, int sourceWidth, int sourceHeight,
        int destinationWidth, int destinationHeight, Rect2D? scissor, bool linear, ImageAspectFlags aspect = 0, bool framebufferSrgb = true)
    {
        VulkanTexture source = _textures.Get(sourceId) ?? throw new ArgumentException("Missing blit source.");
        VulkanTexture destination = _textures.Get(destinationId) ?? throw new ArgumentException("Missing blit destination.");
        if (aspect == 0) aspect = (source.Aspect & ImageAspectFlags.DepthBit) != 0 ? ImageAspectFlags.DepthBit : source.Aspect;
        bool depth = aspect != ImageAspectFlags.ColorBit;
        if (source.Volume || destination.Volume || sourceLayer < 0 || sourceLayer >= source.Layers || destinationLayer < 0 || destinationLayer >= destination.Layers ||
            sourceLevel < 0 || sourceLevel >= source.MipLevels || destinationLevel < 0 || destinationLevel >= destination.MipLevels)
            throw new ArgumentException("Blits require valid 2D image levels.");
        if ((destination.Aspect & aspect) != aspect || depth && linear)
            throw new ArgumentException("Depth transfers require depth images and nearest filtering.");
        long sourceDeltaX = (long)sourceRect.X1 - sourceRect.X0, sourceDeltaY = (long)sourceRect.Y1 - sourceRect.Y0;
        long destinationDeltaX = (long)destinationRect.X1 - destinationRect.X0, destinationDeltaY = (long)destinationRect.Y1 - destinationRect.Y0;
        if (sourceDeltaX == 0 || sourceDeltaY == 0 || destinationDeltaX == 0 || destinationDeltaY == 0) return;
        sourceWidth = Math.Min(sourceWidth, (int)Math.Max(1u, source.Width >> sourceLevel));
        sourceHeight = Math.Min(sourceHeight, (int)Math.Max(1u, source.Height >> sourceLevel));
        destinationWidth = Math.Min(destinationWidth, (int)Math.Max(1u, destination.Width >> destinationLevel));
        destinationHeight = Math.Min(destinationHeight, (int)Math.Max(1u, destination.Height >> destinationLevel));
        long left = Math.Clamp((long)Math.Min(destinationRect.X0, destinationRect.X1), 0, destinationWidth);
        long bottom = Math.Clamp((long)Math.Min(destinationRect.Y0, destinationRect.Y1), 0, destinationHeight);
        long right = Math.Clamp((long)Math.Max(destinationRect.X0, destinationRect.X1), 0, destinationWidth);
        long top = Math.Clamp((long)Math.Max(destinationRect.Y0, destinationRect.Y1), 0, destinationHeight);
        if (scissor is { } clip)
        {
            left = Math.Max(left, clip.Offset.X); bottom = Math.Max(bottom, clip.Offset.Y);
            right = Math.Min(right, (long)clip.Offset.X + clip.Extent.Width); top = Math.Min(top, (long)clip.Offset.Y + clip.Extent.Height);
        }
        if (left >= right || bottom >= top) return;
        if (aspect is ImageAspectFlags.DepthBit or ImageAspectFlags.StencilBit && source.Samples!=SampleCountFlags.Count1Bit && destination.Samples==SampleCountFlags.Count1Bit)
        {
            ResolveGlDepthStencil(source,(uint)sourceLevel,(uint)sourceLayer,destination,(uint)destinationLevel,(uint)destinationLayer,
                sourceRect,destinationRect,sourceWidth,sourceHeight,destinationWidth,destinationHeight,
                new Rect2D(new Offset2D((int)left,(int)bottom),new Extent2D((uint)(right-left),(uint)(top-bottom))),aspect,destinationId);
            return;
        }
        if (aspect == ImageAspectFlags.StencilBit && source.Samples == SampleCountFlags.Count1Bit && destination.Samples == SampleCountFlags.Count1Bit &&
            (sourceDeltaX != destinationDeltaX || sourceDeltaY != destinationDeltaY))
        {
            TransformGlStencil(source,(uint)sourceLevel,(uint)sourceLayer,destination,(uint)destinationLevel,(uint)destinationLayer,
                sourceRect,destinationRect,sourceWidth,sourceHeight,
                new Rect2D(new Offset2D((int)left,(int)bottom),new Extent2D((uint)(right-left),(uint)(top-bottom))));
            return;
        }
        bool linearResolve = !framebufferSrgb && !depth && VulkanTexture.LinearColorFormat(source.Format) != source.Format &&
            source.Samples != SampleCountFlags.Count1Bit && destination.Samples == SampleCountFlags.Count1Bit;
        int copyId = 0, linearMultisampleId = 0, sourceOriginX = 0, sourceOriginY = 0;
        try
        {
            if (linearResolve || source.Image.Handle == destination.Image.Handle || source.Layers > 1 || source.Cube ||
                source.Samples != SampleCountFlags.Count1Bit && destination.Samples == SampleCountFlags.Count1Bit &&
                (sourceDeltaX != destinationDeltaX || sourceDeltaY != destinationDeltaY))
            {
                // GL leaves overlapping self blits undefined. Snapshot only the
                // sampled rectangle (plus the linear-filter halo), never an atlas.
                sourceOriginX = (int)Math.Max(0L, (long)Math.Min(sourceRect.X0, sourceRect.X1) - (linear ? 1 : 0));
                sourceOriginY = (int)Math.Max(0L, (long)Math.Min(sourceRect.Y0, sourceRect.Y1) - (linear ? 1 : 0));
                int endX = (int)Math.Min(sourceWidth, (long)Math.Max(sourceRect.X0, sourceRect.X1) + (linear ? 1 : 0));
                int endY = (int)Math.Min(sourceHeight, (long)Math.Max(sourceRect.Y0, sourceRect.Y1) + (linear ? 1 : 0));
                if (sourceOriginX >= endX || sourceOriginY >= endY) return;
                uint copyWidth = (uint)(endX - sourceOriginX), copyHeight = (uint)(endY - sourceOriginY);
                if (linearResolve)
                {
                    // Resolves name VkImage, not a view. Copy samples without
                    // conversion into matching UNORM storage, then resolve that
                    // image so disabled GL sRGB state never invokes an EOTF.
                    Format format = VulkanTexture.LinearColorFormat(source.Format);
                    linearMultisampleId = _textures.CreateRenderbuffer(copyWidth, copyHeight, format, ImageAspectFlags.ColorBit,
                        (int)source.Samples, out int samples);
                    if ((SampleCountFlags)samples != source.Samples) throw new NotSupportedException("Linear resolve storage has no matching sample count.");
                    VulkanTexture multisample = _textures.Get(linearMultisampleId)!;
                    TransferImages(source, (uint)sourceLevel, (uint)sourceLayer, multisample, 0, 0,
                        sourceOriginX, sourceOriginY, 0, 0, (int)copyWidth, (int)copyHeight, aspect);
                    copyId = _textures.Create(copyWidth, copyHeight, format);
                    TransferImages(multisample, 0, 0, _textures.Get(copyId)!, 0, 0, 0, 0, 0, 0, (int)copyWidth, (int)copyHeight, aspect);
                }
                else
                {
                    copyId = source.Aspect != ImageAspectFlags.ColorBit || source.Samples != SampleCountFlags.Count1Bit && destination.Samples != SampleCountFlags.Count1Bit
                        ? _textures.CreateRenderbuffer(copyWidth, copyHeight, source.Format, source.Aspect,
                            source.Samples == destination.Samples && source.Samples != SampleCountFlags.Count1Bit ? (int)source.Samples : 0, out _)
                        : _textures.Create(copyWidth, copyHeight, source.Format);
                    TransferImages(source, (uint)sourceLevel, (uint)sourceLayer, _textures.Get(copyId)!, 0, 0,
                        sourceOriginX, sourceOriginY, 0, 0, (int)copyWidth, (int)copyHeight, aspect);
                }
                VulkanTexture copy = _textures.Get(copyId)!;
                sourceId = copyId; source = copy; sourceLevel = sourceLayer = 0;
            }
            // A translation has integer clipping boundaries and is an exact
            // image copy, including between frames during resource construction.
            bool sameBits = source.Format == destination.Format || !framebufferSrgb && !depth &&
                VulkanTexture.LinearColorFormat(source.Format) == VulkanTexture.LinearColorFormat(destination.Format);
            if (sourceDeltaX == destinationDeltaX && sourceDeltaY == destinationDeltaY && sameBits &&
                (source.Samples == destination.Samples || source.Samples != SampleCountFlags.Count1Bit && destination.Samples == SampleCountFlags.Count1Bit))
            {
                long ox = (long)sourceRect.X0 - destinationRect.X0, oy = (long)sourceRect.Y0 - destinationRect.Y0;
                left = Math.Max(left, -ox); bottom = Math.Max(bottom, -oy);
                right = Math.Min(right, sourceWidth - ox); top = Math.Min(top, sourceHeight - oy);
                if (left < right && bottom < top)
                    TransferImages(source, (uint)sourceLevel, (uint)sourceLayer, destination, (uint)destinationLevel, (uint)destinationLayer,
                        checked((int)(left + ox - sourceOriginX)), checked((int)(bottom + oy - sourceOriginY)), (int)left, (int)bottom, (int)(right - left), (int)(top - bottom), aspect);
                return;
            }
            if (aspect == ImageAspectFlags.StencilBit || source.Samples != SampleCountFlags.Count1Bit)
                throw new NotSupportedException("Transformed stencil or multisample-to-multisample blits require another transfer path.");
            DrawGlBlit(sourceId, sourceLevel, destinationId, destinationLevel, destinationLayer, sourceRect, destinationRect,
                sourceWidth, sourceHeight, destinationWidth, destinationHeight, sourceOriginX, sourceOriginY,
                new Rect2D(new Offset2D((int)left, (int)bottom), new Extent2D((uint)(right - left), (uint)(top - bottom))), linear, depth, !framebufferSrgb && !depth);
        }
        finally { if (copyId != 0) DeleteTexture(copyId); if (linearMultisampleId != 0) DeleteTexture(linearMultisampleId); }
    }

    private int GlBlitProgram(bool depth)
    {
        int existing = depth ? glDepthBlitProgram : glColorBlitProgram;
        if (existing != 0 && IsProgram(existing)) return existing;
        var vertex = new ShaderStageDefinition { Type = ShaderStageType.VertexShader, Code = """
            #version 330 core
            void main() {
                vec2 p = vec2(float((gl_VertexID & 1) << 2), float((gl_VertexID & 2) << 1)) - vec2(1.0);
                gl_Position = vec4(p, 0.0, 1.0);
            }
            """ };
        var fragment = new ShaderStageDefinition { Type = ShaderStageType.FragmentShader, Code = """
            #version 330 core
            uniform sampler2D sourceTexture;
            uniform vec2 source0;
            uniform vec2 destination0;
            uniform vec2 sourceStep;
            uniform vec2 sourceLimit;
            uniform vec2 sourceOrigin;
            uniform int sourceLevel;
            uniform int linearFilter;
            """ + (depth ? "\n" : "\nlayout(location=0) out vec4 outColor;\n") + """
            void main() {
                vec2 p = source0 + (gl_FragCoord.xy - destination0) * sourceStep;
                if (any(lessThan(p, vec2(0.0))) || any(greaterThanEqual(p, sourceLimit))) discard;
                p = clamp(p, vec2(0.5), sourceLimit - vec2(0.5));
                vec2 position = p - sourceOrigin;
                ivec2 limit = textureSize(sourceTexture, sourceLevel) - ivec2(1);
                vec4 value;
                if (linearFilter == 0) value = texelFetch(sourceTexture, clamp(ivec2(floor(position)), ivec2(0), limit), sourceLevel);
                else {
                    position -= vec2(0.5);
                    ivec2 q = ivec2(floor(position));
                    vec2 f = fract(position);
                    vec4 a = texelFetch(sourceTexture, clamp(q, ivec2(0), limit), sourceLevel);
                    vec4 b = texelFetch(sourceTexture, clamp(q + ivec2(1,0), ivec2(0), limit), sourceLevel);
                    vec4 c = texelFetch(sourceTexture, clamp(q + ivec2(0,1), ivec2(0), limit), sourceLevel);
                    vec4 d = texelFetch(sourceTexture, clamp(q + ivec2(1,1), ivec2(0), limit), sourceLevel);
                    value = mix(mix(a,b,f.x), mix(c,d,f.x), f.y);
                }
            """ + (depth ? "\ngl_FragDepth = value.r;\n}" : "\noutColor = value;\n}") };
        if (!CompileShader(vertex) || !CompileShader(fragment)) throw new InvalidOperationException("GL blit shader staging failed.");
        int program = LinkProgram(new ShaderProgramDefinition(depth ? "gl-compat-depth-blit" : "gl-compat-color-blit", vertex, fragment, null, null));
        if (program == 0) throw new InvalidOperationException("GL blit shader linking failed: " + GetError());
        if (depth) glDepthBlitProgram = program; else glColorBlitProgram = program;
        return program;
    }

    private void DrawGlBlit(int source, int level, int destination, int destinationLevel, int destinationLayer, TextureBlitRectangle sourceRect,
        TextureBlitRectangle destinationRect, int sourceWidth, int sourceHeight, int width, int height,
        int originX, int originY, Rect2D clip, bool linear, bool depth, bool linearColor)
    {
        if (!_frameActive)
        {
            RecordGlGraphics(() => DrawGlBlit(source, level, destination, destinationLevel, destinationLayer, sourceRect,
                destinationRect, sourceWidth, sourceHeight, width, height, originX, originY, clip, linear, depth, linearColor));
            return;
        }
        EndNativePass();
        _targets.EndPass(Commands);
        _targets.EndRendering(Commands);
        // BlitFramebuffer is not a raster draw in GL. Its shader implementation
        // must not resume/count an application's running occlusion query.
        Action<CommandBuffer>? resumeQuery = _targets.ScopeOpened;
        _targets.ScopeOpened = null;
        int clipMask = DisableGlClipping();
        int target = 0;
        try
        {
            target = CreateFramebuffer(width, height);
            AttachFramebufferTextureLevel(target, depth ? 0x8D00 : 0x8CE0, destination, destinationLevel, destinationLayer);
            int program = GlBlitProgram(depth);
            NativePipeline pipeline = RequestNativePipeline(new NativePipelineDescription
            {
                ProgramId = program, Blend = depth ? [] : [AttachmentBlend.Default],
                DepthTest = depth, DepthWrite = depth, DepthCompare = CompareOp.Always,
                Targets = NativeTargetFormats(target, depth ? 0u : 1u, linearColor)!,
            }, out string reason) ?? throw new InvalidOperationException("GL blit pipeline refused: " + reason);
            WriteNative(pipeline, pipeline.Uniform("source0"), sourceRect.X0, sourceRect.Y0);
            WriteNative(pipeline, pipeline.Uniform("destination0"), destinationRect.X0, destinationRect.Y0);
            WriteNative(pipeline, pipeline.Uniform("sourceStep"), (float)(((double)sourceRect.X1 - sourceRect.X0) / ((double)destinationRect.X1 - destinationRect.X0)),
                (float)(((double)sourceRect.Y1 - sourceRect.Y0) / ((double)destinationRect.Y1 - destinationRect.Y0)));
            WriteNative(pipeline, pipeline.Uniform("sourceLimit"), sourceWidth, sourceHeight);
            WriteNative(pipeline, pipeline.Uniform("sourceOrigin"), originX, originY);
            SetUniform(program, GetUniformLocation(program, "sourceLevel"), level);
            SetUniform(program, GetUniformLocation(program, "linearFilter"), linear ? 1 : 0);
            var sampling = SamplerState.Default with { MagFilter = Filter.Nearest, MinFilter = Filter.Nearest,
                AddressU = SamplerAddressMode.ClampToEdge, AddressV = SamplerAddressMode.ClampToEdge, Mipmapped = true };
            if (!BeginNativePass(new NativePassDescription { Name = "GL/Blit", FramebufferId = target, ColorSlots = depth ? 0u : 1u,
                LinearColorAttachments = linearColor,
                Reads = [source], ViewportWidth = width, ViewportHeight = height, Scissor = clip }) ||
                !DrawNativeFullscreen(pipeline, [new NativeTexture(pipeline.Sampler("sourceTexture"), source, sampling, linearColor, UseTextureMipRange: false)], requirePipeline: true))
                throw new InvalidOperationException("GL blit draw failed: " + NativeDrawRefusal);
        }
        finally { try { EndNativePass(); if (target != 0) DeleteFramebuffer(target); } finally { SetGlClipMask(clipMask); _targets.ScopeOpened = resumeQuery; } }
    }
}
