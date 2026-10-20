# Plan: renderer-owned scene submission

Goal: VulkanStory decides what to draw. The game's terrain, entity and particle
renderers stop issuing one draw per pool, sub-mesh or batch. They only report state
changes (chunk part added or removed, entity transform/pose/uniforms, particles
written). VulkanStory keeps that state resident on the GPU, culls it per pass and
eye on the GPU, and records a handful of indirect draws per pass.

Everything else keeps today's per-draw path (the "compat lane"): sky, clouds,
weather, decals, block-entity renderers, held items, GUI, mods, direct-GL mods, and
any renderer a foreign Harmony patch touches. It is not slow in practice because it
is not where the draw volume is.

## Why the current approach cannot get there

All scene draws reach four patched platform methods
(`MeshConsumerPatches.cs:17-33`): `RenderMesh`, multi-draw `RenderMesh`,
`RenderMeshInstanced` and `RenderFullscreenTriangle`. The game's own loops decide
what to draw and call them once per pool, entity sub-mesh or particle pool. The
compute features added on top of that seam (entity records, run combining,
producer hoisting, per-frame terrain records) all still pay a CPU cost per object
per draw:

- Terrain: the game walks every pool part each frame
  (`MeshDataPool.FrustumCull`), VulkanStory captures the accepted ranges
  (`GpuTerrainDraws.cs:77-161`) and appends 12-word records per pool draw
  (`VulkanDevice.TerrainDraws.cs:136-157`). The part data is rebuilt every frame
  although it changes only when a chunk is re-meshed. Shadow cascades still use
  CPU-written `DrawMulti` (`GameGraphicsAdapter.Chunks.cs:36-38`).
- Entities: one `RenderMultiTextureMesh` per sub-mesh
  (`EntityConsumerPatches.cs:17`), uniforms set through the GL uniform route
  (`ShaderUniformRoutes.cs:16-35`), bones through the `Animation` UBO, then
  `BeginNativeDraw` per draw (`VulkanDevice.Native.cs:622-842`) with barriers,
  set-2 snapshots, push resolution and producer dispatch decisions.
- Particles: already one instanced draw per pool. Their remaining cost is the GPU
  simulation's CPU-side world capture and confirmation, not submission.

Measured (r24, 5d81d0d, headless foggy village, three runs each): CPU frame work
9.6-9.8 ms with the GPU features on vs 6.9-7.0 ms off; GPU 19.5-19.8 vs 18.5 ms.

## Ground rules

1. Exactness is defined at the scene boundary: same game state in, same pixels
   out. Rendering does not re-confirm intermediate GPU steps on the CPU.
   Gameplay-affecting simulation (particle collisions, callbacks, random draws)
   is out of scope for this plan.
2. One resident store per system, with a concrete layout. No generic scene graph,
   ECS, render-graph rewrite or new abstraction layer.
3. Native shaders only. The resident path uses instance-indexed variants written
   in `shaders/native`. Programs that only exist through the rewriter (third-party
   shader overrides, `NativeShaders=false`) stay in the compat lane. No SPIR-V
   rewriting.
4. Each phase ships behind its own Performance-page switch, defaults off until its
   acceptance gate passes, and then replaces (deletes) the per-draw machinery it
   supersedes. No phase leaves two parallel GPU paths for the same thing.
5. A phase is done when the measurement says so, not when counters move.

## Phase 0: acceptance harness (small, first)

Everything below is judged with the same batch, extended once:

- Performance: all-on vs all-off, three repeats each, steady-tail CPU frame work
  and GPU span from complete timestamps (already in `compute-wave-r24`'s
  `validate.ps1`).
- Draw volume: add one counter, `sceneDrawCommandsRecorded`, per pass category
  (terrain, entity, particle, compat), so each phase shows the submission drop
  directly.
- Pixel equality: a deterministic variant of the scenario (debug fountains and
  weather off, fixed time of day, fixed camera) captured with the resident path
  on and off at the same tick; attachments compared with `scripts/dev/ssim.py`.
  Terrain and entity opaque/depth must be bit-identical; TAA output within the
  existing SSIM threshold.
- Vulkan sync validation in native and translated modes (translated mode checks
  that the compat lane is intact).

## Phase 1: resident terrain

Terrain is first: it is the largest draw volume, and its geometry is already
resident in the game's own `MeshDataPool` SSBO meshes
(`Core/MeshManager.cs:347-359`, shared quad indices `:659-746`). Only the
per-frame CPU walk and record upload have to go.

### State

`TerrainScene`, owned by the renderer, one per world:

- Part table (device-local, grow-by-doubling): per part `{pool id, render pass,
  atlas, first index, index count, bounding sphere (stock FrustumCullSphere),
  chunk index}`. 32 bytes.
- Updated on the existing seams only: part insert (the stock `InsertAt` path and
  GPU meshing's `Reserve`, `GpuChunkMeshing.Pools.cs:25-64`), part removal
  (`MeshDataPool.RemoveLocation`, already hooked at `GpuTerrainDraws.cs:54`), pool
  disposal (`MeshConsumerPatches.cs:151-171`). Updates are batched into one upload
  per frame.
- Per-frame input (small): camera and frustum per pass and eye, shadow cascade
  matrices, and the stock chunk visibility result as a bitset over loaded
  chunks.

### Culling

One compute pass per (pass, eye or cascade) over the part table, reproducing
the stock CPU rules exactly:

- Frustum sphere test with the stock cull mode of that pool call
  (`EnumFrustumCullMode` per call site in `ChunkRenderer`, 14 sites,
  `ChunkConsumerPatches.cs:21-22`).
- Stock chunk visibility (`ChunkCuller.CullInvisibleChunks`) stays on the CPU,
  because it is a per-column flood fill and already patched
  (`ChunkVisibilityWalk.cs:125`). Its result is uploaded as the bitset; no GPU
  port.
- Stock view-distance and LOD selection, ported from the CPU `IsVisible` path.
- Occlusion (PERF-03) only for main-camera opaque passes, against the existing
  pyramid, with today's conservative rules.

Output: per pool an ordered compacted command list and a count, consumed by
`vkCmdDrawIndexedIndirectCount` (or `vkCmdDrawIndexedIndirect` with the GPU
count written into the command when the feature is missing). The existing
`draws.comp` cull/scan/scatter kernel is reused; its input becomes the resident
table instead of per-frame records.

### Submission

The 14 `MeshDataPoolManager.Render` call sites in `ChunkRenderer`, already
transpiled to `RenderPool` (`ChunkConsumerPatches.cs:108`), stop iterating pools.
Each becomes one VulkanStory call: bind the pass pipeline once, then one
indirect-count draw per pool of that pass. The liquid-motion replay
(`LiquidMotion.cs:67-98`) uses the same compacted lists. Shadow cascades go
through the same path, which removes the CPU-written `DrawMulti` for shadows.

Pools touched by a foreign patch on `ChunkRenderer`/`MeshDataPool`, or pools
whose program is not native, keep the current path.

### Keeps and deletes

- Keeps: `draws.comp` kernel (input changed), occlusion pyramid, GPU chunk
  meshing (it writes into the same pools and updates the part table), CPU chunk
  visibility walk, `SimdFrustumTests` for the compat lane.
- Deletes: `GpuTerrainDraws` per-frame capture and `BoundsFor` pairing, per-frame
  terrain records in `VulkanDevice.TerrainDraws.cs`, `TerrainOcclusionCull`'s
  per-part `IsVisible` postfix for resident pools, the `GpuTerrainDrawPreparation`
  switch (replaced by the phase switch).

### Gate

Terrain draw commands per frame drop to roughly one per (pool, pass); terrain
opaque/depth bit-identical; CPU frame work below today's all-off baseline with
everything else unchanged; GPU span not higher than all-off.

## Phase 2: resident entities

### Scope

Exactly today's "supported stock" set (`EntityVisibility.cs:34-59`): non-player
entities whose renderer is a game, VSEssentials or VSSurvival `EntityShapeRenderer`
subclass or `EntityItemRenderer`, with no foreign patch on `BeforeRender`,
`DoRender3D*`, `OnBeforeRender` or `LoadModelMatrix`, drawn with the native
`entityanimated` / `shadowmapentityanimated` programs. Everything else, including
players, held items, inventory and dropped/falling special cases that fail the
gate, stays in the compat lane.

### State

`EntityScene`:

- Instance table, one row per resident entity sub-mesh: mesh handle, texture
  slots, current and previous model matrix, the per-draw uniform block exactly as
  `entityanimated.glsl`'s program record lays it out (`OptimumProgram`, `:69`),
  pose offset, flags. The row layout is the existing program-record struct, so the
  shader arithmetic is unchanged.
- Pose ring: per frame one storage buffer holding every resident entity's bone
  matrices (current and previous). `entityanimated.glsl` already reads bones from
  a storage block (`:120-135`); the instanced variant indexes it with the row's
  pose offset.
- Rows are written where the stock renderer computes them. For resident entities,
  `DoRender3DOpaque`/`DoRender3DOpaqueBatched`/shadow rendering run their stock
  matrix, light and pose logic, but the uniform route and the `Animation` UBO
  update write into the entity's row and pose slot instead of a draw. The
  `RenderMultiTextureMesh` call for that entity becomes "row is current this
  frame" and records nothing.
- Animation stays on the CPU, as the game computes it for gameplay attachments
  anyway. Bones are uploaded as computed: one contiguous write per frame.

### Culling and submission

One compute pass per (pass, eye or shadow cascade): frustum test on the stock
entity bounds, the existing conservative occlusion rules for the main camera,
compaction into per-(pipeline, mesh) indirect commands with `firstInstance` =
row. Then per pass: bind the pipeline once, one indirect-count draw per distinct
mesh and pipeline. Skinning and vertex warps stay in the vertex shader as in the
stock shader.

### Keeps and deletes

- Keeps: occlusion pyramid and the conservative motion margins, rigid/animated
  motion-history identity (it maps to row ids), the compat lane.
- Deletes, once the gate passes: the entity record SPIR-V rewrite
  (`Shaders/EntityRecordShader.cs`), per-slot record arenas and heaps
  (`VulkanDevice.EntityRecords.cs`), entity geometry packing arenas
  (`VulkanDevice.EntityGeometry.cs`, `entity-pack*.comp`), indirect-count run
  combining and its break counters (`VulkanDevice.EntityDraws.cs`), producer
  hoisting and the pre-graphics producer recording for entities, per-draw
  `GpuEntityDraws` visibility batches, and the shared-deformation, joint-pose and
  world-warp compute paths for resident entities (`VulkanDevice.Deformation.cs`,
  `JointPoses.cs`, `WorldGeometry.cs`), together with their switches. RT-01 can
  add a skinning compute pass over the same pose ring when it needs world-space
  vertices; it does not need to exist before then.

### Gate

Entity draw commands per pass drop to roughly the number of distinct meshes;
entity opaque/depth bit-identical, motion vectors equal; CPU frame work with
phase 1 and 2 on below all-off; no compat-lane regression in translated mode.

## Phase 3: particles (decision, then cleanup)

Particles already submit one instanced draw per pool, so there is nothing to
restructure for submission. What remains is a measured decision:

- The GPU particle simulation costs CPU (world capture and confirmation at
  `TickNow`, the game's lighting lock) and its gain is not measured as positive.
  Recommendation: default `GpuParticleSimulation` off, keep the code until a
  dedicated particle-heavy measurement says otherwise; if it stays negative,
  delete it.
- Particle geometry and visual evolution in compute measurably save CPU (r17,
  r21: turning `GpuParticleGeometry` off costs 2-6 ms). Keep them.

## Phase 4: chunk meshing (measure, then keep or drop)

GPU chunk meshing is exact (equality probe, 0 mismatches) and no longer blocks,
but it only takes re-tessellation after the first mesh. Measure edit-heavy play
(block placing, explosions) with it on and off. Keep it only if it wins there;
it is independent of phases 1 and 2 because it writes into the same pools.

## Phase 5: stereo and ray tracing hooks (no new work, verify)

- Stereo: both phases cull per eye from the same tables, so VR-01 item 6 is a
  second cull dispatch per pass, not a second scene walk.
- RT-01: the part table and the instance table plus pose ring are the inputs
  acceptance structures need. Nothing is built for RT in this plan.

## Order and size

| Phase | Main files | Size |
|---|---|---|
| 0 Harness | `artifacts` scripts, one counter | small |
| 1 Terrain | new `TerrainScene` (renderer), `ChunkConsumerPatches`, `GameGraphicsAdapter.Chunks`, `draws.comp` input, deletions in `GpuTerrainDraws`/`TerrainDraws` | medium-large |
| 2 Entities | new `EntityScene` (renderer), `entityanimated`/`shadowmapentityanimated` instanced variants, `EntityConsumerPatches`, uniform/UBO routes for resident rows, large deletions | large |
| 3 Particles | default switch, possible deletion | small |
| 4 Meshing | measurement only | small |

Phase 2 starts only after phase 1 passes its gate. Phase 1 and 2 work is not
split across concurrent workers in the same worktree; each phase has one owner.

## What this plan does not do

- No generic renderer framework, ECS, or declared render-graph rewrite.
- No GPU port of the game's chunk flood-fill visibility or entity animation.
- No resident path for rewriter-only shaders, mods, GUI, sky/weather or
  block-entity renderers.
- No new compute features until phases 1 and 2 show a measured CPU and GPU gain
  over all-off.

## Risks

- Stock culling parity for terrain (view distance, LOD, cull modes per call
  site). Mitigation: port from the decompiled `MeshDataPool.FrustumCull` and
  `ModelDataPoolLocation.IsVisible`, and gate on bit-identical captures.
- Entity uniform coverage: every uniform the stock entity renderers set must land
  in the row. Mitigation: the row is the native program record, which already
  lists every uniform of `entityanimated.vsh/.fsh`; any uniform written outside
  the record for a resident entity drops that entity to the compat lane for the
  frame and is counted.
- Mods patching `ChunkRenderer`, `MeshDataPool` or entity renderers: the existing
  foreign-patch detection sends them to the compat lane.
- Ordering with compat-lane draws in the same pass (decals, block entities): the
  resident draws are recorded at the same stage position the stock call site had,
  so stage order is unchanged.
