using System;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Optimum.Render.Vulkan.Core;

/// <summary>A vendor-independent render/display size pair returned by the selected backend.</summary>
internal readonly record struct UpscalerPlan(
    int RenderWidth, int RenderHeight, int DisplayWidth, int DisplayHeight, string Quality, float? ProviderLodBias = null)
{
    public bool IsValid => RenderWidth > 0 && RenderHeight > 0 && DisplayWidth > 0 &&
        DisplayHeight > 0 && RenderWidth <= DisplayWidth && RenderHeight <= DisplayHeight;
    public float RenderScale => DisplayWidth > 0 ? (float)RenderWidth / DisplayWidth : 1f;
    public float LodBias => ProviderLodBias ?? OptimumConfig.RecommendedUpscalerLodBias(RenderWidth, DisplayWidth);
}

internal readonly record struct UpscalerFrame(
    int Color, int Depth, int Motion, int Output, IOptimumTemporalContext Temporal);

/// <summary>
/// One selectable reconstruction provider. All installed providers prepare before Vulkan device
/// creation, so switching the selected id later needs only a target rebuild and history reset.
/// </summary>
internal interface IUpscalerBackend : IDisposable
{
    string Id { get; }
    bool Active { get; }
    string? Unavailable { get; }
    IDeviceRequirementContributor? Requirements { get; }
    bool BringUp(VulkanDevice device, IntPtr instance, IntPtr physicalDevice, IntPtr logicalDevice);
    bool TryPlan(int displayWidth, int displayHeight, string quality, out UpscalerPlan plan);
    bool Evaluate(in UpscalerPlan plan, in UpscalerFrame frame, out string? error);
    void RetireFeature();
    void Shutdown();
}

/// <summary>The DLSS-SR implementation of the shared provider contract.</summary>
internal sealed class DlssBackend(DlssUpscaler host) : IUpscalerBackend
{
    public string Id => "dlss";
    public bool Active => host.Active;
    public string? Unavailable => host.Unavailable;
    public IDeviceRequirementContributor? Requirements => host.Requirements;

    public bool BringUp(VulkanDevice device, IntPtr instance, IntPtr physicalDevice, IntPtr logicalDevice) =>
        host.BringUp(device, instance, physicalDevice, logicalDevice);

    public bool TryPlan(int displayWidth, int displayHeight, string quality, out UpscalerPlan plan)
    {
        plan = default;
        if (!host.TryPlan(displayWidth, displayHeight, quality, out UpscalePlan vendor)) return false;
        plan = new UpscalerPlan(vendor.RenderWidth, vendor.RenderHeight,
            vendor.DisplayWidth, vendor.DisplayHeight, quality);
        return plan.IsValid;
    }

    public bool Evaluate(in UpscalerPlan plan, in UpscalerFrame frame, out string? error)
    {
        error = null;
        var vendor = new UpscalePlan(plan.RenderWidth, plan.RenderHeight,
            plan.DisplayWidth, plan.DisplayHeight, DlssUpscaler.QualityOf(plan.Quality));
        if (!host.EnsureFeature(vendor))
        {
            error = host.Unavailable ?? "DLSS feature creation failed";
            return false;
        }
        NgxResult result = host.Evaluate(frame.Color, frame.Depth, frame.Motion, frame.Output,
            NgxDlssEvaluation.FromTemporalContext(frame.Temporal));
        if (result == NgxResult.Success) return true;
        error = "NVSDK_NGX_VULKAN_EvaluateFeature: " + NgxInterop.Describe(result);
        return false;
    }

    public void RetireFeature() => host.RetireFeature();
    public void Shutdown() => host.Shutdown();
    public void Dispose() => host.Dispose();
}
