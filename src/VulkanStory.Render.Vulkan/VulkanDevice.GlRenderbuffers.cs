using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal int MaxGlRenderbufferSize
    {
        get
        {
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, out PhysicalDeviceProperties properties);
            return (int)Math.Min((uint)MaxTextureSize, Math.Min(properties.Limits.MaxFramebufferWidth, properties.Limits.MaxFramebufferHeight));
        }
    }
    internal bool HasGlSrgbAttachment(int name) => _targets.Get(ResolveNativeFramebuffer(name)) is { } target &&
        target.Color.Any(slot => _textures.Get(slot.TextureId)?.Format is Format.R8G8B8A8Srgb or Format.B8G8R8A8Srgb);
    internal int MaxGlRenderbufferSamples
    {
        get
        {
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, out PhysicalDeviceProperties properties);
            for (int count = 64; count > 1; count /= 2)
                if ((properties.Limits.FramebufferColorSampleCounts & (SampleCountFlags)count) != 0) return count;
            return 1;
        }
    }
    internal int FramebufferSamples(int name)
    {
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(name));
        if (target is null) return 0;
        foreach (AttachmentSlot slot in target.Color) if (_textures.Get(slot.TextureId) is { } color) return color.GlSamples;
        return _textures.Get(target.DepthTextureId)?.GlSamples ?? _textures.Get(target.Stencil.TextureId)?.GlSamples ?? 0;
    }
    internal int CreateGlRenderbuffer(int width, int height, int format, int samples, out int actualSamples)
    {
        (Format storage, ImageAspectFlags aspect) = format switch
        {
            0x8051 or 0x8058 => (Format.R8G8B8A8Unorm, ImageAspectFlags.ColorBit),
            0x8C43 => (Format.R8G8B8A8Srgb, ImageAspectFlags.ColorBit),
            0x8229 => (Format.R8Unorm, ImageAspectFlags.ColorBit),
            0x822B => (Format.R8G8Unorm, ImageAspectFlags.ColorBit),
            0x881A => (Format.R16G16B16A16Sfloat, ImageAspectFlags.ColorBit),
            0x8814 => (Format.R32G32B32A32Sfloat, ImageAspectFlags.ColorBit),
            0x81A5 => (Format.D16Unorm, ImageAspectFlags.DepthBit),
            0x81A6 => (Format.X8D24UnormPack32, ImageAspectFlags.DepthBit),
            0x8CAC => (Format.D32Sfloat, ImageAspectFlags.DepthBit),
            0x8D48 => (Format.S8Uint, ImageAspectFlags.StencilBit),
            0x88F0 => (Format.D24UnormS8Uint, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit),
            0x8CAD => (Format.D32SfloatS8Uint, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        return _textures.CreateRenderbuffer((uint)width, (uint)height, storage, aspect, samples, out actualSamples);
    }

    internal void ClearGlStencil(int framebuffer, int value, uint mask, Rect2D? scissor)
    {
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(framebuffer));
        if (target is null || _textures.Get(target.Stencil.TextureId) is not { } image || (mask &= 255) == 0) return;
        AttachmentSlot slot = target.Stencil;
        uint width = Math.Max(1u, image.Width >> (int)slot.MipLevel), height = Math.Max(1u, image.Height >> (int)slot.MipLevel);
        long left = 0, bottom = 0, right = Math.Min(width, target.Width), top = Math.Min(height, target.Height);
        if (scissor is { } clip)
        {
            left = Math.Max(left, clip.Offset.X); bottom = Math.Max(bottom, clip.Offset.Y);
            right = Math.Min(right, (long)clip.Offset.X + clip.Extent.Width); top = Math.Min(top, (long)clip.Offset.Y + clip.Extent.Height);
        }
        if (left >= right || bottom >= top) return;
        EndNativePass();
        if (_frameActive) { _targets.FlushAllPendingClears(Commands); _targets.EndRendering(Commands); }
        if (mask == 255 && left == 0 && bottom == 0 && right == width && top == height)
        {
            CommandBuffer command = _frameActive ? Commands : _uploads.BeginRecording(inlineInFrame: false);
            try
            {
                _textures.Require(_barriers, command, image, ResourceUsage.TransferDst); _barriers.Flush(command);
                var range = new ImageSubresourceRange(ImageAspectFlags.StencilBit, slot.MipLevel, 1, slot.Layer, 1);
                var clear = new ClearDepthStencilValue(0f, unchecked((uint)value) & 255);
                _context.Api.CmdClearDepthStencilImage(command, image.Image, ImageLayout.TransferDstOptimal, &clear, 1, &range);
            }
            finally { if (!_frameActive) _uploads.EndRecording(); }
            if (!_frameActive) _uploads.SubmitStandalone();
            return;
        }
        if (!_frameActive) throw new InvalidOperationException("Partial/masked stencil clears require a graphics recording frame.");
        int temporary = 0, clipMask = DisableGlClipping();
        Action<CommandBuffer>? resume = _targets.ScopeOpened;
        _targets.ScopeOpened = null;
        try
        {
            temporary = CreateFramebuffer((int)width, (int)height);
            AttachFramebufferTextureLevel(temporary, 0x8D20, slot.TextureId, (int)slot.MipLevel, (int)slot.Layer);
            StencilOpState state = NativePipelineDescription.DefaultStencil;
            state.Reference = unchecked((uint)value) & 255; state.WriteMask = mask; state.PassOp = StencilOp.Replace;
            NativePipeline pipeline = RequestNativePipeline(new NativePipelineDescription
            {
                ProgramId = GlClearProgram(true), Targets = NativeTargetFormats(temporary, 0u)!,
                StencilTest = true, StencilFront = state, StencilBack = state,
            }, out string reason) ?? throw new InvalidOperationException(reason);
            if (!BeginNativePass(new NativePassDescription { Name = "GL/StencilClear", FramebufferId = temporary, ColorSlots = 0u,
                ViewportWidth = (int)width, ViewportHeight = (int)height,
                Scissor = new Rect2D(new Offset2D((int)left, (int)bottom), new Extent2D((uint)(right - left), (uint)(top - bottom))) }) ||
                !DrawNativeFullscreen(pipeline, [], requirePipeline: true)) throw new InvalidOperationException(NativeDrawRefusal);
        }
        finally { try { EndNativePass(); if (temporary != 0) DeleteFramebuffer(temporary); } finally { SetGlClipMask(clipMask); _targets.ScopeOpened = resume; } }
    }
}
