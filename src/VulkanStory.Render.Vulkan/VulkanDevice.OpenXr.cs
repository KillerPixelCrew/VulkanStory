using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Vr;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal bool OpenXrEnabled { get; set; }
    internal bool OpenXrSimulated { get; set; }
    internal (int Width, int Height) OpenXrSimulatedEyeSize { get; set; }
    internal XrRuntime? OpenXr { get; private set; }
    internal string OpenXrStatus => OpenXr is { } xr ? xr.Status + " " + xr.FrameStatus : openXrRefusal ?? "OpenXR disabled.";
    private string? openXrRefusal;
    private int openXrTransfer;
    private int openXrTransferWidth, openXrTransferHeight;
    private Format openXrTransferFormat;

    private void PrepareOpenXr(bool headless)
    {
        if (!OpenXrEnabled) return;
        if (OpenXrSimulated)
        {
            OpenXr = SimulatedXrRuntime.Create(OpenXrSimulatedEyeSize);
            return;
        }
        if (headless) return;
        try { OpenXr = OpenXrRuntime.Create(); }
        catch (Exception error)
        {
            openXrRefusal = "OpenXR unavailable: " + error.Message;
            AddDiagnostic(openXrRefusal);
            throw new InvalidOperationException(openXrRefusal, error);
        }
    }

    // Capture the current eye before the shared raster targets are cleared for the
    // next eye. The XR runtime owns the eye images; this device owns the transfer texture.
    internal void CopyOpenXrEye(int eye)
    {
        if (OpenXr is not { ShouldRender: true } xr) return;
        RequireFrameLifetime();
        var source = DefaultColorTexture() ?? throw new InvalidOperationException("The eye frame has no finished colour image.");
        CommandBuffer commands = Commands;
        _targets.FlushAllPendingClears(commands);
        _targets.EndPass(commands);
        _targets.EndRendering(commands);
        if (openXrTransferWidth != xr.Width || openXrTransferHeight != xr.Height || openXrTransferFormat != xr.TransferFormat)
        {
            if (openXrTransfer != 0) ReleaseTexture(openXrTransfer);
            openXrTransfer = _textures.Create((uint)xr.Width, (uint)xr.Height, xr.TransferFormat);
            openXrTransferWidth = xr.Width; openXrTransferHeight = xr.Height;
            openXrTransferFormat = xr.TransferFormat;
        }
        var transfer = _textures.Get(openXrTransfer) ?? throw new InvalidOperationException("The eye transfer texture was lost.");
        _textures.TransitionTexture(commands, source, ImageLayout.TransferSrcOptimal);
        _textures.TransitionTexture(commands, transfer, ImageLayout.TransferDstOptimal);
        var blit = new ImageBlit {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1) };
        // Match the desktop presentation's GL-to-Vulkan vertical orientation.
        blit.SrcOffsets.Element0 = new Offset3D(0, (int)source.Height, 0);
        blit.SrcOffsets.Element1 = new Offset3D((int)source.Width, 0, 1);
        blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
        blit.DstOffsets.Element1 = new Offset3D(xr.Width, xr.Height, 1);
        _context.Api.CmdBlitImage(commands, source.Image, ImageLayout.TransferSrcOptimal,
            transfer.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Nearest);
        _textures.TransitionTexture(commands, transfer, ImageLayout.TransferSrcOptimal);
        Image destination = xr.Image(eye);
        // Simulated images are reused across in-flight frames. Real XR acquire/wait
        // owns that ordering; the simulated copies need a dependency on earlier writes.
        bool simulated = xr is SimulatedXrRuntime;
        var barrier = new ImageMemoryBarrier2 { SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = simulated ? PipelineStageFlags2.AllCommandsBit : PipelineStageFlags2.None,
            SrcAccessMask = simulated ? AccessFlags2.MemoryWriteBit : AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.TransferBit, DstAccessMask = AccessFlags2.TransferWriteBit,
            OldLayout = ImageLayout.Undefined, NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = destination, SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1) };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
        _context.Api.CmdPipelineBarrier2(commands, &dependency);
        var copy = new ImageCopy {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D((uint)xr.Width, (uint)xr.Height, 1) };
        // The blit also handles RGBA/BGRA channel order. A matching UNORM -> sRGB
        // raw copy then preserves gamma-encoded bytes without a second gamma encode.
        // TODO(VR-01): write final composition directly to non-owning XR image views,
        // preserving this colour/Y contract while removing the flip/transfer image.
        _context.Api.CmdCopyImage(commands, transfer.Image, ImageLayout.TransferSrcOptimal,
            destination, ImageLayout.TransferDstOptimal, 1, &copy);
        barrier.SrcStageMask = PipelineStageFlags2.TransferBit;
        barrier.SrcAccessMask = AccessFlags2.TransferWriteBit;
        barrier.DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit;
        barrier.DstAccessMask = AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit;
        barrier.OldLayout = ImageLayout.TransferDstOptimal;
        barrier.NewLayout = ImageLayout.ColorAttachmentOptimal;
        _context.Api.CmdPipelineBarrier2(commands, &dependency);
        xr.MarkCopied(eye);
    }
}
