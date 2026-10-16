# Roadmap

Current release: [v0.1.5](https://github.com/KillerPixelCrew/VulkanStory/releases/tag/v0.1.5) (Windows x64, Vintage Story 1.22.7).

## Done

- Vulkan renderer, SDL3 window and input
- TAA, render scale, SSAO / GTAO
- Upscaling: DLSS, FSR 3.1, FSR 4 (INT8 path on NVIDIA and Intel), XeSS
- Stable geometry boundaries against sky and clouds with TAA and upscaling
- Frame generation: DLSS-G (up to 6×), FSR 3 FG, XeSS-FG
- Low latency: Reflex / PC Latency, Anti-Lag, XeLL
- Options menu integration, with clipped scrolling that moves labels, controls and their backgrounds together, and bounds checks for saved Graphics dropdown selections
- Controllers (remapping, radial menu, glyphs, analog movement + server companion), touch
- Screenshot, timelapse and AVI capture
- Render-pass extension points for other mods
- Core mod API resilience: selected shader stages/includes, compiled motion-output checks, camera/depth SDK motion, shader-certified animated pose/model history, rigid entity and inventory-slot transforms with stable identities, managed dependency filenames, repeated priority hotkey registration, metadata-only outfit discovery, owner-scoped legacy Harmony cleanup, and map renderer recreation on shader reload.
- Windows install, update and removal
- **REN-11: AMD startup compatibility:** process-only relaxed continuation validation prevents the Windows CET rejection of legacy AMD context restores; hardware shadow stacks and official game files are preserved.
- Automatic Windows crash reports from the early loader: managed exceptions, native fast-fail parameters, thread stacks and bundled logs/settings.
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
- **PERF-06: Profile-driven optimizations**
  - Recurring costs: reusable occlusion hierarchy storage, descriptor comparisons without handle boxing, short chunk-map membership snapshots with visibility work outside the shared lock, indexed flood-fill traversal, and private coordinate reuse in the stock FluffyClouds updater. Optional algorithms have live controls.
  - Loading and streaming: background outfit metadata preparation, translation resolution caching with language/reload invalidation, bounded mesh-part capacity classes, independent static-shape templates for fruiting bushes, and pooled PCM accumulation with the original decoder and runtime unmute behavior.

- **PERF-07: Drop and particle budgets** (ideas from RAM-Garbage-Collector, rebuilt as optional patches)
  - Earlier merging of settled matching drops in singleplayer, with bounded work, player ownership checks and the game's automatic stack/collectible rules.
  - Live particle density, per-spawn and per-pool limits for cubes, quads and mod providers. Defaults: 50% density, 256 per spawn and 8,192 per pool; effects marked to ignore user configuration are preserved. Both features have live switches on the Performance page. No forced GC.

- **GL-02 / GL-03: Managed OpenGL compatibility**
  - The shared bridge routes supported operations from both OpenTK GL namespaces, including depth/culling/color-mask state and float vector uniforms. Game-created shader programs can be queried through a borrowed GL view; uniform writes retain the game adapter's motion handling. OpenAL procedure lookups are excluded from graphics refusal.
  - Shared framebuffer names/views, independent read/draw bindings, attachment queries and output routing, mip attachment views/clears, and 2D readback with pack/unpack row layout are implemented. Temporary pass and motion scopes retain complete draw-buffer routing state.
  - Supported generic memory wrappers route through metadata-checked native table entries, sharing the same implementation as managed overloads. Native callback failures produce GL errors and diagnostics; table restoration and callback lifetimes are owned by the routing group.
  - Framebuffer blits/copies retain attachment mips/layers, output routing, clipping transforms and nearest/linear filtering. Same-image snapshots are bounded to the sampled rectangle. `DrawArrays` shares raw-mesh submission without changing element-buffer state; internal blit draws do not count toward occlusion queries.
  - Mutable 2D/3D definitions and uploads retain public names, sampler state and compatible mips, with timeline retirement and attachment invalidation. Volume depth/mips, typed unit bindings, row/slice unpack conversion and declared-level queries/completeness share the core resource path.
  - Indexed blend/color masks drive pipeline state and scissored/masked clear programs. Program-owned public uniform locations are shared by game/direct GL calls, with array ranges, retained metadata/value queries and direct program/int-vector writes. Core string/float queries, sampler binding and debug-group state are routed.
  - Timestamp/elapsed queries use real GPU ticks with timeline-owned pools, availability polling and blocking result reads. Occlusion queries share the sample-query ring; `Finish` waits on submitted GL work. Polygon offset enters cached dynamic raster state with separate mode enables.
  - User clip-plane equations, enable bits and queries belong to each GL context. Native and translated shaders emit enabled clipping distances at vertex/geometry output; clears and blits suspend that state for their internal draws. Shader-provided distances retain their equations. Legacy modelview remains identity while its matrix entry points are unsupported.
  - Compiled SPIR-V resource use controls frame-buffer descriptor binding, including private fields injected by the renderer. Geometry-stage resources and push constants use the enabled device stages. Shader array extents resolve preceding global scalar integer constants.
  - Native procedure exports use the same command implementation and shipped OpenTK ABI metadata, including wrappers that marshal string arrays and string outputs. GLFW and Windows native-library procedure loading are routed; Windows import hooks are restricted to inspected mod-library roots and retain module ownership. Shader-source, info-log and uniform-name parameters preserve the C UTF-8/pointer ABI with bounded outputs and optional length pointers.
  - Extension strings, indexed names and counts share one capability list. VAO names acquire identity on first bind; identity, attribute and pointer queries retain per-context state, including layouts after buffer deletion. `GL_ARB_vertex_array_object` is advertised by the common bridge.
  - Renderbuffers retain shared names, storage, attachment references and queried sample counts. Multisample images and pipelines carry matching sample counts; color resolves use Vulkan image resolves. Depth/stencil attachments, separate front/back stencil operations, masked/scissored stencil clears, constant blend colors and rasterizer discard enter the shared draw path. Depth clears preserve stencil contents. Pixel pack/unpack buffers use the retained buffer storage with checked offsets and pixel-store layout.
  - sRGB draw, clear and blit state selects compatible linear views, matching pipeline formats and pass transitions. Bindless descriptor keys include the image view. Full linear clears use rendering load operations, including outside a frame; disabled-sRGB color resolves use bounded UNORM storage. Image/view lifetimes share the texture timeline.
  - GL draws, scaled blits and partial color/depth/stencil clears outside a display frame use fenced graphics slots with shared descriptor, uniform and retirement ownership. These batches submit without presenting, advancing display counters or starting provider render markers.
  - Texture base/max levels select rebased sampling views and relative sampler LOD limits, including immutable-range clamps and incomplete mutable mip placeholders. Mip generation starts at the effective base; framebuffer attachments and blits retain physical image levels and completeness rules. Feedback copies retain the source sampling range.
  - Bound depth sampling can coexist with stencil writes through the core mixed depth/stencil layout. Attachment state, descriptors and barriers share that choice; depth writes while sampling remain refused. Immutable depth/stencil texture storage retains both aspects, with depth-only sampling views and depth readback.
  - Single-sample stencil scaling, mirroring and clipped blits use packed GPU buffers and a compute transform. Destination clipping retains the source mapping; only stencil is copied. Tiling bounds descriptor ranges and scratch storage, with shared fenced compute descriptors and deferred buffer retirement.
  - Multisample depth/stencil resolves use core sample-zero rendering resolves with explicit color-output-stage synchronization. Bounded scratch tiles preserve translated, mirrored and scissored mappings; depth-only/stencil-only copies preserve the other destination aspect.
  - Equal-sample color/depth/stencil mirroring preserves individual samples through bounded row/column image copies. Single-sample stencil broadcasts use packed byte transfers and stencil bit-plane draws, preserving destination depth without optional stencil-export support.
  - Mutable texture levels retain independent dimensions/formats and zero-size definitions. Matching sampling ranges are assembled on the GPU; attachments, uploads, readback, copies and mip generation retain logical level identity and deferred resource retirement.

## Known defects

- **Third-party shader conflict:** With PrismForge 0.1.0 and Simple Water Tweaks 1.3.0 together, RainReflections rejects PrismForge's replacement `final.fsh` and disables its effect. PrismForge's HDR code targets game 1.22.5 and does not apply on 1.22.7.

- **REN-08:** TAA / temporal reconstruction sometimes drops completely during play.
- **REN-09:** Allocation crashes and severe stalls during framebuffer rebuilds, with device-local use above its reported budget and nearly 20 GB process RAM reported. The reserved workspace covers full-resolution scene/OIT, TAA and GTAO targets before geometry growth; terrain pools share immutable quad indices, and abandoned game meshes retire through the GPU timeline. The reported lag and RAM growth remain open.
- **REN-10:** Additional Vulkan correctness defects in supported attachment, shader translation, mod API and provider transition paths.

## Planned

### 1. Shared compute foundation

- **PERF-08: Shared GPU geometry and draw preparation**
  1. Animation and mesh deformation: offload measured CPU pose/deformation work for supported entity and animated-block paths. Produce shared geometry once per rendered frame for both eyes and ray-tracing acceleration structures, retaining gameplay attachment transforms and correct current/previous geometry for motion vectors.
  2. GPU geometry buffers: provide shared raster/compute/ray-tracing inputs with explicit synchronization, bounded allocation and safe retirement across mesh edits, world changes and runtime settings transitions.
  3. GPU visibility and draw preparation: move terrain/entity frustum and depth-pyramid occlusion tests into compute, compact visible draws and generate indirect commands without CPU readback per object. Support separate eye visibility, conservative camera-motion handling and separate shadow rules. Keep geometry needed by off-screen reflections, shadows and indirect lighting available to ray tracing.
  - Vendor-neutral Vulkan compute, live controls and safe CPU/GPU transitions. Measure CPU/GPU time and frame-time spikes including transfer and synchronization costs.

### 2. Complete VR

- **VR-01: OpenXR VR**
  - First stereo step: OpenXR session and runtime-sized eye swapchains on the renderer's Vulkan device; two tracked eye views, runtime FOVs, predicted frame timing, headset submission before the desktop mirror, and head-locked flat menus. Enable OpenXR on the Device settings page and restart with the headset connected. Raster rendering only; desktop temporal-provider settings are retained. Code TODOs identify direct XR composition, multiview, late pose sampling and the remaining HUD/input work.
  1. Per-eye temporal state: TAA history, motion vectors and jitter; per-eye or disabled upscaler and frame-generation contexts where the SDKs don't support VR.
  2. Motion-controller input through OpenXR actions: movement, look, interaction, hotbar, inventory, with bindings for common controllers.
  3. GUI on world-space panels, plus comfort options (snap/smooth turn, vignette, seated/standing).
  4. Full-body pose from head and hand tracking via inverse kinematics on the player model.
  5. Server companion mod that streams head/hand poses so other players see VR players' tracked movement, with fallback to normal animation for players without it.
  6. Reuse PERF-08 geometry across both eyes and generate eye-specific draw lists. Complete direct XR composition, multiview and late pose sampling with eye-indexed terrain, entity, post-effect and mod-pass inputs.

### 3. Desktop ray tracing

- **RT-01: Vulkan ray tracing and reconstruction**
  1. Reuse PERF-08 geometry for KHR acceleration structures: persistent terrain, entities and animated blocks, with incremental updates for edits, animation, streaming and world transitions. Retain material, alpha-test and mod-geometry behavior.
  2. Integrate ray-traced lighting effects, including shadows, reflections and indirect illumination, into the real game render path with live controls and a raster path when tracing is disabled or unavailable.
  3. Supply current depth, motion, normals, roughness, albedo and hit-distance guides for temporal reconstruction and compute denoising. Keep the reconstruction path vendor-neutral, with vendor-specific backends optional.

### 4. Ray tracing in VR

- **VR-02: Stereo ray tracing**
  1. Combine completed VR-01 and RT-01 paths: share geometry and scene acceleration structures across eyes, with separate eye rays, depth, motion, reconstruction guides and temporal histories.
  2. Integrate tracing and denoising into OpenXR frame timing and submission, with bounded GPU work and live quality controls that preserve stereo consistency and headset responsiveness.

### Independent compute work

- **PERF-09: Additional Vulkan compute offload** (can progress independently of the VR/ray-tracing sequence)
  1. Terrain generation in singleplayer: batch the stock generator's noise and density calculations on the GPU, then feed results into the existing server-side generation passes. Preserve seed/configuration output and chunk borders; overlap readback with CPU work. Keep structures, vegetation and mod callbacks in their existing pipeline, with CPU generation for unsupported paths.
  2. GPU particles: move supported particle simulation and render-geometry production onto the GPU, including lifetime, motion and visual properties. Preserve collision behavior, spawn/death events, mod-provider semantics and previous rendered state for temporal reconstruction; reuse simulation across eyes.
  3. Chunk meshing: move suitable block-face generation, visibility and lighting/AO calculations into compute, with meshes consumed directly by rendering and suitable geometry shared with RT-01. Preserve stock and custom block shapes, material boundaries, chunk-edge updates and mod tessellator behavior; retain CPU handling for unsupported producers.
  - Live controls and safe CPU/GPU transitions. Bound GPU memory and queued work so generation cannot starve rendering. Compare CPU time, GPU time, frame-time spikes and chunk throughput on equivalent workloads; gains must include transfer and synchronization costs.

### Other planned features

- **MOD-01: General mod compatibility**
  1. Extend motion histories to independently moving custom subobjects and procedural geometry using actual producer identities and previous deformation inputs.
  2. Completed the shared mesh/shader/window compatibility path, bounded atlas feedback and SDK/overlay presentation lifetime corrections.
  3. Validated the original issue #10 pack through world loading, provider changes, world exit and clean teardown with RTSS enabled and zero Vulkan errors. [Current evidence](validation/mod-compatibility.md).

- **PACK-01: Shader packs**
  1. Pack contract: replaceable programs, provided uniforms/samplers, readable and writable attachments, pass order, options. Decide which internal layouts become public API.
  2. Loader: `shaderpacks/` folder, pack selection and per-pack options in the Options menu, hot reload; compiled at runtime with the shipped shaderc.
  3. Per-program fallback to the built-in shaders on compile/link errors, with in-game error reporting.
  4. Pipeline cache keyed by the active pack.
  5. Pack-declared composite/deferred passes in the frame graph.

## Deferred

- **TEST-01:** Tests.
- **LINUX-01:** Linux release.
