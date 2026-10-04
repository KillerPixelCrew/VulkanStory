using System;
using VulkanStory.Contracts;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>One FidelityFX frame interpolation context, retired on the frame timeline.</summary>
internal sealed unsafe class Fsr3FrameGeneration : IDisposable
{
    private readonly Fsr3Native api;
    private nint handle;
    private bool needsReset = true;

    private Fsr3FrameGeneration(Fsr3Native api, nint handle, uint width, uint height)
    {
        this.api = api;
        this.handle = handle;
        Width = width;
        Height = height;
    }

    public uint Width { get; }
    public uint Height { get; }
    public bool IsValid => handle != 0;
    public ulong FrameId { get; private set; }

    public static int Create(VulkanDevice device, uint width, uint height,
        out Fsr3FrameGeneration? feature)
    {
        feature = null;
        if (width == 0 || height == 0) return -1;
        Fsr3Native? api = Fsr3Native.TryLoad(out _);
        if (api == null) return -2;
        device.UpscalerHandles(out _, out IntPtr physical, out IntPtr logical);
        if (physical == IntPtr.Zero || logical == IntPtr.Zero) return -3;
        nint handle = 0;
        int result = api.CreateFrameGeneration(logical, physical, width, height,
            (uint)device.Fsr3ProxyFormat, &handle);
        if (result != 0 || handle == 0) return result != 0 ? result : -4;
        feature = new Fsr3FrameGeneration(api, handle, width, height);
        return 0;
    }

    public int Evaluate(VulkanDevice device, int backbufferId, int depthId, int motionId,
        int motionRgId, int hudlessId, int uiId,
        int uprightSceneId, int uprightUiId, int uprightDepthId, int uprightMotionId,
        int uprightOutputId, int outputId,
        in TemporalProviderFrame temporal)
    {
        if (handle == 0) return -8;
        ulong frameId = ++FrameId;
        TemporalProviderFrame current = temporal with { Reset = temporal.Reset || needsReset };
        int result = device.EvaluateFsr3FrameGeneration(api, handle, backbufferId, depthId,
            motionId, motionRgId, hudlessId, uiId, uprightSceneId, uprightUiId,
            uprightDepthId, uprightMotionId, uprightOutputId, outputId, current, frameId);
        if (result == 0) needsReset = false;
        return result;
    }

    public int Disable(nint swapchainContext)
    {
        int result = handle != 0 ? api.DisableFrameGeneration(handle, swapchainContext) : 0;
        if (result == 0) needsReset = true;
        return result;
    }

    public void Dispose()
    {
        nint current = handle;
        if (current == 0) return;
        int result = api.DestroyFrameGeneration(current);
        if (result != 0)
            throw new InvalidOperationException("FidelityFX frame generation destruction failed (" + result + ").");
        handle = 0;
    }
}
