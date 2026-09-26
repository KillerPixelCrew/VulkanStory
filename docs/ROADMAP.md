# Optimum roadmap

**Goal:** a smooth, playable Vintage Story on handheld PCs, not only maximum FPS on strong
GPUs. The reference device is the **MSI Claw 8 AI+** (Intel Core Ultra 7 258V: 4 P-cores
and 4 LP E-cores; Arc 140V iGPU with XMX; unified LPDDR5X; 1920×1200 120 Hz VRR;
Windows 11), and development moves onto it. Work is judged by frame-time consistency
(1% lows), CPU and GPU efficiency under a shared power budget, streaming without hitches,
battery life, and controller-first play. Results from the RTX 4070 Laptop development system
(an Optimus laptop whose Intel iGPU scans out) find costs; they are not the target.

The project is standalone. It renders with Vulkan only and supports Windows and Linux.
macOS is dropped. DX12 remains as an optional Windows interop subsystem for XeSS FG and
FSR 4.

**Done** means integrated into the Vulkan renderer, exposed in the product configuration
where appropriate, and validated with the available automated and in-game checks. A
vendor or GPU missing from the development machine is recorded as a validation gap; it does
not move an otherwise complete integration back into planned work.

Impact ratings below are estimates from reading the code unless a measurement is cited. The
Claw baseline in milestone 1 decides the final order inside each later milestone.

## Done

| Area | Delivered | Evidence |
| --- | --- | --- |
| **Vulkan-native renderer** | Native Vulkan rendering and shaders, frame graph and synchronization, async uploads, bindless resources, native post-processing, TAA, GTAO, separate HUD-less scene/UI resources, and Vulkan presentation | [`vulkan.md`](vulkan.md) |
| **Upscalers** | NVIDIA DLSS SR, Intel XeSS SR (native Vulkan), AMD FSR 3.1 SR, and AMD FSR 4 through the Vulkan/DX12 interop path; presets, fallback, and live switching share one renderer contract | [`upscaler.md`](upscaler.md) |
| **Frame generation** | NVIDIA DLSS-G, Intel XeSS FG, and AMD FSR 3 FG consume the common depth, motion, HUD-less scene, and premultiplied UI resources. The UI exposes 2x–6x; DLSS-G and XeSS-FG clamp to the SDK/GPU maximum, FSR 3 FG stays at 2x | [`frame-generation.md`](frame-generation.md) |
| **Low latency** | NVIDIA Reflex, Intel XeLL, and AMD Anti-Lag use the engine frame identity, frame cap, sleep, and simulation/render/present markers. Input sampling order and the PCL input ping are open (milestone 3) | [`frame-generation.md`](frame-generation.md) |
| **Optimization pass** | GPU timestamps (`stats.gpu`) found the major costs. Persistent chunk meshes moved to mapped VRAM where supported, the GTAO prefilter was parallelized, the foliage alpha test moved ahead of lighting, redundant pipeline binds were removed, and stray OpenGL calls in the cloud renderers were fixed | [`performance-profile-2026-09-26.md`](performance-profile-2026-09-26.md) |
| **XeSS-FG pacing** | The next frame's Vulkan work waits on the GPU for XeSS-FG's DX12 work instead of time-slicing with it, and the proxy `Present` runs on a present thread; the bimodal 2/10 ms presents are gone | [`performance-profile-2026-09-26.md`](performance-profile-2026-09-26.md) |
| **Platform scope** | macOS removed from packaging, installer, bootstrap, CI, and docs | — |

On the RTX 4070 Laptop, the optimization pass cut the native 1080p GPU frame from
**10.30 ms to 4.49 ms**. Focused-window 1080p Quality runs reached 2x output with all three
frame-generation providers: 334 FPS for FSR 3 FG, 258 FPS for DLSS-G, and 286 FPS for
XeSS-FG (229 before its pacing fix). These numbers describe that system and scene; they
are not guarantees.

## Now: milestone 1 — Claw baseline, handheld preset, and power

1. **Claw baseline:** capture native, XeSS SR, and XeSS SR + FG at 1920×1200 and at lower
   render scales, with GPU timestamps, PresentMon, and power/clock telemetry, in surface,
   forest, cave, village, and exploration scenes. Set targets, e.g. a stable 60 FPS and a
   40–60 FPS base for frame generation at the handheld's power limits. Revalidate XeSS FG
   there: on Intel the driver paces presentation (not the cross-vendor pacer measured so
   far) and XeLL is native.
2. **Greedy meshing on:** it is built but off by default (`OptimumConfig.GreedyMeshEnabled`).
   Enable it with a light tolerance of 1–2 (0 merges almost nothing under smooth lighting)
   and a far band. It cuts vertices in the main and shadow passes, and vertex fetch was the
   measured chunk bottleneck. Try `GreedyMeshTextureGrad=false` on Arc.
3. **Handheld shadow tier:** shadow maps are at least 4096² D32 at every quality
   (`VulkanClientPlatform.FrameBuffers.cs`), both cascades are redrawn every frame, and each
   lit pixel takes 18 comparison taps. Add a 2048/1024 tier with D16, and sample only the
   cascade that covers the pixel with 4 gathered taps.
4. **Frame pacing without busy-waiting:** `PreciseFramePacing` (on by default) ends every
   frame in a `Thread.Yield`/`SpinWait` tail (`ClientPlatformWindows.cs`) and keeps a
   process-wide `timeBeginPeriod(1)`. Replace the tail with a high-resolution waitable
   timer. Add power-aware caps (40/45/60, VRR), and prefer FIFO or a cap over uncapped
   MAILBOX on the handheld.
5. **Idle thread wakeups:** 8 client threads poll with `Thread.Sleep(5)`
   (`ClientThread.cs`), and about 6 singleplayer server threads poll every 2 ms
   (`ServerThread.SleepMs`). That is thousands of wakeups per second even when idle. Use
   event-driven waits, or at least longer idle sleeps.
6. **No forced blocking memory cleanup on battery:** with `OptimizeRamMode==2` the client
   forces a blocking full GC with large-object-heap compaction every 602 s (30 s when
   unfocused, `SystemCompressChunks.cs`), a guaranteed periodic hitch.
7. **Handheld preset:** XeSS SR on by default (render scale 0.67–0.77 otherwise), async
   particle caps 8–16k instead of 80k, `particleLevel` below 100, lower LOD bias, texture
   mip cap 4–5 instead of 3, flat clouds, shorter god-ray sampling, and a view distance
   tuned on the Claw.
8. **Unified-memory checks:** confirm the discrete-GPU VRAM placement does no harm on
   shared memory (all heaps are device-local there), and track memory footprint and
   battery life per preset.

## Next (in order)

### 2. Retire the OpenGL renderer

Cleanup that also narrows the SDL3 work to a Vulkan-only window.

- Make Vulkan the only renderer: `OptimumConfig.Renderer` still defaults to `"opengl"`.
  Remove the renderer choice and its migration paths.
- Replace the silent fall back to OpenGL with a clear startup error that names the missing
  Vulkan 1.3 feature, extension, or driver.
- Delete the OpenGL route: legacy post passes, GL state shims kept only for parity, and
  the GL-vs-native differential tests. Keep the GLSL rewriter; it still translates game and
  mod shaders to Vulkan.
- Drop OpenGL-only Cecil transplants and patches once nothing calls them, and give mods a
  documented Vulkan path instead of raw GL.
- Keep the DX12 interop. XeSS FG exists only as a D3D12 swap-chain proxy
  (`xefg_swapchain_d3d12.h`) with D3D12-only XeLL, and FSR 4 runs through the same
  Vulkan↔DX12 shared-image and shared-fence interop (`IDx12SharedRuntime`). It stays
  optional, falls back cleanly, and never stops Vulkan from starting. XeSS SR stays native
  Vulkan (`xess_vk.h`).

### 3. SDL3 platform layer and controller play

Required for handheld play: today the game has no controller support at all. SDL3
replaces OpenTK/GLFW windowing and all input, keyboard and mouse included. SDL only
delivers keyboard and mouse events for windows it owns, so it takes over the window and the
event loop. Controller play is modelled on Minecraft Java's controller mods (Controlify,
Controllable). OpenTK remains only for OpenAL audio.

- **Scope today:** window and input use sits in `ClientPlatformWindows` (83 window
  references, 48 of them `ClientSize`), `GameWindowNative`, and `ClientProgram`'s
  `GameWindow.Run` loop. Every key reaches the game through one translation,
  `KeyConverter.NewKeysToGlKeys`, and the game, API, and mods only see `GlKeys`,
  `KeyEvent`, and `MouseEvent`. A single SDL scancode table at that seam keeps game and mod
  code unchanged.
- **Window and loop:** an SDL3 window and our own frame loop replace `GameWindow.Run`.
  Vulkan gets its surface from SDL. DLSS-G, FSR 3, and the XeSS-FG DXGI proxy get the HWND
  from SDL's window properties. While XeSS FG is active the proxy is the only swap chain on
  that window, so SDL must not claim one, and resize and fullscreen changes must reach the
  proxy. Display modes, DPI, window state, icon, cursors, clipboard, and file drop move to
  SDL.
- **Keyboard, mouse, text:** relative mouse mode on raw input replaces the per-frame
  cursor recentring in `UpdateMousePosition`. SDL text input and IME cover chat, signs, and
  text fields, including composition for non-Latin languages.
- **Latency:** today the Reflex/XeLL/Anti-Lag sleep runs after OpenTK has pumped window
  input, so input waits through the sleep, and `InputSample` is marked together with
  `SimulationStart`. Owning the pump makes the order sleep → `SDL_PumpEvents` → sample.
  SDL3 event timestamps make `InputSample` real, and `SDL_SetWindowsMessageHook` answers
  Streamline PCL's `statsWindowMessage` ping with `ePCLatencyPing`.
- **Controllers:** SDL3's gamepad, sensor, and haptic subsystems through a C# binding such
  as `ppy.SDL3-CS`. Hot-plug, the community mapping database, the Claw's built-in
  controller, Xbox, DualShock 4/DualSense, Switch Pro, and Steam Deck controllers,
  coexisting with Steam Input. Controller actions map onto the hotkey system, so vanilla
  and mod hotkeys stay bindable. Analog movement and look with deadzones and response
  curves, optional gyro aiming, radial menus, and sneak/sprint toggles.
- **Menus without a mouse:** a virtual cursor that snaps to slots and widgets, D-pad focus
  movement, inventory and crafting slot actions (pick up, split, move stack), and an
  on-screen keyboard.
- **Feedback and settings:** controller-specific button glyphs in hints and keybinding
  screens, rumble and haptic trigger effects, per-controller profiles, a remapping and
  sensitivity UI, and seamless switching between controller and keyboard/mouse.
- **Compatibility and validation:** mods that reach into OpenTK windowing or GLFW directly
  need a list and shims where practical. Key-by-key parity for layouts (QWERTY, AZERTY,
  QWERTZ, dead keys), alt-tab, minimize/restore, multi-monitor, and a controller device
  matrix. SDL3 is zlib-licensed.

### 4. Culling rework

Visibility is the classic voxel-engine bottleneck. After the optimization pass, chunk
passes were still about 2.9 ms of a 4.5 ms native 1080p GPU frame, shadows included. On
Minecraft Java, Sodium plus culling add-ons are credited with large FPS gains; the goal is
the equivalent here, proven by measurement.

- **Today:** VS's ray-based `ChunkCuller` plus Optimum's Sodium-style BFS visibility walk
  (face connectivity through `ClientChunk.IsTraversable`), on by default, at whole
  32³-chunk granularity. CPU frustum tests per mesh-pool part (`MeshDataPoolManager`)
  before the multi-draw. Hidden faces between solid neighbours are already removed at
  meshing time (`SideOpaque`). Back faces are only culled by the rasterizer in the opaque,
  topsoil, and decorative passes, after every vertex was fetched and shaded, and all four
  shadow passes draw with face culling off.
- **Measure first:** per-pass counters for loaded, frustum-visible, BFS-visible, and drawn
  chunks and triangles, plus a sampled estimate of how much drawn geometry is occluded, in
  surface, forest, cave, and city scenes. Rank the items below by that data.
- **16³ culling sections:** a VS chunk is 32³ blocks, 8× the volume of Minecraft/Sodium's
  16³ section, so every frustum, BFS, and occlusion decision is coarse. Keep 32³ chunks for
  storage and meshing, but split each chunk's index ranges and connectivity graph into
  eight 16³ sections at tessellation time and cull per section. Multi-draw indirect keeps
  the extra ranges cheap. Check whether the ray culler still earns its cost next to BFS.
- **Per-facing geometry:** tessellate quads into six facing groups (plus unaligned) and draw
  only the groups that can face the camera (Sodium's block-face culling), removing back
  faces before the vertex shader. The same test against the light direction applies to
  shadows.
- **GPU-driven culling:** frustum and occlusion tests in a compute pass that writes the
  existing indirect draws (reprojected depth-pyramid occlusion), with separate tests for
  shadow cascades.
- **Entities and block entities:** entities are culled by dimension, distance with
  hysteresis, a frustum sphere, and their chunk's visibility (`SystemRenderEntities`), so
  anything inside a visible 32³ chunk is animated and drawn even when hidden. Add
  per-entity occlusion (asynchronous ray-casts against block opacity, like Minecraft's
  Entity Culling, or the depth pyramid) with hysteresis. Culled entities skip animation,
  draws, and name tags; shadows get their own light-view test. Put block-entity renderers
  behind the same gate, and add distance/size culling for small decorative geometry.
- **Guardrails:** no popping or see-through holes (the MC-70850 class of bug that BFS
  connectivity guards against), culling state consistent for frame generation and TAA
  history, and every step proven with GPU timestamps and the counters above.

### 5. CPU efficiency and streaming

On the development system the main thread already spends about as long per frame as the GPU
(simulation ~1.6 ms plus render recording/submit ~2.8 ms against a 4.5 ms GPU frame). On a
handheld, CPU time also takes power from the GPU.

- **Entity animation LOD:** block animators have distance LOD (`AnimBlockLod`), but
  entities recompute every joint every frame (`AnimatorBase.OnFrame` →
  `ClientAnimator.calculateMatrices`), and the head controller runs even for culled
  entities. Reuse the block LOD tiers (full rate up close, every 2nd–3rd frame at mid range,
  ~10 Hz far) while keeping attachment points, collision boxes, and animation sounds
  correct. High impact in villages and herds.
- **Particles:** the main particle pools simulate and collide on the main thread
  (`ParticlePoolQuads.OnNewFrame`), and the async pools still extrapolate every particle on
  the CPU each frame. Move main-pool simulation to the async thread, extrapolate in the
  vertex shader, and gate spawns by a shorter distance.
- **Render-thread overhead:** several arrays and descriptor keys are allocated per draw
  (`VulkanDevice.Binding.cs`), sets that name a ring offset are rebuilt every frame, and
  passes copy their read lists with `ToArray()`. Use reused scratch buffers and struct keys,
  cache descriptor state, and let GPU culling generate draws.
- **Chunk upload budget:** the main-thread upload drain is limited by vertex count, not time
  (`ChunkTesselatorManager.OnBeforeFrame`). Give it a time budget to remove exploration
  spikes.
- **Singleplayer loopback:** chunk and map-chunk packets have no direct handler, so they go
  through protobuf serialize → clone → parse plus a ZSTD round trip even in-process. Hand the
  objects to the network-processing thread directly, then share uncompressed chunk data.
- **Hybrid cores:** no thread has an affinity or QoS hint except the main thread
  (`AboveNormal`). Keep the render, tessellation, culling, and XeSS present threads on
  P-cores, and move chunk compression, pipeline prewarm, sound loading, and cache
  persistence to E-cores (EcoQoS). On 8 logical CPUs the worldgen policy adds no extra
  workers, and the singleplayer server runs `BelowNormal`; tune both on the Claw.
- **Far entities:** distance-gate the per-entity interpolation renderers and client
  `Entity.OnGameTick` for far, unrendered entities. Enable the existing
  `DistanceSendFrequency` and far-entity tick stride, and consider strided server AI beyond
  ~48 blocks.
- **Streaming (C2ME-style):** worldgen is already multithreaded (from Stratum), chunk
  deserialization is parallel, and frustum culling uses SIMD. Measure a fly/teleport
  benchmark (chunks generated, lit, meshed, and uploaded per second, plus hitch counts) on
  the Claw, then parallelize or vectorize what it points to: noise with `Vector256`,
  lighting propagation, and tessellation.
- **Runtime:** consider ReadyToRun (checked against the Cecil transplant flow) for startup
  JIT on E-cores, and `System.GC.ConserveMemory`/`RetainVM`. Stay on workstation concurrent
  GC.
- **Small fixes:** string-equality guards before rich-text and hover-text `SetNewText`
  (the hotbar recomposes a Cairo tooltip every frame while it is shown).
- **Rule:** faster math only in measured hot loops; engine overhead is fixed by doing less
  work, not by faster arithmetic.

### 6. GPU efficiency

- **Smaller vertices:** the default path uses 64 bytes of face data per quad (fp32
  positions and edge vectors) plus 4 bytes of light per vertex, with 32-bit indices.
  Quantize to about 32 bytes (chunk-local 16-bit positions, small-integer edges, packed
  flags) and use 16-bit or shader-generated indices. About 40% less vertex bandwidth,
  aimed directly at the measured bottleneck.
- **Shadow caching:** update the far cascade every 2nd–4th frame, or when the sun or camera
  moves meaningfully, with texel-snapped projection.
- **Half-resolution passes:** volumetric clouds march up to 200 steps per pixel at full
  resolution; bloom's bright-pass runs at full resolution but only feeds half-resolution
  blurs; GTAO has no half-resolution option, and upscaling bumps it from Medium to High.
  Add half/quarter-resolution variants with depth-aware upsampling, and slim the G-buffer
  (position target, normal encoding).
- **Unified-memory uploads:** static meshes still go staging → GPU copy
  (`MeshManager.cs`), a pointless extra pass through the same RAM on an iGPU. Write them
  directly into device-local host-visible memory, and use smaller staging and image blocks
  there (about 64 MB+ saved).
- **Dynamic resolution:** none exists. Drive render scale from GPU timestamps to hold a
  target frame time, within upscaler and TAA history rules.
- **Compressed textures:** block, entity, and item atlases are uncompressed RGBA8 (about
  64 MB per 4096² atlas). BC7 cuts texture traffic about 4×; tile bounds and runtime atlas
  rebuilds make it the last step here.

## Later

- **Far-terrain LOD:** merged/downsampled geometry for distant chunks. Very high impact
  for long view distances on a handheld, but the largest effort.
- **Mod-facing renderer API:** stabilize native pass, resource, motion-writer, and
  capability contracts so mods participate without OpenGL assumptions.
- **HDR output:** HDR scene range and tone mapper, display-referred UI, HDR10 or scRGB
  presentation, and an FG-compatible format path.
- **Auto-PBR materials:** normal, roughness/metalness, and emissive companion atlas data
  generated from VS material classes, with authored resource-pack data taking precedence.
- **Ray tracing and denoising:** acceleration-structure maintenance for the editable chunk
  world, then ray-query AO, shadows, and reflections with cross-vendor denoising and DLSS
  Ray Reconstruction where available.
- **Documentation cleanup:** remove stale implementation notes, keep the reasons behind
  non-obvious synchronization and SDK decisions, and make renderer entry points
  approachable once the systems stop moving.

## Validation and polish (ongoing)

- Run FSR 4 on supported AMD hardware; its fallback and code path are covered, but no
  current development device can execute it.
- Resolve or conclusively classify the two real-window resize synchronization-validation
  reports from the performance pass.
- Extend visible-window and cross-vendor coverage for resize, minimize/restore, history
  reset, motion-vector scale/sign, UI recomposition, and multi-frame limits.
- Register `HudDebugScreen.cs.patch` and `ShaderProgram.cs.patch` in
  `patches/cecil-owned.list` (or the ownership test's allow-list); the Cecil ownership test
  fails until they are.
- Keep measuring before taking barrier, descriptor, or interop shortcuts; the profile lists
  candidates, not pre-approved optimizations.

## Product rules

- Handheld playability is the bar: 1% lows, power, and battery count alongside average FPS,
  and claims are measured on the Claw.
- Frame generation always consumes a genuinely HUD-less scene and a separate UI resource;
  generated UI is not acceptable.
- Vendor features remain optional at runtime. Missing SDK binaries or unsupported hardware
  fall back cleanly without preventing Vulkan from starting.
- Requested settings and effective SDK state are distinct. The renderer reports clamping,
  fallback, and generated-present counts rather than pretending a requested multiplier ran.
- Correctness and pacing are judged on visible output and GPU/SDK evidence, not merely by
  successful feature creation or a passing headless capture.
