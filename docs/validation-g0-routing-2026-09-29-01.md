# G0 pinned startup guard and transaction validation — 2026-09-29

Status: **focused tests, tool build, and official IL baseline check passed.** This was one bounded validation batch. No implementation source was changed, no command was rerun, and the game was not launched.

The batch ran Release bootstrap tests, a Release build of `VulkanStory.GameProfile`, and one static profile check against the existing official 1.22.7 installation under a 30-second timeout. The [summary](../artifacts/validation/g0-routing-20260929-01/summary.json), [test log](../artifacts/validation/g0-routing-20260929-01/test.log), [TRX](../artifacts/validation/g0-routing-20260929-01/bootstrap.trx), [build log](../artifacts/validation/g0-routing-20260929-01/build.log), [inventory stdout](../artifacts/validation/g0-routing-20260929-01/inventory.stdout.log), [inventory stderr](../artifacts/validation/g0-routing-20260929-01/inventory.stderr.log), and [captured IL](../artifacts/validation/g0-routing-20260929-01/startup-il.json) are retained.

| Gate | Result |
| --- | --- |
| Bootstrap tests | 17/17 passed, zero failed or skipped. Includes operand drift rejection, incomplete coverage, validation before mutation, partial-install rollback, delayed activation, session failure, and rollback failure reporting. |
| Profile tool build | Exit 0, zero warnings and errors. |
| File hashes and embedded startup baseline | Exit 0 without timeout or stderr. All listed official file hashes and 418 member operands across seven targets matched, including offsets, opcodes, tokens, signatures, and method IL lengths. |

The transaction tests use controlled patch operations; they do not prove real Harmony routing or native session rollback. The baseline check reads original method bodies and does not certify instructions modified by another Harmony owner. Actual SDL replacement groups, the game sidecar, session preparation, visible rendering, and live normal-shortcut observation with the current payload remain open. This evidence completes the guard/transaction verification step, not G0 acceptance.
