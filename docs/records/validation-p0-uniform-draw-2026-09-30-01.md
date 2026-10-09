# P0 native uniform draw validation — 2026-09-30

Status: **51 backend tests, clean build, native snapshot/partial-submit/pixel/present checks passed.** One bounded batch ran once. No implementation source changed, no command was rerun, and no Vintage Story launch or deployment occurred.

Release backend tests, tool build and one hidden `--sdl-uniform-snapshots` run exited 0 within 60-second limits. The [summary](../../artifacts/validation/p0-uniform-draw-20260930-01/summary.json), [test stdout](../../artifacts/validation/p0-uniform-draw-20260930-01/test.stdout.log), [test stderr](../../artifacts/validation/p0-uniform-draw-20260930-01/test.stderr.log), [TRX](../../artifacts/validation/p0-uniform-draw-20260930-01/backend.trx), [build stdout](../../artifacts/validation/p0-uniform-draw-20260930-01/build.stdout.log), [build stderr](../../artifacts/validation/p0-uniform-draw-20260930-01/build.stderr.log), [native stdout](../../artifacts/validation/p0-uniform-draw-20260930-01/uniforms.stdout.log), and [native stderr](../../artifacts/validation/p0-uniform-draw-20260930-01/uniforms.stderr.log) are retained.

| Gate | Result |
| --- | --- |
| Backend tests | 51/51 executed and passed; zero failures/skips. |
| Tool build | Zero warnings/errors. |
| Uniform state and descriptors | Actual retained arena snapshots and immutable named-block descriptor sets fed a translated std140 shader. Unchanged contents reused the offset; changed contents required a distinct offset. |
| Partial submission | Same-frame snapshot survived submission and continued recording without an extra pre-readback wait. |
| Rendered pixels | First draw `(48,48)` red `(255,0,0,255)`, second draw `(80,48)` green `(0,255,0,255)`, and outside clear `(2,2)` matched expected RGBA within tolerance 2. |
| Presentation/lifetime | Blit/present and waited teardown completed without stderr or timeout; descriptor pools, placeholders and snapshot storage survived submitted work. |

This establishes the exercised single named-block draw boundary across a partial submission. It does not certify actual game shader/UBO calls, animation blocks, descriptor state caching across all programs, arena exhaustion/overflow, multi-frame reuse, the excluded device facade, menu/world routing, or providers. Full P0/G1–G3 and release acceptance remain open.
