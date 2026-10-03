# Original platform mesh resource routing

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

The mesh subset adds official AllocateEmptyMesh, UploadMesh, UpdateMesh and
DeleteMesh prefixes. Original VAO/MeshRef objects carry retained renderer handles,
index metadata, draw mode and persistence; custom allocation and MeshData payload
use the existing neutral GameMeshLayout adapters. Ownership lives in a mod-owned
ConditionalWeakTable, with checks before updates/releases.

DeleteMesh still calls original VAO.Dispose once. That method retains its
disposed guard/base.Dispose, with only the expected GL.DeleteVertexArray call
replaced by owner-routed deferred mesh deletion. Active disposal verifies
ownership and zero GL buffer fields before the original cleanup branches can
reach native GL. This is required because new VAOs carry renderer handles, not
individual GL buffers. Dormant behavior remains original. Mesh fields and
allocation/upload algorithms are retained rather than replaced by new resource IDs.

Provenance: baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`, retained
VulkanClientPlatform.Meshes resource bodies and original VAO.Dispose. These are
boundary/ownership adapters; preserve inherited license/notices.

An unrun fixture resolves/installs the four platform targets and VAO disposal,
checks actual patch ownership/removal, and rejects active ownerless upload before
the original body. It does not construct/finalize a native VAO or upload GPU data.
Next validation is one bounded game-test/profile-build batch. SSBO face packing,
draw/state/framebuffer routes, direct mesh-call consumers, actual native allocations/
updates/retirement and complete startup/menu/world/provider/SDL/release remain open.
The incomplete graphics-meshes subset cannot activate the required graphics-api group.

The [first bounded validation](validation-g1-mesh-routing-2026-09-30-01.md)
passed 32 game cases and a clean profile build. Four official mesh signatures
and the VAO original/incoming disposal anchor matched and installed; ownership/
removal and ownerless upload rejection passed. Native mesh behavior remains open.
