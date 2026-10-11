# Roadmap

Current release: [v0.1.1](https://github.com/KillerPixelCrew/VulkanStory/releases/tag/v0.1.1) (Windows x64, Vintage Story 1.22.7).

## Known defects

- **REN-10:** Additional Vulkan correctness defects in supported attachment, shader translation, mod API and provider transition paths.

## Planned

- **RT-01: Full path tracing**
  - In progress: retained terrain and shaped entity bodies with pose motion, frame-slot actor buffers and BLAS refits, traced frame outputs, Vulkan NRD opaque reconstruction, optional Vulkan DLSS RR at render resolution, depth/motion routing to frame generation and live controls. GPU skinning, held items, particles, procedural warping, full material/lighting coverage and AMD reconstruction are still planned.
  1. Portable Vulkan KHR ray tracing with a persistent scene for streamed terrain, animated entities, particles and off-screen geometry.
  2. Game materials and atlases, emissive and sky lighting, diffuse/specular bounces, alpha testing, glass and water.
  3. Current/previous scene state and coherent depth, motion, albedo, normal/roughness and hit-distance guides; a reconstruction path that works across GPU vendors, with optional DLSS Ray Reconstruction and FSR Ray Regeneration.
  4. Live raster/path-tracing and reconstruction controls, world transitions, resize handling and existing post-processing/UI integration.

- **PACK-01: Shader packs**
  1. Pack contract: replaceable programs, provided uniforms/samplers, readable and writable attachments, pass order, options. Decide which internal layouts become public API.
  2. Loader: `shaderpacks/` folder, pack selection and per-pack options in the Options menu, hot reload; compiled at runtime with the shipped shaderc.
  3. Per-program fallback to the built-in shaders on compile/link errors, with in-game error reporting.
  4. Pipeline cache keyed by the active pack.
  5. Pack-declared composite/deferred passes in the frame graph.

- **VR-01: OpenXR VR**
  - First stereo step: OpenXR session and eye swapchains on the renderer's Vulkan device; two tracked eye views, runtime FOVs, headset presentation and a right-eye desktop mirror. Enable OpenXR on the Device settings page and restart with the headset connected. Raster rendering only; desktop temporal-provider settings are retained.
  1. Per-eye temporal state: TAA history, motion vectors and jitter; per-eye or disabled upscaler and frame-generation contexts where the SDKs don't support VR.
  2. Motion-controller input through OpenXR actions: movement, look, interaction, hotbar, inventory, with bindings for common controllers.
  3. GUI on world-space panels, plus comfort options (snap/smooth turn, vignette, seated/standing).
  4. Full-body pose from head and hand tracking via inverse kinematics on the player model.
  5. Server companion mod that streams head/hand poses so other players see VR players' tracked movement, with fallback to normal animation for players without it.

## Deferred

- **GL-02 / GL-03:** OpenGL compatibility adapters and profiles for other mods, once a target mod is chosen.
- **TEST-01:** Tests.
- **LINUX-01:** Linux release.
