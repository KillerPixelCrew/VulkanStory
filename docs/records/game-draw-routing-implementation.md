# Generic original-platform mesh draw routes

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

Four original platform operations join mesh routing: ordinary RenderMesh,
explicit-SSBO multidraw overload, RenderMeshInstanced and RenderFullscreenTriangle.
The original four-argument multidraw helper keeps calling the five-argument
method. Mesh group now has ten platform targets plus VAO disposal.

The adapter uses retained StatedDraw.Record and the transplanted state, with
current program/target/pass held in mod-owned objects. It preserves ordinary
mesh validity errors, quantity gating and retained draw/refusal counting; an
unknown program records nothing and refused programs report once. Fullscreen
draws use no mesh buffers. SSBO layout/index selection remains in the retained
mesh/native-draw code. Shader/sampler binding state is shared with the earlier
routes; state setters and framebuffer integration must populate the rest.

Provenance: baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`,
VulkanClientPlatform.Meshes generic branches, NativeWorld.RecordStatedDraw and
retained StatedRenderState/StatedDraw. Boundary/state ownership changes are
adapters; native draw/pipeline/descriptor algorithms stay retained. Preserve
inherited notices/license scope. Debug draw-stack UI/logging, pass-context
restoration and dedicated GUI/cloud/standard/chunk/decal/particle/other scene
routes are still pending and remain required for the full port.

This generic route does not substitute for the requested retained specialized
features. No session activates it yet. The existing fixture expands binding/
installation and checks active ownerless draw rejection plus prefix removal.
It has not run after this increment. Next validation is one bounded game-test/
profile-build batch. Successful draw state/target selection, sampled shader and
mesh pixels, all dedicated/temporal/provider routes, complete startup/menu/world,
SDL/control/release acceptance remain open.

The [first bounded validation](validation-g1-draw-routing-2026-09-30-01.md)
passed 33 game cases and a clean profile build. Ten mesh signatures installed;
ordinary draw ownership/removal and ownerless rejection passed. Successful
generic pixels/state/accounting and dedicated scene routes remain open.
