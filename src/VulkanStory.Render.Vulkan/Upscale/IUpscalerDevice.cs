using Silk.NET.Vulkan;
using VulkanStory.Contracts;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Retained provider calls implemented by the real renderer owner.</summary>
internal interface IUpscalerDevice
{
    int CreateUpscaleTexture(int width, int height, Format format, bool storage,
        IntPtr pixels = default, int bytesPerPixel = 0);
    void DeleteTexture(int textureId);
    void RetireUpscalerResource(IDisposable resource);
    NgxResult CreateDlssFeature(in NgxDlssSettings settings, out NgxDlssFeature? feature);
    NgxResult EvaluateDlss(NgxDlssFeature feature, int color, int depth, int motion, int output,
        in NgxDlssEvaluation frame);
    void RetireDlssFeature(NgxDlssFeature feature);
    int DrainDeferredDeletions();
    int EvaluateFsr3(Fsr3Native api, nint context, int motion, in UpscalerFrame frame, bool firstFrame);
    int EvaluateXess(XessNative api, nint context, int motion, in UpscalerFrame frame, bool firstFrame);
    bool TryCreateFsr4SharedFrames(Fsr4Runtime runtime, in UpscalerPlan plan,
        out Fsr4SharedFrames? shared, out string reason);
    int EvaluateFsr4(Fsr4Runtime runtime, Fsr4SharedFrames shared, int motion,
        in UpscalerPlan plan, in UpscalerFrame frame, bool firstFrame);
}
