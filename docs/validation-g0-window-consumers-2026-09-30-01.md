# G0 core window-consumer validation — 2026-09-30

Status: **15 game tests, clean profile build, and official signature checks passed.** One bounded validation batch ran once. No implementation source changed, no command was rerun, and no native window, game launch, package, or deployment occurred.

The batch ran Release game tests, built the profile tool in Release, and performed one static inventory against official 1.22.7. Each process exited 0 within its 60-second limit. The [summary](../artifacts/validation/g0-window-consumers-20260930-01/summary.json), [test stdout](../artifacts/validation/g0-window-consumers-20260930-01/test.stdout.log), [test stderr](../artifacts/validation/g0-window-consumers-20260930-01/test.stderr.log), [TRX](../artifacts/validation/g0-window-consumers-20260930-01/game.trx), [build stdout](../artifacts/validation/g0-window-consumers-20260930-01/build.stdout.log), [build stderr](../artifacts/validation/g0-window-consumers-20260930-01/build.stderr.log), [inventory stdout](../artifacts/validation/g0-window-consumers-20260930-01/inventory.stdout.log), [inventory stderr](../artifacts/validation/g0-window-consumers-20260930-01/inventory.stderr.log), and [captured IL](../artifacts/validation/g0-window-consumers-20260930-01/startup-il.json) are retained.

| Gate | Result |
| --- | --- |
| Game tests | 15/15 executed and passed; zero failures or skips. Includes the new actual Harmony fixture plus 14 input/touch cases. |
| Actual patch fixture | All 13 prefixes installed and removed. The display-size getter preserved its original value while dormant, rejected active routing with no adapter, and preserved original behavior after removal. |
| Compilation | Profile build reports zero warnings/errors. |
| Official profile and signatures | File hashes and all 418 startup operands matched; platform/GUI binding metadata and all 13 window-consumer signatures matched. No stderr. |

The Harmony fixture invokes one safe original getter on a constructor-bypassed platform; it does not execute any successful SDL replacement. It establishes real patch installation, dormant pass-through, missing-adapter rejection, and removal for this subset. It does not prove native focus/capture/state/resize operations, partial-install rollback, full transaction/session lifetime, other-mod ordering, or complete window coverage. Cursor/clipboard/capture/exit and remaining consumers, startup producers, graphics routing, and the live session are still pending. G0 and the full port remain open.
