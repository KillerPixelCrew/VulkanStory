using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal bool IsFramebuffer(int name) => _targets.Get(name) is not null;
    internal bool IsTexture(int name) => _textures.Get(name) is not null;
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
        _targets.Attach(name, attachment == 0x8D00 ? -1 : attachment - 0x8CE0, texture, (uint)layer, (uint)mipLevel);
        RefreshFramebufferExtent(name);
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
        target.Width = width == uint.MaxValue ? 1 : width;
        target.Height = height == uint.MaxValue ? 1 : height;
    }

    internal int FramebufferStatus(int name)
    {
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(name));
        if (target is null) return 0x8219; // GL_FRAMEBUFFER_UNDEFINED
        bool attached = false;
        bool Valid(int id, uint mip, uint layer, bool depth)
        {
            if (id == 0) return true;
            attached = true;
            VulkanTexture? texture = _textures.Get(id);
            return texture is not null && !texture.Volume && mip < texture.MipLevels && layer < texture.Layers &&
                ((texture.Aspect & ImageAspectFlags.DepthBit) != 0) == depth;
        }
        foreach (AttachmentSlot slot in target.Color)
            if (!Valid(slot.TextureId, slot.MipLevel, slot.Layer, false)) return 0x8CD6;
        if (!Valid(target.DepthTextureId, target.DepthMipLevel, target.DepthLayer, true)) return 0x8CD6;
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
