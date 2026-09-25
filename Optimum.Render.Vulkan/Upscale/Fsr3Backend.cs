using System;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

internal sealed unsafe class Fsr3Backend : IUpscalerBackend, IDeviceRequirementContributor
{
    private readonly Fsr3Native? api;
    private readonly Action<string> log;
    private VulkanDevice? device;
    private nint physical, logical;
    private Fsr3Context? context;
    private UpscalerPlan initializedPlan;
    private int motion;
    private bool ready, firstFrame;
    private bool shaderInt16, shaderFloat16;
    public string Id => "fsr3";
    public string Name => "FSR 3.1";
    public bool Active => ready && Unavailable == null;
    public string? Unavailable { get; private set; }
    public IDeviceRequirementContributor? Requirements => api == null ? null : this;

    public Fsr3Backend(Action<string> log)
    {
        this.log = log;
        api = Fsr3Native.TryLoad(out string? error);
        Unavailable = error;
    }
    private bool Check(int result, string operation)
    {
        if (result == 0) return true;
        Unavailable = operation + " failed (AMD FidelityFX " + result + ")";
        log("[Optimum] " + Unavailable);
        return false;
    }
    public void ContributeInstanceExtensions(InstanceRequirements requirements) { }
    public void ContributeDeviceRequirements(DeviceRequirements requirements)
    {
        // The SDK chooses its FP16 shader permutation from physical-device
        // support, so the matching Vulkan features must be enabled at creation.
        shaderInt16 = requirements.Api.GetPhysicalDeviceFeatures(requirements.PhysicalDevice).ShaderInt16;
        var supported = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
        };
        requirements.QueryFeatures(&supported);
        shaderFloat16 = supported.ShaderFloat16;
    }
    public void FinalizeDeviceFeatures(DeviceRequirements requirements, void** features)
    {
        for (BaseOutStructure* node = (BaseOutStructure*)*features; node != null; node = node->PNext)
        {
            if (node->SType == StructureType.PhysicalDeviceFeatures2)
                ((PhysicalDeviceFeatures2*)node)->Features.ShaderInt16 = shaderInt16;
            if (node->SType == StructureType.PhysicalDeviceVulkan12Features)
                ((PhysicalDeviceVulkan12Features*)node)->ShaderFloat16 = shaderFloat16;
        }
    }
    public bool BringUp(VulkanDevice target, nint instance, nint physicalDevice, nint logicalDevice)
    {
        if (api == null) return false;
        device = target; physical = physicalDevice; logical = logicalDevice;
        ready = true;
        return true;
    }
    internal static uint QualityOf(string quality) => quality.ToLowerInvariant() switch
    {
        "dlaa" or "native" => 0, "balanced" => 2, "performance" => 3,
        "ultraperformance" => 4, _ => 1,
    };
    public bool TryPlan(int displayWidth, int displayHeight, string quality, out UpscalerPlan plan)
    {
        plan = default;
        if (!Active || displayWidth <= 0 || displayHeight <= 0) return false;
        uint renderWidth = 0, renderHeight = 0;
        if (!Check(api!.Plan((uint)displayWidth, (uint)displayHeight, QualityOf(quality),
            &renderWidth, &renderHeight), "FSR 3.1 resolution query")) return false;
        plan = new UpscalerPlan((int)renderWidth, (int)renderHeight, displayWidth, displayHeight, quality,
            MathF.Log2((float)renderWidth / displayWidth) - 1f);
        return plan.IsValid;
    }
    public bool Evaluate(in UpscalerPlan plan, in UpscalerFrame frame, out string? error)
    {
        error = null;
        if (!Active || !plan.IsValid) { error = Unavailable ?? "FSR 3.1 is unavailable"; return false; }
        if (initializedPlan != plan)
        {
            if (initializedPlan.IsValid) RetireFeature();
            nint handle = 0;
            if (!Check(api!.Create(logical, physical, (uint)plan.DisplayWidth,
                (uint)plan.DisplayHeight, &handle), "FSR 3.1 context creation"))
            { error = Unavailable; return false; }
            context = new Fsr3Context(api, handle);
            motion = device!.CreateUpscaleTexture(plan.RenderWidth, plan.RenderHeight,
                Format.R16G16Sfloat, false);
            initializedPlan = plan;
            firstFrame = true;
        }
        int result = device!.EvaluateFsr3(api!, context!.Handle, motion, frame, firstFrame);
        firstFrame = false;
        if (Check(result, "FSR 3.1 dispatch")) return true;
        error = Unavailable;
        return false;
    }
    public void RetireFeature()
    {
        if (context != null) { device!.RetireUpscalerResource(context); context = null; }
        if (motion != 0) { device!.DeleteTexture(motion); motion = 0; }
        initializedPlan = default;
    }
    public void Shutdown() { RetireFeature(); ready = false; }
    public void Dispose() => Shutdown();

    private sealed class Fsr3Context(Fsr3Native api, nint handle) : IDisposable
    {
        public nint Handle { get; private set; } = handle;
        public void Dispose() { if (Handle != 0) { api.Destroy(Handle); Handle = 0; } }
    }
}
