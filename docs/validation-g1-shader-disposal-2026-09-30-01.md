# G1 shader disposal routing validation — 2026-09-30

Status: **31 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
program retirement/draw, game launch, package or deployment occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../artifacts/validation/g1-shader-disposal-20260930-01/summary.json),
[test stdout](../artifacts/validation/g1-shader-disposal-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/g1-shader-disposal-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/g1-shader-disposal-20260930-01/game.trx),
[build stdout](../artifacts/validation/g1-shader-disposal-20260930-01/build.stdout.log)
and [build stderr](../artifacts/validation/g1-shader-disposal-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 31/31 executed/passed; zero failures/skips. |
| Disposal anchors | Original/incoming counts matched: three DetachShader, three DeleteShader, one DeleteSampler and one DeleteProgram call. |
| Harmony fixture | Disposal transpiler installed; shader Dispose ownership and removal checks passed with the existing group. |
| CPU regression | Successful UBO shadow-byte/lifecycle fixture still passed. |
| Tool build | Zero warnings/errors. No additional profile inventory. |

The fixtures never dispose a real linked shader program/sampler resource.
Runtime disposed-state branches, native modules/pipelines, in-flight GPU retirement,
captured texture invalidation and dedicated scene cache cleanup remain unverified.
Complete graphics/startup/menu/world/provider/SDL/release acceptance stays open.
See [implementation/provenance](game-shader-disposal-implementation.md).
