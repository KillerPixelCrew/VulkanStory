# Retained native mesh cache and sky dome

Date: 2026-09-30. Implementation only; no builds, tests, probes, packages or
game runs. Test work remains deferred until integration is complete.

`GameGraphicsAdapter.NativeMeshPass` directly migrates the retained mesh pass
cache and fixed-state comparison. Program identity, target formats, vertex
layout and depth/cull/topology/polygon/line/blend state still determine reuse.
Uniform/sampler placements are resolved when the adopted pipeline changes.

`GameGraphicsAdapter.Sky` migrates the sky dome mesh draw. It uses the owned mesh
handle/layout, current target and viewport, sky/glow textures, retained opaque
fixed state and per-draw model-view write. The original shader's other uniforms
remain in its backend record. Missing native prerequisites use the existing
stated mesh route. The native pass ends on exceptional paths as well.

`SkyConsumerPatches` replaces the single RenderMesh call in the original sky
renderer, preserving its sun/fog/time uniforms, player-following matrix work
and projection/state restoration. Original and incoming IL must retain exactly
one typed draw anchor. Client association and internal sky/glow IDs use cached,
guarded field accessors, without changing the game assembly.

This is a `graphics-scene-sky` subset. Other sky/cloud/celestial, terrain,
entity/hand, particle/decal and dense motion routes must still join the scene
group. Neither this code nor its guards have been built or executed here.
No rewritten sky pixels or complete world/provider operation are accepted.
