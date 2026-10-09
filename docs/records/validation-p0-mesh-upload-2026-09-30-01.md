# P0 mesh upload boundary validation — 2026-09-30

Status: **51 backend tests, 28 game tests, clean build and native buffer checks passed.** One bounded batch ran once. No implementation source changed, no command was rerun, and no Vintage Story launch or deployment occurred.

Release backend/game tests, tool build and one `--mesh-buffers` run exited 0 within 60-second limits. The [summary](../../artifacts/validation/p0-mesh-upload-20260930-01/summary.json), [backend stdout](../../artifacts/validation/p0-mesh-upload-20260930-01/backend.stdout.log), [backend stderr](../../artifacts/validation/p0-mesh-upload-20260930-01/backend.stderr.log), [backend TRX](../../artifacts/validation/p0-mesh-upload-20260930-01/backend.trx), [game stdout](../../artifacts/validation/p0-mesh-upload-20260930-01/game.stdout.log), [game stderr](../../artifacts/validation/p0-mesh-upload-20260930-01/game.stderr.log), [game TRX](../../artifacts/validation/p0-mesh-upload-20260930-01/game.trx), [build stdout](../../artifacts/validation/p0-mesh-upload-20260930-01/build.stdout.log), [build stderr](../../artifacts/validation/p0-mesh-upload-20260930-01/build.stderr.log), [buffer stdout](../../artifacts/validation/p0-mesh-upload-20260930-01/buffers.stdout.log), and [buffer stderr](../../artifacts/validation/p0-mesh-upload-20260930-01/buffers.stderr.log) are retained.

| Gate | Result |
| --- | --- |
| Backend tests | 51/51 executed and passed; zero failures/skips. Compiles the extracted upload helper and neutral topology/slot contracts. |
| Game tests | 28/28 executed and passed; zero failures/skips. New capture case checks arrays, capacity versus count, custom metadata and offsets. |
| Tool build | Zero warnings/errors. |
| Native upload | Actual `MeshUploads.CreateMesh` position data matched mapped bytes. Updating one vertex at byte offset 12 changed only that vertex. Empty custom-part allocation, SSBO face sizing/bindings, quad indices and deletion checks passed. No stderr or timeout. |

The helper and data boundary now have focused evidence. The full device facade remains excluded, so its updated wrappers are not certified by this build. Other upload streams, SSBO custom-int updates, device-local staging order, persistent chunk mapping, actual game mesh references, indirect/instanced game draws, graphics startup/menu/world routing and providers remain open.
