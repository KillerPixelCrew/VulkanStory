# G1 bitmap/atlas routing compile validation — 2026-09-30

Status: **29 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
upload, game launch, package or deployment check occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../../artifacts/validation/g1-bitmap-routing-20260930-01/summary.json),
[test stdout](../../artifacts/validation/g1-bitmap-routing-20260930-01/test.stdout.log),
[test stderr](../../artifacts/validation/g1-bitmap-routing-20260930-01/test.stderr.log),
[TRX](../../artifacts/validation/g1-bitmap-routing-20260930-01/game.trx),
[build stdout](../../artifacts/validation/g1-bitmap-routing-20260930-01/build.stdout.log)
and [build stderr](../../artifacts/validation/g1-bitmap-routing-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 29/29 executed/passed; zero failures/skips. |
| Source compilation | Retained unsafe external-bitmap and pinned managed-array upload bodies compile against official game types and neutral renderer formats. |
| Eight texture targets | Typed signature resolution and Harmony installation completed, including int-returning bitmap load and by-reference atlas updates. |
| Existing patch fixture | Active deletion without an adapter rejected before original GL; removal cleared its owner-specific deletion prefix. |
| Profile-tool build | Zero warnings/errors. No new inventory execution in this batch. |

The fixture does not invoke the five successful bitmap/atlas prefixes. No
bitmap buffer was pinned/locked, no texture was created/updated or sampled,
and no native mipmap/channel-order/lifetime result was produced. Expanded
compilation and patch installation do not establish successful game graphics.
Cairo/cubemap/array routes, complete graphics/startup groups, menus/worlds,
providers, SDL controls and release remain open. See
[implementation/provenance](game-bitmap-routing-implementation.md).
