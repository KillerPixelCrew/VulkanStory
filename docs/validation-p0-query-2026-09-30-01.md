# P0 occlusion-query validation — 2026-09-30

Status: **47 backend tests, clean build, native query/pixel/presentation checks passed.** One bounded batch ran once. No implementation source changed, no command was rerun, and no Vintage Story launch or deployment occurred.

Release tests, the tool build and one hidden `--sdl-mesh-query` run exited 0 within 60-second limits. The [summary](../artifacts/validation/p0-query-20260930-01/summary.json), [test stdout](../artifacts/validation/p0-query-20260930-01/test.stdout.log), [test stderr](../artifacts/validation/p0-query-20260930-01/test.stderr.log), [TRX](../artifacts/validation/p0-query-20260930-01/backend.trx), [build stdout](../artifacts/validation/p0-query-20260930-01/build.stdout.log), [build stderr](../artifacts/validation/p0-query-20260930-01/build.stderr.log), [query stdout](../artifacts/validation/p0-query-20260930-01/queries.stdout.log), and [query stderr](../artifacts/validation/p0-query-20260930-01/queries.stderr.log) are retained.

| Gate | Result |
| --- | --- |
| Backend tests | 47/47 executed and passed; zero failures/skips. |
| Tool compilation | Zero warnings/errors. |
| Native query path | Actual render-target scope hooks suspended/resumed a query across two indexed-draw scopes; another query covered an empty scope. |
| Availability/results | Unavailable before submission; available after the readback completion wait. Draw result was finite and positive; empty result was zero. Deleted query was unavailable. Exact counts were not logged or asserted. |
| Pixels/presentation | Triangle centre and outside clear pixels matched the existing expected RGBA values within tolerance 2, then blit/present and waited teardown completed. No stderr or timeout. |

This establishes the exercised query boundary without an additional query-specific wait. It does not establish exact cross-hardware counts, pool overflow, frame-slot recycling, partial submissions, full query lifecycle failures, or game glare/culling behavior. Game API/device facade, startup/window routing, menu/world rendering, vendor features and full port/release acceptance remain open.
