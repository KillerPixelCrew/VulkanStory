# Generic UBO upload boundary

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

Both original UBO.Update<T> methods retain their byte-size checks, Bind/Unbind,
GCHandleProvider and pointer lifetime. The new upload boundary discovers the
single BufferData/BufferSubData entry point from each original generic body
(a closed long method is inspected, never executed during preparation).
It validates static pointer-upload signatures and supports the official int or
native-pointer size forms. No generic method is patched, avoiding assumptions
about generic JIT sharing/instantiations.

Owned UBO Bind records the current thread's buffer; Unbind/removal clears it,
and deletion clears it when appropriate. The two exact GL upload methods receive
prefixes. Only active UniformBuffer target uploads are intercepted. A missing
owned binding rejects before GL; active owned uploads forward the retained byte
range to the renderer's uniform shadows. Dormant/other-target calls pass through.
This bounded UBO route is not broad third-party OpenGL translation acceptance.

Counts/offsets use checked native-size conversion. Original whole-struct size
checks and pin cleanup stay in the game body. Backend shadow/snapshot algorithms
are unchanged. Provenance: baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`,
original UBO generic bodies and retained renderer UBO update forwarding; this
is a host-boundary adapter. Preserve inherited notices/provenance.

The group fixture adds original generic size-mismatch and missing-owner checks
for whole/range updates. It has not run after this increment. These cases do
not prove successful generic pointer uploads or native shader snapshots.
Next validation is one bounded game-test/profile-build batch, checking signature
discovery and actual GL entry-point patch installation together with the existing
group. Real bytes, thread-bound state/error paths, disposal, generic struct
coverage and rendered pixels remain open. Complete graphics/startup/menu/world,
provider/SDL/control/release acceptance is still required.

The [first bounded validation](validation-g1-generic-ubo-routing-2026-09-30-01.md)
passed 30 game cases and a clean profile build. Two upload entries discovered,
validated and installed; original size rejection and whole/range missing-owner
guards passed. No successful owned upload or native bytes/pixels were exercised.
