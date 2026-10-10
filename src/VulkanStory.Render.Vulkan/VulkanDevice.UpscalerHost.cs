using VulkanStory.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;

namespace VulkanStory.Render.Vulkan;

/// <summary>Explicit provider-device interface forwarding to the renderer owner.</summary>
public sealed unsafe partial class VulkanDevice
{
    private (ulong FrameId, int Motion, int Reactive) _lastUpscalerInputTextures;

    /// <summary>Texture inputs recorded for the most recent native SR dispatch attempt.</summary>
    internal (ulong FrameId, int Motion, int Reactive) LastUpscalerInputTextures => _lastUpscalerInputTextures;

    /// <inheritdoc/>
    int IUpscalerDevice.CreateUpscaleTexture(int width, int height, Format format, bool storage,
        IntPtr pixels, int bytesPerPixel) => CreateUpscaleTexture(width, height, format, storage, pixels, bytesPerPixel);
    /// <inheritdoc/>
    void IUpscalerDevice.DeleteTexture(int textureId) => DeleteTexture(textureId);
    /// <inheritdoc/>
    void IUpscalerDevice.RetireUpscalerResource(IDisposable resource) => RetireUpscalerResource(resource);
    /// <inheritdoc/>
    NgxResult IUpscalerDevice.CreateDlssFeature(in NgxDlssSettings settings, out NgxDlssFeature? feature) =>
        CreateDlssFeature(settings, out feature);
    /// <inheritdoc/>
    NgxResult IUpscalerDevice.EvaluateDlss(NgxDlssFeature feature, int color, int depth, int motion, int output,
        in NgxDlssEvaluation frame) => EvaluateDlss(feature, color, depth, motion, output, frame);
    /// <inheritdoc/>
    void IUpscalerDevice.RetireDlssFeature(NgxDlssFeature feature) => RetireDlssFeature(feature);
    /// <inheritdoc/>
    int IUpscalerDevice.DrainDeferredDeletions() => DrainDeferredDeletions();
    /// <inheritdoc/>
    int IUpscalerDevice.EvaluateFsr3(Fsr3Native api, nint context, int motion, in UpscalerFrame frame,
        bool firstFrame) => EvaluateFsr3(api, context, motion, frame, firstFrame);
    /// <inheritdoc/>
    int IUpscalerDevice.EvaluateXess(XessNative api, nint context, int motion, in UpscalerFrame frame,
        bool firstFrame) => EvaluateXess(api, context, motion, frame, firstFrame);
    /// <inheritdoc/>
    bool IUpscalerDevice.TryCreateFsr4SharedFrames(Fsr4Runtime runtime, in UpscalerPlan plan,
        out Fsr4SharedFrames? shared, out string reason) =>
        TryCreateFsr4SharedFrames(runtime, plan, out shared, out reason);
    /// <inheritdoc/>
    int IUpscalerDevice.EvaluateFsr4(Fsr4Runtime runtime, Fsr4SharedFrames shared, int motion,
        in UpscalerPlan plan, in UpscalerFrame frame, bool firstFrame) =>
        EvaluateFsr4(runtime, shared, motion, plan, frame, firstFrame);
}
