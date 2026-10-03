# G1 generic UBO upload validation — 2026-09-30

Status: **30 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
buffer write/draw, game launch, package or deployment occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../artifacts/validation/g1-generic-ubo-routing-20260930-01/summary.json),
[test stdout](../artifacts/validation/g1-generic-ubo-routing-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/g1-generic-ubo-routing-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/g1-generic-ubo-routing-20260930-01/game.trx),
[build stdout](../artifacts/validation/g1-generic-ubo-routing-20260930-01/build.stdout.log)
and [build stderr](../artifacts/validation/g1-generic-ubo-routing-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 30/30 executed/passed; zero failures/skips. |
| Generic upload discovery | Both original generic bodies yielded one expected pointer-upload entry point; signature checks passed. |
| Installation | Exact BufferData/BufferSubData prefixes installed with the existing shader/UBO group. |
| Original generic guards | int update against an eight-byte buffer rejected by original size check. Matching long whole/range updates rejected at owned Bind before reaching pin/upload. |
| Tool build | Zero warnings/errors. No extra inventory execution. |

The guard cases do not reach either successful upload prefix. Owned bound state,
pointer/range bytes, other-target/dormant forwarding, generic struct coverage,
thread/error cleanup and resulting shader snapshots/pixels remain unverified.
Native buffer ownership/lifecycle and complete graphics/startup/menu/world,
provider/SDL/control/release acceptance remain open. See
[implementation/provenance](game-generic-ubo-routing-implementation.md).
