using System;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>
/// One FidelityFX Vulkan frame-generation swapchain context. Its replacement
/// entry points are queried together from the SDK and remain valid until the
/// context is destroyed. The caller must finish all presents before disposal.
/// </summary>
internal sealed unsafe class Fsr3SwapchainRuntime : IDisposable
{
    private readonly Fsr3Native _api;
    private nint _context;
    private Exception? _releaseFailure;

    private Fsr3SwapchainRuntime(Fsr3Native api, nint context)
    {
        _api = api;
        _context = context;
    }

    public static bool TryCreate(VulkanContext vk, SwapchainCreateInfoKHR* info,
        out Fsr3SwapchainRuntime? runtime, out string reason)
    {
        runtime = null;
        if (!vk.Fsr3SwapchainQueuesAvailable)
        {
            reason = "FidelityFX Vulkan proxy requires four graphics and compute queues";
            return false;
        }
        Fsr3Native? api = Fsr3Native.TryLoad(out string? loadError);
        if (api == null)
        {
            reason = loadError ?? "FidelityFX Vulkan runtime unavailable";
            return false;
        }
        nint context = 0;
        int code = api.CreateSwapchain(vk.PhysicalDevice, vk.Device, vk.GraphicsQueue,
            vk.Fsr3AsyncQueue, vk.Fsr3PresentQueue, vk.Fsr3AcquireQueue,
            vk.GraphicsQueueFamily, info, &context);
        if (code != 0 || context == 0)
        {
            reason = "FidelityFX swapchain context failed (" + code + ")";
            return false;
        }
        runtime = new Fsr3SwapchainRuntime(api, context);
        reason = "ready";
        return true;
    }

    public SwapchainKHR Handle => _context != 0 ? _api.SwapchainHandle(_context) : default;
    public nint NativeContext => _context;

    public Result Recreate(SwapchainCreateInfoKHR* info, out SwapchainKHR chain)
    {
        RequireLifetime();
        chain = default;
        if (_context == 0) return Result.ErrorInitializationFailed;
        SwapchainKHR created = default;
        Result result = _api.RecreateSwapchain(_context, info, &created);
        chain = created;
        return result;
    }

    public Result GetImages(Device device, SwapchainKHR chain, ref uint count, Image* images)
    {
        RequireLifetime();
        if (_context == 0) return Result.ErrorInitializationFailed;
        fixed (uint* countPtr = &count)
            return _api.GetSwapchainImages(_context, chain, countPtr, images);
    }

    public Result Acquire(Device device, SwapchainKHR chain, Semaphore semaphore, Fence fence,
        ref uint index)
    {
        RequireLifetime();
        if (_context == 0) return Result.ErrorInitializationFailed;
        fixed (uint* indexPtr = &index)
            return _api.AcquireSwapchain(_context, chain, ulong.MaxValue, semaphore, fence, indexPtr);
    }

    public Result Present(Queue queue, PresentInfoKHR* info)
    {
        RequireLifetime();
        return _context != 0 ? _api.PresentSwapchain(_context, queue, info) : Result.ErrorInitializationFailed;
    }

    public void PrepareDestroy(SwapchainKHR chain)
    {
        RequireLifetime();
        if (_context != 0) CheckRelease(_api.PrepareSwapchainDestroy(_context, chain), "disable/drain before swapchain release");
    }
    public void DestroyChain(SwapchainKHR chain)
    {
        RequireLifetime();
        if (_context != 0) CheckRelease(_api.DestroySwapchainChain(_context, chain), "swapchain destruction");
    }
    private void CheckRelease(int result, string operation)
    {
        if (result == 0) return;
        var failure = new InvalidOperationException("FidelityFX " + operation + " failed (" + result + "); ownership retained.");
        _releaseFailure = failure;
        throw failure;
    }
    internal void RequireLifetime()
    {
        if (_releaseFailure != null)
            throw new InvalidOperationException("FidelityFX release failed; cleanup is terminal.", _releaseFailure);
    }

    public ulong LastPresentCount(SwapchainKHR chain) => _context != 0 ?
        _api.SwapchainLastPresentCount(_context, chain) : 0;

    public void Dispose()
    {
        RequireLifetime();
        if (_context == 0) return;
        CheckRelease(_api.DestroySwapchain(_context), "swapchain context destruction");
        _context = 0;
    }
}
