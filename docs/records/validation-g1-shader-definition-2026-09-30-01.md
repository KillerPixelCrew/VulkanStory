# G1 shader-definition boundary validation — 2026-09-30

Status: **27 game tests, clean profile build and official binding checks passed.** One bounded batch ran once. No implementation source changed, no command was rerun, and no game/native rendering run or deployment occurred.

Release game tests, the profile-tool build and one official inventory exited 0 within 60-second limits. The [summary](../../artifacts/validation/g1-shader-definition-20260930-01/summary.json), [test stdout](../../artifacts/validation/g1-shader-definition-20260930-01/test.stdout.log), [test stderr](../../artifacts/validation/g1-shader-definition-20260930-01/test.stderr.log), [TRX](../../artifacts/validation/g1-shader-definition-20260930-01/game.trx), [build stdout](../../artifacts/validation/g1-shader-definition-20260930-01/build.stdout.log), [build stderr](../../artifacts/validation/g1-shader-definition-20260930-01/build.stderr.log), [inventory stdout](../../artifacts/validation/g1-shader-definition-20260930-01/inventory.stdout.log), [inventory stderr](../../artifacts/validation/g1-shader-definition-20260930-01/inventory.stderr.log), and [captured IL](../../artifacts/validation/g1-shader-definition-20260930-01/startup-il.json) are retained.

| Gate | Result |
| --- | --- |
| Game tests | 27/27 executed and passed; zero failures/skips. |
| Shader boundary cases | Same-object refresh/reload identity, distinct equal-source identities and vertex/fragment/geometry/compute tokens passed. |
| Build | Zero warnings/errors. Compiles game adapter and neutral contracts, not the excluded full device facade. |
| Official profile | File hashes, 418 original startup operands and current platform/GUI/window signature contracts matched. No stderr. |

The source/text/token boundary has focused evidence. Include-set capture was compiled but is not exercised by the new fixtures. The staged program facade's revised signatures and dictionary changes remain uncompiled, and no actual game compile/link/reload call is routed through them yet. Native-manifest/override behavior, full device/provider facade, menu/world rendering and product acceptance remain open.
