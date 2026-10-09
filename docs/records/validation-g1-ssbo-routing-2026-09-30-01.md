# G1 SSBO mesh routing validation — 2026-09-30

Status: **33 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
SSBO write/draw, game launch, package or deployment occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../../artifacts/validation/g1-ssbo-routing-20260930-01/summary.json),
[test stdout](../../artifacts/validation/g1-ssbo-routing-20260930-01/test.stdout.log),
[test stderr](../../artifacts/validation/g1-ssbo-routing-20260930-01/test.stderr.log),
[TRX](../../artifacts/validation/g1-ssbo-routing-20260930-01/game.trx),
[build stdout](../../artifacts/validation/g1-ssbo-routing-20260930-01/build.stdout.log)
and [build stderr](../../artifacts/validation/g1-ssbo-routing-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 33/33 executed/passed; zero failures/skips. |
| Mesh bindings | All six platform signatures resolved/installed; SSBO update owner/removal assertions passed with existing VAO checks. |
| CPU face packing | 64-byte record, quad coordinate differences, packed UV dimensions, four distinct flags and zero default colormap passed. |
| Build | Zero warnings/errors. No additional profile inventory. |

The new case does not execute native allocation/update, offset storage writes,
custom-color stride, rotated/out-of-range UV branches or GPU reads. Full actual
mesh lifetime/retirement, draws/state/framebuffers and complete startup/menu/world/
provider/SDL/release acceptance remain open. See
[implementation/provenance](game-ssbo-routing-implementation.md).
