# P0 swapchain validation — third batch, 2026-09-29

Status: **backend build/tests and SDL-backed Vulkan swapchain creation passed.** This was one bounded validation batch after the descriptor helper source fix. No source was changed and no command was rerun.

The batch ran from `D:\Coding\VulkanStory-Rewrite`: Release `dotnet test` of `VulkanStory.Render.Vulkan.Tests`, Release build of `tools/VulkanStory.Preflight`, then the built tool with `--sdl-swapchain` in a process limited to 30 seconds. The [summary](../../artifacts/validation/p0-swapchain-20260929-03/summary.json), console logs, and [TRX](../../artifacts/validation/p0-swapchain-20260929-03/backend.trx) are retained.

| Gate | Recorded result |
| --- | --- |
| Contracts, SDL, backend and tests | Built in Release; TRX records 26/26 executed and passed, zero failed or skipped. |
| Preflight tool build | Succeeded with 0 warnings and 0 errors. |
| `--sdl-swapchain` | Exited 0 without timeout: a hidden SDL3 window, Vulkan device/surface, frame timeline and native Vulkan swapchain were created and released. |

The focused tests include descriptor lifetime/type, swapchain retirement, acquire-semaphore reuse and present-mode fallback contracts. The preflight establishes swapchain creation and teardown on this local Windows/Vulkan setup. It did not acquire an image, submit a transition or rendered frame, call `vkQueuePresentKHR`, display output, exercise the FSR 3 or Streamline proxy swapchains, run game input, or launch Vintage Story. The final blit, frame ring, device facade and game adapter remain staged; P0 and the full port remain open.
