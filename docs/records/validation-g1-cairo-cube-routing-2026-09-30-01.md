# G1 Cairo/cubemap routing compile validation — 2026-09-30

Status: **29 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
upload, game launch, package or deployment occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../../artifacts/validation/g1-cairo-cube-routing-20260930-01/summary.json),
[test stdout](../../artifacts/validation/g1-cairo-cube-routing-20260930-01/test.stdout.log),
[test stderr](../../artifacts/validation/g1-cairo-cube-routing-20260930-01/test.stderr.log),
[TRX](../../artifacts/validation/g1-cairo-cube-routing-20260930-01/game.trx),
[build stdout](../../artifacts/validation/g1-cairo-cube-routing-20260930-01/build.stdout.log)
and [build stderr](../../artifacts/validation/g1-cairo-cube-routing-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 29/29 executed/passed; zero failures/skips. |
| Adapter compilation | Cairo creation/update, six-face cubemap and conditional error helper compile with the official non-copying Cairo reference. |
| Eleven texture targets | Typed signatures resolved and Harmony installation completed through the existing fixture. |
| Patch behavior | Active missing-adapter deletion rejected before original GL; removal cleared its owner-specific deletion prefix. |
| Tool build | Zero warnings/errors. The profile tool was not executed for a new inventory. |

No Cairo surface or cube bitmap was uploaded. The fixture does not invoke the
three successful new prefixes or check error-setting behavior. Native pointer
ownership, channel order, six-face sampling, reallocation and resource lifetime
remain unverified. Texture arrays, complete graphics/startup routing, real
menus/worlds, vendor features, SDL controls and release remain open. See
[implementation/provenance](game-cairo-cube-routing-implementation.md).
