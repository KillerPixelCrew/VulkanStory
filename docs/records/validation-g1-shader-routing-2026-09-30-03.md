# G1 official shader selection validation — 2026-09-30

Status: **30 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
shader draw, game launch, package or deployment occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../../artifacts/validation/g1-shader-routing-20260930-03/summary.json),
[test stdout](../../artifacts/validation/g1-shader-routing-20260930-03/test.stdout.log),
[test stderr](../../artifacts/validation/g1-shader-routing-20260930-03/test.stderr.log),
[TRX](../../artifacts/validation/g1-shader-routing-20260930-03/game.trx),
[build stdout](../../artifacts/validation/g1-shader-routing-20260930-03/build.stdout.log)
and [build stderr](../../artifacts/validation/g1-shader-routing-20260930-03/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 30/30 executed/passed; zero failures/skips. |
| Official targets | Three platform shader prefixes and ShaderProgramBase.Use/Stop resolved. |
| Selection anchors | Original IL and incoming Harmony instruction checks each found exactly one GL.UseProgram(int) per selection method. |
| Real Harmony fixture | Both selection transpilers installed with expected owner metadata and were removed. Active uniform lookup without an adapter rejected before original GL. |
| Compilation | Namespace repair compiled; profile-tool build reports zero warnings/errors. |

The injected-target and missing-namespace failures are resolved for this subset.
The fixture never executes successful Use/Stop, compile/link, uniform lookup or
the selection wrapper against a live renderer. It does not certify original
game tracking/cleanup at runtime, other-mod ordering or all shader GL routes.
Uniform setters, samplers, UBOs/disposal and draw routing still need integration;
the incomplete subset cannot activate a full graphics transaction. Real menu/world
shaders, vendor features, SDL controls and release remain open. No additional
profile-tool inventory was executed in this batch.
