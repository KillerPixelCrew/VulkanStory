# P0 retained presentation validation — second batch, 2026-09-29

Status: **focused backend tests, preflight build, and one hidden SDL Vulkan presentation passed.** This was one bounded validation batch after the `GlEnums` boundary repair. No source was changed and no command was rerun.

The batch ran from `D:\Coding\VulkanStory-Rewrite`: Release `dotnet test` of `VulkanStory.Render.Vulkan.Tests`, Release build of `tools/VulkanStory.Preflight`, then the built tool with `--sdl-present` in a process limited to 30 seconds. The [summary](../../artifacts/validation/p0-present-20260929-02/summary.json), [test log](../../artifacts/validation/p0-present-20260929-02/test.log), [build log](../../artifacts/validation/p0-present-20260929-02/build.log), [preflight stdout](../../artifacts/validation/p0-present-20260929-02/present.stdout.log), [preflight stderr](../../artifacts/validation/p0-present-20260929-02/present.stderr.log), and [TRX](../../artifacts/validation/p0-present-20260929-02/backend.trx) are retained.

| Gate | Recorded result |
| --- | --- |
| Contracts, SDL, backend, and focused tests | Built in Release; TRX records 26/26 executed and passed, zero failed or skipped. |
| Preflight tool build | Succeeded with 0 warnings and 0 errors. |
| `--sdl-present` | Exited 0 without timeout or stderr. A hidden SDL3 window, Vulkan device/surface, frame ring, RGBA8 clear, native swapchain acquisition, retained flipped blit, two ordered submissions, and `vkQueuePresentKHR` completed and resources were released. |

This is local Windows/Vulkan evidence for one defined source image through the retained presentation path. The window was hidden, so it does not prove visible pixels, the SDL game loop, shader draws, menu/world routing, provider proxy presentation, game input, or normal-shortcut operation. The device facade, game adapter, provider attachment, and release package remain open.
