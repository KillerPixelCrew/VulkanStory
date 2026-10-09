using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>
/// The five swapchain calls that a frame-generation presenter must own together.
/// A provider must never mix its acquire/present functions with the native
/// swapchain functions; its images and synchronization belong to that provider.
/// </summary>
internal unsafe interface ISwapchainDispatch : System.IDisposable
{
    /// <summary>Creates a swapchain through this presentation owner.</summary>
    /// <param name="device">Borrowed logical device.</param>
    /// <param name="info">Caller-owned Vulkan creation information valid for the call.</param>
    /// <param name="swapchain">New swapchain on success.</param>
    /// <returns>The Vulkan creation result.</returns>
    Result Create(Device device, SwapchainCreateInfoKHR* info, out SwapchainKHR swapchain);
    /// <summary>Enumerates this owner's swapchain images using Vulkan count/query semantics.</summary>
    /// <param name="device">Borrowed logical device.</param>
    /// <param name="swapchain">Swapchain created by this dispatch owner.</param>
    /// <param name="count">Capacity on entry and returned image count on exit.</param>
    /// <param name="images">Caller-owned array, or null to query the count.</param>
    /// <returns>The Vulkan enumeration result.</returns>
    Result GetImages(Device device, SwapchainKHR swapchain, ref uint count, Image* images);
    /// <summary>Acquires the next image through the same owner that created the swapchain.</summary>
    /// <param name="device">Borrowed logical device.</param>
    /// <param name="swapchain">Swapchain created by this dispatch owner.</param>
    /// <param name="semaphore">Binary semaphore signaled when the image is available.</param>
    /// <param name="imageIndex">Acquired image index on success.</param>
    /// <returns>The Vulkan acquisition result.</returns>
    Result Acquire(Device device, SwapchainKHR swapchain, Semaphore semaphore, ref uint imageIndex);
    /// <summary>Queues presentation through this swapchain owner using borrowed Vulkan present information.</summary>
    Result Present(Queue queue, PresentInfoKHR* info);
    /// <summary>Performs owner-specific disable/drain preparation before a swapchain is destroyed.</summary>
    void PrepareDestroy(SwapchainKHR swapchain);
    /// <summary>Rejects continued use after an owner-specific terminal release failure.</summary>
    void RequireLifetime();
    /// <summary>Destroys a swapchain after its rendering and presentation references are complete.</summary>
    void Destroy(Device device, SwapchainKHR swapchain);
}

/// <summary>Owns native KHR swapchain dispatch without a frame-generation proxy.</summary>
internal sealed unsafe class NativeSwapchainDispatch : ISwapchainDispatch
{
    private readonly KhrSwapchain api;

    internal NativeSwapchainDispatch(KhrSwapchain api) => this.api = api;

    /// <inheritdoc/>
    public Result Create(Device device, SwapchainCreateInfoKHR* info, out SwapchainKHR swapchain) =>
        api.CreateSwapchain(device, info, null, out swapchain);

    /// <inheritdoc/>
    public Result GetImages(Device device, SwapchainKHR swapchain, ref uint count, Image* images) =>
        api.GetSwapchainImages(device, swapchain, ref count, images);

    /// <inheritdoc/>
    public Result Acquire(Device device, SwapchainKHR swapchain, Semaphore semaphore, ref uint imageIndex) =>
        api.AcquireNextImage(device, swapchain, ulong.MaxValue, semaphore, default, ref imageIndex);

    /// <inheritdoc/>
    public Result Present(Queue queue, PresentInfoKHR* info) => api.QueuePresent(queue, info);

    /// <inheritdoc/>
    public void Destroy(Device device, SwapchainKHR swapchain) => api.DestroySwapchain(device, swapchain, null);
    /// <inheritdoc/>
    public void PrepareDestroy(SwapchainKHR swapchain) { }
    /// <summary>Throws after a checked provider release failure so remaining native ownership is retained.</summary>
    public void RequireLifetime() { }

    /// <inheritdoc/>
    public void Dispose() => api.Dispose();
}

/// <summary>Borrows the Streamline runtime and routes the complete swapchain operation set through it.</summary>
internal sealed unsafe class StreamlineSwapchainDispatch : ISwapchainDispatch
{
    private readonly StreamlineRuntime _runtime;
    private readonly Device _device;

    internal StreamlineSwapchainDispatch(StreamlineRuntime runtime, Device device)
    {
        _runtime = runtime;
        _device = device;
    }

    /// <inheritdoc/>
    public Result Create(Device device, SwapchainCreateInfoKHR* info, out SwapchainKHR chain) =>
        _runtime.CreateSwapchain(device, info, out chain);
    /// <inheritdoc/>
    public Result GetImages(Device device, SwapchainKHR chain, ref uint count, Image* images) =>
        _runtime.GetImages(device, chain, ref count, images);
    /// <inheritdoc/>
    public Result Acquire(Device device, SwapchainKHR chain, Semaphore semaphore, ref uint index) =>
        _runtime.Acquire(device, chain, semaphore, ref index);
    /// <inheritdoc/>
    /// <remarks>
    /// DLSS-G presents asynchronously and reports OUT_OF_DATE/SUBOPTIMAL through its error
    /// callback, not this call's result. A recorded one turns an accepted present into
    /// SUBOPTIMAL, so the swapchain rebuilds before its next acquire on every frame,
    /// including frames that do not generate. Other recorded codes stay for the
    /// frame-generation owner.
    /// </remarks>
    public Result Present(Queue queue, PresentInfoKHR* info)
    {
        Result result = _runtime.Present(_device, queue, info);
        Result recorded = _runtime.TakeSwapchainPresentError();
        return result == Result.Success && recorded != Result.Success ? Result.SuboptimalKhr : result;
    }
    /// <inheritdoc/>
    public void Destroy(Device device, SwapchainKHR chain) => _runtime.DestroySwapchain(device, chain);
    // Global DLSS-G disable belongs to the transition owner; retiring an old
    // slot must not disable a live successor using the same runtime.
    /// <inheritdoc/>
    public void PrepareDestroy(SwapchainKHR chain) { }
    /// <summary>Throws after a checked provider release failure so remaining native ownership is retained.</summary>
    public void RequireLifetime() { }
    /// <inheritdoc/>
    public void Dispose() { }
}

/// <summary>Owns the FidelityFX swapchain runtime used for acquisition, images, presentation and destruction.</summary>
internal sealed unsafe class Fsr3SwapchainDispatch : ISwapchainDispatch
{
    private readonly VulkanContext _context;
    private Fsr3SwapchainRuntime? _runtime;

    internal Fsr3SwapchainDispatch(VulkanContext context) => _context = context;

    /// <summary>Latest FidelityFX swapchain creation or recreation failure.</summary>
    internal string? FailureReason { get; private set; }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Result GetImages(Device device, SwapchainKHR chain, ref uint count, Image* images) =>
        _runtime?.GetImages(device, chain, ref count, images) ?? Result.ErrorInitializationFailed;

    /// <inheritdoc/>
    public Result Acquire(Device device, SwapchainKHR chain, Semaphore semaphore, ref uint index) =>
        _runtime?.Acquire(device, chain, semaphore, default, ref index) ?? Result.ErrorInitializationFailed;

    /// <inheritdoc/>
    public Result Present(Queue queue, PresentInfoKHR* info) =>
        _runtime?.Present(queue, info) ?? Result.ErrorInitializationFailed;

    /// <inheritdoc/>
    public void Destroy(Device device, SwapchainKHR chain) => _runtime?.DestroyChain(chain);
    /// <inheritdoc/>
    public void PrepareDestroy(SwapchainKHR chain) => _runtime?.PrepareDestroy(chain);
    /// <summary>Throws after a checked provider release failure so remaining native ownership is retained.</summary>
    public void RequireLifetime() => _runtime?.RequireLifetime();

    /// <summary>Returns the native provider-reported present count, or zero when no context is active.</summary>
    public ulong LastPresentCount(SwapchainKHR chain) => _runtime?.LastPresentCount(chain) ?? 0;
    /// <summary>Borrowed native provider context; zero when it is absent.</summary>
    public nint NativeContext => _runtime?.NativeContext ?? 0;

    /// <inheritdoc/>
    public void Dispose()
    {
        _runtime?.Dispose();
        _runtime = null;
    }
}
