# P0 retained presentation validation — first batch, 2026-09-29

Status: **compile failed before tests or presentation ran.** This was one bounded validation batch. No source was changed and no phase was rerun.

The batch was planned from `D:\Coding\VulkanStory-Rewrite`: Release backend tests, Release preflight build, then `--sdl-present` under a 30-second timeout. It stopped at the first failed phase. The complete [console log](../artifacts/validation/p0-present-20260929-01/test.log) and [machine summary](../artifacts/validation/p0-present-20260929-01/summary.json) are retained. No TRX was produced because test execution did not begin.

| Gate | Recorded result |
| --- | --- |
| Backend `dotnet test` | Exit 1 during compilation. `TextureManager.cs` lines 779–800 has 13 CS0103 errors because `GlEnums` is excluded from the backend project. Zero tests executed. |
| Preflight tool build | Skipped after compile failure. |
| Timed `--sdl-present` | Skipped after compile failure; no GPU presentation evidence from this batch. |

The same compile also emitted CS8602 at `VulkanPresentPreflight.cs:58`, where the nullable `Swapchain?` is used after `TryCreate`. `GlEnums.cs` currently combines raw GL constant translation with two game-enum methods and imports `Vintagestory.API.Client`, so adding the whole file to the game-neutral backend would cross the architecture boundary. The next implementation turn should separate those game-facing methods from the neutral constant translations, include the neutral portion, and resolve the nullable warning. The next validation must be a new bounded batch after that source change.
