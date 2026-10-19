using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private bool standaloneGlRecording;
    internal bool GraphicsRecordingActive => _frameActive;
    internal bool DisplayFrameActive => _frameActive && !standaloneGlRecording;

    /// <summary>Records GL graphics work with ordinary fenced slot resources, without presenting a frame.</summary>
    internal void RecordGlGraphics(Action record)
    {
        if (_frameActive) { record(); return; }
        RequireFrameLifetime();
        _frames.RequireResourceLifetime();
        ulong latencyFrame = _frames.Latency.FrameId;
        standaloneGlRecording = true;
        _frames.Latency.FrameId = 0;
        try
        {
            try
            {
                _nativePass = null;
                _nativeTarget = null;
                FrameSlot slot = _frames.BeginFrame();
                _frameActive = true;
                _bindless?.BeginFrame();
                _readSelfCopies.Collect();
                PrepareGraphicsSlot(slot);
            }
            catch (Exception error) { RetainFailedFrame(error); throw; }

            try { record(); }
            finally
            {
                try
                {
                    EndNativePass();
                    _targets.FlushAllPendingClears(Commands);
                    _targets.EndPass(Commands);
                    _targets.EndRendering(Commands);
                    _bindless?.Flush();
                    _frames.EndFrame();
                    _frameActive = false;
                    ReleaseReadSelfCopies();
                    _readSelfCopies.EndFrame();
                }
                catch (Exception error) { RetainFailedFrame(error); throw; }
            }
        }
        finally
        {
            standaloneGlRecording = false;
            _frames.Latency.FrameId = latencyFrame;
        }
    }

    private void PrepareGraphicsSlot(FrameSlot slot)
    {
        BeginIndirectFrame(slot.Index);
        BeginEntityDrawFrame(slot.Index);
        RetireDeformationGeometry(all: !ComputeDeformationEnabled);
        _descriptorArenas[slot.Index].Reset();
        _computeArenas[slot.Index].Reset();
        _resourceAge.NoteFrame(ResourceIds.Highest);
        _dynamicState.Invalidate();
        _queryRing.BeginSlot(slot.Index, slot.CommandBuffer);
        _gpuTimestamps?.BeginSlot(slot.Index, slot.CommandBuffer);
        _occlusionDepth?.Collect(slot.Index);
        foreach (var depth in _stereoOcclusionDepths) depth?.Collect(slot.Index);
        _readbacks.BeginSlot(slot.Index);
        IDisposable? freedSets = _descriptors.CollectReleases();
        if (freedSets != null) _frames.DeferDeletion(freedSets);
    }
}
