# G0 input behavior validation — second batch, 2026-09-30

Status: **14 behavior tests, clean profile-tool build, and official binding/profile check passed.** One bounded validation batch ran once after the concrete wheel-dependency repair. No implementation source changed, no command was rerun, and no game or native window was launched.

The batch ran Release `VulkanStory.Game.Tests`, built the profile tool in Release, then ran one static inventory against official 1.22.7. All three exited 0 within their 60-second limits. The [summary](../artifacts/validation/g0-input-behavior-20260930-02/summary.json), [test stdout](../artifacts/validation/g0-input-behavior-20260930-02/test.stdout.log), [test stderr](../artifacts/validation/g0-input-behavior-20260930-02/test.stderr.log), [TRX](../artifacts/validation/g0-input-behavior-20260930-02/input.trx), [build stdout](../artifacts/validation/g0-input-behavior-20260930-02/build.stdout.log), [build stderr](../artifacts/validation/g0-input-behavior-20260930-02/build.stderr.log), [inventory stdout](../artifacts/validation/g0-input-behavior-20260930-02/inventory.stdout.log), [inventory stderr](../artifacts/validation/g0-input-behavior-20260930-02/inventory.stderr.log), and [captured IL](../artifacts/validation/g0-input-behavior-20260930-02/startup-il.json) are retained.

| Gate | Result |
| --- | --- |
| Game behavior tests | 14/14 executed and passed; zero failures or skips. Nine arbitration/binding cases and five retained touch cases. |
| Game/SDL/profile compilation | Exit 0; profile build reports zero warnings and errors. Includes the SDL sidecar and deferred wheel-setting callback. |
| Official profile/bindings | File hashes, all 418 operands across seven startup targets, six private field types, and three callback method signatures matched. No stderr. |

The arbitration cases exercise real Harmony field accessors, closed delegate creation, original mouse positioning, original key-release history/wheel/focus state, shared key/button ownership, reference-counted controller release, focus cleanup, independent handler events, physical activity versus controller motion, committed text, once-per-event wheel accumulation, and a sensitivity change between events. The touch cases cover tap, drawable-pixel coordinates, drag cancellation, long press, and multiple-contact suppression. Test objects bypass native platform construction; frame/close callbacks are bound but not invoked.

This closes the compile failure from [the first batch](validation-g0-input-behavior-2026-09-30-01.md) for the repaired test project. It does not prove live SDL event dispatch, stale GUI/IME target handling, native input, actual frame/close invocation, resize/teardown, real Harmony routing, visible first-window ownership, or game/provider rendering. G0/G3 and full port acceptance remain open.
