using System;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan;

/// <summary>
/// The held presentation image of the host's composition gate, and the pipeline-cache readiness
/// the gate reads.
///
/// While a newly entered world is still building its passes (pipelines compiling, motion
/// coverage and temporal history not yet valid), the host keeps the last non-world image on
/// screen. <see cref="CaptureHeldFrame" /> copies the default colour image - what
/// <see cref="Present()" /> copies to the swapchain - into a retained texture, and
/// <see cref="OverwriteDefaultWithHeld" /> copies it back over a hidden world frame. Both work on
/// the default image itself, so every present path (the ordinary blit, the Streamline and FSR3
/// proxies, the XeSS-FG pass-through) shows the held image without a path of its own.
/// </summary>
public sealed unsafe partial class VulkanDevice
{
    /// <summary>The retained copy of the last captured default image; 0 when none is held.</summary>
    private int _heldFrame;

    /// <summary>Lookups that skipped their draw because the pipeline was still compiling, since the device came up.</summary>
    internal long PipelineDrawsSkipped => _pipelines?.DrawsSkipped ?? 0;

    /// <summary>Pipeline keys a draw or a native request is waiting for; prewarm-only jobs no lookup asked for are not counted.</summary>
    internal int PendingDemandPipelines => _pipelines?.PendingDemandKeys ?? 0;

    /// <summary>
    /// Copies the current default colour image into the retained held texture, recreating it when
    /// the default image's extent or format changed. Called after the frame's UI composition.
    /// </summary>
    /// <returns>False outside a frame or without a default image.</returns>
    internal bool CaptureHeldFrame()
    {
        using GpuSection gpuSection = BeginGpuSection("composition_hold_capture");
        if (!_frameActive) return false;
        VulkanTexture? source = DefaultColorTexture();
        if (source == null) return false;
        VulkanTexture? held = _textures.Get(_heldFrame);
        if (held == null || held.Width != source.Width || held.Height != source.Height || held.Format != source.Format)
        {
            ReleaseHeldFrame();
            _heldFrame = CreateUpscaleTexture((int)source.Width, (int)source.Height, source.Format, storage: false);
            held = _textures.Get(_heldFrame);
            if (held == null) { _heldFrame = 0; return false; }
        }
        CommandBuffer commands = Commands;
        // Clears still pending on the default image land first: they are part of what it shows.
        _targets.FlushAllPendingClears(commands);
        _targets.EndPass(commands);
        _targets.EndRendering(commands);
        RecordHeldTransfer(commands, source, held);
        return true;
    }

    /// <summary>
    /// Replaces this frame's default colour image with the held one, after the world and UI have
    /// been composed into it and before frame generation or presentation reads it.
    /// </summary>
    /// <returns>
    /// True when the held image was copied (scale-blitted, linearly, when the window extent changed
    /// since the capture); false when nothing is held or the device cannot blit the pair, in which
    /// case the default image is cleared to opaque black so the hidden world never shows.
    /// </returns>
    internal bool OverwriteDefaultWithHeld()
    {
        using GpuSection gpuSection = BeginGpuSection("composition_hold_overwrite");
        if (!_frameActive) return false;
        VulkanTexture? target = DefaultColorTexture();
        if (target == null) return false;
        CommandBuffer commands = Commands;
        // A clear still pending on the default image would otherwise land after the copy.
        _targets.FlushAllPendingClears(commands);
        _targets.EndPass(commands);
        _targets.EndRendering(commands);
        VulkanTexture? held = _textures.Get(_heldFrame);
        if (held != null && RecordHeldTransfer(commands, held, target)) return true;
        ClearHeldTransferTarget(commands, target);
        return false;
    }

    /// <summary>Releases the retained held image once no submitted frame can still read it.</summary>
    internal void ReleaseHeldFrame()
    {
        if (_heldFrame == 0) return;
        int held = _heldFrame;
        _heldFrame = 0;
        if (_textures != null) ReleaseTexture(held);
    }

    /// <summary>
    /// Records a whole-image colour transfer: an exact copy when extent and format match, a linear
    /// scale-blit when only they differ and the device can filter-blit the pair.
    /// </summary>
    /// <returns>False, with nothing recorded, when neither is possible.</returns>
    private bool RecordHeldTransfer(CommandBuffer commands, VulkanTexture source, VulkanTexture destination)
    {
        bool exact = source.Width == destination.Width && source.Height == destination.Height &&
            source.Format == destination.Format;
        if (!exact && !SupportsLinearBlit(source.Format, destination.Format)) return false;
        _textures.Require(_barriers, commands, source, ResourceUsage.TransferSrc);
        _textures.Require(_barriers, commands, destination, ResourceUsage.TransferDst);
        _barriers.Flush(commands);
        if (exact)
        {
            var region = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                Extent = new Extent3D(source.Width, source.Height, 1),
            };
            _context.Api.CmdCopyImage(commands, source.Image, ImageLayout.TransferSrcOptimal,
                destination.Image, ImageLayout.TransferDstOptimal, 1, &region);
        }
        else
        {
            // Both images keep the renderer's GL orientation, so no flip: only the extent changes.
            var region = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };
            region.SrcOffsets.Element1 = new Offset3D((int)source.Width, (int)source.Height, 1);
            region.DstOffsets.Element1 = new Offset3D((int)destination.Width, (int)destination.Height, 1);
            _context.Api.CmdBlitImage(commands, source.Image, ImageLayout.TransferSrcOptimal,
                destination.Image, ImageLayout.TransferDstOptimal, 1, &region, Filter.Linear);
        }
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("composition hold " + (exact ? "copy " : "blit ") + source.Width + "x" + source.Height +
                " -> " + destination.Width + "x" + destination.Height);
        }
        return true;
    }

    /// <summary>Clears a colour image to opaque black outside any rendering scope.</summary>
    private void ClearHeldTransferTarget(CommandBuffer commands, VulkanTexture destination)
    {
        _textures.Require(_barriers, commands, destination, ResourceUsage.TransferDst);
        _barriers.Flush(commands);
        var black = new ClearColorValue(0f, 0f, 0f, 1f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        _context.Api.CmdClearColorImage(commands, destination.Image, ImageLayout.TransferDstOptimal, &black, 1, &range);
        if (RenderTrace.Enabled) RenderTrace.Write("composition hold: no held image, default cleared to black");
    }

    /// <summary>Whether a linear vkCmdBlitImage from <paramref name="source" /> to <paramref name="destination" /> is supported.</summary>
    private bool SupportsLinearBlit(Format source, Format destination)
    {
        FormatFeatureFlags sourceFeatures = _context.OptimalFormatFeatures(source);
        return (sourceFeatures & FormatFeatureFlags.BlitSrcBit) != 0 &&
            (sourceFeatures & FormatFeatureFlags.SampledImageFilterLinearBit) != 0 &&
            (_context.OptimalFormatFeatures(destination) & FormatFeatureFlags.BlitDstBit) != 0;
    }
}
