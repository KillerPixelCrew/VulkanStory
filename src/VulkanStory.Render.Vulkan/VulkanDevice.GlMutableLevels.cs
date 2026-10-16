using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private sealed record GlLevel(int Image, int Mip, int Width, int Height, int Depth, int Format);
    private sealed class GlMutableLevels(bool volume)
    {
        internal readonly bool Volume = volume;
        internal readonly Dictionary<int, GlLevel> Levels = new();
        internal readonly HashSet<int> Images = new();
    }
    private readonly Dictionary<int, GlMutableLevels> glMutableLevels = new();
    private readonly Dictionary<(int Framebuffer, int Attachment), (int Texture, int Level, int Layer)> glLevelAttachments = new();

    internal void DefineGlMutableLevel(int name, int level, int width, int height, int depth, int format, bool volume)
    {
        RequireFrameLifetime(); _frames.RequireResourceLifetime();
        VulkanTexture? previous = _textures.Get(name);
        if (!glMutableLevels.ContainsKey(name) && width > 0 && height > 0 && depth > 0)
        {
            long w = previous is null ? (long)width << level : previous.Width, h = previous is null ? (long)height << level : previous.Height;
            long d = previous is null ? (volume ? (long)depth << level : 1) : previous.Depth;
            int limit = volume ? MaxVolumeSize : MaxTextureSize;
            if (w <= limit && h <= limit && d <= limit &&
                (previous is null || previous.Format == GlEnums.TextureFormatFromGl(format)) &&
                width == Math.Max(1, w >> level) && height == Math.Max(1, h >> level) && depth == Math.Max(1, d >> level))
            {
                DefineMutableTexture(name, (int)w, (int)h, (int)d, format, 1 + (int)Math.Log2(Math.Max(w, Math.Max(h, d))), volume, level);
                return;
            }
        }
        EndNativePass();
        if (_frameActive) { _targets.FlushAllPendingClears(Commands); _targets.EndRendering(Commands); }
        if (!glMutableLevels.TryGetValue(name, out GlMutableLevels? storage))
        {
            storage = new GlMutableLevels(volume);
            if (previous is not null)
            {
                int detached = _textures.DetachStorage(name);
                previous.GlLevelBacking = true;
                storage.Images.Add(detached);
                for (int mip = 0; mip < previous.MipLevels; mip++)
                    if (previous.DefinedLevels is null || previous.DefinedLevels.Contains(mip))
                        storage.Levels[mip] = new GlLevel(detached, mip, (int)Math.Max(1u, previous.Width >> mip),
                            (int)Math.Max(1u, previous.Height >> mip), (int)Math.Max(1u, previous.Depth >> mip), previous.GlInternalFormat);
            }
            glMutableLevels.Add(name, storage);
        }
        int image = 0;
        if (width > 0 && height > 0 && depth > 0)
        {
            Format nativeFormat = GlEnums.TextureFormatFromGl(format);
            image = volume ? _textures.CreateVolume((uint)width, (uint)height, (uint)depth, nativeFormat, 1) :
                _textures.CreateMipStorage((uint)width, (uint)height, nativeFormat, 1);
            _textures.Get(image)!.GlInternalFormat = format;
            _textures.Get(image)!.GlLevelBacking = true;
            storage.Images.Add(image);
        }
        storage.Levels[level] = new GlLevel(image, 0, width, height, depth, format);
        RebuildGlSamplingStorage(name, storage);
        CollectGlLevelStorage(storage);
    }

    internal void ResolveGlTextureLevel(ref int name, ref int level)
    {
        if (!glMutableLevels.TryGetValue(name, out GlMutableLevels? storage)) return;
        if (!storage.Levels.TryGetValue(level, out GlLevel? image) || image.Image == 0)
            throw new ArgumentException("The texture level has no image storage.");
        name = image.Image; level = image.Mip;
    }

    internal VulkanTexture? GlTextureLevel(int name, int level)
    {
        ResolveGlTextureLevel(ref name, ref level);
        return _textures.Get(name);
    }

    private void RefreshGlLevelAttachments(int name)
    {
        foreach (var attachment in glLevelAttachments.Where(entry => entry.Value.Texture == name).ToArray())
            ApplyGlLevelAttachment(attachment.Key.Framebuffer, attachment.Key.Attachment, name, attachment.Value.Level, attachment.Value.Layer);
    }

    internal bool TryGlFramebufferAttachment(int framebuffer, int attachment, out int texture, out int level, out int layer)
    {
        if (glLevelAttachments.TryGetValue((framebuffer, attachment == 0x821A ? 0x8D20 : attachment), out var logical))
        { texture = logical.Texture; level = logical.Level; layer = logical.Layer; return true; }
        return TryFramebufferAttachment(framebuffer, attachment, out texture, out level, out layer);
    }

    private void ApplyGlLevelAttachment(int framebuffer, int attachment, int name, int level, int layer)
    {
        int physical = name, mip = level;
        if (glMutableLevels.TryGetValue(name, out GlMutableLevels? storage))
        {
            physical = storage.Levels.TryGetValue(level, out GlLevel? value) ? value.Image : 0;
            mip = value?.Mip ?? 0;
        }
        if (attachment is 0x8D00 or 0x821A) _targets.Attach(framebuffer, -1, physical, (uint)layer, (uint)mip);
        if (attachment is 0x8D20 or 0x821A) _targets.Attach(framebuffer, -2, physical, (uint)layer, (uint)mip);
        if (attachment >= 0x8CE0 && attachment < 0x8CE0 + RenderLimits.MaxColorAttachments)
            _targets.Attach(framebuffer, attachment - 0x8CE0, physical, (uint)layer, (uint)mip);
        RefreshFramebufferExtent(framebuffer); FramebufferStorageChanged?.Invoke(framebuffer);
    }

    private bool GlLevelAttachmentComplete(int name, int level)
    {
        if (!glMutableLevels.TryGetValue(name, out GlMutableLevels? storage)) return true;
        VulkanTexture sample = _textures.Get(name)!;
        return storage.Levels.TryGetValue(level, out GlLevel? value) && value.Image != 0 &&
            level >= sample.State.BaseLevel && (sample.State.MaxLevel < 0 || level <= sample.State.MaxLevel) &&
            (level == sample.State.BaseLevel || sample.MipRangeComplete);
    }

    private void RebuildGlSamplingStorage(int name, GlMutableLevels storage)
    {
        SamplerState state = _textures.SamplingState(name);
        storage.Levels.TryGetValue(state.BaseLevel, out GlLevel? first);
        bool valid = first is { Image: > 0 };
        int width = valid ? first!.Width : 1, height = valid ? first!.Height : 1, depth = valid ? first!.Depth : 1;
        int count = 1 + (int)Math.Log2(Math.Max(width, Math.Max(height, depth)));
        Format format = valid ? _textures.Get(first!.Image)!.Format : Format.R8G8B8A8Unorm;
        int replacement = storage.Volume ? _textures.CreateVolume((uint)width, (uint)height, (uint)depth, format, (uint)count) :
            _textures.CreateMipStorage((uint)width, (uint)height, format, (uint)count);
        try
        {
            VulkanTexture sample = _textures.Get(replacement)!;
            sample.GlSamplingOrigin = state.BaseLevel;
            sample.GlInternalFormat = valid ? first!.Format : 0;
            sample.DefinedLevels = new HashSet<int>();
            if (valid)
                for (int mip = 0; mip < count; mip++)
                    if (storage.Levels.TryGetValue(state.BaseLevel + mip, out GlLevel? value) && value.Image > 0 && value.Format == first!.Format &&
                        value.Width == Math.Max(1, width >> mip) && value.Height == Math.Max(1, height >> mip) && value.Depth == Math.Max(1, depth >> mip))
                        sample.DefinedLevels.Add(mip);
            PublishGlTextureStorage(name, replacement);
            replacement = 0;
        }
        finally { if (replacement != 0) DeleteTexture(replacement); }
        RefreshGlLevelAttachments(name);
    }

    // These copies are needed only for layouts GL permits but one VkImage cannot
    // represent. Physical level storage remains authoritative, including FBO writes.
    // Take the snapshot immediately before sampling, without a host readback.
    private void PrepareGlMutableSampling(int name)
    {
        if (!glMutableLevels.TryGetValue(name, out GlMutableLevels? storage) || _textures.Get(name) is not { } sample) return;
        EndGlLevelRendering();
        foreach (int mip in sample.DefinedLevels!)
        {
            GlLevel value = storage.Levels[sample.GlSamplingOrigin + mip];
            CopyGlLevel(_textures.Get(value.Image)!, value.Mip, sample, mip, value.Width, value.Height, value.Depth);
        }
    }

    private void EndGlLevelRendering()
    {
        if (_frameActive) { _targets.FlushAllPendingClears(Commands); _targets.EndRendering(Commands); }
    }

    private void CopyGlLevel(VulkanTexture source, int sourceMip, VulkanTexture destination, int destinationMip, int width, int height, int depth)
    {
        CommandBuffer command = _uploads.BeginRecording(inlineInFrame: _frameActive);
        try
        {
            _textures.Require(_barriers, command, source, ResourceUsage.TransferSrc);
            _textures.Require(_barriers, command, destination, ResourceUsage.TransferDst); _barriers.Flush(command);
            var copy = new ImageCopy { SrcSubresource = new ImageSubresourceLayers(source.Aspect, (uint)sourceMip, 0, 1),
                DstSubresource = new ImageSubresourceLayers(destination.Aspect, (uint)destinationMip, 0, 1),
                Extent = new Extent3D((uint)width, (uint)height, (uint)depth) };
            _context.Api.CmdCopyImage(command, source.Image, ImageLayout.TransferSrcOptimal, destination.Image, ImageLayout.TransferDstOptimal, 1, &copy);
        }
        finally { _uploads.EndRecording(); }
    }

    private void PublishGlTextureStorage(int name, int replacement)
    {
        if (_sampledTextureOverrides.Remove(name, out int feedback)) _readSelfCopies.Release(feedback);
        _transients.Forget(name);
        if (_textures.Get(name) is { } old) { _descriptors.Release(old.Id); _targets.DropPendingClears(old); }
        _textures.ReplaceStorage(name, replacement, _frames);
        foreach (int framebuffer in _targets.InvalidateTexture(name))
        { RefreshFramebufferExtent(framebuffer); FramebufferStorageChanged?.Invoke(framebuffer); }
    }

    private void CollectGlLevelStorage(GlMutableLevels storage)
    {
        // Images shared by several retained mip definitions are released once.
        foreach (int image in storage.Images.ToArray())
            if (!storage.Levels.Values.Any(level => level.Image == image))
            { storage.Images.Remove(image); ReleaseTexture(image); }
    }

    private void ReleaseGlMutableLevels(int name)
    {
        if (!glMutableLevels.Remove(name, out GlMutableLevels? storage)) return;
        foreach (int image in storage.Images) ReleaseTexture(image);
    }

    private bool GenerateGlMutableMips(int name)
    {
        if (!glMutableLevels.TryGetValue(name, out GlMutableLevels? storage)) return false;
        EndNativePass(); PrepareGlMutableSampling(name);
        VulkanTexture sample = _textures.Get(name)!;
        if (!sample.DefinedLevels!.Contains(0)) return true;
        _textures.GenerateMipmaps(name);
        int first = sample.GlSamplingOrigin, last = sample.EffectiveMaxLevel;
        int detached = _textures.DetachStorage(name);
        sample.GlLevelBacking = true;
        for (int mip = 1; mip <= last; mip++)
            storage.Levels[first + mip] = new GlLevel(detached, mip, (int)Math.Max(1u, sample.Width >> mip),
                (int)Math.Max(1u, sample.Height >> mip), (int)Math.Max(1u, sample.Depth >> mip), sample.GlInternalFormat);
        storage.Images.Add(detached);
        RebuildGlSamplingStorage(name, storage); CollectGlLevelStorage(storage);
        return true;
    }
}
