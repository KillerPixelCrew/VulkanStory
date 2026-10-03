# Device warning repairs and native facade preflight

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

The three warnings from [the full-device batch](validation-p0-full-device-2026-09-30-01.md)
have concrete source repairs:

- Context initialization receives its nullable error locally and normalizes
  the failed result to the public facade's nonnullable message contract.
- Shader trace dumping uses the already-normalized pass name.
- `Swapchain.TryCreate` declares its existing successful nonnull result with
  `NotNullWhen(true)`, matching its implementation and clarifying restoration
  ownership to nullable analysis. No queue or swapchain algorithm changed.

`Core/VulkanDevicePreflight.cs` and the tool's `--sdl-device` option now exercise
the complete retained facade. The hidden 128×96 SDL window stays alive until
device disposal. The facade initializes its actual managers/default framebuffer,
then records four red/green/blue/yellow clear frames, reads decoded centre RGBA
through `ReadTextureForParity`, checks every channel within tolerance 2 and
presents each frame. `GetError` is checked per frame. Readback uses the original
partial submission/wait path; frame submission and teardown use the actual
device owner. Streamline and native shader selection are explicitly disabled
for this clear-frame check; no vendor reconstruction or generation is requested.

This is a new integration harness, not an algorithm rewrite or a game-routing
substitute. Four clear frames cannot establish shader/mesh facade calls, sampled
descriptors, AO/temporal/world work, resize/focus lifecycle or any vendor feature.
Pixel checks concern the offscreen render target; they do not capture scanout.

Next validation: one bounded batch of Release backend tests, preflight-tool
build and one `--sdl-device` execution, with dependent steps skipped on failure.
No execution occurred this turn. The warning repairs and native facade behavior
remain unverified; all game/provider/release milestones stay open.

The [first bounded validation](validation-p0-device-native-2026-09-30-01.md)
passed 75 backend tests and warning-free compilation, resolving the nullable
warnings. Native execution failed its first pixel assertion: the harness passed
0 instead of the native default framebuffer sentinel -1, so no clear was recorded.
Repair the argument in a later implementation turn; successful facade
pixels/presentation remain unverified.

## Default-target repair — 2026-09-30

The harness now calls `ClearNativeColor(PassDeclaration.DefaultFramebuffer, ...)`
using the existing native sentinel. This corrects the diagnosed argument error;
the retained renderer contract, four-frame assertions, readback/present and
teardown remain unchanged. Source-only repair, with no build/test/native run.
Next validation is one tool build and one `--sdl-device` run, stopping on failure;
the prior 75 CPU cases need no repeat for this harness-only argument correction.
The recorded native failure remains unresolved until the corrected run passes.

The [corrected bounded batch](validation-p0-device-native-2026-09-30-02.md)
passed a clean build and all four native clear/readback/present frames with
normal device-before-window teardown. The argument failure is resolved.
Actual game graphics/startup, facade draw paths and providers remain open.
