# G1 scalar/vector uniform routing validation — 2026-09-30

Status: **30 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
uniform write/draw, game launch, package or deployment occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../artifacts/validation/g1-uniform-routing-20260930-01/summary.json),
[test stdout](../artifacts/validation/g1-uniform-routing-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/g1-uniform-routing-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/g1-uniform-routing-20260930-01/game.trx),
[build stdout](../artifacts/validation/g1-uniform-routing-20260930-01/build.stdout.log)
and [build stderr](../artifacts/validation/g1-uniform-routing-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 30/30 executed/passed; zero failures/skips. |
| Uniform bindings/anchors | Ten scalar/vector signatures resolved; original and incoming Harmony IL each contained one expected GL setter per overload. |
| Installation | Ten uniform transpilers installed with the shader subset. Scalar owner metadata and removal assertions passed, alongside Use/Stop and missing-adapter lookup cases. |
| Tool build | Zero warnings/errors. No new profile inventory execution. |

The fixture does not invoke successful uniform wrappers or test their actual
current-program lookup, writes, retained guards/conversions or native shader
pixels. Array/matrix, sampler/texture/UBO/disposal/draw routing and complete
startup/menu/world/provider/SDL/release acceptance remain open. See
[implementation/provenance](game-uniform-routing-implementation.md).
