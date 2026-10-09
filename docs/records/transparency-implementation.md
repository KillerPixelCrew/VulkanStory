# Retained OIT resources and transparent merge

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`GameGraphicsAdapter.Oit` migrates the retained reveal texture, three-layer
accumulation array, attachment setup, nearest/clamped sampling, six draw-buffer
selection, multiply/additive factors and white/zero clears. Texture IDs and the
current transparent framebuffer belong to the adapter instead of injected game
statics. Units 6/7 and the original water/cloud sampler assignments are retained.
Failure disables the OIT helper for the session, releases resources and restores
ordinary transparent state and the previous shader where possible.

Transparent composition consumes the original transparent color/reveal/glow and
the owned OIT inputs. The native pass keeps the original viewport, world color
slots and source-alpha blend; its temporal motion slot accumulates additively.
The stated fallback uses the same shader/inputs and restores the world mask and
motion blend state. This does not certify dense motion producer coverage.

`TransparencyConsumerPatches` routes original BeforeOIT/AfterOIT callbacks,
BeforeOIT disposal and MergeTransparentRenderPass. The original before-renderer
API association is cached and guarded. Disposal only frees resources owned by
that API, avoiding a late old-world dispose releasing another world's targets.
Framebuffer rebuilding and session shutdown also release owned OIT resources
while the device is alive.

The group is a `graphics-scene-transparency` subset. Terrain, entities/hands,
particles/decals, sky/clouds, liquid/other motion writers, compiled scene-shader
mode publication and the remaining host factories must still join the complete
scene/startup profile. No code has been compiled or installed in a running game
for this increment, and no OIT/transparent/motion pixels are accepted.
