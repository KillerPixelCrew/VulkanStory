# Retained decal pool scope

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`GameGraphicsAdapter.Decals` migrates the retained scoped decal multi-draw. The
decal/block atlas handles come from the existing program binding records. Native
mesh cache, vertex layout, viewport, primary/motion slots, standard blending,
depth test/write and unculled state follow the retained contract. Open motion
slots replace rather than blend. Missing native prerequisites use the existing
stated multi-draw.

`DecalConsumerPatches` replaces the single original MeshDataPool.Draw call with
a scope wrapper. Original and incoming IL must retain that exact typed anchor.
The original pool still performs culling and chooses ranges; it needs no new
public member or injected model field. The wrapper closes the scope in finally,
including exceptions. The adapter's existing mesh dispatch chooses native decals
only inside this scope, under the original decals program.

This is a graphics-scene-decals subset. Terrain/clouds, entities/hands and dense
motion producers, shader-mode publication and remaining host factories still
prevent complete scene/startup registration. No code/guard has been compiled or
run here, and no decal/world/provider result is accepted.
