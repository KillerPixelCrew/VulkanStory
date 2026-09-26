using System;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

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

/// <summary>
/// NGX owns this feature and parameter block. The caller must retire it after the last
/// evaluate's GPU timeline value and before the process-wide NGX shutdown.
/// </summary>
internal sealed unsafe class NgxFrameGenerationFeature : IDisposable
{
    private IntPtr handle;
    private IntPtr parameters;
    private bool disposed;

    private NgxFrameGenerationFeature(IntPtr handle, IntPtr parameters, uint width, uint height, Format format)
    {
        this.handle = handle;
        this.parameters = parameters;
        Width = width;
        Height = height;
        Format = format;
    }

    public uint Width { get; }
    public uint Height { get; }
    public Format Format { get; }
    public bool IsValid => !disposed && handle != IntPtr.Zero;

    public static NgxResult Create(CommandBuffer commands, uint width, uint height, Format format,
        out NgxFrameGenerationFeature? feature)
    {
        feature = null;
        if (commands.Handle == 0 || width == 0 || height == 0) return NgxResult.FailInvalidParameter;
        if (!NgxShim.IsAvailable) return NgxResult.FailShimMissing;

        NgxResult allocated = NgxInterop.AllocateParameters(out IntPtr parameters);
        if (!NgxInterop.Succeeded(allocated)) return allocated;
        if (parameters == IntPtr.Zero) return NgxResult.FailInvalidParameter;

        var block = new NgxParameters(parameters);
        block.SetUInt(NgxParameterNames.CreationNodeMask, 1);
        block.SetUInt(NgxParameterNames.VisibilityNodeMask, 1);
        block.SetUInt(NgxParameterNames.Width, width);
        block.SetUInt(NgxParameterNames.Height, height);
        block.SetUInt(NgxFrameGenerationNames.BackbufferFormat, (uint)format);
        block.SetUInt(NgxFrameGenerationNames.UiRecompositionEnabled, 1);

        NgxResult created = NgxShim.CreateFeature((IntPtr)commands.Handle,
            NgxFeature.FrameGeneration, parameters, out IntPtr handle);
        if (!NgxInterop.Succeeded(created) || handle == IntPtr.Zero)
        {
            NgxInterop.DestroyParameters(parameters);
            return NgxInterop.Succeeded(created) ? NgxResult.FailUnableToInitializeFeature : created;
        }
        feature = new NgxFrameGenerationFeature(handle, parameters, width, height, format);
        return created;
    }

    /// <summary>
    /// Records one 2x interpolation. Resources must already be in shader-read or general
    /// layout as appropriate. All pointers, including the four matrix arrays, remain pinned
    /// until EvaluateFeature returns; NGX stores them in its parameter map during the call.
    /// </summary>
    public NgxResult Evaluate(CommandBuffer commands, NgxResourceVk backbuffer,
        NgxResourceVk depth, NgxResourceVk motion, NgxResourceVk hudless,
        NgxResourceVk ui, NgxResourceVk interpolated, NgxResourceVk real,
        in NgxFrameGenerationCamera camera, bool reset)
    {
        if (!IsValid) return NgxResult.FailFeatureNotFound;
        if (!camera.IsValid || commands.Handle == 0 ||
            backbuffer.ImageViewInfo.ImageView == 0 || depth.ImageViewInfo.ImageView == 0 ||
            motion.ImageViewInfo.ImageView == 0 || interpolated.ImageViewInfo.ImageView == 0 ||
            backbuffer.ImageViewInfo.Width != Width || backbuffer.ImageViewInfo.Height != Height ||
            interpolated.ImageViewInfo.Width != Width || interpolated.ImageViewInfo.Height != Height ||
            backbuffer.ImageViewInfo.Format != (uint)Format ||
            interpolated.ImageViewInfo.Format != (uint)Format ||
            (real.ImageViewInfo.ImageView != 0 &&
             (real.ImageViewInfo.Width != Width || real.ImageViewInfo.Height != Height ||
              real.ImageViewInfo.Format != (uint)Format || real.ReadWrite != 1)) ||
            interpolated.ReadWrite != 1)
            return NgxResult.FailInvalidParameter;

        var block = new NgxParameters(parameters);
        fixed (float* viewToClip = camera.ViewToClip,
            clipToView = camera.ClipToView,
            clipToPrevious = camera.ClipToPreviousClip,
            previousToClip = camera.PreviousClipToClip)
        {
            block.SetVoidPointer(NgxFrameGenerationNames.Backbuffer, (IntPtr)(&backbuffer));
            block.SetVoidPointer(NgxFrameGenerationNames.Depth, (IntPtr)(&depth));
            block.SetVoidPointer(NgxFrameGenerationNames.Motion, (IntPtr)(&motion));
            block.SetVoidPointer(NgxFrameGenerationNames.Hudless,
                hudless.ImageViewInfo.ImageView == 0 ? IntPtr.Zero : (IntPtr)(&hudless));
            block.SetVoidPointer(NgxFrameGenerationNames.Ui,
                ui.ImageViewInfo.ImageView == 0 ? IntPtr.Zero : (IntPtr)(&ui));
            block.SetVoidPointer(NgxFrameGenerationNames.Interpolated, (IntPtr)(&interpolated));
            block.SetVoidPointer(NgxFrameGenerationNames.Real,
                real.ImageViewInfo.ImageView == 0 ? IntPtr.Zero : (IntPtr)(&real));
            block.SetVoidPointer(NgxFrameGenerationNames.ViewToClip, (IntPtr)viewToClip);
            block.SetVoidPointer(NgxFrameGenerationNames.ClipToView, (IntPtr)clipToView);
            block.SetVoidPointer(NgxFrameGenerationNames.ClipToPrevious, (IntPtr)clipToPrevious);
            block.SetVoidPointer(NgxFrameGenerationNames.PreviousToClip, (IntPtr)previousToClip);
            block.SetVoidPointer(NgxFrameGenerationNames.ClipToLensClip, IntPtr.Zero);
            block.SetVoidPointer(NgxFrameGenerationNames.UiAlpha, IntPtr.Zero);
            block.SetVoidPointer(NgxFrameGenerationNames.Distortion, IntPtr.Zero);
            block.SetVoidPointer(NgxFrameGenerationNames.DisableInterpolation, IntPtr.Zero);
            block.SetUInt(NgxFrameGenerationNames.MultiFrameCount, 1);
            block.SetUInt(NgxFrameGenerationNames.MultiFrameIndex, 1);
            block.SetUInt(NgxFrameGenerationNames.Reset, reset ? 1u : 0u);
            block.SetUInt(NgxFrameGenerationNames.DepthInverted, 0);
            block.SetUInt(NgxFrameGenerationNames.ColorBuffersHdr, 0);
            block.SetUInt(NgxFrameGenerationNames.CameraMotionIncluded, 1);
            block.SetFloat(NgxFrameGenerationNames.MotionScaleX, 2f / motion.ImageViewInfo.Width);
            block.SetFloat(NgxFrameGenerationNames.MotionScaleY, 2f / motion.ImageViewInfo.Height);
            block.SetFloat(NgxFrameGenerationNames.JitterX, camera.JitterX);
            block.SetFloat(NgxFrameGenerationNames.JitterY, camera.JitterY);
            block.SetFloat(NgxFrameGenerationNames.Near, camera.Near);
            block.SetFloat(NgxFrameGenerationNames.Far, camera.Far);
            block.SetFloat(NgxFrameGenerationNames.Fov, camera.FovRadians);
            block.SetFloat(NgxFrameGenerationNames.AspectRatio, camera.AspectRatio);
            block.SetFloat(NgxFrameGenerationNames.PositionX, camera.PositionX);
            block.SetFloat(NgxFrameGenerationNames.PositionY, camera.PositionY);
            block.SetFloat(NgxFrameGenerationNames.PositionZ, camera.PositionZ);
            block.SetFloat(NgxFrameGenerationNames.UpX, camera.UpX);
            block.SetFloat(NgxFrameGenerationNames.UpY, camera.UpY);
            block.SetFloat(NgxFrameGenerationNames.UpZ, camera.UpZ);
            block.SetFloat(NgxFrameGenerationNames.RightX, camera.RightX);
            block.SetFloat(NgxFrameGenerationNames.RightY, camera.RightY);
            block.SetFloat(NgxFrameGenerationNames.RightZ, camera.RightZ);
            block.SetFloat(NgxFrameGenerationNames.ForwardX, camera.ForwardX);
            block.SetFloat(NgxFrameGenerationNames.ForwardY, camera.ForwardY);
            block.SetFloat(NgxFrameGenerationNames.ForwardZ, camera.ForwardZ);
            return NgxShim.EvaluateFeature((IntPtr)commands.Handle, handle, parameters, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (handle != IntPtr.Zero && NgxShim.IsAvailable) NgxShim.ReleaseFeature(handle);
        if (parameters != IntPtr.Zero) NgxInterop.DestroyParameters(parameters);
        handle = IntPtr.Zero;
        parameters = IntPtr.Zero;
    }
}

/// <summary>Parameter-map keys from nvsdk_ngx_defs_dlssg.h, SDK 310.9.1.</summary>
internal static class NgxFrameGenerationNames
{
    public const string BackbufferFormat = "DLSSG.BackbufferFormat";
    public const string UiRecompositionEnabled = "DLSSG.UserInterfaceRecompositionEnabled";
    public const string Backbuffer = "DLSSG.Backbuffer";
    public const string Depth = "DLSSG.Depth";
    public const string Motion = "DLSSG.MVecs";
    public const string Hudless = "DLSSG.HUDLess";
    public const string Ui = "DLSSG.UI";
    public const string UiAlpha = "DLSSG.UIAlpha";
    public const string Distortion = "DLSSG.BidirectionalDistortionField";
    public const string Interpolated = "DLSSG.OutputInterpolated";
    public const string Real = "DLSSG.OutputReal";
    public const string DisableInterpolation = "DLSSG.OutputDisableInterpolation";
    public const string ViewToClip = "DLSSG.CameraViewToClip";
    public const string ClipToView = "DLSSG.ClipToCameraView";
    public const string ClipToLensClip = "DLSSG.ClipToLensClip";
    public const string ClipToPrevious = "DLSSG.ClipToPrevClip";
    public const string PreviousToClip = "DLSSG.PrevClipToClip";
    public const string MotionScaleX = "DLSSG.MvecScaleX";
    public const string MotionScaleY = "DLSSG.MvecScaleY";
    public const string JitterX = "DLSSG.JitterOffsetX";
    public const string JitterY = "DLSSG.JitterOffsetY";
    public const string Near = "DLSSG.CameraNear";
    public const string Far = "DLSSG.CameraFar";
    public const string Fov = "DLSSG.CameraFOV";
    public const string AspectRatio = "DLSSG.CameraAspectRatio";
    public const string PositionX = "DLSSG.CameraPosX";
    public const string PositionY = "DLSSG.CameraPosY";
    public const string PositionZ = "DLSSG.CameraPosZ";
    public const string UpX = "DLSSG.CameraUpX";
    public const string UpY = "DLSSG.CameraUpY";
    public const string UpZ = "DLSSG.CameraUpZ";
    public const string RightX = "DLSSG.CameraRightX";
    public const string RightY = "DLSSG.CameraRightY";
    public const string RightZ = "DLSSG.CameraRightZ";
    public const string ForwardX = "DLSSG.CameraFwdX";
    public const string ForwardY = "DLSSG.CameraFwdY";
    public const string ForwardZ = "DLSSG.CameraFwdZ";
    public const string Reset = "DLSSG.Reset";
    public const string DepthInverted = "DLSSG.DepthInverted";
    public const string ColorBuffersHdr = "DLSSG.ColorBuffersHDR";
    public const string CameraMotionIncluded = "DLSSG.CameraMotionIncluded";
    public const string MultiFrameCount = "DLSSG.MultiFrameCount";
    public const string MultiFrameIndex = "DLSSG.MultiFrameIndex";
}
