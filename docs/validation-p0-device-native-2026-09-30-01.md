# P0 full-device native validation — 2026-09-30

Status: **75 backend tests and warning-free compilation passed; native preflight
failed on first-frame pixels.** One bounded batch ran once. No implementation
source changed, no command was rerun, and no game/package/deployment occurred.

| Step | Result |
| --- | --- |
| Release backend tests | Exit 0; 75/75 executed/passed, zero failures/skips. Compilation reported no warnings. |
| Release preflight build | Exit 0; zero warnings/errors. |
| One `--sdl-device` run | Exit 1; no timeout. First centre red channel was 0, expected 255. No frame reached the success/present log. |

The [summary](../artifacts/validation/p0-device-native-20260930-01/summary.json),
[test stdout](../artifacts/validation/p0-device-native-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/p0-device-native-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/p0-device-native-20260930-01/backend.trx),
[build stdout](../artifacts/validation/p0-device-native-20260930-01/build.stdout.log),
[build stderr](../artifacts/validation/p0-device-native-20260930-01/build.stderr.log),
[device stdout](../artifacts/validation/p0-device-native-20260930-01/device.stdout.log)
and [device stderr](../artifacts/validation/p0-device-native-20260930-01/device.stderr.log)
are retained. Every process completed within its 60-second limit.

## Diagnosis from logs and source

The harness calls `ClearNativeColor(0, ...)`. The native API uses
`PassDeclaration.DefaultFramebuffer`, whose value is **-1** in `Graph/FrameGraph.cs`.
`ResolveNativeFramebuffer` only maps that sentinel to the real default target.
With 0, `BindForNativeClear` gets no framebuffer and returns without clearing.
The subsequent texture readback therefore cannot prove the intended red output.

Repair the harness to pass the native default sentinel in the next implementation
turn, preserving the renderer's existing contract. No source repair or second run
occurred in this validation turn.

The run reached successful facade initialization and first-frame readback without
an exception, but the expected clear was never recorded. This does not establish
correct facade pixels, successful present, four frames or normal shutdown under
submitted presentation work. The three nullable warning sites are resolved by
the recorded compile. Native facade acceptance stays open, alongside game
startup/menus/worlds, vendor features, SDL controls and release.
