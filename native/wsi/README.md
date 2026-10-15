# Final WSI presentation dependency

This application-local explicit Vulkan layer places the final presentation signal
after work appended by overlay `vkQueuePresentKHR` hooks. An empty queue submission
waits on the incoming binary semaphores and signals image-owned binary semaphores;
the real WSI present waits on those final signals. The signal scope includes all
preceding queue work, including overlay rendering.

Real swapchain-maintenance1 present fences establish when the semaphores can be
reused or destroyed. The layer borrows caller fences when present, records their
completion before reset/destruction, and otherwise supplies its own fences when
the device enabled maintenance1. Unextended presentation is passed through.
NV latency host commands share the real presentation host-access guard. The
SDK's latency semaphore wait remains outside that guard.

`OverlayPresentLayer` adds the private manifest directory to this process's loader
search path and enables the layer before validation. No registry installation,
RTSS profile changes, hook exclusions or driver settings are involved.

Contracts: [loader/layer dispatch](https://github.com/KhronosGroup/Vulkan-Loader/blob/main/docs/LoaderLayerInterface.md),
[semaphore reuse](https://docs.vulkan.org/guide/latest/swapchain_semaphore_reuse.html),
[present-fence lifetime](https://docs.vulkan.org/refpages/latest/refpages/source/VkSwapchainPresentFenceInfoKHR.html).
