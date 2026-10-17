using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

/// <summary>Resource and presentation seam for ReScaleFrame's FG controller.</summary>
public sealed unsafe partial class VulkanDevice : IFrameGenerationDevice
{
    ulong IFrameGenerationDevice.LatencyFrameId => LatencyFrameId;
    bool IFrameGenerationDevice.StreamlineFrameGenerationAvailable => StreamlineFrameGenerationAvailable;
    bool IFrameGenerationDevice.StreamlineFrameGenerationReady => StreamlineFrameGenerationReady;
    bool IFrameGenerationDevice.Fsr3ProxyReady => Fsr3ProxyReady;
    bool IFrameGenerationDevice.Fsr3DirectPresentation => Fsr3DirectPresentation;
    nint IFrameGenerationDevice.Fsr3ProxyContext => Fsr3ProxyContext;
    Format IFrameGenerationDevice.Fsr3ProxyFormat => Fsr3ProxyFormat;
    string? IFrameGenerationDevice.Fsr3ProxyFailure => Fsr3ProxyFailure;
    string? IFrameGenerationDevice.XessProxyFailure => XessProxyFailure;
    bool IFrameGenerationDevice.CanPresentGeneratedFrame => CanPresentGeneratedFrame;
    bool IFrameGenerationDevice.PresentationVsyncEnabled => PresentationVsyncEnabled;
    uint IFrameGenerationDevice.XessConfiguredGeneratedFrames => XessConfiguredGeneratedFrames;
    uint? IFrameGenerationDevice.XessLastReportedGeneratedLimit => XessLastReportedGeneratedLimit;
    void IFrameGenerationDevice.UpscalerHandles(out nint instance, out nint physical, out nint logical) =>
        UpscalerHandles(out instance, out physical, out logical);
    void IFrameGenerationDevice.SetFrameGenerationPresentation(string provider) => SetFrameGenerationPresentation(provider);
    void IFrameGenerationDevice.ResetGeneratedFramePresent() => ResetGeneratedFramePresent();
    bool IFrameGenerationDevice.QueueGeneratedFrameForPresent(int texture) => QueueGeneratedFrameForPresent(texture);
    void IFrameGenerationDevice.SuspendStreamlineFrameGeneration() => SuspendStreamlineFrameGeneration();
    void IFrameGenerationDevice.PauseXessFrameGeneration() => PauseXessFrameGeneration();
    void IFrameGenerationDevice.SuspendXessFrameGeneration() => SuspendXessFrameGeneration();
    int IFrameGenerationDevice.TakeStreamlinePresentError() => TakeStreamlinePresentError();
    int IFrameGenerationDevice.SetStreamlineFrameGeneration(bool enabled, uint generatedFrames) =>
        SetStreamlineFrameGeneration(enabled, generatedFrames);
    int IFrameGenerationDevice.GetStreamlineFrameGenerationStateDetails(out StreamlineFrameGenerationState state) =>
        GetStreamlineFrameGenerationStateDetails(out state);
    int IFrameGenerationDevice.TagStreamlineFrame(int depth, int motion, int hudless, int ui,
        int uprightDepth, int uprightMotion, int uprightHudless, int uprightUi,
        in NgxFrameGenerationCamera camera, bool reset) =>
        TagStreamlineFrame(depth, motion, hudless, ui, uprightDepth, uprightMotion, uprightHudless, uprightUi, camera, reset);
    int IFrameGenerationDevice.PrepareXessFrame(int depth, int motion, int motionRg, int hudless, int ui,
        in XessPresentationFrame constants) => PrepareXessFrame(depth, motion, motionRg, hudless, ui, constants);
    int IFrameGenerationDevice.EvaluateFsr3FrameGeneration(Fsr3Native api, nint context,
        int backbuffer, int depth, int motion, int motionRg, int hudless,
        int ui, int uprightScene, int uprightUi, int uprightDepth,
        int uprightMotion, int uprightOutput, int output,
        in TemporalProviderFrame temporal, ulong frameId) =>
        EvaluateFsr3FrameGeneration(api, context, backbuffer, depth, motion, motionRg, hudless,
            ui, uprightScene, uprightUi, uprightDepth, uprightMotion, uprightOutput, output, temporal, frameId);
    void IFrameGenerationDevice.NoteSdkActualPresents(uint count) => VulkanStats.NoteSdkActualPresents(count);
}
