# Concrete terrain shader and sampler services

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Test work remains deferred until integration is finished.

`GameSessionServices` no longer requires external terrain-readiness, reload or
LOD-bias callbacks. The session binds those operations to the owned graphics and
settings state and the original client.

- Readiness requires linked, undisposed terrain/topsoil programs and no active
  registered shader load. Deferred provider target rebuilds retain that gate.
- Reload uses the original shader registry and active client's shader listeners,
  covering registered content programs as well as the default terrain programs.
  Failed reloads disable published motion/AO modes and motion coverage.
- Effective terrain bias preserves the retained formula: logarithmic reduced
  render-scale bias plus the TAA mip bias, overridden by an active SR plan's
  clamped provider bias.
- At the original world-loop entry, cached client/chunk bindings supply current
  atlas IDs. Native texture and terrainTex/terrainTexLinear sampler parameters
  update together, including new atlases and shader-recreated samplers. Unchanged
  bias is skipped; untouched zero-bias resources retain their defaults.
- SR/settings callbacks apply the same owned calculation and reload completion
  reapplies it to the newly created samplers. No injected game LOD method or
  Optimum configuration is required.

Provenance: retained `ChunkRenderer.ApplyOptimumTextureLodBias`,
`ShaderRegistry.ApplyOptimumTerrainSamplerLodBias`, and
`OptimumConfig.EffectiveTerrainLodBias` at baseline
`386e0d05386d0b228b439d09aeca851428f7bbf3`, with original client reload listeners
from the official snapshot. Provider algorithms and shader mathematics stay intact.

Platform/controller callbacks, other session service construction, complete
scene/profile registration and packaged runtime delivery remain unfinished.
No live shader reload, atlas replacement, mip selection or enabled provider
execution has been accepted by this source increment.
