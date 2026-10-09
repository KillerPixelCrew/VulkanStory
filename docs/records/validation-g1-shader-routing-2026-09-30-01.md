# G1 shader routing validation — 2026-09-30

Status: **compilation passed; 29 tests passed and one target-binding fixture
failed.** One bounded batch ran once. No implementation source changed, no
command was rerun, and no native/game/package/deployment check occurred.

Release game tests exited 1 with 30 executed, 29 passed, 1 failed and zero skips.
The dependent profile-tool build was skipped. The
[summary](../../artifacts/validation/g1-shader-routing-20260930-01/summary.json),
[stdout](../../artifacts/validation/g1-shader-routing-20260930-01/test.stdout.log),
[stderr](../../artifacts/validation/g1-shader-routing-20260930-01/test.stderr.log)
and [TRX](../../artifacts/validation/g1-shader-routing-20260930-01/game.trx) are
retained. The command finished within 60 seconds.

## Failure and source diagnosis

`ShaderConsumerRoutingTests` fails in validation with `MissingMethodException`
for `ClientPlatformWindows.UseShaderProgram(int)`. The first three targets
resolved before that fourth target failed. No shader prefixes were installed.

The old patcher lists `UseShaderProgram` among added platform operations;
the old donor calls it from `ShaderProgramBase.Use/Stop`. This is an injected
boundary, not an official platform method. The preserved original snapshot's
`ShaderProgramBase.Use/Stop` contains direct GL selection instead. Its source
is reference evidence for choosing the next patch, not an authorized runtime
assembly or proof of current official IL.

Next implementation must bind the existing official shader Use/Stop methods
and replace their precise GL selection operations while preserving the original
current-program tracking, texture/sampler and cleanup behavior. Inspect the
official method profile before fixing; do not merely drop program selection
from scope or introduce an injected platform dependency. Change the fixture to
a real official target. No repair or second batch ran in this validation turn.

Shader adapter compilation has evidence. Actual shader patch installation,
selection/link execution, uniform/disposal/draw routing, complete startup,
menus/worlds, vendor features, SDL controls and release remain open.
