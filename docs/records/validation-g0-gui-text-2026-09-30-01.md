# G0 GUI text coordinator validation — 2026-09-30

Status: **clean build, 14 input regressions, and official GUI field metadata checks passed.** One bounded validation batch ran once. No implementation source changed, no command was rerun, and no game/native window or IME session was launched.

The batch built the profile tool in Release, ran Release `VulkanStory.Game.Tests`, and ran one static inventory against official 1.22.7. Each process exited 0 within a 60-second limit. The [summary](../../artifacts/validation/g0-gui-text-20260930-01/summary.json), [build stdout](../../artifacts/validation/g0-gui-text-20260930-01/build.stdout.log), [build stderr](../../artifacts/validation/g0-gui-text-20260930-01/build.stderr.log), [test stdout](../../artifacts/validation/g0-gui-text-20260930-01/test.stdout.log), [test stderr](../../artifacts/validation/g0-gui-text-20260930-01/test.stderr.log), [TRX](../../artifacts/validation/g0-gui-text-20260930-01/input.trx), [inventory stdout](../../artifacts/validation/g0-gui-text-20260930-01/inventory.stdout.log), [inventory stderr](../../artifacts/validation/g0-gui-text-20260930-01/inventory.stderr.log), and [captured IL](../../artifacts/validation/g0-gui-text-20260930-01/startup-il.json) are retained.

| Gate | Result |
| --- | --- |
| Compilation | Zero warnings/errors. Includes `GameGuiBindings`, `SdlGuiTextInput`, callback attachment, and sidecar composition revision handling. |
| Input regressions | 14/14 executed and passed; zero failures/skips. Existing arbitration/cached-input/touch cases, not GUI coordinator behavior tests. |
| Official profile | File hashes and all 418 original member operands across seven startup methods matched. |
| Binding metadata | Existing six platform fields/three callbacks and five new GUI fields matched. New fields: current screen, running game, loaded dialogs, caret X, and horizontal render offset. |

`GamePlatformBindingProfile.Validate1227` invokes both platform and GUI metadata validators. The successful inventory therefore covers the new GUI field types despite the console's generic platform-binding message. It does not create GUI field accessor delegates or exercise focused-field discovery, native IME rectangles, stale queued commits, controller overlays, composition revisions, or text-input lifecycle. These still need integration fixtures/live evidence. Actual startup/window/graphics patch groups and renderer session attachment remain pending; G0/G3 are open.
