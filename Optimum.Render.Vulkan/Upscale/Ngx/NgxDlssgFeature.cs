using System;
using System.Numerics;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// What a DLSS frame generation feature is created for. Changing any of it means a
/// new feature: NGX sizes its internal buffers from the display size and format at
/// creation, and UI recomposition is a create-time decision (the header says so of
/// <see cref="NgxParameterNames.DlssgUserInterfaceRecompositionEnabled" />), so none
/// of it can be varied per evaluate (see <see cref="NgxDlssgFeature.Matches" />).
/// </summary>
/// <param name="DisplayWidth">The backbuffer's width: DLSS-G runs at display resolution.</param>
/// <param name="DisplayHeight">The backbuffer's height.</param>
/// <param name="BackbufferFormat">
/// The backbuffer's <c>VkFormat</c>. Both outputs must have exactly this format
/// (DLSS-FG Programming Guide v310.7.0, "Output Interpolated Frame").
/// </param>
/// <param name="UserInterfaceRecomposition">
/// <c>DLSSG.UserInterfaceRecompositionEnabled</c>. The header (DLSS 4 section of
/// <c>nvsdk_ngx_defs_dlssg.h</c>): "When enabled, the algorithm will generate output
/// frames using the HUDless and UI Color &amp; Alpha textures if present." The guide
/// text we have never mentions it, so what it changes is measured, not assumed:
/// <c>NgxDlssgEvaluateTests</c> runs the same frames with it on and off.
/// </param>
internal readonly record struct NgxDlssgSettings(
    uint DisplayWidth,
    uint DisplayHeight,
    Format BackbufferFormat,
    bool UserInterfaceRecomposition)
{
    public override string ToString() =>
        DisplayWidth + "x" + DisplayHeight + " " + BackbufferFormat +
        " uir=" + (UserInterfaceRecomposition ? 1 : 0);
}

/// <summary>
/// One frame's DLSS-G evaluate inputs besides the images, in NGX's own terms
/// (<c>NVSDK_NGX_DLSSG_Opt_Eval_Params</c>, <c>nvsdk_ngx_params_dlssg.h</c>).
///
/// <list type="bullet">
/// <item><b>Matrices</b> are NGX's <c>float[4][4]</c>: row-major "assuming
/// post-multiplication" (guide v310.7.0, "Required Input Engine Data"), and none may
/// contain the TAA jitter (header). <see cref="Matrix4x4" /> is 16 floats in
/// M11..M44 order, so its memory is that row-major array verbatim. A GL-style
/// column-major <c>float[16]</c> for column vectors is the same 16 numbers in the
/// same memory order - column-major storage of M is row-major storage of M^T, and
/// M^T is the post-multiplied form of M - so <see cref="MatrixFromGl" /> copies
/// rather than transposes.</item>
/// <item><b>Motion-vector scale.</b> The guide: "scale factors used to rescale your
/// input motion vector values to the required pixel-units. Common values are 1 (if
/// the values are already in the correct units)". Our vectors are render pixels at
/// the motion image's resolution (temporal contract §7.1), so (1, 1), the same as
/// DLSS SR's <see cref="NgxDlssEvaluation.MotionVectorScaleX" />. The header's
/// "normalize to [-1,1]" comment on the struct field contradicts the guide; the
/// guide's pixel convention is the one the SR evaluate already proved on this
/// driver.</item>
/// <item><b>Jitter</b> is the same displacement the SR evaluate takes
/// (<see cref="NgxDlssEvaluation.JitterOffsetX" />).</item>
/// </list>
/// </summary>
internal readonly record struct NgxDlssgEvaluation
{
    public NgxDlssgEvaluation()
    {
    }

    public Matrix4x4 CameraViewToClip { get; init; } = Matrix4x4.Identity;
    public Matrix4x4 ClipToCameraView { get; init; } = Matrix4x4.Identity;

    /// <summary>Identity: Optimum applies no lens distortion.</summary>
    public Matrix4x4 ClipToLensClip { get; init; } = Matrix4x4.Identity;

    public Matrix4x4 ClipToPrevClip { get; init; } = Matrix4x4.Identity;
    public Matrix4x4 PrevClipToClip { get; init; } = Matrix4x4.Identity;

    public float JitterOffsetX { get; init; }
    public float JitterOffsetY { get; init; }

    /// <summary>(1, 1) for render-pixel vectors; see the type comment.</summary>
    public float MotionVectorScaleX { get; init; } = 1f;
    public float MotionVectorScaleY { get; init; } = 1f;

    public Vector3 CameraPosition { get; init; }
    public Vector3 CameraUp { get; init; } = Vector3.UnitY;
    public Vector3 CameraRight { get; init; } = Vector3.UnitX;
    public Vector3 CameraForward { get; init; } = -Vector3.UnitZ;

    public float CameraNear { get; init; } = 0.1f;
    public float CameraFar { get; init; } = 1000f;

    /// <summary>Radians.</summary>
    public float CameraFov { get; init; } = MathF.PI / 2f;

    /// <summary>View-space width over height.</summary>
    public float CameraAspectRatio { get; init; } = 1f;

    /// <summary>Depth is 0 = near in Optimum (temporal contract §7.3), so false.</summary>
    public bool DepthInverted { get; init; }

    /// <summary>Our motion vectors carry camera motion (§7.1).</summary>
    public bool CameraMotionIncluded { get; init; } = true;

    /// <summary>
    /// The previous frame has no connection to this one. The guide: "When the
    /// algorithm is reset, the next interpolated frame will be a copy of the input
    /// color."
    /// </summary>
    public bool Reset { get; init; }

    /// <summary>A menu or paused screen: no game frames are being rendered.</summary>
    public bool NotRenderingGameFrames { get; init; }

    /// <summary>
    /// <c>DLSSG.BackbufferFrameID</c>: optional, +1 per fully rendered backbuffer.
    /// 0 leaves it unset.
    /// </summary>
    public ulong BackbufferFrameId { get; init; }

    /// <summary>1 for 2x generation; the guide: "Must be 1 for single-frame generation."</summary>
    public uint MultiFrameCount { get; init; } = 1;

    /// <summary>1 for 2x generation.</summary>
    public uint MultiFrameIndex { get; init; } = 1;

    /// <summary>The SDK struct's default, 40 (header).</summary>
    public float MinRelativeLinearDepthObjectSeparation { get; init; } = 40f;

    /// <summary>
    /// A GL-convention matrix (column-major <c>float[16]</c>, column vectors) as NGX's
    /// row-major post-multiplied <c>float[4][4]</c>: the same numbers in the same
    /// memory order; see the type comment.
    /// </summary>
    public static Matrix4x4 MatrixFromGl(ReadOnlySpan<float> glColumnMajor)
    {
        if (glColumnMajor.Length < 16) throw new ArgumentException("a 4x4 matrix needs 16 values");
        return new Matrix4x4(
            glColumnMajor[0], glColumnMajor[1], glColumnMajor[2], glColumnMajor[3],
            glColumnMajor[4], glColumnMajor[5], glColumnMajor[6], glColumnMajor[7],
            glColumnMajor[8], glColumnMajor[9], glColumnMajor[10], glColumnMajor[11],
            glColumnMajor[12], glColumnMajor[13], glColumnMajor[14], glColumnMajor[15]);
    }
}

/// <summary>
/// A live DLSS frame generation feature (<c>NVSDK_NGX_Feature_FrameGeneration</c>,
/// id 11): the NGX handle plus the parameter block it was created with and evaluates
/// through. It mirrors <see cref="NgxDlssFeature" /> step for step, and for the same
/// reasons:
/// <list type="number">
/// <item><see cref="Create" /> allocates its own parameter block, sets what
/// <c>NGX_VK_CREATE_DLSSG</c> sets plus the UI recomposition switch, and calls
/// <c>CreateFeature1</c> on a command buffer inside an open frame.</item>
/// <item><see cref="Evaluate" /> sets every per-frame parameter
/// <c>NGX_VK_EVALUATE_DLSSG</c> sets - optional ones to null or zero, because the
/// block is reused - and calls <c>EvaluateFeature</c> on the frame's command buffer.
/// It places no barriers: NGX performs no synchronisation, and neither does this
/// type.</item>
/// <item><see cref="Dispose" /> releases the handle and destroys the block. Only the
/// frame ring's retire queue calls it, once every frame that could name the handle
/// has completed (<c>VulkanDevice.RetireFrameGeneration</c>).</item>
/// </list>
///
/// <para><b>No VRAM estimate.</b> <c>NGX_VK_ESTIMATE_VRAM_DLSSG</c> pulls a function
/// pointer out of the parameter block and calls it directly; that function lives in
/// libnvidia-ngx, so calling it from managed code hits the return-address abort every
/// other NGX entry point does (<see cref="NgxInterop.ManagedCallSiteIsSupported" />).
/// It would need a new shim entry point mirroring
/// <c>OptimumNgx_DlssGetOptimalSettings</c>, and creation does not depend on it, so it
/// is skipped: an out-of-memory creation answers FAIL_OutOfGPUMemory like any other
/// refusal.</para>
///
/// Nothing here throws. Every call answers an <see cref="NgxResult" />.
///
/// <para><b>Threading.</b> Created, evaluated and retired on the render thread only;
/// the present thread never sees this type, only the output images the evaluate
/// wrote. No field is shared.</para>
/// </summary>
internal sealed unsafe class NgxDlssgFeature : IDisposable
{
    private IntPtr _handle;
    private IntPtr _parameters;
    private bool _disposed;

    private NgxDlssgFeature(IntPtr handle, IntPtr parameters, NgxDlssgSettings settings)
    {
        _handle = handle;
        _parameters = parameters;
        Settings = settings;
    }

    /// <summary>What this feature was created for; see <see cref="Matches" />.</summary>
    public NgxDlssgSettings Settings { get; }

    /// <summary>The NVSDK_NGX_Handle*, or zero once released.</summary>
    public IntPtr Handle => _handle;

    public bool IsValid => !_disposed && _handle != IntPtr.Zero;

    /// <summary>
    /// Whether this feature can serve <paramref name="settings" />. A different size
    /// (the guide: "you must release the existing DLSS-FG feature and re-create it with
    /// the new subrect dimensions"), format or UI recomposition choice is a new feature.
    /// </summary>
    public bool Matches(NgxDlssgSettings settings) => IsValid && Settings == settings;

    /// <summary>
    /// Creates the feature on <paramref name="commandBuffer" />, which must be recording
    /// inside an open frame. The parameter block is allocated, not the shared
    /// capability block, for the same reason as <see cref="NgxDlssFeature.Create" />.
    /// </summary>
    public static NgxResult Create(
        IntPtr device, CommandBuffer commandBuffer, NgxDlssgSettings settings, out NgxDlssgFeature? feature)
    {
        feature = null;
        if (!NgxInterop.ManagedCallSiteIsSupported) return NgxResult.FailShimMissing;
        if (settings.DisplayWidth == 0 || settings.DisplayHeight == 0) return NgxResult.FailInvalidParameter;

        NgxResult allocated = NgxInterop.AllocateParameters(out IntPtr parameters);
        if (!NgxInterop.Succeeded(allocated)) return allocated;
        if (parameters == IntPtr.Zero) return NgxResult.FailInvalidParameter;

        var block = new NgxParameters(parameters);

        // Exactly what NGX_VK_CREATE_DLSSG sets, in its order: the generic Width and
        // Height are the backbuffer's (NVSDK_NGX_DLSSG_Create_Params.Width/Height), and
        // the helper forwards no render size.
        block.SetUInt(NgxParameterNames.CreationNodeMask, 1);
        block.SetUInt(NgxParameterNames.VisibilityNodeMask, 1);
        block.SetUInt(NgxParameterNames.Width, settings.DisplayWidth);
        block.SetUInt(NgxParameterNames.Height, settings.DisplayHeight);
        block.SetUInt(NgxParameterNames.DlssgBackbufferFormat, (uint)settings.BackbufferFormat);

        // Not in the helper: the DLSS 4 create-time switch that makes HUDLess and UI
        // drive the generation (see NgxDlssgSettings.UserInterfaceRecomposition).
        block.SetUInt(NgxParameterNames.DlssgUserInterfaceRecompositionEnabled,
            settings.UserInterfaceRecomposition ? 1u : 0u);

        IntPtr handle = IntPtr.Zero;
        NgxResult created = NgxShim.IsAvailable
            ? NgxShim.CreateFeature1(device, (IntPtr)commandBuffer.Handle, NgxFeature.FrameGeneration,
                parameters, out handle)
            : NgxResult.FailShimMissing;

        if (!NgxInterop.Succeeded(created) || handle == IntPtr.Zero)
        {
            NgxInterop.DestroyParameters(parameters);
            return NgxInterop.Succeeded(created) ? NgxResult.FailUnableToInitializeFeature : created;
        }

        feature = new NgxDlssgFeature(handle, parameters, settings);
        return created;
    }

    /// <summary>
    /// Sets the per-frame parameters and calls <c>EvaluateFeature</c>.
    ///
    /// Resources and matrices are taken by value and their addresses are this call's
    /// stack, so they stay valid for the whole evaluate - NGX stores the pointers in
    /// the block and dereferences them inside EvaluateFeature. A null
    /// <paramref name="hudless" />, <paramref name="ui" /> or
    /// <paramref name="outputReal" /> is set as a null pointer, never left over from
    /// an earlier frame.
    /// </summary>
    public NgxResult Evaluate(
        CommandBuffer commandBuffer,
        NgxResourceVk backbuffer, NgxResourceVk depth, NgxResourceVk motionVectors,
        NgxResourceVk? hudless, NgxResourceVk? ui,
        NgxResourceVk outputInterpolated, NgxResourceVk? outputReal,
        in NgxDlssgEvaluation frame)
    {
        if (!IsValid) return NgxResult.FailFeatureNotFound;
        if (!NgxShim.IsAvailable) return NgxResult.FailShimMissing;

        var block = new NgxParameters(_parameters);

        NgxResourceVk hudlessValue = hudless.GetValueOrDefault();
        NgxResourceVk uiValue = ui.GetValueOrDefault();
        NgxResourceVk realValue = outputReal.GetValueOrDefault();

        Matrix4x4 viewToClip = frame.CameraViewToClip;
        Matrix4x4 clipToView = frame.ClipToCameraView;
        Matrix4x4 clipToLens = frame.ClipToLensClip;
        Matrix4x4 clipToPrev = frame.ClipToPrevClip;
        Matrix4x4 prevToClip = frame.PrevClipToClip;

        // The order of NGX_VK_EVALUATE_DLSSG.
        block.SetVoidPointer(NgxParameterNames.DlssgBackbuffer, (IntPtr)(&backbuffer));
        block.SetVoidPointer(NgxParameterNames.DlssgMVecs, (IntPtr)(&motionVectors));
        block.SetVoidPointer(NgxParameterNames.DlssgDepth, (IntPtr)(&depth));
        block.SetVoidPointer(NgxParameterNames.DlssgHudless, hudless.HasValue ? (IntPtr)(&hudlessValue) : IntPtr.Zero);
        block.SetVoidPointer(NgxParameterNames.DlssgUi, ui.HasValue ? (IntPtr)(&uiValue) : IntPtr.Zero);
        // pUI carries premultiplied colour and alpha together; the guide: "Only one of
        // UIAlpha or UI need to be provided."
        block.SetVoidPointer(NgxParameterNames.DlssgUiAlpha, IntPtr.Zero);
        block.SetVoidPointer(NgxParameterNames.DlssgBidirectionalDistortionField, IntPtr.Zero);
        block.SetVoidPointer(NgxParameterNames.DlssgOutputInterpolated, (IntPtr)(&outputInterpolated));
        block.SetVoidPointer(NgxParameterNames.DlssgOutputReal, outputReal.HasValue ? (IntPtr)(&realValue) : IntPtr.Zero);
        // A buffer NGX would write a "skip this interpolation" flag into. Not provided:
        // the present thread shows every generated frame NGX returned Success for.
        block.SetVoidPointer(NgxParameterNames.DlssgOutputDisableInterpolation, IntPtr.Zero);

        block.SetUInt(NgxParameterNames.DlssgMultiFrameCount, frame.MultiFrameCount == 0 ? 1 : frame.MultiFrameCount);
        block.SetUInt(NgxParameterNames.DlssgMultiFrameIndex, frame.MultiFrameIndex == 0 ? 1 : frame.MultiFrameIndex);

        block.SetVoidPointer(NgxParameterNames.DlssgCameraViewToClip, (IntPtr)(&viewToClip));
        block.SetVoidPointer(NgxParameterNames.DlssgClipToCameraView, (IntPtr)(&clipToView));
        block.SetVoidPointer(NgxParameterNames.DlssgClipToLensClip, (IntPtr)(&clipToLens));
        block.SetVoidPointer(NgxParameterNames.DlssgClipToPrevClip, (IntPtr)(&clipToPrev));
        block.SetVoidPointer(NgxParameterNames.DlssgPrevClipToClip, (IntPtr)(&prevToClip));

        block.SetFloat(NgxParameterNames.DlssgJitterOffsetX, frame.JitterOffsetX);
        block.SetFloat(NgxParameterNames.DlssgJitterOffsetY, frame.JitterOffsetY);
        // Zero would annihilate every vector; the SR helper substitutes 1 and so do we.
        block.SetFloat(NgxParameterNames.DlssgMvecScaleX,
            frame.MotionVectorScaleX == 0f ? 1f : frame.MotionVectorScaleX);
        block.SetFloat(NgxParameterNames.DlssgMvecScaleY,
            frame.MotionVectorScaleY == 0f ? 1f : frame.MotionVectorScaleY);
        block.SetFloat(NgxParameterNames.DlssgCameraPinholeOffsetX, 0f);
        block.SetFloat(NgxParameterNames.DlssgCameraPinholeOffsetY, 0f);

        block.SetFloat(NgxParameterNames.DlssgCameraPosX, frame.CameraPosition.X);
        block.SetFloat(NgxParameterNames.DlssgCameraPosY, frame.CameraPosition.Y);
        block.SetFloat(NgxParameterNames.DlssgCameraPosZ, frame.CameraPosition.Z);
        block.SetFloat(NgxParameterNames.DlssgCameraUpX, frame.CameraUp.X);
        block.SetFloat(NgxParameterNames.DlssgCameraUpY, frame.CameraUp.Y);
        block.SetFloat(NgxParameterNames.DlssgCameraUpZ, frame.CameraUp.Z);
        block.SetFloat(NgxParameterNames.DlssgCameraRightX, frame.CameraRight.X);
        block.SetFloat(NgxParameterNames.DlssgCameraRightY, frame.CameraRight.Y);
        block.SetFloat(NgxParameterNames.DlssgCameraRightZ, frame.CameraRight.Z);
        block.SetFloat(NgxParameterNames.DlssgCameraFwdX, frame.CameraForward.X);
        block.SetFloat(NgxParameterNames.DlssgCameraFwdY, frame.CameraForward.Y);
        block.SetFloat(NgxParameterNames.DlssgCameraFwdZ, frame.CameraForward.Z);

        block.SetFloat(NgxParameterNames.DlssgCameraNear, frame.CameraNear);
        block.SetFloat(NgxParameterNames.DlssgCameraFar, frame.CameraFar);
        block.SetFloat(NgxParameterNames.DlssgCameraFov, frame.CameraFov);
        block.SetFloat(NgxParameterNames.DlssgCameraAspectRatio, frame.CameraAspectRatio);

        // LDR RGBA8 colour, display-ready [0, 1]: the guide refuses scRGB and linear
        // scene-referred input, and ours is neither.
        block.SetUInt(NgxParameterNames.DlssgColorBuffersHdr, 0);
        block.SetUInt(NgxParameterNames.DlssgDepthInverted, frame.DepthInverted ? 1u : 0u);
        block.SetUInt(NgxParameterNames.DlssgCameraMotionIncluded, frame.CameraMotionIncluded ? 1u : 0u);
        block.SetUInt(NgxParameterNames.DlssgReset, frame.Reset ? 1u : 0u);
        block.SetUInt(NgxParameterNames.DlssgAutomodeOverrideReset, 0);
        block.SetUInt(NgxParameterNames.DlssgNotRenderingGameFrames, frame.NotRenderingGameFrames ? 1u : 0u);
        block.SetUInt(NgxParameterNames.DlssgOrthoProjection, 0);
        block.SetFloat(NgxParameterNames.DlssgMvecInvalidValue, 0f);
        block.SetUInt(NgxParameterNames.DlssgMvecDilated, 0);
        block.SetUInt(NgxParameterNames.DlssgMenuDetectionEnabled, 0);

        // Whole images everywhere: every subrect is base (0, 0) and size 0, which is
        // what the helper sends from a default-initialised struct.
        block.SetUInt(NgxParameterNames.DlssgMVecsSubrectBaseX, 0);
        block.SetUInt(NgxParameterNames.DlssgMVecsSubrectBaseY, 0);
        block.SetUInt(NgxParameterNames.DlssgMVecsSubrectWidth, 0);
        block.SetUInt(NgxParameterNames.DlssgMVecsSubrectHeight, 0);
        block.SetUInt(NgxParameterNames.DlssgDepthSubrectBaseX, 0);
        block.SetUInt(NgxParameterNames.DlssgDepthSubrectBaseY, 0);
        block.SetUInt(NgxParameterNames.DlssgDepthSubrectWidth, 0);
        block.SetUInt(NgxParameterNames.DlssgDepthSubrectHeight, 0);
        block.SetUInt(NgxParameterNames.DlssgHudlessSubrectBaseX, 0);
        block.SetUInt(NgxParameterNames.DlssgHudlessSubrectBaseY, 0);
        block.SetUInt(NgxParameterNames.DlssgHudlessSubrectWidth, 0);
        block.SetUInt(NgxParameterNames.DlssgHudlessSubrectHeight, 0);
        block.SetUInt(NgxParameterNames.DlssgUiSubrectBaseX, 0);
        block.SetUInt(NgxParameterNames.DlssgUiSubrectBaseY, 0);
        block.SetUInt(NgxParameterNames.DlssgUiSubrectWidth, 0);
        block.SetUInt(NgxParameterNames.DlssgUiSubrectHeight, 0);

        block.SetFloat(NgxParameterNames.DlssgMinRelativeLinearDepthObjectSeparation,
            frame.MinRelativeLinearDepthObjectSeparation);

        block.SetUInt(NgxParameterNames.DlssgInputBackbufferSubrectBaseX, 0);
        block.SetUInt(NgxParameterNames.DlssgInputBackbufferSubrectBaseY, 0);
        block.SetUInt(NgxParameterNames.DlssgInputBackbufferSubrectWidth, 0);
        block.SetUInt(NgxParameterNames.DlssgInputBackbufferSubrectHeight, 0);
        block.SetUInt(NgxParameterNames.DlssgOutputInterpolatedSubrectBaseX, 0);
        block.SetUInt(NgxParameterNames.DlssgOutputInterpolatedSubrectBaseY, 0);
        block.SetUInt(NgxParameterNames.DlssgOutputInterpolatedSubrectWidth, 0);
        block.SetUInt(NgxParameterNames.DlssgOutputInterpolatedSubrectHeight, 0);
        block.SetUInt(NgxParameterNames.DlssgOutputRealSubrectBaseX, 0);
        block.SetUInt(NgxParameterNames.DlssgOutputRealSubrectBaseY, 0);
        block.SetUInt(NgxParameterNames.DlssgOutputRealSubrectWidth, 0);
        block.SetUInt(NgxParameterNames.DlssgOutputRealSubrectHeight, 0);

        if (frame.BackbufferFrameId != 0)
        {
            block.SetULong(NgxParameterNames.DlssgBackbufferFrameId, frame.BackbufferFrameId);
        }

        return NgxShim.EvaluateFeature((IntPtr)commandBuffer.Handle, _handle, _parameters, IntPtr.Zero);
    }

    /// <summary>
    /// Releases the feature and its parameter block. Only ever called from the frame
    /// ring's retire queue, for the reason <see cref="NgxDlssFeature.Dispose" /> gives.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        IntPtr handle = _handle;
        IntPtr parameters = _parameters;
        _handle = IntPtr.Zero;
        _parameters = IntPtr.Zero;

        if (handle != IntPtr.Zero && NgxShim.IsAvailable) LastReleaseResult = NgxShim.ReleaseFeature(handle);
        if (parameters != IntPtr.Zero) LastDestroyParametersResult = NgxInterop.DestroyParameters(parameters);
    }

    /// <summary>What <c>ReleaseFeature</c> answered, for the log and the tests.</summary>
    public NgxResult LastReleaseResult { get; private set; }

    /// <summary>What <c>DestroyParameters</c> answered.</summary>
    public NgxResult LastDestroyParametersResult { get; private set; }
}
