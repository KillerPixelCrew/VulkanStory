using System;
using Silk.NET.Vulkan;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Optimum.Render.Vulkan.Core;

internal enum VendorLatencyKind { None, Reflex, AntiLag }

/// <summary>Enables the supported vendor extension before vkCreateDevice.</summary>
internal sealed unsafe class VendorLatencyRequirements : IDeviceRequirementContributor
{
    private readonly bool _headless;
    private readonly string _choice;

    public VendorLatencyRequirements(bool headless)
    {
        _headless = headless;
        _choice = Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_LATENCY")?.Trim().ToLowerInvariant() ?? "auto";
    }

    public string Name => "vendor latency";
    public VendorLatencyKind Kind { get; private set; }

    public void ContributeInstanceExtensions(InstanceRequirements requirements) { }

    public void ContributeDeviceRequirements(DeviceRequirements requirements)
    {
        if (_headless || _choice is "off" or "none" or "0") return;
        uint vendor = requirements.Api.GetPhysicalDeviceProperties(requirements.PhysicalDevice).VendorID;
        bool requestNv = _choice is "reflex" or "nv" || (_choice == "auto" && vendor == 0x10de);
        bool requestAmd = _choice is "antilag" or "amd" || (_choice == "auto" && vendor == 0x1002);

        // The context has already requested VK_KHR_present_id and its feature.
        if (requestNv && requirements.IsEnabled("VK_KHR_present_id") &&
            requirements.Request("VK_NV_low_latency2", requestedBy: Name))
        {
            Kind = VendorLatencyKind.Reflex;
            return;
        }

        if (!requestAmd || !requirements.Has("VK_AMD_anti_lag")) return;
        var antiLag = new PhysicalDeviceAntiLagFeaturesAMD
        {
            SType = StructureType.PhysicalDeviceAntiLagFeaturesAmd,
        };
        requirements.QueryFeatures(&antiLag);
        if (!antiLag.AntiLag || !requirements.Request("VK_AMD_anti_lag", requestedBy: Name)) return;
        antiLag.PNext = null;
        requirements.ChainFeature(antiLag);
        Kind = VendorLatencyKind.AntiLag;
    }
}

/// <summary>
/// Drives VK_NV_low_latency2 or VK_AMD_anti_lag on the render thread. The
/// application calls Sleep before sampling input and Marker at the existing
/// frame boundaries. Swapchain creation and retirement are passed in explicitly.
/// </summary>
internal sealed unsafe class VendorLatency : IDisposable
{
    private readonly VulkanContext _context;
    private readonly NvLowLatency2Functions? _nv;
    private readonly AmdAntiLagFunctions? _amd;
    private readonly Semaphore _sleepSemaphore;
    private SwapchainKHR _swapchain;
    private ulong _sleepValue;
    private ulong _frameId;
    private ulong _presentId;
    private ulong _amdPendingFrame;
    private bool _amdPresentOwed;
    private int _maxFps;
    private int _mode = 1;
    private int _appliedMaxFps = -1;
    private bool _reflexReady;
    private bool _disposed;

    private VendorLatency(VulkanContext context, NvLowLatency2Functions? nv, AmdAntiLagFunctions? amd)
    {
        _context = context;
        _nv = nv;
        _amd = amd;
        if (nv == null) return;
        var type = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
        };
        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &type };
        VulkanResult.Check(context.Api.CreateSemaphore(context.Device, &info, null, out _sleepSemaphore),
            "vkCreateSemaphore for Reflex sleep");
    }

    public static VendorLatency? TryCreate(VulkanContext context, VendorLatencyKind kind)
    {
        if (kind == VendorLatencyKind.Reflex)
        {
            NvLowLatency2Functions? nv = NvLowLatency2Functions.Load(context.Api, context.Device);
            return nv == null ? null : new VendorLatency(context, nv, null);
        }
        if (kind == VendorLatencyKind.AntiLag)
        {
            AmdAntiLagFunctions? amd = AmdAntiLagFunctions.Load(context.Api, context.Device);
            return amd == null ? null : new VendorLatency(context, null, amd);
        }
        return null;
    }

    public VendorLatencyKind Kind => _nv != null ? VendorLatencyKind.Reflex : VendorLatencyKind.AntiLag;
    public bool OwnsFrameCap => !_disposed && _mode != 0 && (_amd != null || _reflexReady);

    public void SetMode(int mode)
    {
        int next = Math.Clamp(mode, 0, 2);
        if (_mode == next) return;
        _mode = next;
        if (_nv != null) ApplyReflexMode();
        if (_amd != null)
        {
            _amdPresentOwed = false;
            AntiLagUpdate(AntiLagStageAMD.InputAmd, 0, modeOnly: true);
        }
    }

    public void SetFrameCap(int maxFps)
    {
        int cap = Math.Max(0, maxFps);
        if (cap == _maxFps) return;
        _maxFps = cap;
        if (_nv != null) ApplyReflexMode();
    }

    public void* ChainSwapchainCreateInfo(void* next)
    {
        if (_nv == null) return next;
        var info = new SwapchainLatencyCreateInfoNV
        {
            SType = StructureType.SwapchainLatencyCreateInfoNV,
            PNext = next,
            LatencyModeEnable = true,
        };
        // The caller owns the create call, so keep the struct in stable storage
        // across it. This pointer is freed only after all swapchains are retired.
        *_swapchainCreateInfo = info;
        return _swapchainCreateInfo;
    }

    private readonly SwapchainLatencyCreateInfoNV* _swapchainCreateInfo =
        (SwapchainLatencyCreateInfoNV*)System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(SwapchainLatencyCreateInfoNV));

    public void OnSwapchainCreated(SwapchainKHR swapchain)
    {
        _swapchain = swapchain;
        ApplyReflexMode();
    }

    public void OnSwapchainRetired()
    {
        _swapchain = default;
        _reflexReady = false;
    }

    private void ApplyReflexMode()
    {
        if (_nv == null || _swapchain.Handle == 0 || _disposed) return;
        var mode = new LatencySleepModeInfoNV
        {
            SType = StructureType.LatencySleepModeInfoNV,
            LowLatencyMode = _mode != 0,
            LowLatencyBoost = _mode == 2,
            MinimumIntervalUs = _mode != 0 && _maxFps > 0 ? (uint)Math.Max(1, 1_000_000 / _maxFps) : 0,
        };
        _reflexReady = _nv.SetLatencySleepMode(_context.Device, _swapchain, ref mode) == Result.Success;
    }

    public void Sleep(ulong frameId, ulong presentId)
    {
        if (_disposed) return;
        _frameId = frameId;
        _presentId = presentId;
        if (_amd != null)
        {
            // Mode is applied only when the cap changes. INPUT and PRESENT carry
            // the same frame index, including when the FPS cap is uncapped.
            if (_appliedMaxFps != _maxFps)
            {
                _appliedMaxFps = _maxFps;
                AntiLagUpdate(AntiLagStageAMD.InputAmd, 0, modeOnly: true);
            }
            if (_mode == 0) return;
            _amdPresentOwed = true;
            _amdPendingFrame = frameId;
            AntiLagUpdate(AntiLagStageAMD.InputAmd, frameId, modeOnly: false);
            return;
        }
        if (_nv == null || !_reflexReady || _mode == 0 || _swapchain.Handle == 0) return;
        ulong value = ++_sleepValue;
        var sleep = new LatencySleepInfoNV
        {
            SType = StructureType.LatencySleepInfoNV,
            SignalSemaphore = _sleepSemaphore,
            Value = value,
        };
        Result result = _nv.LatencySleep(_context.Device, _swapchain, ref sleep);
        if (result != Result.Success) return;
        Semaphore semaphore = _sleepSemaphore;
        var wait = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &semaphore,
            PValues = &value,
        };
        // Bound the wait so a driver failure cannot freeze the client forever.
        _context.Api.WaitSemaphores(_context.Device, &wait, 1_000_000_000UL);
    }

    public void Marker(ulong frameId, LatencyMarker marker)
    {
        if (_disposed) return;
        if (_amd != null)
        {
            if (marker == LatencyMarker.PresentStart && _amdPresentOwed && frameId == _amdPendingFrame)
            {
                AntiLagUpdate(AntiLagStageAMD.PresentAmd, frameId, modeOnly: false);
                _amdPresentOwed = false;
            }
            return;
        }
        if (_nv == null || !_reflexReady || _swapchain.Handle == 0 || frameId != _frameId) return;
        var info = new SetLatencyMarkerInfoNV
        {
            SType = StructureType.SetLatencyMarkerInfoNV,
            PresentID = _presentId,
            Marker = (LatencyMarkerNV)marker,
        };
        _nv.SetLatencyMarker(_context.Device, _swapchain, ref info);
    }

    public void SkipPresent() => _amdPresentOwed = false;

    private void AntiLagUpdate(AntiLagStageAMD stage, ulong frameId, bool modeOnly)
    {
        var presentation = new AntiLagPresentationInfoAMD
        {
            SType = StructureType.AntiLagPresentationInfoAmd,
            Stage = stage,
            FrameIndex = frameId,
        };
        var data = new AntiLagDataAMD
        {
            SType = StructureType.AntiLagDataAmd,
            Mode = _mode == 0 ? AntiLagModeAMD.OffAmd : AntiLagModeAMD.OnAmd,
            MaxFps = _mode == 0 ? 0u : (uint)_maxFps,
            PPresentationInfo = modeOnly ? null : &presentation,
        };
        _amd!.AntiLagUpdate(_context.Device, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_amd != null)
        {
            var data = new AntiLagDataAMD { SType = StructureType.AntiLagDataAmd, Mode = AntiLagModeAMD.OffAmd };
            _amd.AntiLagUpdate(_context.Device, ref data);
        }
        _disposed = true;
        if (_sleepSemaphore.Handle != 0)
            _context.Api.DestroySemaphore(_context.Device, _sleepSemaphore, null);
        System.Runtime.InteropServices.Marshal.FreeHGlobal((nint)_swapchainCreateInfo);
    }
}
