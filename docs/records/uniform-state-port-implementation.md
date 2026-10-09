# Uniform-buffer state and indirect ring extraction

Date: 2026-09-30. Implementation only, including completion after an interrupted implementation turn. No builds, tests, probes, packages, or game runs occurred.

`ClientUniformBuffer` and its manager are extracted from the retained device program partial into the game-neutral backend compile list. CPU shadows, unchanged-byte comparisons, versioned frame snapshots, minimum allocation, named block bindings, generic-unbind semantics and deletion remain unchanged. The per-draw uniform-arena copy is also extracted; the staged device binding method delegates to it while retaining its existing exhaustion diagnostic. The staged facade's public uniform operations and dictionary lookups now use the shared manager rather than a duplicate implementation.

`Frame/IndirectRing.cs` joins compilation unchanged. It preserves per-slot bump allocation, non-wrapping overflow, frame-boundary growth, high-water demand and slot recycling. Source baseline for both groups: `386e0d05386d0b228b439d09aeca851428f7bbf3`.

Three new changed-boundary fixtures cover snapshot reuse versus changed bytes/new frame identities, invalid/partial writes, and named block bindings surviving generic unbind and deletion of an older handle. The original indirect-ring regression is copied with its expected values intact. These cases are source-only; the next validation should compile and run the backend tests once.

The complete `VulkanDevice` partials remain staged and excluded. This does not establish game shader routing, descriptor binding of client blocks, actual per-draw GPU uniform snapshots, arena exhaustion, animation rendering, indirect GPU buffer allocation/draws, or provider integration. Those paths remain part of the full facade and game graphics acceptance.

The [first bounded validation](validation-p0-uniform-state-2026-09-30-01.md) passed all 51 backend cases and a clean tool build. The new shadow/binding and retained indirect bookkeeping cases have evidence; live uniform-arena copies and device/game routing remain unverified.
