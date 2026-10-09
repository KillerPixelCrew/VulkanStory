# P0 neutral capture validation — 2026-09-30

Status: **54 backend tests, clean build, decoded native pixels and presentation passed.** One bounded batch ran once. No implementation source changed, no command was rerun, and no Vintage Story launch or deployment occurred.

Release tests, tool build and one hidden `--sdl-mesh-readback` run exited 0 within 60-second limits. The [summary](../../artifacts/validation/p0-capture-boundary-20260930-01/summary.json), [test stdout](../../artifacts/validation/p0-capture-boundary-20260930-01/test.stdout.log), [test stderr](../../artifacts/validation/p0-capture-boundary-20260930-01/test.stderr.log), [TRX](../../artifacts/validation/p0-capture-boundary-20260930-01/backend.trx), [build stdout](../../artifacts/validation/p0-capture-boundary-20260930-01/build.stdout.log), [build stderr](../../artifacts/validation/p0-capture-boundary-20260930-01/build.stderr.log), [native stdout](../../artifacts/validation/p0-capture-boundary-20260930-01/capture.stdout.log), and [native stderr](../../artifacts/validation/p0-capture-boundary-20260930-01/capture.stderr.log) are retained.

| Gate | Result |
| --- | --- |
| Backend tests | 54/54 executed and passed; zero failures/skips. |
| New decoder cases | RGBA/BGRA colour/row/alpha preservation, HDR single-channel half expansion, depth representation and short-input rejection passed. |
| Tool build | Zero warnings/errors. Includes retained dump helper and neutral capture data. |
| Native capture path | Real indexed image readback decoded to the new capture object; triangle/background RGBA assertions passed within tolerance 2, then presentation and waited teardown completed. No stderr or timeout. |

This checks the listed capture representations and selected native RGBA8 pixels. No dump file writer was executed. Other formats, game screenshots/AVI, capture/resource ownership and excluded device readback methods remain unverified. Full device/startup/menu/world/provider routing and release acceptance remain open.
