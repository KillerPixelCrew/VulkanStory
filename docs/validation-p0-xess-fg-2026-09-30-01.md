# P0 XeSS FG compile validation — 2026-09-30

Status: **70 backend tests and clean tool build passed.** One bounded batch ran
once. No implementation source changed, no command was rerun, and no native
bridge build, preflight, SDK evaluation, game launch, package or deployment occurred.

Release backend tests and the preflight-tool build exited 0 within their
60-second limits. The [summary](../artifacts/validation/p0-xess-fg-20260930-01/summary.json),
[test stdout](../artifacts/validation/p0-xess-fg-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/p0-xess-fg-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/p0-xess-fg-20260930-01/backend.trx),
[build stdout](../artifacts/validation/p0-xess-fg-20260930-01/build.stdout.log)
and [build stderr](../artifacts/validation/p0-xess-fg-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Backend cases | 70/70 executed and passed; zero failures/skips. |
| Managed XeSS FG ABI | Presentation frame size 216 and all field offsets passed on x64, including matrices at 56/120 and fence values at 200/208. |
| Compile group | Retained interop requirements, native runtime wrapper and threaded presenter compile with the shared image/fence group. |
| Tool build | Zero warnings/errors. |

The native C++ bridge and its static assertions/exports were not built or loaded.
The test never constructs the presenter, starts its thread, creates shared
resources/fences or invokes XeSS FG/XeLL. Runtime dependency loading, adapter
matching, synchronization, actual SDL HWND/DXGI ownership, SDK-generated frames,
marker timing, resize/switch/fallback and shutdown remain unverified. The full
device and changed multiplier property remain excluded, so their compilation
and settings wiring remain open. Full renderer/SR/FG/latency/SDL/game/release
acceptance stays open. See [the source/provenance record](xess-fg-port-implementation.md).
