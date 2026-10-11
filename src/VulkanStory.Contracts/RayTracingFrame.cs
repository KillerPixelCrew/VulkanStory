using System.Numerics;

namespace VulkanStory.Contracts;

/// <summary>Borrowed current/previous camera matrices and real lighting inputs for one traced frame.</summary>
public readonly record struct RayTracingFrame(
    ReadOnlyMemory<float> InverseProjection, ReadOnlyMemory<float> CameraToWorld,
    ReadOnlyMemory<float> PreviousWorldToClip, ReadOnlyMemory<float> CurrentWorldToClip,
    Vector3 OriginDelta, float FarPlane, Vector2 Jitter,
    Vector3 SunDirection, Vector3 SunColour, float SunIntensity,
    Vector3 SkyUpper, Vector3 SkyHorizon, uint FrameIdentity, int Samples, int Bounces, bool Reset,
    ReadOnlyMemory<float> View, ReadOnlyMemory<float> Projection,
    ReadOnlyMemory<float> PreviousView, ReadOnlyMemory<float> PreviousProjection,
    float Milliseconds, string Reconstruction)
{
    public bool HasMatrices => InverseProjection.Length == 16 && CameraToWorld.Length == 16 &&
        PreviousWorldToClip.Length == 16 && CurrentWorldToClip.Length == 16 &&
        View.Length == 16 && Projection.Length == 16 && PreviousView.Length == 16 && PreviousProjection.Length == 16;
}
