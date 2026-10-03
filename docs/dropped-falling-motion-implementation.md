# Dropped item and falling block motion

Updated 2026-10-01. Source integration only. No build, test, probe, package or
game run; tests remain deferred until integration is complete.

`DroppedFallingMotionConsumerPatches` adds an Essentials assembly subset for
`EntityItemRenderer.DoRender3DOpaque` and
`ModSystemRenderFallingBlocksFast.OnRenderFrame`. The original final
`IRenderAPI.RenderMultiTextureMesh` calls feed the retained standard motion
history before drawing and restore their owned motion windows in `finally`.

- Dropped items use their renderer as the stable entity identity, the actual
  drawn mesh as shape identity, and the original final `ModelMat` float array.
  Shadow draws do not open or advance this motion history.
- Falling blocks use each actual block entity as identity, the drawn mesh as
  shape identity, and the shared renderer's final `ModelMat.Values`. A checked
  mesh-field/local-load pattern carries the entity directly from the original
  draw operand into the wrapper. Blocks sharing a renderer or mesh keep separate
  histories; no enumeration-slot identity or injected game field is needed.
- History updates require the compiled standard writer and active temporal
  jitter/motion window. Original visibility, transform/tumble, lighting, particle
  effects, texture selection, mesh generation/caching and draw bodies remain.
- Typed original matrix/mesh/method bindings and one original/incoming draw
  anchor per method guard the subset. The falling identity extraction also
  checks the mesh field, local load, sampler argument and default texture unit.

Provenance: retained Essentials sources `EntityRenderer/EntityItemRenderer.cs`
and `Entities/EntityBlockFalling.cs`, with the matching runtime motion patches at
baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`. The runtime patch's unrelated
distance optimization is not included in this renderer port.

The subset is dormant until complete scene/profile registration. Remaining
content/motion routes and complete per-frame coverage still prevent declaring
the frame motion-valid. No dropped/falling pixels, Harmony installation, shader
behavior or SDK motion input has been accepted by this source increment.
