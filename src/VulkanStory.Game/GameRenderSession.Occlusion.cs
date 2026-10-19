using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private string? occlusionCaptureRefusal;

    /// <summary>The attached world's current camera position, which terrain draws are relative to; null without a world.</summary>
    internal (double X, double Y, double Z)? OcclusionCameraPosition() =>
        Temporal.CurrentClient?.EntityPlayer?.CameraPos is { } camera ? (camera.X, camera.Y, camera.Z) : null;

    /// <summary>
    /// Captures Primary depth right after the opaque terrain pass, with the camera the terrain was drawn with, as the next
    /// occlusion source. The game's own view (camera at the origin) and projection are the ones its chunk shaders received.
    /// </summary>
    internal void CaptureOcclusionDepth()
    {
        if (stopping || device == null || device.OpenXr != null || Temporal.CurrentClient is not ClientMain client || client.EntityPlayer?.CameraPos is not { } camera) return;
        if (platform.FrameBuffers is not { Count: > 0 } buffers || buffers[0] is not { Disposed: false, DepthTextureId: > 0 } primary) return;
        float[] view = (float[])client.api.Render.CameraMatrixOriginf.Clone();
        float[] projection = (float[])client.api.Render.CurrentProjectionMatrix.Clone();
        if (device.OcclusionDepth.Capture(primary.DepthTextureId, new OcclusionCamera(view, projection, camera.X, camera.Y, camera.Z),
            Performance.PerformancePatchSet.Current is { TerrainOcclusionCull: true, GpuTerrainDrawPreparation: true }))
        {
            occlusionCaptureRefusal = null;
            return;
        }
        string reason = device.OcclusionDepth.LastError ?? "unknown";
        if (reason == occlusionCaptureRefusal) return;
        occlusionCaptureRefusal = reason;
        platform.Logger.Warning("VulkanStory: occlusion depth not captured: {0}", reason);
    }
}
