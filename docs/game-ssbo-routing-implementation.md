# Retained SSBO face packing and mesh update

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

AllocateEmptySSBOMesh and UpdateSSBOMesh join the four existing mesh platform
routes. Allocation preserves custom part descriptions, draw mode/persistence and
the SSBO flag. Update reuses original mesh identity/owner and neutral capture.
The face-packing loop is directly transplanted into GameSsboFacePacking with
thread-local borrowed scratch storage. Official FaceData remains game-side;
the renderer sees only packed bytes.

Original UV bounds/rotation tests, coordinate differences, corner flags and
custom-int stride/color data remain unchanged. The original XyzOffset/12×16
destination calculation and 16×VerticesCount byte count are retained. Ordinary
mesh streams update first; pinned packed records overwrite the storage slot last.
Pin cleanup remains in finally. The helper's nullable custom-int array declaration
is adjusted mechanically; no packing arithmetic is rewritten.

Provenance: baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`, retained
VulkanClientPlatform.Meshes UpdateSSBOMesh/AllocateEmptySSBOMesh. Extraction and
host/metadata access are adapters; preserve inherited notices/license scope.

A new unrun CPU case checks the original 64-byte FaceData record, quad coordinate
differences, packed UV size and four distinct corner flags. Existing Harmony
fixture expands to all six mesh targets with SSBO update ownership/removal.
Next validation is one bounded game-test/profile-build batch. Native SSBO
allocation/offset bytes, custom-color/rotation/invalid-UV branches, GPU sampling,
mesh draws/state/framebuffer and full startup/menu/world/provider/SDL/release
acceptance remain open. This subset cannot activate complete graphics routing.

The [first bounded validation](validation-g1-ssbo-routing-2026-09-30-01.md)
passed 33 game cases and a clean profile build. Six mesh targets installed,
and the CPU face layout/quad UV/geometry/flags fixture passed. Native offset
bytes, remaining packing branches and real draw/lifetime acceptance remain open.
