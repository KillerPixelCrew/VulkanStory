# Retained animated entity draw

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`GameGraphicsAdapter.Entities` directly migrates the retained entity/registered
hand program eligibility, mesh layout/pipeline cache, sampled textures and
per-attachment blend/write masks. Registered entityanimated/shadow programs use
the native draw; other/custom-sampler programs use the existing stated mesh
route. Color/glow, G-buffer replacement and open/closed motion masks retain the
original rules. Existing animation UBO uploads are consumed rather than replaced.

`EntityConsumerPatches` substitutes the single mesh call in the original
RenderAPIBase.RenderMultiTextureMesh loop. Original texture binding, mesh iteration
and uniform/animation work still execute. The current sampler's captured texture
and all other declared bindings are supplied to the native draw. Original and
incoming IL must retain the exact typed anchor. No injected platform method or
new game field is used.

This is a graphics-scene-entities subset. Remaining direct hand/item routes,
previous bone/model/warp histories and the complete dense motion producer chain,
shader-mode publication and remaining host factories still need integration.
No new code/guard has been compiled or run, and no entity/hand/world/provider
result is accepted.
