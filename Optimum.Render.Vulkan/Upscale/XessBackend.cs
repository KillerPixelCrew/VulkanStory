using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Vintagestory.API.Config;

namespace Optimum.Render.Vulkan.Core;

internal sealed unsafe class XessBackend : IUpscalerBackend, IDeviceRequirementContributor
{
    internal const uint InitFlags = 1u << 8; // HDR, low-res unjittered motion, forward depth, auto exposure.
    private readonly XessNative? api;
    private readonly Action<string> log;
    private VulkanDevice? device;
    private nint instance, physical, logical;
    private XessContext? context;
    private UpscalerPlan initializedPlan;
    private int motion;
    private bool ready, firstFrame;
    public string Id => "xess";
    public string Name => "XeSS-SR";
    public bool Active => ready && Unavailable == null;
    public string? Unavailable { get; private set; }
    public IDeviceRequirementContributor? Requirements => api == null ? null : this;

    public XessBackend(Action<string> log)
    {
        this.log = log;
        api = XessNative.TryLoad(out string? error);
        Unavailable = error;
    }
    private bool Check(int result, string operation)
    {
        if (result >= 0) return true; // Positive values are SDK warnings.
        Unavailable = operation + " failed (XeSS " + result + ")";
        log("[Optimum] " + Unavailable);
        return false;
    }
    private bool CheckExtensionList(uint count, byte** names)
    {
        if (count <= 256 && (count == 0 || names != null)) return true;
        Unavailable = "XeSS returned an invalid Vulkan extension list";
        log("[Optimum] " + Unavailable);
        return false;
    }
    public void ContributeInstanceExtensions(InstanceRequirements requirements)
    {
        if (api == null || Unavailable != null) return;
        uint count = 0, version = 0; byte** names = null;
        if (!Check(api.InstanceExtensions(&count, &names, &version), "XeSS instance requirements")) return;
        if (version > Vk.Version13) { Unavailable = "XeSS requires a newer Vulkan API"; return; }
        if (!CheckExtensionList(count, names)) return;
        for (uint i = 0; i < count; i++)
        {
            string? extension = names[i] == null ? null : Marshal.PtrToStringUTF8((nint)names[i]);
            if (string.IsNullOrWhiteSpace(extension) || !requirements.Request(extension, Name))
            {
                Unavailable = "XeSS requires an unavailable instance extension";
                return;
            }
        }
    }
    public void ContributeDeviceRequirements(DeviceRequirements requirements)
    {
        if (api == null || Unavailable != null) return;
        instance = requirements.Instance.Handle; physical = requirements.PhysicalDevice.Handle;
        uint count = 0; byte** names = null;
        if (!Check(api.DeviceExtensions(instance, physical, &count, &names), "XeSS device extensions")) return;
        if (!CheckExtensionList(count, names)) return;
        // Probe support before modifying the renderer's feature chain.
        void* probe = null;
        if (!Check(api.DeviceFeatures(instance, physical, &probe), "XeSS device features")) return;
        for (uint i = 0; i < count; i++)
        {
            string? extension = names[i] == null ? null : Marshal.PtrToStringUTF8((nint)names[i]);
            if (string.IsNullOrWhiteSpace(extension) || !requirements.Request(extension, requestedBy: Name))
            {
                Unavailable = "XeSS requires an unavailable device extension";
                return;
            }
        }
    }
    public void FinalizeDeviceFeatures(DeviceRequirements requirements, void** features)
    {
        if (api == null || Unavailable != null) return;
        try
        {
            Check(InvokeDeviceFeatureNegotiation(features,
                chain => api.DeviceFeatures(instance, physical, chain)), "XeSS feature negotiation");
        }
        catch (Exception e)
        {
            Unavailable = "XeSS feature negotiation failed: " + e.Message;
            log("[Optimum] " + Unavailable);
        }
    }

    internal delegate int DeviceFeatureCall(void** features);

    // The SDK may change any node in the supplied pNext chain before returning an
    // error. Save every node we currently put in that chain, not only the core
    // Vulkan 1.2/1.3 nodes, so another provider sees the original feature set.
    internal static int InvokeDeviceFeatureNegotiation(void** features, DeviceFeatureCall call)
    {
        void* original = *features;
        var saved = new List<(nint Address, byte[] Data)>();
        var visited = new HashSet<nint>();
        for (BaseOutStructure* node = (BaseOutStructure*)original; node != null; node = node->PNext)
        {
            if (!visited.Add((nint)node) || saved.Count >= 32)
                throw new InvalidOperationException("invalid Vulkan feature chain");
            int size = node->SType switch
            {
                StructureType.PhysicalDeviceFeatures2 => sizeof(PhysicalDeviceFeatures2),
                StructureType.PhysicalDeviceVulkan12Features => sizeof(PhysicalDeviceVulkan12Features),
                StructureType.PhysicalDeviceVulkan13Features => sizeof(PhysicalDeviceVulkan13Features),
                StructureType.PhysicalDeviceFaultFeaturesExt => sizeof(PhysicalDeviceFaultFeaturesEXT),
                StructureType.PhysicalDeviceColorWriteEnableFeaturesExt => sizeof(PhysicalDeviceColorWriteEnableFeaturesEXT),
                StructureType.PhysicalDeviceExtendedDynamicState3FeaturesExt => sizeof(PhysicalDeviceExtendedDynamicState3FeaturesEXT),
                StructureType.PhysicalDevicePresentIDFeaturesKhr => sizeof(PhysicalDevicePresentIdFeaturesKHR),
                StructureType.PhysicalDeviceSwapchainMaintenance1FeaturesExt => sizeof(PhysicalDeviceSwapchainMaintenance1FeaturesEXT),
                _ => throw new InvalidOperationException("unknown Vulkan feature node: " + node->SType),
            };
            var data = new byte[size];
            Marshal.Copy((nint)node, data, 0, size);
            saved.Add(((nint)node, data));
        }
        bool success = false;
        try
        {
            int result = call(features);
            success = result >= 0;
            return result;
        }
        finally
        {
            // A successful call deliberately keeps XeSS's additions and bits.
            // Restore on error or exception; the caller marks XeSS unavailable.
            if (!success)
            {
                *features = original;
                foreach (var (address, data) in saved)
                    Marshal.Copy(data, 0, address, data.Length);
            }
        }
    }
    public bool BringUp(VulkanDevice target, nint instance, nint physicalDevice, nint logicalDevice)
    {
        if (api == null || Unavailable != null) return false;
        device = target; this.instance = instance; physical = physicalDevice; logical = logicalDevice;
        ready = CreateContext();
        return ready;
    }
    private bool CreateContext()
    {
        nint handle = 0;
        if (!Check(api!.Create(instance, physical, logical, &handle), "xessVKCreateContext")) return false;
        context = new XessContext(api, handle);
        return true;
    }
    internal static int QualityOf(string? quality) => quality?.ToLowerInvariant() switch
    {
        "ultraperformance" => 100, "performance" => 101, "balanced" => 102,
        "ultraquality" => 104, "ultraqualityplus" => 105, "dlaa" or "native" => 106, _ => 103,
    };
    public bool TryPlan(int displayWidth, int displayHeight, string quality, out UpscalerPlan plan)
    {
        plan = default;
        if (!Active || displayWidth <= 0 || displayHeight <= 0) return false;
        if (context == null && !CreateContext()) return false;
        XessSize output = new(displayWidth, displayHeight), optimal = default, minimum = default, maximum = default;
        quality = string.IsNullOrWhiteSpace(quality) ? "quality" : quality;
        if (!Check(api!.Resolution(context!.Handle, &output, QualityOf(quality), &optimal, &minimum, &maximum),
            "xessGetOptimalInputResolution")) return false;
        plan = new UpscalerPlan((int)optimal.Width, (int)optimal.Height, displayWidth, displayHeight, quality,
            OptimumConfig.RecommendedUpscalerLodBias((int)optimal.Width, displayWidth));
        return plan.IsValid;
    }
    public bool Evaluate(in UpscalerPlan plan, in UpscalerFrame frame, out string? error)
    {
        error = null;
        if (!Active || !plan.IsValid) { error = Unavailable ?? "XeSS is unavailable"; return false; }
        if (initializedPlan != plan)
        {
            // Never reinitialize a context that an in-flight command buffer references.
            if (initializedPlan.IsValid) RetireFeature();
            if (context == null && !CreateContext()) { error = Unavailable; return false; }
            var init = new XessInit { Output = new(plan.DisplayWidth, plan.DisplayHeight),
                Quality = QualityOf(plan.Quality), Flags = InitFlags };
            if (!Check(api!.Init(context!.Handle, &init), "xessVKInit")) { error = Unavailable; return false; }
            motion = device!.CreateUpscaleTexture(plan.RenderWidth, plan.RenderHeight, Format.R16G16Sfloat, false);
            initializedPlan = plan;
            firstFrame = true;
        }
        int result = device!.EvaluateXess(api!, context!.Handle, motion, frame, firstFrame);
        firstFrame = false;
        if (Check(result, "xessVKExecute")) return true;
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

    private sealed class XessContext(XessNative api, nint handle) : IDisposable
    {
        public nint Handle { get; private set; } = handle;
        public void Dispose() { if (Handle != 0) { api.Destroy(Handle); Handle = 0; } }
    }
}
