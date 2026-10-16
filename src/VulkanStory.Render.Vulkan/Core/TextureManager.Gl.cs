using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

internal sealed unsafe partial class TextureManager
{
    internal int CreateTransferSampled(uint width,uint height,Format format) => CreateImage(width,height,format,1,1,false,
        ImageUsageFlags.SampledBit|ImageUsageFlags.TransferSrcBit|ImageUsageFlags.TransferDstBit,ImageAspectFlags.ColorBit,MemoryPoolClass.DeviceImages);

    internal int CreateMipStorage(uint width, uint height, Format format, uint levels)
    {
        ImageAspectFlags aspect = AspectOf(format);
        ImageUsageFlags usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit |
            (IsDepthFormat(format) ? ImageUsageFlags.DepthStencilAttachmentBit : ImageUsageFlags.ColorAttachmentBit);
        return CreateImage(width, height, format, levels, 1, false, usage, aspect, MemoryPoolClass.DeviceImages);
    }
    /// <summary>Publishes replacement storage under the same API name; recorded GPU users retain the old object.</summary>
    internal void ReplaceStorage(int name, int replacementName, FrameRing ring)
    {
        _uploads.EnterLock();
        try
        {
            RestoreBindingLocked(name);
            VulkanTexture replacement = Get(replacementName) ?? throw new ArgumentException("Replacement image is absent.");
            VulkanTexture? previous = Get(name);
            if (!_reservedNames.Remove(name, out SamplerState state))
                state = previous?.State ?? throw new ArgumentException("Texture name is absent.");
            replacement.State = state;
            _textures[name] = replacement;
            _textures[replacementName] = null;
            _freeIds.Push(replacementName);
            if (previous is not null)
            {
                previous.Released = true;
                Deleted?.Invoke(previous);
                ring.DeferDeletion(previous);
                VulkanStats.NoteTextureDeleted();
            }
        }
        finally { _uploads.ExitLock(); }
    }

    internal void UploadVolume(int name, int level, int x, int y, int z, uint width, uint height, uint depth, IntPtr pixels, int bytesPerPixel)
    {
        VulkanTexture texture = Get(name) ?? throw new ArgumentException("Volume image is absent.");
        ulong bytes = checked((ulong)width * height * depth * (ulong)bytesPerPixel);
        if (bytes == 0) return;
        VulkanStats.NoteUploadRequest();
        CommandBuffer command = _uploads.BeginRecording(_uploads.UsedByPendingFrame(texture.FrameUse));
        try
        {
            if (!ReferenceEquals(Get(name), texture)) return;
            StagingSlice staging = _uploads.Stage(bytes);
            System.Buffer.MemoryCopy((void*)pixels, (void*)staging.Pointer, checked((long)bytes), checked((long)bytes));
            TransitionTexture(command, texture, ImageLayout.TransferDstOptimal);
            var copy = new BufferImageCopy
            {
                BufferOffset = staging.Offset,
                ImageSubresource = new ImageSubresourceLayers(texture.Aspect, (uint)level, 0, 1),
                ImageOffset = new Offset3D(x, y, z), ImageExtent = new Extent3D(width, height, depth),
            };
            _context.Api.CmdCopyBufferToImage(command, staging.Buffer, texture.Image, ImageLayout.TransferDstOptimal, 1, &copy);
            TransitionTexture(command, texture, ImageLayout.ShaderReadOnlyOptimal);
        }
        finally { _uploads.EndRecording(); }
    }
}
