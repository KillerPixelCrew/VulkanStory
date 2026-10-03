# Liquid velocity redraw — source integration

Updated 2026-10-01. Implementation only; no build, test, package or game run.

## Migrated behavior

- The original world-loop scene-motion boundary invokes the liquid redraw before
  sky/cloud motion, under the original transparent-render flag.
- `GameGraphicsAdapter.LiquidMotion` owns the retained liquid-motion program and
  uses the original `ChunkRenderer` liquid pools (render pass 4), camera, matrix
  stack, culling and per-pool origin/dimension uniform updates.
- The redraw writes only the primary motion attachment, with blending disabled,
  depth testing/writes enabled, culling disabled and the retained 0.3 reactive
  value. The terrain scope now preserves the motion writer's exact attachment
  mask instead of widening it to include scene colors.
- Current warp uniforms follow the original shader Use path. Previous warp,
  camera displacement, unjittered projection and camera matrix come from the
  session-owned temporal state. SSBO mode is temporarily disabled via a cached
  original-field accessor because the liquid pool uses the ordinary mesh layout.
- Shader identity, SSBO mode, matrix stack, blend and motion window are restored
  after drawing, including exceptional exits. Shader reload and session teardown
  retire the owned program and its terrain pipeline cache entry.

## Provenance and boundaries

The draw sequence comes from the retained
`build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs`
`RenderLiquidMotion` at baseline
`386e0d05386d0b228b439d09aeca851428f7bbf3`.
`Shaders/chunkliquidmotion.vsh`, `.fsh` and `Shaders/vertexwarp.vsh` are unchanged
copies of the retained shader sources. The previous-state warp include is loaded
from the embedded copy; its noise/flag includes resolve through the game's asset
manager. Recursive or missing includes and compilation/link failures disable this
owned program with a diagnostic until reload.

No injected game method, modified game assembly or platform subclass is needed.
The original shader identity is used only to connect existing mesh-pool uniforms
to the routed backend API.

## Remaining work

Complete scene/profile registration, compiled motion/AO shader-mode publication,
instance producer attachment and remaining content renderers are still required.
Dense motion coverage is not published, and this source change does not establish
valid SDK inputs or visible liquid pixels. Native compilation and game output
remain unverified; tests remain deferred until integration is finished.
