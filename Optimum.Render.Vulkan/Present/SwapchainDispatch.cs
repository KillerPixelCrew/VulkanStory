using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The five swapchain calls that a frame-generation presenter must own together.
/// A provider must never mix its acquire/present functions with the native
/// swapchain functions; its images and synchronization belong to that provider.
/// </summary>
internal unsafe interface ISwapchainDispatch : System.IDisposable
{
    Result Create(Device device, SwapchainCreateInfoKHR* info, out SwapchainKHR swapchain);
    Result GetImages(Device device, SwapchainKHR swapchain, ref uint count, Image* images);
    Result Acquire(Device device, SwapchainKHR swapchain, Semaphore semaphore, ref uint imageIndex);
    Result Present(Queue queue, PresentInfoKHR* info);
    void Destroy(Device device, SwapchainKHR swapchain);
}

internal sealed unsafe class NativeSwapchainDispatch : ISwapchainDispatch
{
    private readonly KhrSwapchain api;

    internal NativeSwapchainDispatch(KhrSwapchain api) => this.api = api;

    public Result Create(Device device, SwapchainCreateInfoKHR* info, out SwapchainKHR swapchain) =>
        api.CreateSwapchain(device, info, null, out swapchain);

    public Result GetImages(Device device, SwapchainKHR swapchain, ref uint count, Image* images) =>
        api.GetSwapchainImages(device, swapchain, ref count, images);

    public Result Acquire(Device device, SwapchainKHR swapchain, Semaphore semaphore, ref uint imageIndex) =>
        api.AcquireNextImage(device, swapchain, ulong.MaxValue, semaphore, default, ref imageIndex);

    public Result Present(Queue queue, PresentInfoKHR* info) => api.QueuePresent(queue, info);

    public void Destroy(Device device, SwapchainKHR swapchain) => api.DestroySwapchain(device, swapchain, null);

    public void Dispose() => api.Dispose();
}

internal sealed unsafe class StreamlineSwapchainDispatch : ISwapchainDispatch
{
    private readonly StreamlineRuntime _runtime;
    private readonly Device _device;

    internal StreamlineSwapchainDispatch(StreamlineRuntime runtime, Device device)
    {
        _runtime = runtime;
        _device = device;
    }

    public Result Create(Device device, SwapchainCreateInfoKHR* info, out SwapchainKHR chain) =>
        _runtime.CreateSwapchain(device, info, out chain);
    public Result GetImages(Device device, SwapchainKHR chain, ref uint count, Image* images) =>
        _runtime.GetImages(device, chain, ref count, images);
    public Result Acquire(Device device, SwapchainKHR chain, Semaphore semaphore, ref uint index) =>
        _runtime.Acquire(device, chain, semaphore, ref index);
    public Result Present(Queue queue, PresentInfoKHR* info) => _runtime.Present(_device, queue, info);
    public void Destroy(Device device, SwapchainKHR chain) => _runtime.DestroySwapchain(device, chain);
    public void Dispose() { }
}

internal sealed unsafe class Fsr3SwapchainDispatch : ISwapchainDispatch
{
    private readonly VulkanContext _context;
    private Fsr3SwapchainRuntime? _runtime;

    internal Fsr3SwapchainDispatch(VulkanContext context) => _context = context;

    internal string? FailureReason { get; private set; }

    public Result Create(Device device, SwapchainCreateInfoKHR* info, out SwapchainKHR chain)
    {
        chain = default;
        if (_runtime == null)
        {
            if (!Fsr3SwapchainRuntime.TryCreate(_context, info, out _runtime, out string reason))
            {
                FailureReason = reason;
                return Result.ErrorInitializationFailed;
            }
            chain = _runtime!.Handle;
            FailureReason = null;
            return Result.Success;
        }
        Result result = _runtime.Recreate(info, out chain);
        if (result != Result.Success) FailureReason = "FidelityFX proxy recreation returned " + result;
        return result;
    }

    public Result GetImages(Device device, SwapchainKHR chain, ref uint count, Image* images) =>
        _runtime?.GetImages(device, chain, ref count, images) ?? Result.ErrorInitializationFailed;

    public Result Acquire(Device device, SwapchainKHR chain, Semaphore semaphore, ref uint index) =>
        _runtime?.Acquire(device, chain, semaphore, default, ref index) ?? Result.ErrorInitializationFailed;

    public Result Present(Queue queue, PresentInfoKHR* info) =>
        _runtime?.Present(queue, info) ?? Result.ErrorInitializationFailed;

    public void Destroy(Device device, SwapchainKHR chain) => _runtime?.DestroyChain(chain);

    public ulong LastPresentCount(SwapchainKHR chain) => _runtime?.LastPresentCount(chain) ?? 0;
    public nint NativeContext => _runtime?.NativeContext ?? 0;

    public void Dispose()
    {
        _runtime?.Dispose();
        _runtime = null;
    }
}
