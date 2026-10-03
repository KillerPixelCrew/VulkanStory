# P0 SR provider compile validation — 2026-09-30

Status: **68 backend tests and clean tool build passed.** One bounded batch ran once. No implementation source changed, no command was rerun, and no native preflight, live SR/game run or deployment occurred.

Release backend tests and the preflight-tool build exited 0 within 60-second limits. The [summary](../artifacts/validation/p0-sr-provider-20260930-01/summary.json), [test stdout](../artifacts/validation/p0-sr-provider-20260930-01/test.stdout.log), [test stderr](../artifacts/validation/p0-sr-provider-20260930-01/test.stderr.log), [TRX](../artifacts/validation/p0-sr-provider-20260930-01/backend.trx), [build stdout](../artifacts/validation/p0-sr-provider-20260930-01/build.stdout.log), and [build stderr](../artifacts/validation/p0-sr-provider-20260930-01/build.stderr.log) are retained.

| Gate | Result |
| --- | --- |
| Backend tests | 68/68 executed and passed; zero failures/skips. |
| Disabled-DLSS case | Unrequested preparation returns before native loading, leaves selection untouched and reports no created/active feature. |
| Provider compilation | Retained DLSS host/shared backend, FSR 3.1 backend, XeSS native/backend, renderer host interface and texture ABI adapters compile. |
| Tool build | Zero warnings/errors. |

The full device/forwarders and FSR 4 interop remain excluded. No vendor feature loaded/evaluated through the new renderer host. No enabled/disabled game commands, internal/output/present extent logs, MSE/bilinear comparison, regional SR pixels, motion-vector producer validation, composition-order evidence or enabled runtime fallback/resize/toggle trials exist from this batch. See [the SR evidence record](sr-provider-port-implementation.md#sr-evidence-report). SDK input algorithms were retained, not accepted by compilation alone. Full SR/FG/latency, game/facade and release acceptance remain open.
