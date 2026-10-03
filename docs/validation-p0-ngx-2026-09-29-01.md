# P0 NGX source validation — 2026-09-29

Status: **managed backend and native NGX bridge built; 18 focused managed tests passed.** This was one bounded validation batch. No source was changed and no command was rerun.

The batch ran from `D:\Coding\VulkanStory-Rewrite`: a Release `dotnet test` of `VulkanStory.Render.Vulkan.Tests`, then `native/ngx/build.ps1 -OutputDirectory artifacts/validation/p0-ngx-20260929-01/native`. The [summary](../artifacts/validation/p0-ngx-20260929-01/summary.json), per-command console logs, and [TRX](../artifacts/validation/p0-ngx-20260929-01/backend.trx) are retained in that run directory.

| Gate | Recorded result |
| --- | --- |
| Contracts and backend build | Both built in Release; no warning was shown in the test command output. |
| Managed tests | 18/18 executed and passed, zero failed or skipped; exit 0. Includes NGX resource ABI offsets, temporal jitter/reset mapping, process lifetime order, and FG camera validity. |
| Native C bridge | `VulkanStoryNgx.dll` built; script exit 0. SHA-256 `789C6E088FE5E4694EDCDCB38D8941EB50073B77536B12705262E662FDD1EBCF`. |

The bridge was compiled but its exports were not inspected from the binary, and the managed runtime did not load it. No NVIDIA NGX or Streamline feature library, Vulkan device, game process, SDL window, upscaler evaluate, or generated frame ran in this batch. The texture-manager adapter and retained Streamline present path remain outside the compiled backend project. The full renderer, SR, FG, and SDL game integration remain open.
