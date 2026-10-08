using Vintagestory.Client.NoObf;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    /// <summary>Builds borrowed allocation and publication callbacks only after every required session subsystem exists.</summary>
    /// <returns>Complete callback set borrowing this session and its provider/temporal owners.</returns>
    private GameFramebufferHost CreateFramebufferHost()
    {
        RequireOwner();
        if (graphics == null || temporal == null || upscalers == null || frameGeneration == null || window == null)
            throw new InvalidOperationException("Framebuffer host requires the owned session subsystems.");
        return new GameFramebufferHost(
            PixelSize: () => Window.PixelSize,
            Settings: CaptureFramebufferSettings,
            PlanUpscale: upscalers.Plan,
            DisableUpscaler: upscalers.Disable,
            DisableTaa: DisableFramebufferTaa,
            Notification: message => platform.Logger.Notification("{0}", message),
            Error: message => platform.Logger.Error("{0}", message),
            ResetFrameGeneration: frameGeneration.Reset,
            ReleaseAmbientOcclusionTargets: graphics.ReleaseAmbientOcclusionTargets,
            TargetsBuilt: FramebufferTargetsBuilt,
            RequestTemporalReset: temporal.RequestReset);
    }
    private GameFramebufferSettings CaptureFramebufferSettings()
    {
        RequireOwner();
        RendererSettingsState state = services.RendererSettings;
        return new GameFramebufferSettings(
            SsaoQuality: ClientSettings.SSAOQuality,
            ShadowMapQuality: ClientSettings.ShadowMapQuality,
            SsaaLevel: ClientSettings.SSAA,
            RenderScale: state.Settings.RenderScale,
            TaaRequested: state.EffectiveTaa,
            FrameGenerationRequested: state.Settings.FrameGeneration != "off",
            HandheldShadowTier: state.Settings.HandheldShadowTier,
            FrameGenerationProvider: state.Settings.FrameGeneration);
    }
    private void DisableFramebufferTaa(string reason)
    {
        RequireOwner();
        services.RendererSettings.DisableTaa();
        Temporal.RequestReset();
        Temporal.PublishMotionCoverage(false);
        platform.Logger.Warning("VulkanStory: TAA disabled for this session: {0}", reason);
    }
    private void FramebufferTargetsBuilt(IReadOnlyList<FrameBufferRef> targets, bool taaReady)
    {
        RequireOwner();
        ArgumentNullException.ThrowIfNull(targets);
        if (taaReady != Graphics.TaaTargetsReady)
            throw new InvalidOperationException("Framebuffer readiness differs from the owned allocation.");
        // Resources tied to the old target layout cannot survive replacement.
        Graphics.ReleaseOit();
        frameGeneration!.Reset();
        Temporal.PublishMotionCoverage(false);
        // FinishFramebuffers invokes the owned temporal reset after this callback.
    }
}
