using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

internal sealed unsafe partial class TextureManager
{
    internal int CreateRenderbuffer(uint width, uint height, Format format, ImageAspectFlags aspect, int requestedSamples, out int actualSamples)
    {
        ImageUsageFlags usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit |
            (aspect == ImageAspectFlags.ColorBit ? ImageUsageFlags.ColorAttachmentBit : ImageUsageFlags.DepthStencilAttachmentBit);
        if (requestedSamples <= 1 && (aspect & (ImageAspectFlags.ColorBit | ImageAspectFlags.DepthBit)) != 0) usage |= ImageUsageFlags.SampledBit;
        Result result = _context.Api.GetPhysicalDeviceImageFormatProperties(_context.PhysicalDevice, format, ImageType.Type2D,
            ImageTiling.Optimal, usage, 0, out ImageFormatProperties properties);
        if (result != Result.Success || width > properties.MaxExtent.Width || height > properties.MaxExtent.Height)
            throw new NotSupportedException("Renderbuffer format or dimensions are unsupported: " + format);
        int selected = 0;
        for (int count = 1; count <= 64; count *= 2)
            if (count >= Math.Max(1, requestedSamples) && (properties.SampleCounts & (SampleCountFlags)count) != 0) { selected = count; break; }
        if (selected == 0) throw new NotSupportedException("Renderbuffer sample count is unsupported: " + requestedSamples);
        actualSamples = requestedSamples == 0 ? 0 : selected;
        if (width == 0 || height == 0) return 0;
        int id = CreateImage(width, height, format, 1, 1, false, usage, aspect, MemoryPoolClass.DeviceImages, samples: (SampleCountFlags)selected);
        Get(id)!.GlSamples = actualSamples;
        Get(id)!.IsGlRenderbuffer = true;
        return id;
    }
}
