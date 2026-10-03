# G1 mesh resource routing validation — 2026-09-30

Status: **32 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
mesh allocation/draw, game launch, package or deployment occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../artifacts/validation/g1-mesh-routing-20260930-01/summary.json),
[test stdout](../artifacts/validation/g1-mesh-routing-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/g1-mesh-routing-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/g1-mesh-routing-20260930-01/game.trx),
[build stdout](../artifacts/validation/g1-mesh-routing-20260930-01/build.stdout.log)
and [build stderr](../artifacts/validation/g1-mesh-routing-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 32/32 executed/passed; zero failures/skips. |
| Mesh bindings | Four official allocation/upload/update/delete signatures resolved and installed. |
| VAO disposal | Original/incoming single DeleteVertexArray anchor matched; disposal guard/replacement installed. |
| Fixture | Upload/disposal ownership/removal checks passed; active ownerless upload rejected before original GL. |
| Tool build | Zero warnings/errors. No extra profile inventory. |

No successful owned VAO creation/update/disposal or native mesh bytes were
exercised. GL-buffer-field guard behavior, actual disposed-state transitions,
in-flight retirement, generic/native draws and SSBO face packing remain open.
Complete graphics/startup/menu/world/provider/SDL/release acceptance stays open.
See [implementation/provenance](game-mesh-routing-implementation.md).
