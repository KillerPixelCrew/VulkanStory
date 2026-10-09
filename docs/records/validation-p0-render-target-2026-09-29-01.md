# P0 render-target validation — first batch, 2026-09-29

Status: **focused backend tests, preflight build, and one hidden SDL Vulkan render-target presentation passed.** This was one bounded validation batch after including the retained render-target/frame-graph group. No source was changed and no command was rerun.

The batch ran from `D:\Coding\VulkanStory-Rewrite`: Release `dotnet test` of `VulkanStory.Render.Vulkan.Tests`, Release build of `tools/VulkanStory.Preflight`, then the built tool with `--sdl-present` in a process limited to 30 seconds. The [summary](../../artifacts/validation/p0-render-target-20260929-01/summary.json), [test log](../../artifacts/validation/p0-render-target-20260929-01/test.log), [build log](../../artifacts/validation/p0-render-target-20260929-01/build.log), [preflight stdout](../../artifacts/validation/p0-render-target-20260929-01/present.stdout.log), [preflight stderr](../../artifacts/validation/p0-render-target-20260929-01/present.stderr.log), and [TRX](../../artifacts/validation/p0-render-target-20260929-01/backend.trx) are retained.

| Gate | Recorded result |
| --- | --- |
| Contracts, SDL, backend, and focused tests | Built in Release; TRX records 28/28 executed and passed, zero failed or skipped. The two new cases cover alternating history-plan reuse and the retained UI blend correction. |
| Preflight tool build | Succeeded with 0 warnings and 0 errors. |
| `--sdl-present` | Exited 0 without timeout or stderr. A hidden SDL3 window, Vulkan device/surface, framebuffer attachment, dynamic rendering scope, color clear, frame-ring submission, native swapchain acquire, retained flipped blit, present submission, and `vkQueuePresentKHR` completed and resources were released. |

This is local Windows/Vulkan evidence for one render-target clear and present through the retained path. The hidden window does not prove visible pixels, shader draws, the game's menu/world loop, SDL input routing, provider proxy presentation, normal-shortcut runtime ownership, or distribution packaging. Those milestones remain open.
