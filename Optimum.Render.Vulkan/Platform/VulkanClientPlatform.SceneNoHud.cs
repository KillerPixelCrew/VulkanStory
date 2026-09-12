using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// DLSS-FG design, step 2: the HUD-less scene snapshot - the guide's <c>pHudless</c>.
///
/// The composited scene exists without any overlay for exactly one moment in the frame:
/// <c>RenderFinalComposition</c> writes it, and the very next stage
/// (<c>RenderAfterFinalComposition</c>) draws selection boxes and work-item guides onto
/// that same image, with the 2D GUI following after the blit. The base takes the snapshot
/// at the end of the composition and calls this to do the copy; the slot it copies into is
/// published as <c>SceneNoHudFrameBufferIndex</c>, the way <c>MotionAttachmentIndex</c> is,
/// and is not allocated at all unless something wants it.
///
/// One <c>vkCmdBlitImage</c>, nearest, which is an exact copy whenever the two are the same
/// size - they are, both display-resolution, on every frame except the one where an
/// upscaler stood down after its display-resolution targets were already built. No
/// pipeline, no descriptor set and no shader of ours, so a frame that asks for the snapshot
/// pays one transfer and nothing else.
///
/// The blit ends the composition's render pass and takes its own layout transitions
/// (<c>VulkanDevice.BlitColorScaled</c> requires TransferSrc on the composite and
/// TransferDst on the snapshot and flushes the barriers), which is the same thing the
/// upscale blit does mid-frame. The overlays that follow bind their target and declare
/// their own pass exactly as they do on a frame with no snapshot, so nothing here needs a
/// pass declaration of its own.
/// </summary>
public partial class VulkanClientPlatform
{
    public override bool CopyOptimumSceneNoHud(FrameBufferRef source, FrameBufferRef destination)
    {
        if (device == null) return false;
        return device.BlitColorScaled(
            source.ColorTextureIds[0], destination.ColorTextureIds[0], linear: false);
    }
}
