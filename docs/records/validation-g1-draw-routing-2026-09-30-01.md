# G1 generic draw routing validation — 2026-09-30

Status: **33 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
mesh draw, game launch, package or deployment occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../../artifacts/validation/g1-draw-routing-20260930-01/summary.json),
[test stdout](../../artifacts/validation/g1-draw-routing-20260930-01/test.stdout.log),
[test stderr](../../artifacts/validation/g1-draw-routing-20260930-01/test.stderr.log),
[TRX](../../artifacts/validation/g1-draw-routing-20260930-01/game.trx),
[build stdout](../../artifacts/validation/g1-draw-routing-20260930-01/build.stdout.log)
and [build stderr](../../artifacts/validation/g1-draw-routing-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 33/33 executed/passed; zero failures/skips. |
| Expanded bindings | All ten mesh signatures resolved and installed, including ordinary/instanced/multidraw/fullscreen draws. |
| Fixture | Ordinary draw ownership/removal passed; active ownerless draw rejected before original GL. |
| Compilation | Retained draw helper call/state/accounting compiled; tool build reports zero warnings/errors. |

No successful draw was recorded or presented through the game adapter. Actual
state/target/pass selection, mesh validity behavior, counting, sampled pixels,
dedicated scene/pass-context and temporal/provider integration remain unverified.
Complete startup/menu/world/SDL/release acceptance stays open. No extra profile
inventory ran. See [implementation/provenance](game-draw-routing-implementation.md).
