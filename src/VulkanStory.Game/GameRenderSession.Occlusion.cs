using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private string? occlusionCaptureRefusal;

    // RenderStereo sets TerrainEyeIndex from OpenXrEye before rendering each eye.
    // Resolve at the cull, not session creation, using the same owner as Capture.
    internal IOcclusionTest? OcclusionSource() => device?.OcclusionDepth.Test;

    /// <summary>The attached world's current camera position for depth capture and movement padding; null without a world.</summary>
    internal (double X, double Y, double Z)? OcclusionCameraPosition()
    {
        if (device?.OpenXr != null && OpenXrEye >= 0 && Temporal.CurrentClient is ClientMain client)
        {
            var eye = client.MainCamera.CameraEyePos;
            return (eye.X, eye.Y, eye.Z);
        }
        return Temporal.CurrentClient?.EntityPlayer?.CameraPos is { } camera ? (camera.X, camera.Y, camera.Z) : null;
    }

    /// <summary>
    /// Captures Primary depth right after the opaque terrain pass, with the camera the terrain was drawn with, as the next
    /// occlusion source. The game's own view (camera at the origin) and projection are the ones its chunk shaders received.
    /// </summary>
    internal void CaptureOcclusionDepth()
    {
        if (stopping || device == null || (device.OpenXr != null && OpenXrEye < 0) ||
            Temporal.CurrentClient is not ClientMain client || client.EntityPlayer?.CameraPos is not { } camera) return;
        if (platform.FrameBuffers is not { Count: > 0 } buffers || buffers[0] is not { Disposed: false, DepthTextureId: > 0 } primary) return;
        float[] view = (float[])client.api.Render.CameraMatrixOriginf.Clone();
        float[] projection = (float[])client.api.Render.CurrentProjectionMatrix.Clone();
        double x = camera.X, y = camera.Y, z = camera.Z;
        if (device.OpenXr != null)
        {
            var eye = client.MainCamera.CameraEyePos;
            (x, y, z) = (eye.X, eye.Y, eye.Z);
            // Rebase to this physical eye. Head movement then participates in the
            // same displacement margin as ordinary camera movement.
            view[12] = view[13] = view[14] = 0;
        }
        var settings = Performance.PerformancePatchSet.Current;
        bool gpu = settings?.ResidentTerrain == true;
        if (device.OcclusionDepth.Capture(primary.DepthTextureId, new OcclusionCamera(view, projection, x, y, z), gpu))
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
