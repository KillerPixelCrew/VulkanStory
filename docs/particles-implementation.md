# Retained native particle pools

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`GameGraphicsAdapter.Particles` migrates native cube and quad instanced draws.
Cube particles retain standard blending, depth test/write and replacement
motion blending while the motion window is open. Quad particles require the
transparent target and retain its current OIT draw-buffer/blend contract, depth
testing without depth writes and declared sampler inputs. Both use the owned
mesh handle/layout, current viewport, native mesh pipeline cache and existing
stated instanced fallback. Native passes end on exceptional paths.

`ParticleConsumerPatches` substitutes the two original instanced draw calls in
SystemRenderParticles.Render(int,float). Original and incoming IL must retain
both exact typed anchors. Pool updates, shader uniforms and caller state remain
in the original renderer. The patch group is a graphics-scene-particles subset.

Terrain/clouds, entity/hand/decal and dense motion producer routes, shader-mode
publication and remaining host factories still prevent complete scene/startup
registration. Neither new code nor guards have been compiled or executed here.
No rewritten particle/world/provider result is accepted.
