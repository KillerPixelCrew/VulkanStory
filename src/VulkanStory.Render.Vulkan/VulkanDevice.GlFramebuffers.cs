using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal bool IsFramebuffer(int name) => _targets.Get(name) is not null;
    internal bool IsTexture(int name) => _textures.Get(name) is { IsGlRenderbuffer: false };
    internal bool IsDepthTexture(int name) => _textures.Get(name) is { } texture && (texture.Aspect & ImageAspectFlags.DepthBit) != 0;

    internal void SetFramebufferColorRoutes(int name, int[]? routes)
    {
        int id = ResolveNativeFramebuffer(name);
        VulkanFramebuffer? target = _targets.Get(id);
        if (target is null) return;
        // Identity selections, including GL_NONE holes, keep the existing
        // attachment scope. Their output write mask is supplied by stated state.
        bool identity = routes is null;
        if (routes is not null)
        {
            identity = true;
            for (int i = 0; i < routes.Length; i++) if (routes[i] >= 0 && routes[i] != i) { identity = false; break; }
        }
        int[]? mapping = identity ? null : routes;
        if (mapping is null ? target.ColorRoutes is null : target.ColorRoutes is not null && mapping.AsSpan().SequenceEqual(target.ColorRoutes)) return;
        EndStagePass();
        _targets.SetColorRoutes(id, mapping);
    }

    internal bool TryFramebufferAttachment(int name, int attachment, out int texture, out int mipLevel, out int layer)
    {
        texture = mipLevel = layer = 0;
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(name));
        if (target is null) return false;
        if (attachment == 0x8D00)
        { texture = target.DepthTextureId; mipLevel = (int)target.DepthMipLevel; layer = (int)target.DepthLayer; return true; }
        if (attachment is 0x8D20 or 0x821A)
        { AttachmentSlot stencil = target.Stencil; texture = stencil.TextureId; mipLevel = (int)stencil.MipLevel; layer = (int)stencil.Layer; return true; }
        int slot = attachment - 0x8CE0;
        if ((uint)slot >= (uint)target.Color.Length) return false;
        AttachmentSlot value = target.Color[slot];
        texture = value.TextureId; mipLevel = (int)value.MipLevel; layer = (int)value.Layer;
        return true;
    }

    internal void AttachFramebufferTextureLevel(int name, int attachment, int texture, int mipLevel, int layer = 0)
    {
        if (!IsFramebuffer(name)) throw new ArgumentOutOfRangeException(nameof(name));
        EndStagePass();
        if (attachment is 0x8D00 or 0x821A) glLevelAttachments[(name, 0x8D00)] = (texture, mipLevel, layer);
        if (attachment is 0x8D20 or 0x821A) glLevelAttachments[(name, 0x8D20)] = (texture, mipLevel, layer);
        if (attachment >= 0x8CE0 && attachment < 0x8CE0 + RenderLimits.MaxColorAttachments)
            glLevelAttachments[(name, attachment)] = (texture, mipLevel, layer);
        ApplyGlLevelAttachment(name, attachment, texture, mipLevel, layer);
    }

    private void RefreshFramebufferExtent(int name)
    {
        VulkanFramebuffer? target = _targets.Get(name);
        if (target is null) return;
        uint width = uint.MaxValue, height = uint.MaxValue;
        void Include(int id, uint mip)
        {
            VulkanTexture? texture = _textures.Get(id);
            if (texture is null || mip >= texture.MipLevels) return;
            width = Math.Min(width, Math.Max(1u, texture.Width >> (int)mip));
            height = Math.Min(height, Math.Max(1u, texture.Height >> (int)mip));
        }
        foreach (AttachmentSlot slot in target.Color) Include(slot.TextureId, slot.MipLevel);
        Include(target.DepthTextureId, target.DepthMipLevel);
        Include(target.Stencil.TextureId, target.Stencil.MipLevel);
        target.Width = width == uint.MaxValue ? 1 : width;
        target.Height = height == uint.MaxValue ? 1 : height;
    }

    internal int FramebufferStatus(int name)
    {
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(name));
        if (target is null) return 0x8219; // GL_FRAMEBUFFER_UNDEFINED
        foreach (var attachment in glLevelAttachments.Where(entry => entry.Key.Framebuffer == target.Id))
            if (!GlLevelAttachmentComplete(attachment.Value.Texture, attachment.Value.Level)) return 0x8CD6;
        bool attached = false;
        int? samples = null;
        bool mismatchedSamples = false;
        bool Valid(int id, uint mip, uint layer, bool depth)
        {
            if (id == 0) return true;
            attached = true;
            VulkanTexture? texture = _textures.Get(id);
            if (texture is not null) { if (samples.HasValue && samples != texture.GlSamples) mismatchedSamples = true; samples ??= texture.GlSamples; }
            return texture is not null && !texture.Volume && mip < texture.MipLevels && layer < texture.Layers &&
                texture.AttachmentLevelValid(mip) &&
                (depth ? (texture.Aspect & ImageAspectFlags.DepthBit) != 0 : texture.Aspect == ImageAspectFlags.ColorBit);
        }
        foreach (AttachmentSlot slot in target.Color)
            if (!Valid(slot.TextureId, slot.MipLevel, slot.Layer, false)) return 0x8CD6;
        if (!Valid(target.DepthTextureId, target.DepthMipLevel, target.DepthLayer, true)) return 0x8CD6;
        if (target.Stencil.TextureId != 0)
        {
            VulkanTexture? stencil = _textures.Get(target.Stencil.TextureId);
            if (stencil is null || (stencil.Aspect & ImageAspectFlags.StencilBit) == 0 ||
                !stencil.AttachmentLevelValid(target.Stencil.MipLevel) || target.Stencil.Layer >= stencil.Layers) return 0x8CD6;
            if (samples.HasValue && samples != stencil.GlSamples) mismatchedSamples = true;
            if (target.DepthTextureId != 0 && (target.DepthTextureId != target.Stencil.TextureId || target.DepthLayer != target.Stencil.Layer || target.DepthMipLevel != target.Stencil.MipLevel)) return 0x8CDD;
            attached = true;
        }
        if (mismatchedSamples) return 0x8D56;
        if (!attached) return 0x8CD7;
        RefreshFramebufferExtent(target.Id);
        return 0x8CD5;
    }

    internal (int Width, int Height) FramebufferExtent(int name)
    {
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(name));
        return target is null ? (0, 0) : ((int)target.Width, (int)target.Height);
    }
}
