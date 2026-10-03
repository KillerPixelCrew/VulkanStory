# P0 provider contracts validation — 2026-09-29

Status: **contracts tests and backend project build passed.** This was one bounded batch of two targeted commands. No source was changed and no command was rerun.

From `D:\Coding\VulkanStory-Rewrite`, the batch ran `dotnet test tests\VulkanStory.Contracts.Tests\VulkanStory.Contracts.Tests.csproj -c Release` with a TRX logger, followed by `dotnet build src\VulkanStory.Render.Vulkan\VulkanStory.Render.Vulkan.csproj -c Release`. The [summary](../artifacts/validation/p0-provider-contracts-20260929-01/summary.json), per-command console logs, and [contracts TRX](../artifacts/validation/p0-provider-contracts-20260929-01/contracts.trx) are retained in the run directory.

| Gate | Recorded result |
| --- | --- |
| `VulkanStory.Contracts` and tests | Built; TRX records 3/3 executed and passed, zero failed or skipped. Process exit 0. |
| Vulkan backend project | Built against the new contracts assembly; zero warnings and errors. Process exit 0. |
| Provider and game runtime | Not built or run. The edited DLSS/XeSS/FSR/FG files are still outside the backend project's explicit compile list. |

The tests cover the transplanted LOD-bias formula with explicit offset, provider bias precedence, plan validity, and the inverse-view input requirement. The batch did not exercise native SDK loading, a Vulkan device, SDL, the official game, upscaler evaluation, frame generation, or the game adapter that must supply `TemporalProviderFrame` and implement `IUpscalerRuntimeState`. P0 and the full port remain open.
