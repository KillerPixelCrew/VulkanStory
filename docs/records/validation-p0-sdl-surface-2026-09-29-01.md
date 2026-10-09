# P0 SDL3 Vulkan surface validation — 2026-09-29

Status: **backend build/tests and SDL3 Vulkan surface preflight passed.** This was one bounded validation batch. No source was changed and no command was rerun.

The batch ran from `D:\Coding\VulkanStory-Rewrite`: Release `dotnet test` of `VulkanStory.Render.Vulkan.Tests`, Release build of `tools/VulkanStory.Preflight`, then the built tool with `--sdl-surface` in a process limited to 30 seconds. The [summary](../../artifacts/validation/p0-sdl-surface-20260929-01/summary.json), step console logs, and [TRX](../../artifacts/validation/p0-sdl-surface-20260929-01/backend.trx) are retained.

| Gate | Recorded result |
| --- | --- |
| Contracts, SDL and backend build | Succeeded; no warning appeared in the build/test output. |
| Focused managed tests | 20/20 executed and passed, zero failed or skipped. |
| Preflight tool build | Succeeded with 0 warnings and 0 errors. |
| `--sdl-surface` | Exited 0 without timeout: a hidden SDL3 window, Vulkan device and SDL-created Vulkan surface were created and released. |

The run establishes that the SDL3 native library was resolved and the SDL/Vulkan surface handoff worked on this Windows machine. It did not record the exact native SDL3 module path or GPU/driver identity. It did not create a swapchain, acquire/present an image, run the game frame loop, map SDL input to Vintage Story, exercise Streamline's Win32 proxy surface path, or run upscaling/frame generation. Those remain P0/G0/G1/G3 gates; the full port remains open.
