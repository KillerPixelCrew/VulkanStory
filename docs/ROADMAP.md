# Roadmap

Current release: [v0.1.1-dev](https://github.com/KillerPixelCrew/VulkanStory/releases/tag/v0.1.1-dev) (Windows x64, Vintage Story 1.22.7).

## Done

- Vulkan renderer, SDL3 window and input
- TAA, render scale, SSAO / GTAO
- Upscaling: DLSS, FSR 3.1, FSR 4 (INT8 path on NVIDIA and Intel), XeSS
- Stable geometry boundaries against sky and clouds with TAA and upscaling
- Frame generation: DLSS-G (up to 6×), FSR 3 FG, XeSS-FG
- Low latency: Reflex / PC Latency, Anti-Lag, XeLL
- Options menu integration
- Controllers (remapping, radial menu, glyphs, analog movement + server companion), touch
- Screenshot, timelapse and AVI capture
- Render-pass extension points for other mods
- Windows install, update and removal
- Loading screen stays up until the first world frame is complete
- Shaders ship as one container, `shaders-vk.pak`
- **PERF-01: Smoothness ports** (ideas from Optimum, rebuilt as optional Harmony patches; each has a switch on the Performance settings page, all on by default)
  - Culling: skip foliage in the far shadow cascade, entity shadow distance cull, the occlusion culler's rays precomputed into one shared ray tree, particle and dropped-item distance gates
  - Chunk streaming: skip unchanged upload-queue sorting, tessellator mesh-part pool, parallel raw chunk reads in singleplayer
  - Entities: allocation-free animation checks, falling-block mesh fix, villager/trader outfit shape cache with outfit textures pre-inserted gradually after world load
  - Worldgen (singleplayer): the game's own per-pass worldgen workers, made safe with column footprint leases, a shared Vegetation/sunlight lock, per-generator locks and atomic entity ids, with a fallback to the chunk thread alone when other mods touch worldgen or a worker fails
- **PERF-03: GPU occlusion culling** (two switches on the Performance settings page, on by default)
  - Terrain chunk parts and entities hidden behind opaque terrain are left out of the main camera's draws, tested against a max-depth pyramid of the opaque terrain from a recent frame
  - Test boxes grow by the camera's movement since that depth was captured; boxes outside the captured view or crossing its near plane are always drawn; players are never culled
  - Shadow passes keep their own culling
- **PERF-04: Additional Optimum ports** (runtime controls on the Performance settings page)
  - Inventory render-info reuse, wind sample caching without slowing smoothing, dynamic-light scans and nearest selection, animated-block LOD without freezing distant machines, entity retessellation budgeting, BFS visibility, pathfinding node pooling with independent returned paths, entity light batching and SIMD frustum tests with full coefficient refresh and original precision.
  - Outfit animator caching with separate mutable poses per entity, parallel Terrain columns with independent workspaces and a reusable worldgen worker pool, dedicated chisel LOD pools with live distance control, bounded Vulkan world-map pages with a compressed disk cache, and physics collection reuse with reader leases and activation capacity batching without delaying simulation.
- **PERF-05: Performance inspection harness**: bounded EventPipe captures from `.vulkanstory profile start [seconds]`, stop/status; per-thread call stacks, sampled allocations, frame/system timings, searchable offline flame graphs and Speedscope export. GPU execution time is not measured.

## Known defects

- **REN-08:** TAA / temporal reconstruction sometimes drops completely during play.
- **REN-09:** Allocation crashes and severe stalls during framebuffer rebuilds, with device-local use above its reported budget and nearly 20 GB process RAM reported. The reserved workspace covers full-resolution scene/OIT, TAA and GTAO targets before geometry growth; terrain pools share immutable quad indices, and abandoned game meshes retire through the GPU timeline. The reported lag and RAM growth remain open.
- **REN-10:** Additional Vulkan correctness defects in supported attachment, shader translation, mod API and provider transition paths.

## Planned

- **PERF-06: Profile-driven optimizations (in progress)**
  - Recurring renderer costs: reusable occlusion hierarchy storage with a live switch, descriptor comparisons without handle boxing, and render/upload synchronization changes grounded in lock ownership.
  - Loading and streaming: move outfit metadata discovery out of budgeted frame ticks, cache translation/search resolution with language and asset invalidation, improve bounded mesh-part reuse and immutable shape parsing, and reduce audio decode growth/copies while preserving runtime unmute.

- **PACK-01: Shader packs**
  1. Pack contract: replaceable programs, provided uniforms/samplers, readable and writable attachments, pass order, options. Decide which internal layouts become public API.
  2. Loader: `shaderpacks/` folder, pack selection and per-pack options in the Options menu, hot reload; compiled at runtime with the shipped shaderc.
  3. Per-program fallback to the built-in shaders on compile/link errors, with in-game error reporting.
  4. Pipeline cache keyed by the active pack.
  5. Pack-declared composite/deferred passes in the frame graph.

- **VR-01: OpenXR VR**
  1. OpenXR session and swapchains on the existing Vulkan device; headset presentation alongside or instead of the SDL window.
  2. Stereo rendering (two views or multiview) with per-eye projection/view from head tracking.
  3. Per-eye temporal state: TAA history, motion vectors and jitter; per-eye or disabled upscaler and frame-generation contexts where the SDKs don't support VR.
  4. Motion-controller input through OpenXR actions: movement, look, interaction, hotbar, inventory, with bindings for common controllers.
  5. GUI on world-space panels, plus comfort options (snap/smooth turn, vignette, seated/standing).
  6. Full-body pose from head and hand tracking via inverse kinematics on the player model.
  7. Server companion mod that streams head/hand poses so other players see VR players' tracked movement, with fallback to normal animation for players without it.

## Deferred

- **GL-02 / GL-03:** OpenGL compatibility adapters and profiles for other mods, once a target mod is chosen.
- **TEST-01:** Tests.
- **LINUX-01:** Linux release.
