namespace VulkanStory.Contracts;

/// <summary>Camera state the game used for the frame whose depth becomes the occlusion source.</summary>
/// <param name="View">Column-major world-rotation view matrix with the camera at the origin (camera-relative rendering).</param>
/// <param name="Projection">Column-major projection matrix of that frame, without jitter.</param>
/// <param name="CameraX">World X of the camera the view is relative to.</param>
/// <param name="CameraY">World Y of the camera.</param>
/// <param name="CameraZ">World Z of the camera.</param>
public sealed record OcclusionCamera(float[] View, float[] Projection, double CameraX, double CameraY, double CameraZ);

/// <summary>Answers whether a world-space box was hidden behind already-drawn opaque geometry.</summary>
/// <remarks>Game-independent: callers convert their own bounds. A test that cannot decide answers false (visible).</remarks>
public interface IOcclusionTest
{
    /// <summary>Camera of the frame the current occlusion depth came from, or null while none is available.</summary>
    OcclusionCamera? Source { get; }

    /// <summary>True only when the box lies wholly behind the occluder depth in every covered sample.</summary>
    bool IsOccluded(double minX, double minY, double minZ, double maxX, double maxY, double maxZ);
}
