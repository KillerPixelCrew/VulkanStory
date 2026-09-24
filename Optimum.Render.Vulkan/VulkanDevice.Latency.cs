using Optimum.Render.Vulkan.Core;

namespace Optimum.Render.Vulkan;

/// <summary>Frame identity and CPU timing across input, rendering, submissions and presentation.</summary>
public sealed partial class VulkanDevice : Platform.ILatencyStageListener
{
    void Platform.ILatencyStageListener.OnFrameRenderStart() => NoteRenderStageStarted();

    internal FrameTimingRecorder Latency { get; private set; } = new();
    internal ulong LatencyFrameId => _latencyFrameId;
    private ulong _latencyFrameId;
    private bool _latencyFrameIdPending;
    private ulong _latencyRenderStartFrame;

    private void InitializeFrameTiming()
    {
        Latency = new FrameTimingRecorder(MirrorValidationMessage);
        VulkanStats.LatencySource = Latency;
    }

    /// <summary>Called before input; BeginFrame supplies an identity for headless callers.</summary>
    public ulong BeginLatencyFrame()
    {
        _latencyFrameIdPending = true;
        return ++_latencyFrameId;
    }

    private void BeginLatencyFrameIdentity()
    {
        if (!_latencyFrameIdPending) BeginLatencyFrame();
        _latencyFrameIdPending = false;
        _frames.Latency.FrameId = _latencyFrameId;
    }

    internal void NoteRenderStageStarted()
    {
        if (_latencyRenderStartFrame == _latencyFrameId) return;
        _latencyRenderStartFrame = _latencyFrameId;
        Latency.Marker(_latencyFrameId, LatencyMarker.SimulationEnd);
        Latency.Marker(_latencyFrameId, LatencyMarker.RenderSubmitStart);
    }

    private void DisposeLatency()
    {
        if (ReferenceEquals(VulkanStats.LatencySource, Latency)) VulkanStats.LatencySource = null;
    }
}
