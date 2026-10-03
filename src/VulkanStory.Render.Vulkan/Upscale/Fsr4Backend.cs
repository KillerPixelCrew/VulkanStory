using System;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;

namespace VulkanStory.Render.Vulkan.Core;

internal sealed class Fsr4Backend : IUpscalerBackend
{
    private readonly Action<string> log;
    private nint bridge;
    private nint physical;
    private IUpscalerDevice? device;
    private Fsr4Runtime? runtime;
    private Fsr4SharedFrames? shared;
    private UpscalerPlan initializedPlan;
    private int motion;
    private bool ready, firstFrame;

    public string Id => "fsr4";
    public bool Active => ready && Unavailable == null;
    public string? Unavailable { get; private set; }
    public IDeviceRequirementContributor? Requirements => null;

    public Fsr4Backend(Action<string> log)
    {
        this.log = log;
        if (!Fsr4Runtime.TryLoad(out bridge, out string reason)) Unavailable = reason;
    }

    public bool BringUp(IUpscalerDevice target, nint instance, nint physicalDevice, nint logicalDevice)
    {
        if (bridge == 0) return false;
        if (Fsr4Runtime.Probe(bridge, physicalDevice) != 0)
        {
            Unavailable = "FSR 4 requires a supported AMD GPU";
            return false;
        }
        device = target;
        physical = physicalDevice;
        ready = true;
        return true;
    }

    public bool TryPlan(int displayWidth, int displayHeight, string quality,
        float lodBiasOffset, out UpscalerPlan plan)
    {
        plan = default;
        if (!Active || displayWidth <= 0 || displayHeight <= 0) return false;
        float ratio = quality?.ToLowerInvariant() switch
        {
            "dlaa" or "native" => 1f,
            "balanced" => 1.7f,
            "performance" => 2f,
            "ultraperformance" => 3f,
            _ => 1.5f,
        };
        quality = string.IsNullOrWhiteSpace(quality) ? "quality" : quality;
        int renderWidth = Math.Max(1, (int)(displayWidth / ratio));
        int renderHeight = Math.Max(1, (int)(displayHeight / ratio));
        plan = new UpscalerPlan(renderWidth, renderHeight, displayWidth, displayHeight,
            quality, null, lodBiasOffset);
        return plan.IsValid;
    }

    public bool Evaluate(in UpscalerPlan plan, in UpscalerFrame frame, out string? error)
    {
        error = null;
        if (!Active || !plan.IsValid)
        {
            error = Unavailable ?? "FSR 4 is unavailable";
            return false;
        }
        if (initializedPlan != plan)
        {
            RetireFeature();
            if (!Fsr4Runtime.TryCreate(bridge, physical, plan, out runtime, out string reason))
                return Fail(reason, out error);
            if (!device!.TryCreateFsr4SharedFrames(runtime!, plan, out shared, out reason))
            {
                runtime!.Dispose(); runtime = null;
                return Fail(reason, out error);
            }
            motion = device.CreateUpscaleTexture(plan.RenderWidth, plan.RenderHeight,
                Format.R16G16Sfloat, false);
            initializedPlan = plan;
            firstFrame = true;
        }
        int result = device!.EvaluateFsr4(runtime!, shared!, motion, plan, frame, firstFrame);
        firstFrame = false;
        if (result == 0) return true;
        return Fail("FSR 4 DX12 dispatch failed (" + result.ToString("X8") + ")", out error);
    }

    private bool Fail(string reason, out string? error)
    {
        Unavailable = reason;
        error = reason;
        log("[VulkanStory] " + reason);
        return false;
    }

    public void RetireFeature()
    {
        // A shared DX12 image can still be used by either GPU queue. Wait for
        // both before releasing the imported Vulkan memory and the SDK context.
        shared?.Dispose(); shared = null;
        runtime?.Dispose(); runtime = null;
        if (motion != 0) { device!.DeleteTexture(motion); motion = 0; }
        initializedPlan = default;
    }

    public void Shutdown()
    {
        RetireFeature();
        ready = false;
        if (bridge != 0) { NativeLibrary.Free(bridge); bridge = 0; }
    }
    public void Dispose() => Shutdown();
}
