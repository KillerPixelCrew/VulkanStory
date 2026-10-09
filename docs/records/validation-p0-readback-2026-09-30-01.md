# P0 readback/pixel validation — 2026-09-30

Status: **47 backend tests, clean build, indexed pixel checks and presentation passed.** One bounded validation batch ran once. No implementation source changed, no command was rerun, and no Vintage Story game launch or deployment occurred.

Release backend tests, the preflight build, and one hidden `--sdl-mesh-readback` run all exited 0 within 60-second limits. The [summary](../../artifacts/validation/p0-readback-20260930-01/summary.json), [test stdout](../../artifacts/validation/p0-readback-20260930-01/test.stdout.log), [test stderr](../../artifacts/validation/p0-readback-20260930-01/test.stderr.log), [TRX](../../artifacts/validation/p0-readback-20260930-01/backend.trx), [build stdout](../../artifacts/validation/p0-readback-20260930-01/build.stdout.log), [build stderr](../../artifacts/validation/p0-readback-20260930-01/build.stderr.log), [pixel stdout](../../artifacts/validation/p0-readback-20260930-01/pixels.stdout.log), and [pixel stderr](../../artifacts/validation/p0-readback-20260930-01/pixels.stderr.log) are retained.

| Gate | Result |
| --- | --- |
| Backend tests | 47/47 executed and passed; zero failures/skips. Includes five alignment cases and the retained RGBA/BGRA regression. |
| Build | Zero warnings and errors. |
| Native readback | Retained manager recorded a full RGBA8 image copy, submitted the frame, waited on its ticket and copied bytes to host. |
| Pixel assertions | Triangle centre `(64,48)` matched `(242,140,26,255)` and outside clear `(2,2)` matched `(26,89,191,255)`, each channel within tolerance 2. |
| Presentation/teardown | Existing SDL/Vulkan blit and present path completed, followed by waited resource teardown. No stderr or timeout. |

This establishes selected rendered pixels for one indexed triangle and one cleared attachment through the transplanted backend. It does not prove all pixels, game shaders/geometry, captures/AVI orientation or alpha, arbitrary readback formats, partial-frame arena growth/reuse, instancing/indirect GPU draws, menu/world frames, native input, normal-shortcut routing, or any provider feature. Full P0/G1–G3 and release acceptance remain open.
