using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

/// <summary>Supplies current presentation resources to ReScaleFrame's latency owner.</summary>
public sealed unsafe partial class VulkanDevice : ILatencyHost
{
    StreamlineRuntime? ILatencyHost.Streamline => _context?.Streamline;
    IXessLatencyPresenter? ILatencyHost.XessPresenter => _xessPresenter;
    bool ILatencyHost.XessSelected => XessSelected;
    bool ILatencyHost.XessGenerationEnabled => _xessGenerationEnabled;
    bool ILatencyHost.XessGenerationPaused => _xessGenerationPaused;
    bool ILatencyHost.HasSwapchain => _swapchain != null;
    ulong ILatencyHost.LatencyFrameId => _latencyFrameId;
    ulong ILatencyHost.RealPresentId => RealPresentId;
    void ILatencyHost.ReserveFramePresentIds(bool mayGenerate) => ReserveFramePresentIds(mayGenerate);
    void ILatencyHost.SetXessGenerationEnabled(bool enabled) =>
        SetXessGenerationEnabled(_xessPresenter ?? throw new InvalidOperationException("XeSS presenter was released during latency setup."), enabled);
    void ILatencyHost.ReportValidation(string message) => MirrorValidationMessage(message);
    long ILatencyHost.BeginReflexWait() => VulkanStats.WaitStart();
    void ILatencyHost.EndReflexWait(long start) => VulkanStats.NoteWait(WaitSite.ReflexSleep, start);
}
