using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal int MaxVolumeSize => (int)_context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits.MaxImageDimension3D;
    internal event Action<int>? FramebufferStorageChanged;

    internal void DefineMutableTexture(int name, int width, int height, int depth, int internalFormat, int levels, bool volume, int redefinedLevel)
    {
        RequireFrameLifetime();
        _frames.RequireResourceLifetime();
        if (redefinedLevel < 0 && glMutableLevels.ContainsKey(name))
        { EndNativePass(); EndGlLevelRendering(); ReleaseGlMutableLevels(name); }
        Format format = GlEnums.TextureFormatFromGl(internalFormat);
        _textures.RestoreBinding(name);
        VulkanTexture? previous = _textures.Get(name);
        if (previous is not null && previous.Width == width && previous.Height == height && previous.Depth == depth &&
            previous.Format == format && previous.Volume == volume && previous.MipLevels == levels)
        {
            previous.GlInternalFormat = internalFormat;
            previous.GlImmutable = redefinedLevel < 0;
            previous.GlSamplingOrigin = 0;
            if (redefinedLevel < 0) previous.DefinedLevels = null;
            else previous.DefinedLevels?.Add(redefinedLevel);
            RefreshGlLevelAttachments(name);
            return;
        }
        EndNativePass();
        if (_frameActive) { _targets.FlushAllPendingClears(Commands); _targets.EndRendering(Commands); }
        int replacementName = volume ? _textures.CreateVolume((uint)width, (uint)height, (uint)depth, format, (uint)levels) :
            _textures.CreateMipStorage((uint)width, (uint)height, format, (uint)levels);
        try
        {
            VulkanTexture replacement = _textures.Get(replacementName)!;
            replacement.GlInternalFormat = internalFormat;
            replacement.GlImmutable = redefinedLevel < 0;
            replacement.DefinedLevels = redefinedLevel < 0 ? null : new HashSet<int> { redefinedLevel };
            if (previous is not null && previous.Format == format && previous.Volume == volume)
            {
                CommandBuffer command = _uploads.BeginRecording(inlineInFrame: _frameActive);
                try
                {
                    _textures.Require(_barriers, command, previous, ResourceUsage.TransferSrc);
                    _textures.Require(_barriers, command, replacement, ResourceUsage.TransferDst);
                    _barriers.Flush(command);
                    for (uint level = 0; level < Math.Min(previous.MipLevels, replacement.MipLevels); level++)
                    {
                        if (level == redefinedLevel) continue;
                        if (previous.DefinedLevels is not null && !previous.DefinedLevels.Contains((int)level)) continue;
                        uint w = Math.Max(1u, replacement.Width >> (int)level), h = Math.Max(1u, replacement.Height >> (int)level), d = Math.Max(1u, replacement.Depth >> (int)level);
                        if (w != Math.Max(1u, previous.Width >> (int)level) || h != Math.Max(1u, previous.Height >> (int)level) || d != Math.Max(1u, previous.Depth >> (int)level)) continue;
                        var copy = new ImageCopy
                        {
                            SrcSubresource = new ImageSubresourceLayers(previous.Aspect, level, 0, 1),
                            DstSubresource = new ImageSubresourceLayers(replacement.Aspect, level, 0, 1),
                            Extent = new Extent3D(w, h, d),
                        };
                        _context.Api.CmdCopyImage(command, previous.Image, ImageLayout.TransferSrcOptimal, replacement.Image, ImageLayout.TransferDstOptimal, 1, &copy);
                        replacement.DefinedLevels?.Add((int)level);
                    }
                }
                finally { _uploads.EndRecording(); }
            }
            if (_sampledTextureOverrides.Remove(name, out int feedback)) _readSelfCopies.Release(feedback);
            _transients.Forget(name);
            if (previous is not null) { _descriptors.Release(previous.Id); _targets.DropPendingClears(previous); }
            _textures.ReplaceStorage(name, replacementName, _frames);
            replacementName = 0;
            foreach (int framebuffer in _targets.InvalidateTexture(name))
            { RefreshFramebufferExtent(framebuffer); FramebufferStorageChanged?.Invoke(framebuffer); }
            RefreshGlLevelAttachments(name);
        }
        finally { if (replacementName != 0) DeleteTexture(replacementName); }
    }

    internal void UploadTextureStorage(int name, int level, int x, int y, int z, int width, int height, int depth, IntPtr pixels, int bytesPerPixel)
    {
        ResolveGlTextureLevel(ref name, ref level);
        VulkanTexture image = _textures.Get(name) ?? throw new ArgumentException("Texture image is absent.");
        if (_frameActive) { _targets.FlushPendingClears(Commands, image); _targets.EndRendering(Commands); }
        if (image.Volume) _textures.UploadVolume(name, level, x, y, z, (uint)width, (uint)height, (uint)depth, pixels, bytesPerPixel);
        else _textures.Upload(name, level, x, y, (uint)width, (uint)height, pixels, bytesPerPixel);
    }
}
