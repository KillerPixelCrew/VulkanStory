# G1 fixed-state routing validation — 2026-09-30

Status: **34 game tests, successful patched CPU-state fixture and clean
profile-tool build passed.** One bounded batch ran once. No implementation source
changed, no command was rerun, and no native draw/game/package/deployment ran.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../artifacts/validation/g1-state-routing-20260930-01/summary.json),
[test stdout](../artifacts/validation/g1-state-routing-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/g1-state-routing-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/g1-state-routing-20260930-01/game.trx),
[build stdout](../artifacts/validation/g1-state-routing-20260930-01/build.stdout.log)
and [build stderr](../artifacts/validation/g1-state-routing-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 34/34 executed/passed; zero failures/skips. |
| State binding/install | Nineteen official state signatures resolved and prefixes installed. |
| Actual CPU dispatch | Original patched viewport, negative scissor clipping/getter, depth test/write/compare, cull direction, mask, width/wireframe and blend enable/disable populated owned state with expected values. |
| Build | Zero warnings/errors. No extra profile inventory execution. |

The fixture uses a constructor-bypassed platform and an uninitialized device's
CPU state. It does not establish actual pipeline/viewport/scissor pixels, all
blend factors, SSAO/motion overrides, dormant GL behavior, stencil operations
or unsupported/wrong-thread paths. Remaining graphics state/texture/framebuffer/
clear/query/capture, full startup/menu/world/provider/SDL/release acceptance
remain open. See [implementation/provenance](game-state-routing-implementation.md).
