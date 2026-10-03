using VulkanStory.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    int IUpscalerDevice.CreateUpscaleTexture(int width, int height, Format format, bool storage,
        IntPtr pixels, int bytesPerPixel) => CreateUpscaleTexture(width, height, format, storage, pixels, bytesPerPixel);
    void IUpscalerDevice.DeleteTexture(int textureId) => DeleteTexture(textureId);
    void IUpscalerDevice.RetireUpscalerResource(IDisposable resource) => RetireUpscalerResource(resource);
    NgxResult IUpscalerDevice.CreateDlssFeature(in NgxDlssSettings settings, out NgxDlssFeature? feature) =>
        CreateDlssFeature(settings, out feature);
    NgxResult IUpscalerDevice.EvaluateDlss(NgxDlssFeature feature, int color, int depth, int motion, int output,
        in NgxDlssEvaluation frame) => EvaluateDlss(feature, color, depth, motion, output, frame);
    void IUpscalerDevice.RetireDlssFeature(NgxDlssFeature feature) => RetireDlssFeature(feature);
    int IUpscalerDevice.DrainDeferredDeletions() => DrainDeferredDeletions();
    int IUpscalerDevice.EvaluateFsr3(Fsr3Native api, nint context, int motion, in UpscalerFrame frame,
        bool firstFrame) => EvaluateFsr3(api, context, motion, frame, firstFrame);
    int IUpscalerDevice.EvaluateXess(XessNative api, nint context, int motion, in UpscalerFrame frame,
        bool firstFrame) => EvaluateXess(api, context, motion, frame, firstFrame);
    bool IUpscalerDevice.TryCreateFsr4SharedFrames(Fsr4Runtime runtime, in UpscalerPlan plan,
        out Fsr4SharedFrames? shared, out string reason) =>
        TryCreateFsr4SharedFrames(runtime, plan, out shared, out reason);
    int IUpscalerDevice.EvaluateFsr4(Fsr4Runtime runtime, Fsr4SharedFrames shared, int motion,
        in UpscalerPlan plan, in UpscalerFrame frame, bool firstFrame) =>
        EvaluateFsr4(runtime, shared, motion, plan, frame, firstFrame);
}
