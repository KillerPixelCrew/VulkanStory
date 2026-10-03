# G0 cursor/clipboard adapter validation — 2026-09-30

Status: **18 game tests, clean profile build, and official binding checks passed.** One bounded validation batch ran once. No implementation source changed, no command was rerun, and no native window, game launch, packaging, or deployment occurred.

Release game tests, the profile-tool build, and one official inventory all exited 0 within 60-second limits. The [summary](../artifacts/validation/g0-cursor-clipboard-20260930-01/summary.json), [test stdout](../artifacts/validation/g0-cursor-clipboard-20260930-01/test.stdout.log), [test stderr](../artifacts/validation/g0-cursor-clipboard-20260930-01/test.stderr.log), [TRX](../artifacts/validation/g0-cursor-clipboard-20260930-01/game.trx), [build stdout](../artifacts/validation/g0-cursor-clipboard-20260930-01/build.stdout.log), [build stderr](../artifacts/validation/g0-cursor-clipboard-20260930-01/build.stderr.log), [inventory stdout](../artifacts/validation/g0-cursor-clipboard-20260930-01/inventory.stdout.log), [inventory stderr](../artifacts/validation/g0-cursor-clipboard-20260930-01/inventory.stderr.log), and [captured IL](../artifacts/validation/g0-cursor-clipboard-20260930-01/startup-il.json) are retained.

| Gate | Result |
| --- | --- |
| Game tests | 18/18 executed and passed; zero failures/skips. Three new wrapper/setter cases plus retained input/touch and actual Harmony cases. |
| Wrapper fixtures | Dormant clipboard/display/focus services forward to a fake original implementation without native access. Active calls check the owner/session before native access. |
| Protected setter fixture | Actual closed delegates update original `XPlatInterface` and `CurrentMouseCursor` properties on a constructor-bypassed platform. |
| Harmony fixture | Expanded 16-prefix group installs/removes; dormant display-size behavior and active missing-adapter rejection pass. |
| Compilation/profile | Zero warnings/errors; official hashes, 418 startup operands, platform/GUI fields, two protected setters, and 16 window-consumer signatures match. No stderr. |

This proves the listed managed boundaries. Cursor bitmap conversion/native cursor creation, successful SDL clipboard/focus operations, wrapper attachment/restoration during real session teardown, settings access, GUI/IME execution, full consumer coverage, startup producers, graphics routing, and provider/world rendering remain unverified. G0/G3 and the full port remain open.
