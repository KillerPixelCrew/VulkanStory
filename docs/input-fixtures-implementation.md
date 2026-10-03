# Input behavior fixtures

Date: 2026-09-29. Implementation only; no builds or tests were run.

`VulkanStory.Game.Tests` adds behavior fixtures for the new official-platform input bridge. Cases preserve the original controller arbitration expectations: either source may release a shared key without releasing the other; controller references release on the final action; touch release preserves physical/controller mouse holds; and physical input activity remains independent of controller cursor motion. Additional changed-boundary cases cover original key history, separate handler events, original mouse-field updates, focus-handler dispatch, residual controller cleanup, and wheel accumulation once for multiple handlers.

The fixture bypasses the original platform constructor using an uninitialized object, then supplies only input handler lists, its stopwatch, and focus listeners. `GamePlatformBindings` itself creates the actual Harmony field accessors and closed callback delegates. Input tests invoke the original mouse-position callback and access original fields; they do not invoke frame/close callbacks, create a native window, or initialize Vulkan. After the 2026-09-30 fixture repair, wheel testing supplies sensitivity through a callback and does not access `ClientSettings`. Test parallelism stays disabled for deterministic integration execution.

Five original `SdlTouchMouseTests` are copied with mechanical namespace/import changes, preserving tap, drawable-pixel motion, drag cancellation, long press, and multiple-contact suppression cases and their expected values. Provenance: `Optimum.Render.Vulkan.Tests` at baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`.

The test assembly resolves game dependencies from the developer's official installation after checking the pinned file profile. Game assemblies have copying disabled and remain outside the test output/package. The new test project is in the development solution; it is not a player dependency.

Next validation should run the new game test project and build/check the profile tool in one bounded batch. Sidecar SDL dispatch, IME-target integration, frame/close invocation, real startup patch transactions, and provider/world rendering remain separate acceptance work. These fixtures have not yet compiled or run.

The [first validation batch](validation-g0-input-behavior-2026-09-30-01.md) compiled the sidecar but failed the test-project build on the private `ClientSettings` constructor. No tests ran. Source inspection also found settings disk loading in its static initializer; the intended in-memory fixture above is not implemented correctly. The next fix must supply wheel sensitivity explicitly and remove settings-static access from the fixture.

## Wheel dependency repair — 2026-09-30

`GameInputBridge` now requires a `Func<float>` sensitivity source. The actual sidecar supplies `ClientSettings.MouseWheelSensivity` through a deferred callback, retaining the current setting for each wheel event. The test fixture supplies its own value, and the wheel case also checks a change between events. Multiplication, wheel accumulation, and per-handler event delivery remain unchanged. The test no longer constructs, assigns, or reads game settings. This concrete repair is source-only; no build or test ran in its implementation turn. The failed batch remains the latest behavior-test result until the next bounded validation.

Subsequent [validation](validation-g0-input-behavior-2026-09-30-02.md) passed all 14 behavior cases, the profile build, and official binding checks. Actual cached input access and the listed arbitration/touch behavior now have evidence; sidecar native dispatch and live routing remain separate gates.
