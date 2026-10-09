# G1 successful UBO integration validation — 2026-09-30

Status: **compilation passed; 30 cases passed and successful generic bytes
failed.** One bounded batch ran once. No implementation source changed, no
command was rerun, and no native/game/package/deployment check occurred.

Release game tests exited 1: 31 executed, 30 passed, 1 failed, zero skips.
The profile-tool build was skipped. The
[summary](../../artifacts/validation/g1-ubo-integration-20260930-01/summary.json),
[stdout](../../artifacts/validation/g1-ubo-integration-20260930-01/test.stdout.log),
[stderr](../../artifacts/validation/g1-ubo-integration-20260930-01/test.stderr.log)
and [TRX](../../artifacts/validation/g1-ubo-integration-20260930-01/game.trx) are
retained. The test process completed within 60 seconds.

## Evidence and diagnosis

The new fixture successfully installed the group, created an original owned UBO,
and observed its expected zeroed 16-byte shadow. Its first generic whole-struct
update failed exact bytes at line 39: expected first byte 8, actual 232. Partial,
object update and disposal assertions were not reached. Cleanup ran in finally.

Original snapshot `VintagestoryApi/Util/ObjectHandleExtensions.cs` shows
GCHandleProvider allocates a normal GCHandle and `Pointer` returns
GCHandle.ToIntPtr, a **handle token**, not AddrOfPinnedObject. Original generic
UBO methods pass that value to BufferData/SubData. The new upload boundary treats
it as a data address, explaining copied handle-storage bytes instead of payload.
This source diagnosis and observed mismatch require correction; prior guard-only
passes did not establish successful generic byte semantics.

Next implementation must adapt this helper-produced value to genuinely pinned
payload storage for the bounded owned UBO route and release that pin with the
original helper lifetime. Do not blindly convert arbitrary raw GL pointers to
GCHandles: object-range/native-pointer paths already supply data addresses.
Keep original size checks and scope the helper adaptation to the owned uniform
buffer path. No fix or rerun occurred this validation turn.

Successful generic writes remain failed/unaccepted. CPU range/disposal and GPU
snapshots/pixels, complete graphics/startup/menu/world/provider/SDL/release remain open.
