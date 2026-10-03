using VulkanStory.Contracts;
using Vintagestory.API.Client;

namespace VulkanStory.Game;

// Settings are captured by the process session from ordinary game settings
// and VulkanStory settings. No renderer/provider reads a game singleton.
internal sealed record GameFramebufferSettings(int SsaoQuality, int ShadowMapQuality,
    float SsaaLevel, float RenderScale, bool TaaRequested, bool FrameGenerationRequested,
    bool HandheldShadowTier);

internal sealed record GameFramebufferHost(
    Func<(int Width, int Height)> PixelSize,
    Func<GameFramebufferSettings> Settings,
    Func<int, int, UpscalerPlan?> PlanUpscale,
    Action<string> DisableUpscaler, Action<string> DisableTaa,
    Action<string> Notification, Action<string> Error,
    Action ResetFrameGeneration, Action ReleaseAmbientOcclusionTargets,
    Action<IReadOnlyList<FrameBufferRef>, bool> TargetsBuilt, Action RequestTemporalReset);

internal sealed partial class GameGraphicsAdapter
{
    private GameFramebufferHost? framebufferHost;
    internal void ConfigureFramebufferHost(GameFramebufferHost host)
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Framebuffer configuration requires the session owner thread.");
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(host.PixelSize);
        ArgumentNullException.ThrowIfNull(host.Settings);
        ArgumentNullException.ThrowIfNull(host.PlanUpscale);
        ArgumentNullException.ThrowIfNull(host.DisableUpscaler);
        ArgumentNullException.ThrowIfNull(host.DisableTaa);
        ArgumentNullException.ThrowIfNull(host.Notification);
        ArgumentNullException.ThrowIfNull(host.Error);
        ArgumentNullException.ThrowIfNull(host.ResetFrameGeneration);
        ArgumentNullException.ThrowIfNull(host.ReleaseAmbientOcclusionTargets);
        ArgumentNullException.ThrowIfNull(host.TargetsBuilt);
        ArgumentNullException.ThrowIfNull(host.RequestTemporalReset);
        if (device is null) throw new ObjectDisposedException(nameof(GameGraphicsAdapter));
        if (framebufferHost != null) throw new InvalidOperationException("The framebuffer host is already configured.");
        framebufferHost = host;
    }
    private GameFramebufferHost RequireFramebufferHost() => framebufferHost ??
        throw new InvalidOperationException("The renderer framebuffer host is not configured.");
}
