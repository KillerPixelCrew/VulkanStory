# G0 input behavior validation — first batch, 2026-09-30

Status: **test-project compilation failed; no behavior tests executed.** One bounded batch ran once. No implementation source changed, no checks were rerun, and no game launch or deployment occurred.

The planned batch was Release `VulkanStory.Game.Tests`, followed on success by the profile-tool build and one official profile inventory. Each process had a 60-second limit. The test command exited 1 without timeout. Following checks were skipped. The [summary](../../artifacts/validation/g0-input-behavior-20260930-01/summary.json), [stdout](../../artifacts/validation/g0-input-behavior-20260930-01/test.stdout.log), and [stderr](../../artifacts/validation/g0-input-behavior-20260930-01/test.stderr.log) are retained; no TRX or inventory was produced.

| Gate | Result |
| --- | --- |
| SDL and game integration dependencies | Compiled successfully, including the newly added SDL sidecar. |
| Game behavior test project | CS1729 at `InputArbitrationTests.cs:148`: `ClientSettings` has no accessible parameterless constructor. |
| Behavior test execution | Not reached. No arbitration, touch, or actual cached-access result is claimed. |
| Profile build/inventory | Skipped after the failed test step. Earlier accepted metadata evidence remains unchanged. |

Source diagnosis: the settings constructor is private. Its static initializer also loads settings from disk and uses the game platform logger on failure. Reflecting the private constructor or assigning its static `Inst` would not deliver the intended isolated in-memory wheel fixture. The concrete next implementation fix is to make wheel sensitivity an explicit input-bridge callback, supplied by the real game's settings at sidecar attachment and by a deterministic value in tests. Preserve the wheel arithmetic and once-per-event accumulation. Remove test access to `ClientSettings` entirely before the next bounded validation.

G0/G3 remain open. The sidecar has compile evidence only; its SDL dispatch and lifecycle are not exercised by this batch.
