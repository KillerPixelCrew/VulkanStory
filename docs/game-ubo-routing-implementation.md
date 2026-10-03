# Original UBO lifecycle source port

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

The shader subset adds official platform CreateUBO plus UBO Bind, Unbind,
object-range Update and a disposal call-site replacement. Creation returns the
original UBO type with renderer handle/size; block/binding/owner metadata lives
in a mod-owned ConditionalWeakTable rather than injected BlockName/BindingPoint
fields. Original binding point and named-block semantics remain in the retained
renderer manager. Lifecycle calls resolve the buffer's original renderer owner.

Object-range updates retain bind/pin/upload/unbind and free the pin in finally.
This exception-path improvement guarantees unpinning when upload throws; normal
range/size semantics are unchanged. Disposal replaces only the single original
GL.DeleteBuffers call, carrying the UBO receiver while preserving base.Dispose
and its original disposed flag. Dormant routes keep original GL behavior;
active ownerless buffers fail before native calls. Repeated disposal keeps its
owner association and relies on existing idempotent backend handle deletion.

Baseline: `386e0d05386d0b228b439d09aeca851428f7bbf3`, original UBO/UBORef
and ClientPlatformWindows.CreateUBO, retained VulkanClientPlatform.Shaders UBO
forwarders. Boundary/state changes are adapters; the pin-finally exception repair
is the explicit behavior change. Preserve inherited notices/provenance.

Generic UBO.Update<T> overloads are still unported and contain raw GL BufferData/
BufferSubData calls. This partial lifecycle group cannot justify graphics
activation. Generic upload routing, complete resource/state/draw/startup and
menu/world/provider/SDL/release acceptance remain required.

The existing fixture adds actual UBO Bind/Dispose installation/removal and
active missing-owner rejection for Bind. It has not run after this increment.
Next validation: one bounded game-test/profile-build batch. Real creation,
range bytes, pin failure cleanup, repeated disposal and GPU snapshots/pixels
remain unverified; no native or game run occurred here.

The [first bounded validation](validation-g1-ubo-routing-2026-09-30-01.md)
passed 30 game cases and a clean profile build. Five UBO signatures and disposal
original/incoming anchor matched and installed; Bind owner rejection and
Bind/Dispose ownership/removal passed. Native bytes/lifetimes and generic uploads remain open.
