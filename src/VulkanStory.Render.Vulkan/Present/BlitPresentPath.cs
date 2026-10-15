using VulkanStory.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>
/// The presentation blit: the whole frame, GUI
/// included, renders into the owned default image, and this copies it into the
/// acquired swapchain image, flipped.
///
/// This inverted blit is the entire Y-flip story for the backend. Everything
/// upstream stays in OpenGL's orientation, which is what keeps intermediate
/// targets and screenshots byte-identical to the GL path; the display wants row 0
/// at the top, so the source rows are read bottom-to-top exactly once, here.
/// </summary>
internal sealed unsafe class BlitPresentPath
{
    private readonly VulkanContext _context;
    private readonly TextureManager _textures;
    /// <summary>Created at the first record, so a path built for its stage table alone needs no texture table.</summary>
    private BarrierBatcher? _barriers;

    /// <summary>The acquired image's state; reset per frame, since its contents are discarded.</summary>
    private readonly ResourceStateTracker _swapchainImage = new(1, 1, depth: false);

    public BlitPresentPath(VulkanContext context, TextureManager textures)
    {
        _context = context;
        _textures = textures;
    }

    /// <summary>Transfer-stage wait mask for acquiring the image consumed by the present blit.</summary>
    public PipelineStageFlags AcquireWaitStage => PresentWaitStages.BlitAcquireWait;

    /// <summary>Records source-to-swapchain image transitions and the final blit; a missing source records only swapchain transitions.</summary>
    public void Record(CommandBuffer commandBuffer, in PresentTarget target, VulkanTexture? source)
    {
        Image destination = target.Image;

        // The acquired image is overwritten entirely. Native swapchain images
        // and FidelityFX replacement buffers can both discard previous contents.
        // The destination and the source move in one barrier command.
        // The acquire touched it last, at the stage this submission waits on it.
        BarrierBatcher barriers = _barriers ??= _textures.CreateBatcher();
        _swapchainImage.Reset((PipelineStageFlags2)(ulong)AcquireWaitStage);
        barriers.Require(destination, ImageAspectFlags.ColorBit, _swapchainImage, 0, 1, 0, 1,
            ResourceUsage.TransferDst, discard: true);

        if (source != null) _textures.Require(barriers, commandBuffer, source, ResourceUsage.TransferSrc);
        barriers.Flush(commandBuffer);

        if (source != null)
        {

            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };
            // Source Y runs backwards: this is the flip.
            blit.SrcOffsets.Element0 = new Offset3D(0, (int)source.Height, 0);
            blit.SrcOffsets.Element1 = new Offset3D((int)source.Width, 0, 1);
            blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
            blit.DstOffsets.Element1 = new Offset3D((int)target.Extent.Width, (int)target.Extent.Height, 1);

            _context.Api.CmdBlitImage(commandBuffer,
                source.Image, ImageLayout.TransferSrcOptimal,
                destination, ImageLayout.TransferDstOptimal,
                1, &blit, Filter.Linear);
        }

        // Proxy backbuffers are intermediate images. Their SDK consumer defines
        // the final layout: DLSS-G copies from TRANSFER_SRC, FidelityFX samples
        // SHADER_READ_ONLY. Only actual WSI images enter PRESENT_SRC.
        ResourceUsage consumedAs = target.Slot.IsStreamlineReplacement ? ResourceUsage.TransferSrc :
            target.Slot.IsFsr3Replacement ? ResourceUsage.SampleFragment : ResourceUsage.PresentSrc;
        barriers.Require(destination, ImageAspectFlags.ColorBit, _swapchainImage, 0, 1, 0, 1,
            consumedAs, discard: false);
        barriers.Flush(commandBuffer);
    }
}
