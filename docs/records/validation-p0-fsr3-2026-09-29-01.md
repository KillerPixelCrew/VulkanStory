# P0 FSR 3 source validation — 2026-09-29

Status: **managed backend tests and native FSR 3 bridge build passed; eight inherited C++ warnings remain.** This was one bounded validation batch. No source was changed and no command was rerun.

The batch ran from `D:\Coding\VulkanStory-Rewrite`: a Release `dotnet test` of `VulkanStory.Render.Vulkan.Tests`, then `native/fsr3/build.ps1` against the local FidelityFX SDK 1.1.4 headers and Vulkan SDK 1.4.357.0. The [summary](../../artifacts/validation/p0-fsr3-20260929-01/summary.json), console logs and [TRX](../../artifacts/validation/p0-fsr3-20260929-01/backend.trx) are retained in the run directory.

| Gate | Recorded result |
| --- | --- |
| Managed contracts/backend build | Release build succeeded; no managed warnings appeared in the test command output. |
| Managed tests | 19/19 executed and passed, zero failed or skipped; exit 0. Includes retained FSR 3 SR/FG ABI sizes and jitter field offset. |
| Native C++ bridge | `VulkanStoryFsr3.dll` built; script exit 0. SHA-256 `7ED2E8CA400869480E585E39A9B53F2D00A6813C6A7EE6D615FD41021F9FA91B`. |

The compiler emitted eight warnings: five casts from `GetProcAddress` to FidelityFX function pointers, two casts to Vulkan's `PFN_vkGetDeviceProcAddr`, and one enum/integer conditional return. Source inspection found the same expressions in the original copied bridge. Their runtime behavior was not exercised here.

The signed `amd_fidelityfx_vk.dll` was not loaded, bridge exports were not inspected from the binary, and no Vulkan device, SDL window, game process, SR evaluation, swapchain proxy, or generated present ran. `Fsr3Backend`, `Fsr3FrameGeneration`, `Fsr3SwapchainRuntime`, and the device integration remain staged. P0 and the full port remain open.
