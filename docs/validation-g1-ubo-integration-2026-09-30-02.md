# G1 UBO token-repair integration validation — 2026-09-30

Status: **31 game tests, successful UBO CPU byte/lifecycle fixture and clean
profile-tool build passed.** One bounded batch ran once. No implementation source
changed, no command was rerun, and no native/GPU/game/package/deployment ran.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../artifacts/validation/g1-ubo-integration-20260930-02/summary.json),
[test stdout](../artifacts/validation/g1-ubo-integration-20260930-02/test.stdout.log),
[test stderr](../artifacts/validation/g1-ubo-integration-20260930-02/test.stderr.log),
[TRX](../artifacts/validation/g1-ubo-integration-20260930-02/game.trx),
[build stdout](../artifacts/validation/g1-ubo-integration-20260930-02/build.stdout.log)
and [build stderr](../artifacts/validation/g1-ubo-integration-20260930-02/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 31/31 executed/passed; zero failures/skips. |
| Actual creation | Original patched platform CreateUBO returned UBO with expected size/positive renderer handle and zeroed sixteen-byte shadow. |
| Generic uploads | Original whole/range methods wrote exact payload bytes; range copied the struct beginning to offset eight while preserving the first field. |
| Object upload | Four-byte object-range patch changed only its intended range. |
| Cleanup/lifecycle | Bound state and observed helper tokens cleared; original base disposed flag and removal/repeated disposal assertions passed. |
| Build | Zero warnings/errors. No extra profile inventory. |

The [first exact-byte failure](validation-g1-ubo-integration-2026-09-30-01.md)
is resolved for the exercised sixteen-byte struct and object range. The device
was not initialized with a Vulkan context; all bytes were retained CPU uniform
shadows. This does not establish GPU snapshot/descriptor/draw results, broad
generic/JIT type coverage, pin-failure paths, cross-thread behavior or complete
game graphics. Full startup/menu/world/providers/SDL/release remain open. See
[repair implementation](game-ubo-handle-repair-implementation.md).
