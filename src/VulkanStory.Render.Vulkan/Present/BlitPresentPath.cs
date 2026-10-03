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

    public PipelineStageFlags AcquireWaitStage => PresentWaitStages.BlitAcquireWait;

    public void Record(CommandBuffer commandBuffer, in PresentTarget target, VulkanTexture? source)
    {
        Image destination = target.Image;

        // Every present leaves the swapchain image in PRESENT_SRC, and nothing
        // else writes it, so UNDEFINED discards nothing that matters. The
        // destination and the source move in one barrier command.
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

        // TRANSFER_DST (written at TRANSFER) to PRESENT_SRC. With synchronization2,
        // the release has no destination stage or access; the present semaphore
        // orders the presentation engine after this transition.
        barriers.Require(destination, ImageAspectFlags.ColorBit, _swapchainImage, 0, 1, 0, 1,
            ResourceUsage.PresentSrc, discard: false);
        barriers.Flush(commandBuffer);
    }
}
