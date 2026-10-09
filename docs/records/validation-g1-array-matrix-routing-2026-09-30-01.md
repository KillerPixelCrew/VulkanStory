# G1 array/matrix uniform routing validation — 2026-09-30

Status: **30 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
uniform write/draw, game launch, package or deployment occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../../artifacts/validation/g1-array-matrix-routing-20260930-01/summary.json),
[test stdout](../../artifacts/validation/g1-array-matrix-routing-20260930-01/test.stdout.log),
[test stderr](../../artifacts/validation/g1-array-matrix-routing-20260930-01/test.stderr.log),
[TRX](../../artifacts/validation/g1-array-matrix-routing-20260930-01/game.trx),
[build stdout](../../artifacts/validation/g1-array-matrix-routing-20260930-01/build.stdout.log)
and [build stderr](../../artifacts/validation/g1-array-matrix-routing-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 30/30 executed/passed; zero failures/skips. |
| Eighteen uniform targets | Signatures and single-setter original/incoming anchors matched; all transpilers installed. |
| Array/matrix fixture | Uniforms4/UniformMatrices owner and removal assertions passed alongside scalar and Use/Stop checks. |
| Compilation | Receiver-carrying wrappers and ref-Matrix4 adaptation compile; tool build reports zero warnings/errors. |

No successful wrapper dispatched to a live renderer. Program IDs, counts,
transpose rejection, matrix order, branch/exception behavior and actual uniform
contents/pixels were not behaviorally exercised. Installation/compilation is
not full uniform acceptance. Samplers/texture binding, UBOs/disposal/draw,
complete startup/menu/world, providers, SDL controls and release remain open.
No extra profile inventory ran. See [implementation/provenance](game-array-matrix-routing-implementation.md).
