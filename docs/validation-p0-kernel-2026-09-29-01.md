# P0 kernel and SDL validation — 2026-09-29

Status: **SDL passed; Vulkan backend kernel did not compile.** This was one bounded batch of two targeted project test commands. No source was changed and no command was rerun.

The batch ran from `D:\Coding\VulkanStory-Rewrite` with Release configuration. Its [summary](../artifacts/validation/p0-kernel-20260929-01/summary.json) records backend exit 1 and SDL exit 0. Each command's console output is retained as `backend.console.txt` and `sdl.console.txt` in that run directory.

| Gate | Recorded result |
| --- | --- |
| Vulkan backend project and tests | Restore passed. Compile failed at `Core/VulkanCapabilities.cs:108` with CS0103: `Shaders` is unavailable. Backend tests did not execute; no backend TRX exists. |
| SDL project and tests | Built without the earlier CA2255 warning. Five tests passed, zero failed or skipped; `sdl.trx` is retained. |
| Native/game/live | Not run. No SDL native library, Vulkan device, game process, upscaler, or frame generation provider was exercised. |

Source inspection explains the backend failure: `DescriptorIndexingFloor.BindlessSampledImages` uses `Shaders.SetConvention.TextureArrayCapacityTotal`, but the new explicit project compile list does not yet include the transplanted `Shaders/SetConvention.cs`. That file defines the descriptor capacities and has no apparent game or provider dependency. The next implementation turn should include it in the backend project, then a later validation turn can run a new bounded batch. The failed batch does not establish the backend kernel's other compile or test results.

Follow-up implementation: `SetConvention.cs`, three standalone shader source files, and the native Reflex/Anti-Lag function wrappers were added to the backend project; shader include constants now point at `shaders/native/include`. Focused shader source tests were added. These later source edits have not been built or run in this validation record.
