# Vulkan swapchain source boundary

Date: 2026-09-29. **Source-only implementation; not built or run in this turn.** The backend project now includes the retained frame timeline, swapchain core, native/Streamline/FSR 3 dispatches, swapchain retirement, FSR 3 swapchain proxy wrapper, and vendor latency hook. The existing acquire/present IDs, semaphore recycling, retirement proofs, present modes, and synchronization rules remain in their source files.

The final texture blit moved unchanged from `Present/Swapchain.cs` to `Present/BlitPresentPath.cs`. That file stays excluded until the texture manager joins the backend project. This separates swapchain ownership from the renderer's image-copy step without replacing the working blit or its single Y flip. The compiled swapchain group has no Vintage Story, GLFW, or injected game-API reference. Its latency/FIFO diagnostic environment names now use `VULKANSTORY_*`.

`tools/VulkanStory.Preflight --sdl-swapchain` creates a hidden SDL window, a Vulkan device and surface, a real frame timeline, then creates and releases a native swapchain. `Swapchain.TryCreate` takes ownership of the surface on entry; the preflight's cleanup follows that contract. CPU-only tests carry forward the source renderer's retirement, acquire-semaphore, and present-mode fallback expectations.

The next bounded validation can build and run the focused tests and this swapchain creation preflight. That will not record a command buffer, transition an image to `PRESENT_SRC`, call `vkQueuePresentKHR`, display a frame, or run the game frame loop. Those steps require the retained frame ring, texture manager and final blit to join the device facade, followed by the SDL game adapter and live menu/world checks.
