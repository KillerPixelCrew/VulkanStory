# G0 SDL input extraction validation — 2026-09-29

Status: **compilation, existing bootstrap tests, and official binding metadata checks passed.** One bounded validation batch ran once. No implementation source changed, no command was rerun, no package was deployed, and the game was not launched.

The batch built `VulkanStory.GameProfile` in Release, ran Release bootstrap tests, then ran the static profile tool against the official 1.22.7 installation. Each process was limited to 60 seconds; none timed out. The [summary](../../artifacts/validation/g0-input-20260929-01/summary.json), [build stdout](../../artifacts/validation/g0-input-20260929-01/build.stdout.log), [build stderr](../../artifacts/validation/g0-input-20260929-01/build.stderr.log), [test stdout](../../artifacts/validation/g0-input-20260929-01/test.stdout.log), [test stderr](../../artifacts/validation/g0-input-20260929-01/test.stderr.log), [TRX](../../artifacts/validation/g0-input-20260929-01/bootstrap.trx), [inventory stdout](../../artifacts/validation/g0-input-20260929-01/inventory.stdout.log), [inventory stderr](../../artifacts/validation/g0-input-20260929-01/inventory.stderr.log), and [captured IL](../../artifacts/validation/g0-input-20260929-01/startup-il.json) are retained.

| Gate | Result |
| --- | --- |
| Tool/game/SDL build | Exit 0, zero warnings and errors. Includes the adapted keyboard/touch helpers, input arbitration, cached-binding implementation, and static binding verifier. |
| Existing bootstrap regression tests | 17/17 executed and passed; zero failures or skips. Covers startup guards and controlled routing transactions, not the newly extracted input behavior. |
| Official profile | Exit 0, no stderr. File hashes and all 418 original member operands across seven startup methods matched. |
| New platform binding metadata | Six declared private field types and three original managed void method signatures matched: mouse coordinates, wheel total, last key-release time/key, focus handlers, mouse positioning, close cancellation, and frame dispatch. |

The metadata check does not instantiate the platform, create the cached field/delegate bindings, or invoke a game callback. This batch does not exercise input arbitration, touch gestures, focus cleanup, controller release, IME targeting, SDL dispatch, game frames, or actual Harmony routing. Those checks require subsequent fixtures and sidecar integration. No complete G0/G3 acceptance milestone closes here.
