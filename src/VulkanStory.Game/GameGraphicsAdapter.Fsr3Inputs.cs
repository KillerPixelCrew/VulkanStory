using Vintagestory.API.Client;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    internal bool UsesFsr3Inputs => postSettings?.EffectiveUpscaler == "fsr3";
    internal bool Fsr3HasComposition { get; private set; }
    internal TextureCaptureData? Fsr3MaterialMotionCapture { get; private set; }
    internal ulong Fsr3MaterialMotionCaptureFrameId { get; private set; }

    internal void ResetFsr3InputPublication()
    {
        Fsr3HasComposition = false;
        ClearFsr3InputCapture();
    }

    internal void ClearFsr3InputCapture()
    {
        Fsr3MaterialMotionCapture = null;
        Fsr3MaterialMotionCaptureFrameId = 0;
    }

    // FSR keeps material response separate from OIT coverage. Capture after all
    // AfterOIT/material writers and the normal liquid tail, before sky coverage.
    internal void CaptureFsr3MaterialInputs(bool transparentRendered)
    {
        if (!UsesFsr3Inputs) return;
        var renderer = RequireDevice();
        var primary = FramebufferAt(EnumFrameBuffer.Primary);
        renderer.CaptureFsr3MaterialReactive(primary.ColorTextureIds[FrameState.MotionAttachment]);
        Fsr3HasComposition = transparentRendered;
        if (CaptureSrMotionInputs)
        {
            Fsr3MaterialMotionCapture = renderer.ReadTextureForParity(primary.ColorTextureIds[FrameState.MotionAttachment]) ??
                throw new InvalidOperationException("Scheduled FSR3 material-motion capture failed.");
            Fsr3MaterialMotionCaptureFrameId = renderer.LatencyFrameId;
        }
    }
}
