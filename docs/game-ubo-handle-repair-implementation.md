# Generic UBO handle-token repair

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

The [failed exact-byte fixture](validation-g1-ubo-integration-2026-09-30-01.md)
showed that original GCHandleProvider.Pointer supplies a normal handle token.
The repaired boundary observes that getter only while an owned UBO is bound
and routing is active, preserving its original result. It records the helper
identity and bound buffer on the calling thread. Generic original bodies must
contain one expected helper getter as well as their upload anchor.

At the intercepted UBO upload, only an observed token is mapped to that helper's
live Handle.Target. Its payload is pinned for the synchronous renderer shadow
copy and freed in finally. No GCHandle.FromIntPtr conversion is attempted on
arbitrary native pointers. Unobserved raw pointers keep the prior byte-copy
path; object-range updates already supply an actual pinned address.

Original helper disposal removes its observation without skipping original
normal-handle cleanup. Unbind, bound-buffer deletion and patch removal clear
thread-local observations. Repeated getter reads replace the same observation
instead of allocating another lifetime. The pin need not survive the upload:
the renderer immediately copies CPU shadow bytes, so no GPU stores this address.

This is a concrete correctness repair of the newly exposed game boundary, not
a shader/layout/synchronization change. Original generic size checks and using
scope remain intact. The fixture now checks zero outstanding observations after
whole/range/object updates. No fixture ran this turn; exact bytes, pin failure,
cleanup, disposal and native GPU snapshot/pixel acceptance remain open.

Next validation: one bounded Release game-test/profile-build batch. Preserve
the earlier failure record; complete graphics/startup/menu/world/provider/SDL
and release objective remains open.

The [repaired bounded batch](validation-g1-ubo-integration-2026-09-30-02.md)
passed 31 game cases and a clean profile build. Whole/range/object CPU bytes,
neighbor preservation, observation/unbinding cleanup and repeated disposal
passed through original patched methods. The exercised token mismatch is
resolved; GPU snapshots/pixels and broader generic/error coverage remain open.
