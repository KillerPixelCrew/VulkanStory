using System;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>The unjittered camera data required by the Vulkan DLSS-G evaluate helper.</summary>
internal readonly record struct NgxFrameGenerationCamera(
    float[] ViewToClip, float[] ClipToView, float[] ClipToPreviousClip,
    float[] PreviousClipToClip, float Near, float Far, float FovRadians,
    float AspectRatio, float JitterX, float JitterY,
    float PositionX, float PositionY, float PositionZ,
    float UpX, float UpY, float UpZ, float RightX, float RightY, float RightZ,
    float ForwardX, float ForwardY, float ForwardZ)
{
    public bool IsValid => MatrixIsValid(ViewToClip) && MatrixIsValid(ClipToView) &&
        MatrixIsValid(ClipToPreviousClip) && MatrixIsValid(PreviousClipToClip) &&
        float.IsFinite(Near) && Near > 0 && float.IsFinite(Far) && Far > Near &&
        float.IsFinite(FovRadians) && FovRadians > 0 && float.IsFinite(AspectRatio) && AspectRatio > 0 &&
        float.IsFinite(JitterX) && float.IsFinite(JitterY) &&
        float.IsFinite(PositionX) && float.IsFinite(PositionY) && float.IsFinite(PositionZ) &&
        float.IsFinite(UpX) && float.IsFinite(UpY) && float.IsFinite(UpZ) &&
        float.IsFinite(RightX) && float.IsFinite(RightY) && float.IsFinite(RightZ) &&
        float.IsFinite(ForwardX) && float.IsFinite(ForwardY) && float.IsFinite(ForwardZ);

    private static bool MatrixIsValid(float[]? matrix)
    {
        if (matrix?.Length != 16) return false;
        foreach (float value in matrix) if (!float.IsFinite(value)) return false;
        return true;
    }
}

